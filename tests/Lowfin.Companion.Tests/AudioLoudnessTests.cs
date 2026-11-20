using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;
using Moq;

namespace Lowfin.Companion.Tests;

public sealed class AudioLoudnessTests : IDisposable {
    private readonly string root=Path.Combine(Path.GetTempPath(),"lowfin-audio-test-"+Guid.NewGuid().ToString("N"));
    public AudioLoudnessTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    private LoudnessTrack Track(Guid id,string source="version-a",int stream=1) {
        string file=Path.Combine(root,source+".mkv");if(!File.Exists(file)) File.WriteAllText(file,"sample");
        return new(id,source,stream,file,2,"stereo","aac",48000);
    }
    private sealed class Source : ILoudnessSource {
        public List<LoudnessTrack> Items=[];
        public bool QueryFails;
        public IEnumerable<Guid> All(CancellationToken token) { if(QueryFails) throw new IOException("fixture unavailable");return Items.Select(t=>t.ItemId).Distinct(); }
        public IReadOnlyList<LoudnessTrack> Tracks(Guid id)=>Items.Where(t=>t.ItemId==id).ToArray();
    }
    private sealed class Analyzer : ILoudnessAnalyzer {
        public int Calls;public Action<LoudnessTrack>? During;public bool Fail;
        public Task<LoudnessMeasurement> Measure(LoudnessTrack track,string profile,string fingerprint,IProgress<double>? progress,CancellationToken token) {
            Calls++;During?.Invoke(track);token.ThrowIfCancellationRequested();if(Fail) throw new InvalidDataException("test");
            progress?.Report(.5);return Task.FromResult(new LoudnessMeasurement(track.StreamIndex,profile,fingerprint,-24,8,-2,DateTimeOffset.UtcNow));
        }
    }
    private AudioLoudnessIndex Index(Source source,Analyzer analyzer)=>new(new(root),source,analyzer,NullLogger<AudioLoudnessIndex>.Instance);
    private static async Task<LoudnessStoredStatus> Stored(LoudnessStore store) {
        for(int attempt=0;attempt<500;attempt++) {var value=store.StoredStatus();if(value.Ready||value.Failed)return value;await Task.Delay(10);}
        throw new TimeoutException("Stored profile inventory did not complete.");
    }
    [Fact] public void StoredStatusWireNamesDoNotDependOnJellyfinNamingPolicy() {
        foreach(var policy in new System.Text.Json.JsonNamingPolicy?[]{null,System.Text.Json.JsonNamingPolicy.CamelCase}) {
            string json=System.Text.Json.JsonSerializer.Serialize(new LoudnessStoredStatus(true,false,4,12),new System.Text.Json.JsonSerializerOptions {PropertyNamingPolicy=policy});
            using var document=System.Text.Json.JsonDocument.Parse(json);var value=document.RootElement;
            Assert.True(value.GetProperty("ready").GetBoolean());Assert.False(value.GetProperty("failed").GetBoolean());Assert.Equal(4,value.GetProperty("items").GetInt32());Assert.Equal(12,value.GetProperty("profiles").GetInt64());
            Assert.False(value.TryGetProperty("Ready",out _));
        }
    }
    [Fact] public async Task StoredTotalsSurviveRestartAndTrackReplacementAndDeletion() {
        Guid id=Guid.NewGuid(),second=Guid.NewGuid();var source=new Source {Items=[Track(id),Track(second,"second")]};
        await Index(source,new()).Reconcile(default);
        var restarted=new LoudnessStore(root);var total=await Stored(restarted);
        Assert.True(total.Ready);Assert.False(total.Failed);Assert.Equal(4,total.Profiles);Assert.Equal(2,total.Items);
        var snapshot=restarted.Read(id);restarted.Write(id,snapshot with {Entries=snapshot.Entries.Take(1).ToArray()});
        Assert.Equal(3,restarted.StoredStatus().Profiles);restarted.Remove(second);
        Assert.Equal(1,restarted.StoredStatus().Profiles);Assert.Equal(1,restarted.StoredStatus().Items);
        restarted.Remove(id);Assert.Equal(0,restarted.StoredStatus().Profiles);Assert.Equal(0,restarted.StoredStatus().Items);
    }
    [Fact] public async Task StoredInventoryDoesNotDoubleCountConcurrentWritesOrRestoreDeletedEntries() {
        var writer=new LoudnessStore(root);Guid id=Guid.NewGuid();
        var entry=new LoudnessEntry("source",new(1,LoudnessProfiles.Native,"fingerprint",-24,8,-2,DateTimeOffset.UtcNow));
        writer.Write(id,new(1,[entry],[]));var restarted=new LoudnessStore(root);restarted.StoredStatus();
        for(int i=0;i<100;i++) {restarted.Write(id,new(1,[entry,entry with {Measurement=entry.Measurement with {Profile=LoudnessProfiles.Stereo}}],[]));restarted.Remove(id);}
        Assert.Equal(0,(await Stored(restarted)).Profiles);Assert.Equal(0,restarted.StoredStatus().Items);
    }
    [Fact] public async Task EmptyStoreCountsWithoutCreatingMeasurementFiles() {
        var store=new LoudnessStore(Path.Combine(root,"empty"));var total=await Stored(store);
        Assert.True(total.Ready);Assert.Equal(0,total.Profiles);Assert.False(Directory.Exists(Path.Combine(root,"empty")));
    }
    [Fact] public async Task PartialRecordsCannotLeaveStoredInventoryCountingForever() {
        Guid id=Guid.NewGuid();var source=new Source {Items=[Track(id)]};await Index(source,new()).Reconcile(default);
        string folder=Path.Combine(root,"audio-loudness"),good=Path.Combine(folder,id.ToString("N")+".json");string original=File.ReadAllText(good);
        foreach(string json in new[]{"{\"version\":1,\"entries\":null,\"failures\":null}","{\"version\":1,\"entries\":[null,{}],\"failures\":[null]}","{bad json"})
            File.WriteAllText(Path.Combine(folder,Guid.NewGuid().ToString("N")+".json"),json);
        var restarted=new LoudnessStore(root);var result=await Stored(restarted);
        Assert.True(result.Ready);Assert.False(result.Failed);Assert.Equal(2,result.Profiles);Assert.Equal(1,result.Items);
        Assert.Equal(original,File.ReadAllText(good));
    }
    [Fact] public async Task HealthyProfilesSurviveMissingFailureArraysAndMalformedNeighborRows() {
        Guid id=Guid.NewGuid();var source=new Source {Items=[Track(id)]};await Index(source,new()).Reconcile(default);
        string path=Path.Combine(root,"audio-loudness",id.ToString("N")+".json");
        var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();json.Remove("failures");json["entries"]!.AsArray().Add((System.Text.Json.Nodes.JsonNode?)null);json["entries"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject());File.WriteAllText(path,json.ToJsonString());
        var restarted=new LoudnessStore(root);Assert.Equal(2,restarted.Read(id).Entries.Length);Assert.Empty(restarted.Read(id).Failures);
        var result=await Stored(restarted);Assert.True(result.Ready);Assert.Equal(2,result.Profiles);Assert.Equal(1,result.Items);
    }
    [Fact] public async Task EveryVersionAndFileStreamHasSeparatePersistentProfiles() {
        Guid id=Guid.NewGuid();var source=new Source { Items=[Track(id),Track(id,stream:3),Track(id,"version-b")] };var analyzer=new Analyzer();var index=Index(source,analyzer);
        await index.Reconcile(default);Assert.Equal(3,analyzer.Calls);
        Assert.Equal(4,index.Get(id,"version-a").Length);Assert.Equal(2,index.Get(id,"version-b").Length);
        var restarted=Index(source,analyzer);await restarted.Reconcile(default);Assert.Equal(3,analyzer.Calls);
        // A metadata-only event must not decode any audio again.
        restarted.Queue(id);await restarted.Update(default);Assert.Equal(3,analyzer.Calls);
        File.AppendAllText(source.Items[0].Path,"changed");Assert.Empty(index.Get(id,"version-a"));
        await restarted.Reconcile(default);Assert.Equal(5,analyzer.Calls);Assert.Equal(2,index.Get(id,"version-b").Length);
    }
    [Fact] public async Task ChangingFileDuringMeasurementNeverPublishesStaleGain() {
        Guid id=Guid.NewGuid();var track=Track(id);var source=new Source { Items=[track] };var analyzer=new Analyzer { During=t=>File.AppendAllText(t.Path,"changed") };var index=Index(source,analyzer);
        await index.Reconcile(default);Assert.Empty(index.Get(id,track.SourceId));Assert.True(index.HasPending);
    }
    [Fact] public async Task CancelledScanKeepsAlreadyCompletedProfiles() {
        Guid id=Guid.NewGuid();var track=Track(id) with {Channels=6,Layout="5.1"};var source=new Source { Items=[track] };using var cancel=new CancellationTokenSource();var analyzer=new Analyzer();
        analyzer.During=t=> { if(analyzer.Calls==2) cancel.Cancel(); };var index=Index(source,analyzer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>index.Reconcile(cancel.Token));Assert.Single(index.Get(id,track.SourceId));
        analyzer.During=null;await index.Reconcile(default);Assert.Equal(3,analyzer.Calls);Assert.Equal(2,index.Get(id,track.SourceId).Length);
    }
    [Fact] public async Task FailuresBackOffButChangedFilesRetryImmediately() {
        Guid id=Guid.NewGuid();var track=Track(id);var source=new Source { Items=[track] };var analyzer=new Analyzer { Fail=true };var index=Index(source,analyzer);
        await index.Reconcile(default);await index.Reconcile(default);Assert.Equal(2,analyzer.Calls);Assert.Empty(index.Get(id,track.SourceId));
        analyzer.Fail=false;File.AppendAllText(track.Path,"new");await index.Reconcile(default);Assert.Equal(3,analyzer.Calls);Assert.Equal(2,index.Get(id,track.SourceId).Length);
    }
    [Fact] public async Task TemporarilyUnavailableFileKeepsMeasurementsWithoutServingUnverifiedGain() {
        Guid id=Guid.NewGuid();var track=Track(id);var source=new Source {Items=[track]};var analyzer=new Analyzer();var index=Index(source,analyzer);await index.Reconcile(default);int calls=analyzer.Calls;
        File.Move(track.Path,track.Path+".offline");await index.Reconcile(default);Assert.Empty(index.Get(id,track.SourceId));Assert.Equal(2,new LoudnessStore(root).Read(id).Entries.Length);
        File.Move(track.Path+".offline",track.Path);await index.Reconcile(default);Assert.Equal(calls,analyzer.Calls);Assert.Equal(2,index.Get(id,track.SourceId).Length);
    }
    [Fact] public async Task DeletionAndTrackRemovalRemoveMeasurements() {
        Guid id=Guid.NewGuid();var source=new Source { Items=[Track(id),Track(id,stream:3)] };var index=Index(source,new());await index.Reconcile(default);
        source.Items.RemoveAt(1);index.Queue(id);await index.Update(default);Assert.Equal(2,index.Get(id,"version-a").Length);
        source.Items.Clear();index.Queue(id);await index.Update(default);Assert.Empty(new LoudnessStore(root).Read(id).Entries);
    }
    [Fact] public void MeasurementParserRejectsSilenceAndUsesFinalSummary() {
        var track=Track(Guid.NewGuid());string summary="Summary:\n I: -24.2 LUFS\n LRA: 9.5 LU\n True peak:\n Peak: -1.7 dBFS";
        var result=LoudnessProfiles.Parse("I: -50 LUFS\n"+summary,track,LoudnessProfiles.Native,"test");Assert.Equal(-24.2,result.IntegratedLufs);Assert.Equal(-1.7,result.TruePeakDb);
        Assert.Throws<InvalidDataException>(()=>LoudnessProfiles.Parse(summary.Replace("-24.2","-70.0"),track,LoudnessProfiles.Native,"test"));
        Assert.Throws<InvalidDataException>(()=>LoudnessProfiles.Parse(summary.Replace("-1.7","-inf"),track,LoudnessProfiles.Native,"test"));
    }
    [Fact] public void DownmixRequiresKnownLayoutAndArgumentsUseExactFileStreamIndex() {
        Assert.Null(LoudnessProfiles.StereoFilter(8,"7.1"));Assert.Null(LoudnessProfiles.StereoFilter(6,"unknown"));Assert.NotNull(LoudnessProfiles.StereoFilter(6,"5.1(side)"));
        var args=AudioLoudnessAnalyzer.Arguments(Track(Guid.NewGuid(),stream:5),LoudnessProfiles.Stereo);
        Assert.Contains("0:5",args);Assert.Contains("-vn",args);Assert.Contains("file,pipe",args);Assert.Contains(args,a=>a.Contains("pan=stereo"));
    }
    [Fact] public void ScheduledJobIsNamedAndDailyReconciliationIsRegistered() {
        var task=new AudioLoudnessTask(Index(new(),new()));Assert.Equal("LowfinCompanionAudioLoudness",task.Key);Assert.Single(task.GetDefaultTriggers());Assert.Equal(TimeSpan.FromHours(24).Ticks,task.GetDefaultTriggers().Single().IntervalTicks);
        Assert.False(new PluginConfiguration().AudioLoudnessEnabled);
    }
    [Fact] public async Task EmptyOrAudioFreeLibraryDoesNotRequireAnExistingStoreDirectory() {
        var index=Index(new(),new());index.Queue(Guid.NewGuid());await index.Update(default);await index.Reconcile(default);
    }
    [Fact] public async Task FailedReconciliationRemainsRequestedForTheNextRun() {
        var source=new Source {QueryFails=true};var index=Index(source,new());
        await Assert.ThrowsAsync<IOException>(()=>index.Reconcile(default));Assert.True(index.FullRequested);
        source.QueryFails=false;await index.Reconcile(default);Assert.False(index.FullRequested);
    }
    [Fact] public async Task MeasurementsUseLessThanOneKilobytePerStereoTrack() {
        Guid id=Guid.NewGuid();var source=new Source { Items=[Track(id)] };await Index(source,new()).Reconcile(default);
        long length=new FileInfo(Path.Combine(root,"audio-loudness",id.ToString("N")+".json")).Length;Assert.InRange(length,300,1024);
    }
    [FfmpegFact] public async Task ActualFfmpegMeasuresEachEmbeddedTrackAndMatchingStereoProfile() {
        string executable=FfmpegFactAttribute.Executable!;string file=Path.Combine(root,"two-tracks.mkv");
        var start=new ProcessStartInfo(executable) { UseShellExecute=false,RedirectStandardError=true };
        foreach(string arg in new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","sine=frequency=440:duration=2:sample_rate=48000","-f","lavfi","-i","sine=frequency=440:duration=2:sample_rate=48000","-filter_complex","[0:a]volume=0.1[a];[1:a]volume=0.8[b]","-map","[a]","-map","[b]","-c:a","pcm_s16le",file}) start.ArgumentList.Add(arg);
        using(var process=Process.Start(start)!) { var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Assert.True(process.ExitCode==0,await error); }
        var encoder=new Mock<IMediaEncoder>();encoder.SetupGet(e=>e.EncoderPath).Returns(executable);var analyzer=new AudioLoudnessAnalyzer(encoder.Object);Guid id=Guid.NewGuid();
        var quiet=new LoudnessTrack(id,"test",0,file,1,"mono","pcm_s16le",48000,TimeSpan.FromSeconds(2).Ticks);var loud=quiet with {StreamIndex=1};
        var a=await analyzer.Measure(quiet,LoudnessProfiles.Native,quiet.Fingerprint()!,null,default);
        var b=await analyzer.Measure(loud,LoudnessProfiles.Native,loud.Fingerprint()!,null,default);
        var stereo=await analyzer.Measure(quiet,LoudnessProfiles.Stereo,quiet.Fingerprint()!,null,default);
        Assert.InRange(b.IntegratedLufs-a.IntegratedLufs,17.8,18.3);Assert.InRange(stereo.IntegratedLufs-a.IntegratedLufs,2.8,3.2);Assert.True(b.TruePeakDb>a.TruePeakDb);
    }
}

public sealed class FfmpegFactAttribute : FactAttribute {
    public static string? Executable=>Environment.GetEnvironmentVariable("LOWFIN_TEST_FFMPEG")??(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Select(path=>Path.Combine(path,"ffmpeg")).FirstOrDefault(File.Exists);
    public FfmpegFactAttribute() { if(Executable==null) Skip="FFmpeg is not installed; pure measurement/cache tests still run."; }
}
