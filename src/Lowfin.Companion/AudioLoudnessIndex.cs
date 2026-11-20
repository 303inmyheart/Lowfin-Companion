using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion;

public sealed class AudioLoudnessIndex(LoudnessStore store,ILoudnessSource source,ILoudnessAnalyzer analyzer,ILogger<AudioLoudnessIndex> log) {
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly ConcurrentDictionary<Guid,byte> pending=new();
    private readonly object sync=new();
    private CancellationTokenSource? active;
    private int full=1,completed,measured,failed,reused,derived,unavailable;
    private bool running;
    private DateTimeOffset? lastCompleted;
    public bool FullRequested=>Volatile.Read(ref full)!=0;
    public bool HasPending=>!pending.IsEmpty;
    public bool Running { get { lock(sync) return running; } }
    public DateTimeOffset? LastReconciled {get;private set;}
    public void RequestFull()=>Interlocked.Exchange(ref full,1);
    public void Queue(Guid id) { if(id==Guid.Empty) return;if(pending.Count>=4096) RequestFull();else pending.TryAdd(id,0); }
    public void Interrupt() { lock(sync) active?.Cancel();RequestFull(); }
    public object Status() { var stored=store.StoredStatus();lock(sync) return new { running,itemsChecked=completed,profilesMeasured=measured,profilesFailed=failed,reusedProfiles=reused,derivedProfiles=derived,unavailableTracks=unavailable,queued=pending.Count,reconciliationPending=FullRequested,lastCompleted,stored }; }
    public LoudnessEntry[] Get(Guid id,string sourceId) {
        // Recheck file identity before serving; no stale gain while an updated file awaits analysis.
        var tracks=source.Tracks(id).Where(track=>track.SourceId==sourceId).ToArray();
        var fingerprints=tracks.ToDictionary(track=>track.StreamIndex,track=>track.Fingerprint());
        return store.Read(id).Entries.Where(entry=>entry.SourceId==sourceId&&fingerprints.TryGetValue(entry.Measurement.StreamIndex,out string? fingerprint)&&fingerprint!=null&&entry.Measurement.Fingerprint==fingerprint).ToArray();
    }
    public async Task Reconcile(CancellationToken cancellation,IProgress<double>? progress=null) {
        await Run(true,cancellation,progress);
    }
    public Task Update(CancellationToken cancellation,IProgress<double>? progress=null)=>Run(false,cancellation,progress);
    private async Task Run(bool all,CancellationToken cancellation,IProgress<double>? progress) {
        progress?.Report(0);await gate.WaitAsync(cancellation);
        using var run=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock(sync) { active=run;running=true;completed=measured=failed=reused=derived=unavailable=0; }
        try {
            if(all) Interlocked.Exchange(ref full,0);
            // Reuse each paged library object instead of looking it up again for every file.
            // Only a page is held in memory, even during a multi-day initial measurement.
            Guid[] queued=all?[]:pending.Keys.ToArray();
            int total=all?source.TotalCount()??0:queued.Length;
            var candidates=all?source.Candidates(run.Token):queued.Select(id=>new LoudnessCandidate(id));
            foreach(var candidate in candidates) {
                Guid id=candidate.Id;
                run.Token.ThrowIfCancellationRequested();pending.TryRemove(id,out _);
                int done=completed;
                await Check(id,run.Token,new InlineProgress(value=>progress?.Report(Math.Min(99.9,100d*(done+value)/Math.Max(1,total)))),candidate.Tracks);
                lock(sync) completed++;
                progress?.Report(Math.Min(99.9,100d*completed/Math.Max(1,total)));
            }
            lock(sync) { lastCompleted=DateTimeOffset.UtcNow;if(all) LastReconciled=lastCompleted; }
            progress?.Report(100);
        } catch { RequestFull();throw; }
        finally { lock(sync) { running=false;active=null; }gate.Release(); }
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value)=>report(value); }
    private async Task Check(Guid id,CancellationToken cancellation,IProgress<double> progress,IReadOnlyList<LoudnessTrack>? candidate=null) {
        IReadOnlyList<LoudnessTrack> tracks=candidate??source.Tracks(id);
        if(tracks.Count==0) { store.Remove(id);return; }
        var old=store.Read(id);var persisted=old;var entries=new List<LoudnessEntry>();var failures=new List<LoudnessFailure>();
        void Persist(LoudnessSnapshot next) {
            if(persisted.Entries.SequenceEqual(next.Entries)&&persisted.Failures.SequenceEqual(next.Failures)) return;
            store.Write(id,next);persisted=next;
        }
        int total=tracks.Sum(track=>LoudnessProfiles.For(track).Count()),processed=0;
        foreach(var track in tracks) {
            string? fingerprint=track.Fingerprint();
            if(fingerprint==null) {
                // An offline share is not a deleted library item. Preserve facts until the file
                // is readable again; Get() still refuses them while identity cannot be verified.
                entries.AddRange(old.Entries.Where(entry=>entry.SourceId==track.SourceId&&entry.Measurement.StreamIndex==track.StreamIndex));
                failures.AddRange(old.Failures.Where(entry=>entry.SourceId==track.SourceId&&entry.StreamIndex==track.StreamIndex));
                lock(sync) unavailable++;continue;
            }
            foreach(string profile in LoudnessProfiles.For(track)) {
                cancellation.ThrowIfCancellationRequested();
                var cached=old.Entries.FirstOrDefault(entry=>entry.SourceId==track.SourceId&&entry.Measurement.StreamIndex==track.StreamIndex&&entry.Measurement.Profile==profile&&entry.Measurement.Fingerprint==fingerprint);
                if(cached!=null) { entries.Add(cached);lock(sync) reused++;progress.Report((double)++processed/total);continue; }
                var failure=old.Failures.FirstOrDefault(entry=>entry.SourceId==track.SourceId&&entry.StreamIndex==track.StreamIndex&&entry.Profile==profile&&entry.Fingerprint==fingerprint&&entry.RetryAfter>DateTimeOffset.UtcNow);
                if(failure!=null) { failures.Add(failure);progress.Report((double)++processed/total);continue; }
                try {
                    var native=profile==LoudnessProfiles.Stereo&&track.Channels==2?entries.FirstOrDefault(entry=>entry.SourceId==track.SourceId&&entry.Measurement.StreamIndex==track.StreamIndex&&entry.Measurement.Profile==LoudnessProfiles.Native&&entry.Measurement.Fingerprint==fingerprint):null;
                    // The two-channel stereo matrix is exactly the identity. Its second profile
                    // can reuse the native measurement without decoding the entire file twice.
                    var result=native!=null?native.Measurement with {Profile=profile}:await analyzer.Measure(track,profile,fingerprint,new InlineProgress(value=>progress.Report((processed+Math.Clamp(value,0,1))/total)),cancellation);
                    // A file replacement while decoding invalidates the complete result.
                    if(track.Fingerprint()!=fingerprint) { Queue(id);continue; }
                    entries.Add(new(track.SourceId,result));lock(sync) {if(native==null) measured++;else derived++;}
                } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { throw; }
                catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) {
                    lock(sync) failed++;
                    failures.Add(new(track.SourceId,track.StreamIndex,profile,fingerprint,DateTimeOffset.UtcNow.AddHours(24)));
                    log.LogWarning("Lowfin loudness: analysis deferred for item {ItemId}, audio stream {StreamIndex}, profile {Profile} ({ErrorType}).",id,track.StreamIndex,profile,error.GetType().Name);
                }
                // Persist after every completed profile, so cancellation/crashes do not lose hours of work.
                // Keep untouched old results until this item's pass is complete.
                var keep=old.Entries.Where(entry=>!entries.Any(done=>done.SourceId==entry.SourceId&&done.Measurement.StreamIndex==entry.Measurement.StreamIndex&&done.Measurement.Profile==entry.Measurement.Profile));
                Persist(new(1,entries.Concat(keep).ToArray(),failures.ToArray()));
                progress.Report((double)++processed/total);
            }
        }
        Persist(new(1,entries.ToArray(),failures.ToArray()));
    }
}
