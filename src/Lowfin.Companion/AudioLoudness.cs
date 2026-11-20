using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lowfin.Companion;

// File stream indices, not the ordinal of an audio track. Profiles are versioned DSP contracts.
public sealed record LoudnessTrack(Guid ItemId,string SourceId,int StreamIndex,string Path,int Channels,string Layout,string Codec,int SampleRate,long RuntimeTicks=0) {
    public string? Fingerprint() {
        try {
            var file=new FileInfo(Path);if(!file.Exists||file.Length==0) return null;
            string facts=$"{Path}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}\n{file.CreationTimeUtc.Ticks}\n{StreamIndex}\n{Channels}\n{Layout}\n{Codec}\n{SampleRate}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)));
        } catch(IOException) { return null; } catch(UnauthorizedAccessException) { return null; }
    }
}
public sealed record LoudnessMeasurement(int StreamIndex,string Profile,string Fingerprint,double IntegratedLufs,double RangeLu,double TruePeakDb,DateTimeOffset MeasuredAt);
public sealed record LoudnessEntry(string SourceId,LoudnessMeasurement Measurement);
public sealed record LoudnessFailure(string SourceId,int StreamIndex,string Profile,string Fingerprint,DateTimeOffset RetryAfter);
public sealed record LoudnessSnapshot(int Version,LoudnessEntry[] Entries,LoudnessFailure[] Failures);
public sealed record LoudnessStoredStatus(
    [property:System.Text.Json.Serialization.JsonPropertyName("ready")] bool Ready,
    [property:System.Text.Json.Serialization.JsonPropertyName("failed")] bool Failed,
    [property:System.Text.Json.Serialization.JsonPropertyName("items")] int Items,
    [property:System.Text.Json.Serialization.JsonPropertyName("profiles")] long Profiles);

public static class LoudnessProfiles {
    public const string Native="native-v1",Stereo="lowfin-stereo-v1";
    public static IEnumerable<string> For(LoudnessTrack track) {
        yield return Native;
        // Unknown channel orders must not be treated as the Android downmix contract.
        if(StereoFilter(track.Channels,track.Layout)!=null) yield return Stereo;
    }
    public static string? StereoFilter(int channels,string layout) {
        string[] layouts=["","mono","stereo","3.0","quad","5.0","5.1"];
        if(channels<1||channels>6||(!string.Equals(layout,layouts[channels],StringComparison.OrdinalIgnoreCase)&&!(channels==6&&layout=="5.1(side)")&&!(channels==5&&layout=="5.0(side)"))) return null;
        double[,] matrix=channels switch {
            1=>new double[,]{{1,1}},2=>new double[,]{{1,0},{0,1}},3=>new double[,]{{1,0},{0,1},{.7071,.7071}},
            4=>new double[,]{{1,0},{0,1},{.7071,0},{0,.7071}},5=>new double[,]{{1,0},{0,1},{.7071,.7071},{.7071,0},{0,.7071}},
            _=>new double[,]{{1,0},{0,1},{.7071,.7071},{.5,.5},{.7071,0},{0,.7071}}
        };
        double headroom=1;for(int output=0;output<2;output++) { double sum=0;for(int i=0;i<channels;i++) sum+=matrix[i,output];headroom=Math.Max(headroom,sum); }
        string outputChannel(int output)=>string.Join('+',Enumerable.Range(0,channels).Where(i=>matrix[i,output]!=0).Select(i=>(matrix[i,output]/headroom).ToString("R",CultureInfo.InvariantCulture)+"*c"+i));
        return "pan=stereo|c0="+outputChannel(0)+"|c1="+outputChannel(1);
    }
    public static LoudnessMeasurement Parse(string output,LoudnessTrack track,string profile,string fingerprint) {
        // ebur128 prints a final summary; verbose frame logs are suppressed by ffmpeg's info level.
        int summary=output.LastIndexOf("Summary:",StringComparison.Ordinal);if(summary<0) throw new InvalidDataException("Missing loudness summary.");
        string tail=output[summary..];
        double value(string pattern) {
            var match=Regex.Match(tail,pattern,RegexOptions.CultureInvariant);
            if(!match.Success||!double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double number)||!double.IsFinite(number)) throw new InvalidDataException("Invalid loudness measurement.");return number;
        }
        double integrated=value(@"\bI:\s*([-+\d.]+)\s*LUFS"),range=value(@"\bLRA:\s*([-+\d.]+)\s*LU"),peak=value(@"\bPeak:\s*([-+\d.]+)\s*dBFS");
        if(integrated<=-70||integrated>10||range<0||range>100||peak>20||peak< -120) throw new InvalidDataException("No usable programme loudness.");
        return new(track.StreamIndex,profile,fingerprint,integrated,range,peak,DateTimeOffset.UtcNow);
    }
}

