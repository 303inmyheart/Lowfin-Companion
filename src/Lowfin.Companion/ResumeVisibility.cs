using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using MediaBrowser.Controller.Entities;

namespace Lowfin.Companion;

/// <summary>Mirror Jellyfin's resume-library exclusions without changing stored playback positions.</summary>
public static class ResumeVisibility {
    public static bool Included(IEnumerable<Guid> ancestors,IEnumerable<Guid> allowedLibraries) {
        var allowed=allowedLibraries.ToHashSet(); return ancestors.Any(allowed.Contains);
    }
    public static bool InHome(User user,BaseItem item) {
        var excluded=user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        if(excluded.Length==0) return true;
        var allowed=BaseItem.LibraryManager.GetUserRootFolder().GetChildren(user,true)
            .Where(folder=>folder is Folder&&!excluded.Contains(folder.Id)).Select(folder=>folder.Id);
        return Included(item.GetAncestorIds(),allowed);
    }
}
