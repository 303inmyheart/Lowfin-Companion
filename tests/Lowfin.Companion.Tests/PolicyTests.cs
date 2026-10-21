using Lowfin.Companion;
using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class PolicyTests {
    private static long Ticks(double hours) => TimeSpan.FromHours(hours).Ticks;
    [Fact] public void TwelveHourVideoDoesNotCompleteAnHourEarly() {
        var rule=new CompletionRule { Mode=CompletionMode.CustomPercentage,Percentage=99.5m };
        Assert.False(rule.Completes(Ticks(11),Ticks(12)));
        Assert.False(rule.Completes(Ticks(11.939),Ticks(12)));
        Assert.True(rule.Completes(Ticks(11.94),Ticks(12)));
    }
    [Fact] public void RemainingTimeUsesSecondsNotRuntimePercentage() {
        var rule=new CompletionRule { Mode=CompletionMode.RemainingTime,RemainingSeconds=120 };
        long runtime=Ticks(12);
        Assert.False(rule.Completes(runtime-TimeSpan.FromMinutes(3).Ticks,runtime));
        Assert.True(rule.Completes(runtime-TimeSpan.FromMinutes(2).Ticks,runtime));
    }
    [Fact] public void UnknownDurationNeverInfersCustomCompletion() {
        foreach(var mode in Enum.GetValues<CompletionMode>()) Assert.False(new CompletionRule { Mode=mode }.Completes(Ticks(20),0));
    }
    [Fact] public void NeverAndDefaultDoNotForceAutoCompletion() {
        Assert.False(new CompletionRule { Mode=CompletionMode.NeverAutoComplete }.Completes(Ticks(12),Ticks(12)));
        Assert.False(new CompletionRule { Mode=CompletionMode.Default }.Completes(Ticks(12),Ticks(12)));
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(100.1)]
    public void InvalidPercentagesRejected(double value) => Assert.Throws<ArgumentException>(()=>new CompletionRule { Percentage=(decimal)value }.Validate());
    [Fact] public void ManualOrderRemainsOrderedAfterWaitsAndAcrossProcessRestart() {
        var order=new ActionOrder(); var record=new MediaRecord(); long older=order.Next(),newer=order.Next(); order.Apply(record,newer);
        Assert.False(order.Newer(record,older)); Assert.True(new ActionOrder().Newer(record,1));
    }
    [Fact] public void ResumeExclusionsUseAllowedLibraryAncestryAndAllowAnotherVisibleLibrary() {
        var hidden=Guid.NewGuid(); var shown=Guid.NewGuid(); var season=Guid.NewGuid();
        Assert.False(ResumeVisibility.Included([season,hidden],[shown]));
        Assert.True(ResumeVisibility.Included([season,hidden,shown],[shown]));
        Assert.False(ResumeVisibility.Included([season,shown],[]));
    }
}
