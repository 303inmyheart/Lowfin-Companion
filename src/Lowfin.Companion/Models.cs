using System.Text.Json.Serialization;
using MediaBrowser.Model.Session;

namespace Lowfin.Companion;

[JsonConverter(typeof(JsonStringEnumConverter<CompletionMode>))]
public enum CompletionMode { Default, CustomPercentage, RemainingTime, NeverAutoComplete }
public sealed record CompletionRule {
    public CompletionMode Mode { get; init; } = CompletionMode.Default;
    public decimal Percentage { get; init; } = 99.5m;
    public int RemainingSeconds { get; init; } = 120;
    public bool PersistentResume { get; init; } = true;
    public void Validate() {
        if (!Enum.IsDefined(Mode) || Percentage <= 0 || Percentage > 100 || RemainingSeconds < 0 || RemainingSeconds > 604800) throw new ArgumentException("Invalid completion rule.");
    }
    public bool Completes(long position, long duration) {
        if (position <= 0 || duration <= 0) return false;
        return Mode switch {
            CompletionMode.CustomPercentage => (decimal)Math.Min(position,duration) * 100m >= (decimal)duration * Percentage,
            CompletionMode.RemainingTime => Math.Max(0,duration-position) <= (long)RemainingSeconds * TimeSpan.TicksPerSecond,
            _ => false
        };
    }
}
public sealed record LongVideoRule {
    public bool Enabled { get; init; }
    public int MinimumSeconds { get; init; } = 14400;
    public CompletionRule Rule { get; init; } = new() { Mode=CompletionMode.RemainingTime };
    public void Validate() { Rule.Validate(); if (MinimumSeconds < 60 || MinimumSeconds > 2592000 || Rule.Mode==CompletionMode.Default) throw new ArgumentException("Invalid long-video rule."); }
}
public sealed class PlaybackLease {
    public Guid Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string PlaySessionId { get; set; } = "";
    public long Sequence { get; set; }
    public bool Stopped { get; set; }
    public bool DeliveryPending { get; set; }
}
public sealed class MediaRecord {
    public int FormatVersion { get; set; } = 1;
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public long Revision { get; set; }
    public CompletionRule? Rule { get; set; }
    public LongVideoRule? LongVideo { get; set; }
    public long ResumeTicks { get; set; }
    public long RuntimeTicks { get; set; }
    public bool ResumeOwned { get; set; }
    public bool Played { get; set; }
    public bool ManualFence { get; set; }
    public PlaybackLease? Lease { get; set; }
    public Guid NativeItemId { get; set; }
    public long NativePositionTicks { get; set; }
    public bool NativePlayed { get; set; }
    public Guid ManualBoot { get; set; }
    public long ManualOrdinal { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed record ItemState(long Revision, CompletionRule? Rule, CompletionRule EffectiveRule, bool UsesLongVideoRule, long ResumeTicks, long RuntimeTicks, bool HasResume, bool Played, DateTimeOffset UpdatedAt,Guid? LeaseId,long Sequence,bool ManualFence,bool Active,bool ResumeVisible);
public sealed record UpdateRuleRequest(long ExpectedRevision, CompletionRule? Rule);
public sealed record ActionRequest(long ExpectedRevision, string Action);
public sealed class PlaybackRequest {
    public Guid LeaseId { get; set; }
    public long ExpectedRevision { get; set; }
    public long Sequence { get; set; }
    public string Event { get; set; } = "Progress";
    public PlaybackStartInfo Playback { get; set; } = new();
    public bool Failed { get; set; }
}
public sealed record PlaybackAck(bool Accepted, bool Duplicate, string? Reason, ItemState State);
public sealed class ConflictException(string message) : Exception(message);

public sealed class ActionOrder {
    public Guid Boot { get; } = Guid.NewGuid();
    private long counter;
    public long Next() => Interlocked.Increment(ref counter);
    public bool Newer(MediaRecord record,long order) => record.ManualBoot!=Boot || order>record.ManualOrdinal;
    public void Apply(MediaRecord record,long order) { record.ManualBoot=Boot; record.ManualOrdinal=order; }
}
