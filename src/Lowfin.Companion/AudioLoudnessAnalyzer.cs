using System.Diagnostics;
using System.Text;
using MediaBrowser.Controller.MediaEncoding;

namespace Lowfin.Companion;

public interface ILoudnessAnalyzer {
    Task<LoudnessMeasurement> Measure(LoudnessTrack track,string profile,string fingerprint,IProgress<double>? progress,CancellationToken cancellation);
}
public sealed class AudioLoudnessAnalyzer(IMediaEncoder encoder) : ILoudnessAnalyzer {
    public static string[] Arguments(LoudnessTrack track,string profile) {
        string? downmix=profile==LoudnessProfiles.Stereo?LoudnessProfiles.StereoFilter(track.Channels,track.Layout):null;
        if(profile!=LoudnessProfiles.Native&&(profile!=LoudnessProfiles.Stereo||downmix==null)) throw new ArgumentException("Unknown loudness profile.");
        return ["-hide_banner","-nostdin","-nostats","-progress","pipe:1","-loglevel","info","-threads","1","-filter_threads","1","-protocol_whitelist","file,pipe","-i",track.Path,"-map","0:"+track.StreamIndex,"-vn","-sn","-dn","-af",(downmix==null?"":downmix+",")+"ebur128=peak=true:framelog=verbose","-f","null","-"];
    }
    public async Task<LoudnessMeasurement> Measure(LoudnessTrack track,string profile,string fingerprint,IProgress<double>? progress,CancellationToken cancellation) {
        if(string.IsNullOrWhiteSpace(encoder.EncoderPath)) throw new InvalidOperationException("Jellyfin FFmpeg unavailable.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromHours(24));
        var start=new ProcessStartInfo(encoder.EncoderPath) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        foreach(string arg in Arguments(track,profile)) start.ArgumentList.Add(arg);
        using var process=new Process { StartInfo=start };process.Start();
        // Keep only a bounded tail. Never log stderr: FFmpeg includes private media paths.
        async Task<string> Tail(StreamReader reader) {
            var result=new StringBuilder();var chars=new char[4096];int count;
            while((count=await reader.ReadAsync(chars.AsMemory(),timeout.Token))>0) { result.Append(chars,0,count);if(result.Length>65536) result.Remove(0,result.Length-32768); }
            return result.ToString();
        }
        async Task ReadProgress() {
            string? line;while((line=await process.StandardOutput.ReadLineAsync(timeout.Token))!=null) {
                if(track.RuntimeTicks>0&&line.StartsWith("out_time_us=",StringComparison.Ordinal)&&long.TryParse(line.AsSpan(12),out long time)) progress?.Report(Math.Clamp(time*10d/track.RuntimeTicks,0,.999));
            }
        }
        var stderr=Tail(process.StandardError);var stdout=ReadProgress();
        try {
            await process.WaitForExitAsync(timeout.Token);string output=await stderr;await stdout;
            if(process.ExitCode!=0) throw new InvalidDataException("Audio analysis failed.");
            return LoudnessProfiles.Parse(output,track,profile,fingerprint);
        } finally {
            if(!process.HasExited) { try { process.Kill(true); } catch(InvalidOperationException) { } }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stderr,stdout); } catch(OperationCanceledException) { }
        }
    }
}
