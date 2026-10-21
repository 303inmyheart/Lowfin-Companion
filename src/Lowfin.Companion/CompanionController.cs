using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Querying;
using Jellyfin.Extensions.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lowfin.Companion;

[ApiController,Authorize,Route("Lowfin/Companion/v1")]
public sealed class CompanionController(CompanionService service,IUserManager users,ILibraryManager library,ISessionManager sessions,HostCompatibility compatibility,CacheFeed cache,IDtoService dto,ProviderGateway providers,BadgeIndex badgeIndex) : ControllerBase {
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private User AuthenticatedUser() {
        // Claim names verified against Jellyfin 12.2's InternalClaimTypes. API keys with no user context cannot select arbitrary users.
        if(!Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"),out var id)||id==Guid.Empty) throw new UnauthorizedAccessException();
        return users.GetUserById(id) ?? throw new UnauthorizedAccessException();
    }
    private BaseItem Item(User user,Guid id) {
        var item=library.GetItemById(id);
        if(item is null || !CompanionService.SupportedItem(item) || !item.IsVisibleStandalone(user)) throw new KeyNotFoundException();
        return item;
    }
    private async Task<IActionResult> Run(Func<User,Task<object>> operation) {
        if(!compatibility.Supported) return StatusCode(503,new { error="Unsupported Jellyfin host version." });
        try { return new JsonResult(await operation(AuthenticatedUser()).ConfigureAwait(false),Json); }
        catch(UnauthorizedAccessException) { return Unauthorized(); }
        catch(KeyNotFoundException) { return NotFound(); }
        catch(ConflictException exception) { return Conflict(new { error=exception.Message }); }
        catch(ArgumentException exception) { return BadRequest(new { error=exception.Message }); }
        catch(System.Security.SecurityException) { return StatusCode(403,new { error="Server providers are not enabled for this user." }); }
        catch(ProviderException) { return StatusCode(502,new { error="Provider unavailable." }); }
        catch(HttpRequestException) { return StatusCode(502,new { error="Provider unavailable." }); }
        catch(TaskCanceledException) { return StatusCode(504,new { error="Provider timed out." }); }
        catch(IOException) { return StatusCode(503,new { error="Companion storage unavailable." }); }
    }
    [HttpGet("capabilities")]
    public IActionResult Capabilities() {
        User authenticated; try { authenticated=AuthenticatedUser(); } catch(UnauthorizedAccessException) { return Unauthorized(); }
        return new JsonResult(new { available=compatibility.Supported,apiVersion=1,pluginVersion="0.5.1",imageBadges=MediaBadges.Settings(Plugin.Instance?.Configuration),jellyfinVersion=compatibility.Version.ToString(),features=compatibility.Supported?new[]{"smartCompletion","persistentResume","longVideoRule","scopedResume","resumeVisibility","explicitOptIn","incrementalCache"}.Concat(providers.Allowed(authenticated)?new[]{"serverProviders"}:Array.Empty<string>()).ToArray():Array.Empty<string>() },Json);
    }
    [HttpGet("badges")]
    public Task<IActionResult> Badges([FromQuery]string ids="")=>Run(user=> {
        string[] values=ids.Split(',',StringSplitOptions.RemoveEmptyEntries);
        if(values.Length==0||values.Length>64) throw new ArgumentException("Expected 1 to 64 item IDs.");
        var requested=new HashSet<Guid>();
        foreach(string value in values) { if(!Guid.TryParse(value,out var id)||id==Guid.Empty) throw new ArgumentException("Invalid item ID."); requested.Add(id); }
        if(Plugin.Instance?.Configuration.ImageBadgesEnabled!=true) return Task.FromResult<object>(new { items=Array.Empty<object>() });
        Response.Headers["Cache-Control"]="no-store";
        var items=new List<object>();bool pending=false;string scope=cache.Scope(user);
        foreach(var id in requested) {
            var item=library.GetItemById(id);
            if(item is not (MediaBrowser.Controller.Entities.Video or Series or Season) || !item.IsVisibleStandalone(user)) continue;
            var mask=badgeIndex.Get(id,user.Id,scope,item is Series or Season);if(mask.HasValue) items.Add(new { id=id.ToString("N"),mask=mask.Value });else pending=true;
        }
        return Task.FromResult<object>(new { items=items.ToArray(),pending });
    });
    [HttpGet("badges/status"),Authorize(Policy="RequiresElevation")]
    public IActionResult BadgeStatus()=>Ok(badgeIndex.Status());
    [HttpGet("providers")]
    public Task<IActionResult> ProviderStatus()=>Run(user=> { if(!providers.Allowed(user)) throw new System.Security.SecurityException(); return Task.FromResult(providers.Status()); });
    [HttpGet("items/{itemId:guid}/ratings")]
    public Task<IActionResult> Ratings(Guid itemId,[FromQuery]string sources="omdb,mdblist,tmdb")=>Run(async user=> {
        var item=library.GetItemById(itemId); if(item==null||item is not (MediaBrowser.Controller.Entities.Movies.Movie or Series or Episode)||!item.IsVisibleStandalone(user)) throw new KeyNotFoundException();
        if(!providers.Allowed(user)) throw new System.Security.SecurityException();
        Response.Headers["Cache-Control"]="no-store";
        Series? series=null;if(item is Episode episode) { var parent=library.GetItemById(episode.SeriesId) as Series;if(parent!=null&&parent.IsVisibleStandalone(user)) series=parent; }
        return (object)await providers.Ratings(item,sources,HttpContext.RequestAborted,series);
    });
    [HttpGet("items/{itemId:guid}/statistics")]
    public Task<IActionResult> Statistics(Guid itemId)=>Run(async user=> {
        var item=library.GetItemById(itemId); if(item==null||item is not (MediaBrowser.Controller.Entities.Movies.Movie or Series or Episode)||!item.IsVisibleStandalone(user)) throw new KeyNotFoundException();
        if(!providers.Allowed(user)) throw new System.Security.SecurityException();
        return (object)await providers.Summary(user,item,library,HttpContext.RequestAborted);
    });
    [HttpGet("providers/tracearr/servers")]
    public Task<IActionResult> TracearrServers()=>Run(async user=> { if(!user.HasPermission(PermissionKind.IsAdministrator)) throw new UnauthorizedAccessException(); return (object)await providers.Servers(HttpContext.RequestAborted); });
    [HttpGet("cache/changes")]
    public Task<IActionResult> Cache([FromQuery]string? cursor=null,[FromQuery]int waitSeconds=0,[FromQuery]int limit=8) => Run(async user=> {
        if(limit<1||limit>20||waitSeconds<0||waitSeconds>20) throw new ArgumentException("Invalid cache request limits.");
        var previous=CacheFeed.Parse(cursor); var snapshot=cache.Journal.Snapshot(); string scope=cache.Scope(user);
        if(!string.IsNullOrEmpty(cursor)&&(previous==null||previous.Scope!=scope||CacheJournal.Expired(snapshot,previous.Epoch,previous.After)))
            return new { reset=true,cursor=CacheFeed.Cursor(snapshot,snapshot.Head,scope),changes=Array.Empty<object>(),more=false };
        if(previous==null) return new { reset=false,cursor=CacheFeed.Cursor(snapshot,snapshot.Head,scope),changes=Array.Empty<object>(),more=false };
        await cache.Journal.Wait(previous.After,waitSeconds,HttpContext.RequestAborted).ConfigureAwait(false);
        user=users.GetUserById(user.Id)??throw new UnauthorizedAccessException(); scope=cache.Scope(user); snapshot=cache.Journal.Snapshot();
        if(previous.Scope!=scope||CacheJournal.Expired(snapshot,previous.Epoch,previous.After))
            return new { reset=true,cursor=CacheFeed.Cursor(snapshot,snapshot.Head,scope),changes=Array.Empty<object>(),more=false };
        var page=snapshot.Changes.Where(x=>x.Sequence>previous.After).Take(limit).ToArray();
        if(page.Any(x=>x.Reset)) return new { reset=true,cursor=CacheFeed.Cursor(snapshot,snapshot.Head,scope),changes=Array.Empty<object>(),more=false };
        var options=new DtoOptions(false) { Fields=[ItemFields.Overview,ItemFields.Genres,ItemFields.ProviderIds,ItemFields.ParentId,ItemFields.DateCreated,ItemFields.MediaSources,ItemFields.MediaStreams,ItemFields.PrimaryImageAspectRatio,ItemFields.SortName,ItemFields.RecursiveItemCount,ItemFields.Chapters] };
        var roots=library.GetUserRootFolder().GetChildren(user,true).OfType<Folder>().Select(x=>x.Id).ToHashSet();
        var changes=new List<object>();
        foreach(var entry in page.GroupBy(x=>x.ItemId).Select(group=>group.Last())) {
            var item=library.GetItemById(entry.ItemId);
            // Deletion/move/access changes cannot safely be inferred from a missing DTO: reconcile rather than leak hidden metadata.
            if(item==null||!item.IsVisibleStandalone(user)) return new { reset=true,cursor=CacheFeed.Cursor(snapshot,snapshot.Head,scope),changes=Array.Empty<object>(),more=false };
            if(!CacheFeed.Supported(item)) continue;
            var members=item.GetAncestorIds().Where(roots.Contains).Distinct().Select(x=>x.ToString("N")).ToArray();
            changes.Add(new { item=JsonSerializer.SerializeToElement(dto.GetBaseItemDto(item,options,user),JsonDefaults.PascalCaseOptions),libraries=members });
        }
        long after=page.Length==0?previous.After:page[^1].Sequence;
        return new { reset=false,cursor=CacheFeed.Cursor(snapshot,after,scope),changes=changes.ToArray(),more=after<snapshot.Head };
    });
    [HttpGet("resume")]
    public Task<IActionResult> Resume([FromQuery]int limit=50,[FromQuery]string? knownItems=null,[FromQuery]Guid? parentId=null) => Run(user=> {
        string[] values=string.IsNullOrWhiteSpace(knownItems)?[]:knownItems.Split(',');
        if(values.Length>100) throw new ArgumentException("Too many cached items.");
        var ids=new List<Guid>(); foreach(string value in values) { if(!Guid.TryParse(value,out var id)||id==Guid.Empty) throw new ArgumentException("Invalid cached item."); ids.Add(id); }
        if(parentId==Guid.Empty) throw new ArgumentException("Invalid library scope.");
        return service.Resume(user,library,limit,ids,parentId);
    });
    [HttpGet("items/{itemId:guid}")]
    public Task<IActionResult> Get(Guid itemId) => Run(async user=>(object)await service.Get(user,Item(user,itemId)).ConfigureAwait(false));
    [HttpPut("items/{itemId:guid}/rule")]
    public Task<IActionResult> Rule(Guid itemId,[FromBody]UpdateRuleRequest request) => Run(async user=>(object)await service.UpdateRule(user,Item(user,itemId),request).ConfigureAwait(false));
    [HttpPost("items/{itemId:guid}/actions")]
    public Task<IActionResult> Action(Guid itemId,[FromBody]ActionRequest request) => Run(async user=>(object)await service.Action(user,Item(user,itemId),request).ConfigureAwait(false));
    [HttpGet("long-videos")]
    public Task<IActionResult> Global() => Run(async user=>(object)await service.GetGlobal(user).ConfigureAwait(false));
    [HttpPut("long-videos")]
    public Task<IActionResult> GlobalUpdate([FromBody]GlobalRuleRequest request) => Run(async user=>(object)await service.UpdateGlobal(user,request.ExpectedRevision,request.Rule).ConfigureAwait(false));
    [HttpPost("items/{itemId:guid}/playback")]
    public Task<IActionResult> Playback(Guid itemId,[FromBody]PlaybackRequest request) => Run(async user=> {
        string? device=User.FindFirstValue("Jellyfin-DeviceId"),client=User.FindFirstValue("Jellyfin-Client");
        if(client!="Lowfin"||string.IsNullOrWhiteSpace(device)) throw new UnauthorizedAccessException();
        var item=Item(user,itemId);
        var session=await sessions.LogSessionActivity(client,User.FindFirstValue("Jellyfin-Version")??"",device,User.FindFirstValue("Jellyfin-Device")??"Android TV",HttpContext.Connection.RemoteIpAddress?.ToString()??"",user).ConfigureAwait(false);
        return (object)await service.Report(user,item,session,request).ConfigureAwait(false);
    });
}
public sealed record GlobalRuleRequest(long ExpectedRevision,LongVideoRule Rule);
