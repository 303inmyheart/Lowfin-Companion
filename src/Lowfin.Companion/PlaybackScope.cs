using MediaBrowser.Controller.Entities;
using Jellyfin.Database.Implementations.Entities;

namespace Lowfin.Companion;

/// <summary>Identifies the actual gateway operation across awaits; never guesses from an active session list.</summary>
internal sealed class PlaybackScope : IDisposable {
    private static readonly AsyncLocal<PlaybackScope?> CurrentSlot=new();
    private readonly PlaybackScope? previous;
    private bool active=true;
    public static PlaybackScope? Current => CurrentSlot.Value is { active:true } scope?scope:null;
    public required User User { get; init; }
    public required BaseItem Item { get; init; }
    public required BaseItem ProgressItem { get; init; }
    public required MediaRecord Record { get; init; }
    public required CompletionRule Rule { get; init; }
    public long Position { get; init; }
    public bool WritingCorrection { get; set; }
    public PlaybackScope() { previous=CurrentSlot.Value; CurrentSlot.Value=this; }
    public bool Matches(Guid user,BaseItem item) => User.Id==user && (Item.Id==item.Id||ProgressItem.Id==item.Id);
    public void Dispose() { active=false; CurrentSlot.Value=previous; }
}
