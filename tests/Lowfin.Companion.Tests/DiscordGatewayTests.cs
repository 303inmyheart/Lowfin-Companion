using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Lowfin.Companion.Discord;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Lowfin.Companion.Tests;
/// <summary>Exercise the actual ClientWebSocket transport against loopback; no Discord account/network.</summary>
public sealed class DiscordGatewayTests {
    private sealed class TestLog : ILogger<DiscordGateway> {
        public readonly ConcurrentQueue<string> Lines=new();
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Lines.Enqueue(formatter(state,exception));
    }
    private static async Task Send(WebSocket socket,JsonObject data,CancellationToken cancellation) {
        var bytes=Encoding.UTF8.GetBytes(data.ToJsonString());
        for(int n=0;n<bytes.Length;n+=65536)await socket.SendAsync(bytes.AsMemory(n,Math.Min(65536,bytes.Length-n)),WebSocketMessageType.Text,n+65536>=bytes.Length,cancellation);
    }
    private static async Task<JsonObject> Read(WebSocket socket,CancellationToken cancellation) {
        using var stream=new MemoryStream();var buffer=new byte[8192];WebSocketReceiveResult result;
        do {result=await socket.ReceiveAsync(buffer,cancellation);Assert.Equal(WebSocketMessageType.Text,result.MessageType);stream.Write(buffer,0,result.Count);}while(!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
    private static WebApplication Server() {
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");app.UseWebSockets();return app;
    }
    [Fact] public async Task LargeFragmentedReadyPublishesQueuedPresenceAndStopClearsIt() {
        Assert.Empty(DiscordGateway.DefaultEndpoint.Query);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var cancellation=deadline.Token;
        var received=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);var cleared=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app=Server();
        app.Run(async context=>{
            try {
                Assert.Contains("Mozilla/5.0",context.Request.Headers.UserAgent.ToString());Assert.Empty(context.Request.Query);
                using var ws=await context.WebSockets.AcceptWebSocketAsync();
                await Send(ws,new(){["op"]=10,["d"]=new JsonObject{["heartbeat_interval"]=45000}},cancellation);
                var identify=await Read(ws,cancellation);Assert.Equal(2,identify["op"]!.GetValue<int>());Assert.Equal("fake-test-token",identify["d"]!["token"]!.GetValue<string>());
                Assert.Equal("Discord Client",identify["d"]!["properties"]!["browser"]!.GetValue<string>());
                // A realistic user READY exceeds the old 1 MiB cap and arrives fragmented.
                await Send(ws,new(){["op"]=0,["t"]="READY",["s"]=41,["d"]=new JsonObject{["padding"]=new string('x',2*1024*1024)}},cancellation);
                bool presence=false;
                while(!cancellation.IsCancellationRequested) {
                    var message=await Read(ws,cancellation);int op=message["op"]!.GetValue<int>();
                    if(op==1){await Send(ws,new(){["op"]=11,["d"]=null},cancellation);continue;}
                    Assert.Equal(3,op);var data=message["d"]!.AsObject();
                    if(!presence){presence=true;received.TrySetResult(data);}
                    else if(data["activities"]!.AsArray().Count==0){Assert.Equal("invisible",data["status"]!.GetValue<string>());cleared.TrySetResult();return;}
                }
            }catch(Exception error){received.TrySetException(error);cleared.TrySetException(error);}
        });
        await app.StartAsync(cancellation);
        var log=new TestLog();await using var gateway=new DiscordGateway("fake-test-token",log,endpoint:new Uri(app.Urls.Single().Replace("http://","ws://")));
        try {
            var activity=DiscordActivity.Build(new Movie{Name="Example movie",RunTimeTicks=TimeSpan.FromHours(2).Ticks},0,false,new(){ApplicationId="123"},DateTimeOffset.UtcNow);
            gateway.Update(new(){["activities"]=new JsonArray(activity),["status"]="dnd",["since"]=0,["afk"]=false});
            var data=await received.Task.WaitAsync(cancellation);Assert.Equal(0,data["since"]!.GetValue<int>());Assert.Equal("Example movie",data["activities"]![0]!["details"]!.GetValue<string>());
            var assets=data["activities"]![0]!["assets"]!.AsObject();Assert.False(assets.ContainsKey("large_image"));Assert.False(assets.ContainsKey("small_image"));
            await gateway.DisposeAsync();await cleared.Task.WaitAsync(cancellation);
            Assert.Contains(log.Lines,x=>x.Contains("session ready"));Assert.DoesNotContain(log.Lines,x=>x.Contains("fake-test-token"));
        }finally{deadline.Cancel();await app.StopAsync();}
    }
    [Fact] public async Task AuthenticationCloseCodeIsVisibleWithoutLeakingTokenOrRetrying() {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));var cancellation=deadline.Token;int connections=0;
        await using var app=Server();
        app.Run(async context=>{
            Interlocked.Increment(ref connections);using var ws=await context.WebSockets.AcceptWebSocketAsync();
            await Send(ws,new(){["op"]=10,["d"]=new JsonObject{["heartbeat_interval"]=45000}},cancellation);
            await Read(ws,cancellation);await ws.CloseOutputAsync((WebSocketCloseStatus)4004,"peer-text-must-not-be-logged",cancellation);
        });
        await app.StartAsync(cancellation);var log=new TestLog();await using var gateway=new DiscordGateway("fake-test-token",log,endpoint:new Uri(app.Urls.Single().Replace("http://","ws://")));
        try {
            gateway.Update(new(){["activities"]=new JsonArray(),["status"]="dnd",["since"]=0,["afk"]=false});
            for(int i=0;i<100&&!log.Lines.Any(x=>x.Contains("4004"));i++)await Task.Delay(20,cancellation);
            Assert.Contains(log.Lines,x=>x.Contains("4004"));await Task.Delay(2200,cancellation);Assert.Equal(1,connections);
            Assert.DoesNotContain(log.Lines,x=>x.Contains("fake-test-token")||x.Contains("peer-text-must-not-be-logged"));
        }finally{deadline.Cancel();await app.StopAsync();}
    }
}
