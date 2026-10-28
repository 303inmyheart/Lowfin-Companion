using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
namespace Lowfin.Companion.Discord;
public interface IDiscordGateway : IAsyncDisposable {void Update(JsonObject presence);}
public interface IDiscordGatewayFactory {IDiscordGateway Create(string token);}
public sealed class DiscordGatewayFactory(ILoggerFactory logs,IHttpClientFactory clients) : IDiscordGatewayFactory {
    public IDiscordGateway Create(string token)=>new DiscordGateway(token,logs.CreateLogger<DiscordGateway>(),clients);
}
/// <summary>One reconnecting gateway per configured user, isolated from playback event threads.</summary>
public sealed class DiscordGateway(string token,ILogger<DiscordGateway> log,IHttpClientFactory? clients=null,Uri? endpoint=null) : IDiscordGateway {
    // User-account READY includes guild/channel/read-state data, unlike a small bot READY.
    private const int MaxMessageBytes=32*1024*1024;
    public static Uri DefaultEndpoint {get;}=new("wss://gateway.discord.gg/");
    public const string UserAgent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
    private sealed record Frame(int Op,string? Type,long? Sequence,int HeartbeatInterval);
    private sealed class Closed(int? code) : IOException {public int? Code {get;}=code;}
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim changed=new(0,1),sendGate=new(1,1);
    private readonly object gate=new();
    private Task? runner;private JsonObject? latest;private ClientWebSocket? socket;private long sequence=-1;
    private volatile bool ready,ack=true;private int disposed;
    public void Update(JsonObject presence) {
        lock(gate) {if(Volatile.Read(ref disposed)!=0)return;latest=(JsonObject)presence.DeepClone();runner??=Task.Run(Run);}
        Pulse();
    }
    private void Pulse() {try {changed.Release();}catch(SemaphoreFullException){}catch(ObjectDisposedException){}}
    private async Task Send(int op,JsonNode? data,CancellationToken cancellation) {
        await sendGate.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            var ws=socket;if(ws?.State!=WebSocketState.Open)return;
            var bytes=Encoding.UTF8.GetBytes(new JsonObject {["op"]=op,["d"]=data}.ToJsonString());
            await ws.SendAsync(bytes,WebSocketMessageType.Text,true,cancellation).ConfigureAwait(false);
            if(op==3)log.LogDebug("Discord RPC presence sent.");
        }finally {sendGate.Release();}
    }
    private static async Task<Frame> Read(ClientWebSocket ws,CancellationToken cancellation) {
        var buffer=new byte[16384];using var stream=new MemoryStream();WebSocketReceiveResult message;
        do {
            message=await ws.ReceiveAsync(buffer,cancellation).ConfigureAwait(false);
            if(message.MessageType==WebSocketMessageType.Close)throw new Closed(message.CloseStatus is {} code?(int)code:null);
            if(message.MessageType!=WebSocketMessageType.Text)throw new IOException("Unexpected binary gateway message");
            if(stream.Length+message.Count>MaxMessageBytes)throw new IOException("Gateway message exceeds 32 MiB limit");
            stream.Write(buffer,0,message.Count);
        }while(!message.EndOfMessage);
        // Read only envelope fields. Do not materialize an entire user READY as JsonNodes.
        using var document=JsonDocument.Parse(stream.GetBuffer().AsMemory(0,(int)stream.Length));var root=document.RootElement;
        long? sequence=root.TryGetProperty("s",out var s)&&s.TryGetInt64Value(out var number)?number:null;
        string? type=root.TryGetProperty("t",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
        int interval=45000;
        if(root.TryGetProperty("d",out var d)&&d.ValueKind==JsonValueKind.Object&&d.TryGetProperty("heartbeat_interval",out var h)&&h.TryGetInt32(out var value))interval=Math.Clamp(value,1000,120000);
        return new(root.GetProperty("op").GetInt32(),type,sequence,interval);
    }
    private async Task<Uri> Endpoint(CancellationToken cancellation) {
        if(endpoint is not null)return endpoint; // Loopback endpoint used by transport integration tests.
        if(clients is not null)try {
            using var http=clients.CreateClient("Default");http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",UserAgent);http.MaxResponseContentBufferSize=65536;
            using var response=await http.GetAsync("https://discord.com/api/gateway",cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            if(json.RootElement.TryGetProperty("url",out var property)&&Uri.TryCreate(property.GetString(),UriKind.Absolute,out var uri)&&uri.Scheme=="wss"&&(uri.Host=="discord.gg"||uri.Host.EndsWith(".discord.gg",StringComparison.OrdinalIgnoreCase)))return uri;
        }catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception error){log.LogDebug("Discord RPC gateway discovery unavailable ({Kind}); using the default endpoint.",error.GetType().Name);}
        // The supplied user-token integration uses the unversioned gateway, not bot API v9.
        return DefaultEndpoint;
    }
    private async Task Run() {
        int retries=0;
        while(!lifetime.IsCancellationRequested) {
            using var session=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("User-Agent",UserAgent);socket=ws;ready=false;ack=true;Interlocked.Exchange(ref sequence,-1);
            bool reachedReady=false;
            try {
                using(var connect=CancellationTokenSource.CreateLinkedTokenSource(session.Token)) {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));var target=await Endpoint(connect.Token).ConfigureAwait(false);
                    await ws.ConnectAsync(target,connect.Token).ConfigureAwait(false);
                }
                log.LogDebug("Discord RPC gateway connected; waiting for HELLO.");
                Frame hello;
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(session.Token)) {timeout.CancelAfter(TimeSpan.FromSeconds(15));hello=await Read(ws,timeout.Token).ConfigureAwait(false);}
                if(hello.Op!=10)throw new IOException("Missing gateway HELLO");
                await Send(2,new JsonObject {["token"]=token,["intents"]=0,["properties"]=new JsonObject {["os"]="Windows 10",["browser"]="Discord Client",["device"]="Discord Client"}},session.Token).ConfigureAwait(false);
                var receive=Receive(ws,()=>reachedReady=true,session.Token);var heartbeat=Heartbeat(hello.HeartbeatInterval,session.Token);var publish=Publish(session.Token);
                try {await await Task.WhenAny(receive,heartbeat,publish).ConfigureAwait(false);}
                finally {
                    session.Cancel();ws.Abort();
                    try {await Task.WhenAll(receive,heartbeat,publish).ConfigureAwait(false);}catch(OperationCanceledException){}catch(WebSocketException){}catch(IOException){}
                }
            }catch(OperationCanceledException) when(lifetime.IsCancellationRequested){break;}
            catch(Closed close) {
                if(close.Code is 4004 or 4010 or 4011 or 4012 or 4013 or 4014) {
                    log.LogError("Discord RPC authentication/identify rejected (close code {Code}). Check the user token and application configuration; no reconnect loop will be started.",close.Code);break;
                }
                if(close.Code==4008)retries=Math.Max(retries,5);
                log.LogWarning("Discord RPC gateway closed (code {Code}); reconnecting with backoff.",close.Code);
            }catch(IOException error) {
                log.LogWarning("Discord RPC connection interrupted ({Reason}); reconnecting with backoff.",error.Message);
            }catch(Exception error) {
                var status=(error as HttpRequestException??error.InnerException as HttpRequestException)?.StatusCode;
                log.LogWarning("Discord RPC connection interrupted ({Kind}, HTTP {Status}); reconnecting with backoff.",error.GetType().Name,status is null?null:(int?)status);
            }finally {ready=false;socket=null;}
            if(reachedReady)retries=0;
            try {await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,2*Math.Pow(2,Math.Min(retries++,5)))),lifetime.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    private async Task Receive(ClientWebSocket ws,Action onReady,CancellationToken cancellation) {
        while(!cancellation.IsCancellationRequested) {
            var frame=await Read(ws,cancellation).ConfigureAwait(false);
            if(frame.Sequence is {} number)Interlocked.Exchange(ref sequence,number);
            switch(frame.Op) {
                case 0:if(frame.Type=="READY") {ready=true;onReady();log.LogInformation("Discord RPC session ready.");Pulse();}break;
                case 1:await Send(1,CurrentSequence(),cancellation).ConfigureAwait(false);break;
                case 7:throw new IOException("Gateway requested reconnect");
                case 9:log.LogError("Discord RPC session rejected. Check whether the user token has expired.");lifetime.Cancel();return;
                case 11:ack=true;break;
            }
        }
    }
    private JsonNode? CurrentSequence() {long value=Interlocked.Read(ref sequence);return value<0?null:JsonValue.Create(value);}
    private async Task Heartbeat(int interval,CancellationToken cancellation) {
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble()*interval),cancellation).ConfigureAwait(false);
        while(!cancellation.IsCancellationRequested) {
            if(!ack)throw new IOException("Gateway heartbeat timed out");ack=false;
            await Send(1,CurrentSequence(),cancellation).ConfigureAwait(false);
            await Task.Delay(interval,cancellation).ConfigureAwait(false);
        }
    }
    private async Task Publish(CancellationToken cancellation) {
        while(!cancellation.IsCancellationRequested) {
            await changed.WaitAsync(cancellation).ConfigureAwait(false);if(!ready)continue;
            JsonObject? value;lock(gate)value=(JsonObject?)latest?.DeepClone();
            if(value is not null)await Send(3,value,cancellation).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync() {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        if(ready) {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try {await Send(3,new JsonObject {["activities"]=new JsonArray(),["status"]="invisible",["afk"]=false,["since"]=0},timeout.Token).ConfigureAwait(false);}catch(Exception){}
        }
        lifetime.Cancel();socket?.Abort();Task? task;lock(gate){latest=null;task=runner;}
        if(task is not null)try {await task.ConfigureAwait(false);}catch(OperationCanceledException){}
        lifetime.Dispose();changed.Dispose();sendGate.Dispose();
    }
}
internal static class GatewayJsonExtensions {
    public static bool TryGetInt64Value(this JsonElement element,out long value) {value=0;return element.ValueKind==JsonValueKind.Number&&element.TryGetInt64(out value);}
}