public sealed class LoudnessStore(string directory) {
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly string root=System.IO.Path.Combine(directory,"audio-loudness");
    private readonly object totalsGate=new();
    private readonly Dictionary<Guid,int> counts=new();
    private Task? inventory;
    private bool totalsReady,totalsFailed;
    private long profiles;
    private int measuredItems;
    private void Count(Guid id,int count) {
        int previous=counts.GetValueOrDefault(id);counts[id]=count;
        profiles+=count-previous;measuredItems+=(count>0?1:0)-(previous>0?1:0);
    }
    public LoudnessStoredStatus StoredStatus() {
        lock(totalsGate) {
            // One lazy background inventory after restart, never a directory scan on every refresh.
            inventory??=Task.Run(Inventory);
            if(inventory.IsFaulted) {totalsFailed=true;_ = inventory.Exception;}
            return new(totalsReady,totalsFailed,measuredItems,profiles);
        }
    }
    private void Inventory() {
        try {
            if(Directory.Exists(root)) foreach(string file in Directory.EnumerateFiles(root,"*.json")) {
                if(!Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(file),"N",out Guid id)) continue;
                lock(totalsGate) {if(counts.ContainsKey(id)) continue;}
                // Slow storage must not hold the totals/write lock while reading a record.
                int count=Read(id).Entries.Length;
                lock(totalsGate) {
                    // A concurrent write/deletion already counted its newer state.
                    if(!counts.ContainsKey(id)) Count(id,count);
                }
            }
            lock(totalsGate) totalsReady=true;
        } catch(Exception error) when(error is IOException or UnauthorizedAccessException) { lock(totalsGate) totalsFailed=true; }
    }
    private string FileName(Guid id)=>System.IO.Path.Combine(root,id.ToString("N")+".json");
    public LoudnessSnapshot Read(Guid id) {
        try {
            using var file=new FileStream(FileName(id),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var data=JsonSerializer.Deserialize<LoudnessSnapshot>(file,Json);
            if(data?.Version==1) {
                // A partial/older record must not fault the background inventory forever.
                // Keep healthy measurements when only optional arrays or individual rows are bad.
                var entries=data.Entries??[];var failures=data.Failures??[];
                if(entries.Any(entry=>entry?.Measurement==null)) entries=entries.Where(entry=>entry?.Measurement!=null).ToArray();
                if(failures.Any(failure=>failure==null)) failures=failures.Where(failure=>failure!=null).ToArray();
                return ReferenceEquals(entries,data.Entries)&&ReferenceEquals(failures,data.Failures)?data:new(1,entries,failures);
            }
        }
        catch(Exception error) when(error is IOException or JsonException or UnauthorizedAccessException) { }
        return new(1,[],[]);
    }
    public void Write(Guid id,LoudnessSnapshot snapshot) {
        lock(totalsGate) {
            Directory.CreateDirectory(root);string target=FileName(id),temp=target+".tmp";
            try { File.WriteAllText(temp,JsonSerializer.Serialize(snapshot,Json));File.Move(temp,target,true);Count(id,snapshot.Entries.Length); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
    public void Remove(Guid id) { lock(totalsGate) {try { File.Delete(FileName(id)); } catch(DirectoryNotFoundException) { }Count(id,0);} }
}
