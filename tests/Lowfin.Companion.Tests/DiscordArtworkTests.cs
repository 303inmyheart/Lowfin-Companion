using System.Net;
using System.Text.Json.Nodes;
using Lowfin.Companion.Discord;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit;
namespace Lowfin.Companion.Tests;
public sealed class DiscordArtworkTests : IDisposable {
    private readonly string folder=Path.Combine(Path.GetTempPath(),"lowfin-original-artwork-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
    private sealed class Handler : HttpMessageHandler {
        public int Uploads,Registrations,SourceReads;public byte[]? Uploaded,Source;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) {
            if(request.RequestUri!.Host=="covers.example.invalid") {
                SourceReads++;Assert.False(request.Headers.Contains("Authorization"));return new(HttpStatusCode.OK){Content=new ByteArrayContent(Source!)};
            }
            if(request.RequestUri!.Host=="uguu.se") {
                Uploads++;Assert.False(request.Headers.Contains("Authorization"));
                var content=Assert.IsType<MultipartFormDataContent>(request.Content);var file=Assert.Single(content);Assert.Equal("image/png",file.Headers.ContentType!.MediaType);Uploaded=await file.ReadAsByteArrayAsync(cancellation);
                return new(HttpStatusCode.OK){Content=new StringContent("{\"success\":true,\"files\":[{\"url\":\"https://public.example.invalid/cover.png\"}]}")};
            }
            Assert.Equal("discord.com",request.RequestUri.Host);Assert.Equal("fake-discord-token",Assert.Single(request.Headers.GetValues("Authorization")));Registrations++;
            return new(HttpStatusCode.OK){Content=new StringContent("[{\"external_asset_path\":\"external/original-cover\"}]")};
        }
    }
    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(false,true)]
    public async Task OriginalPipelineRendersCorrectMimeUsesSeasonOrSeriesAndPersistsUploads(bool seasonHasCover,bool remoteSource) {
        Directory.CreateDirectory(folder);string imagePath=Path.Combine(folder,"poster.png");
        using(var bitmap=new SKBitmap(64,96)){using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.Red);using var image=SKImage.FromBitmap(bitmap);using var png=image.Encode(SKEncodedImageFormat.Png,90);File.WriteAllBytes(imagePath,png.ToArray());}
        byte[] sourceBytes=File.ReadAllBytes(imagePath);
        var info=new ItemImageInfo {Path=remoteSource?"https://covers.example.invalid/poster.png":imagePath,Type=ImageType.Primary,DateModified=DateTime.UtcNow};
        var series=new Series {Id=Guid.NewGuid(),Name="Series",ImageInfos=[info]};var season=new Season {Id=Guid.NewGuid(),Name="Season",SeriesId=series.Id,ImageInfos=seasonHasCover?[info]:[]};
        var first=new Episode {Id=Guid.NewGuid(),Name="Episode 1",SeasonId=season.Id,SeriesId=series.Id};var next=new Episode {Id=Guid.NewGuid(),Name="Episode 2",SeasonId=season.Id,SeriesId=series.Id};
        var items=new Dictionary<Guid,BaseItem>{{series.Id,series},{season.Id,season},{first.Id,first},{next.Id,next}};
        var library=new Mock<ILibraryManager>();library.Setup(x=>x.GetItemById(It.IsAny<Guid>())).Returns<Guid>(id=>items.GetValueOrDefault(id));
        var paths=new Mock<IApplicationPaths>();paths.SetupGet(x=>x.CachePath).Returns(folder);
        var handler=new Handler {Source=sourceBytes};var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        var cache=new ArtworkUploadCache(paths.Object,NullLogger<ArtworkUploadCache>.Instance);var artwork=new DiscordArtwork(library.Object,clients.Object,cache);
        var options=new DiscordOptions {ApplicationId="123",UploadArtwork=true};
        Assert.Equal("mp:external/original-cover",await artwork.Resolve(first,options,"fake-discord-token",default));
        Assert.Equal("mp:external/original-cover",await artwork.Resolve(next,options,"fake-discord-token",default));Assert.Equal(1,handler.Uploads);Assert.Equal(1,handler.Registrations);Assert.Equal(remoteSource?1:0,handler.SourceReads);
        using var decoded=SKBitmap.Decode(handler.Uploaded!);Assert.Equal(512,decoded.Width);Assert.Equal(512,decoded.Height);
        var stored=JsonNode.Parse(File.ReadAllText(cache.FilePath))!.AsObject();string source=(seasonHasCover?season.Id:series.Id).ToString("N");Assert.Contains(stored.Select(x=>x.Key),key=>key.StartsWith(source+"|"));
        var reopened=new DiscordArtwork(library.Object,clients.Object,new ArtworkUploadCache(paths.Object,NullLogger<ArtworkUploadCache>.Instance));
        Assert.Equal("mp:external/original-cover",await reopened.Resolve(first,options,"fake-discord-token",default));Assert.Equal(1,handler.Uploads);Assert.Equal(2,handler.Registrations);
    }
}
