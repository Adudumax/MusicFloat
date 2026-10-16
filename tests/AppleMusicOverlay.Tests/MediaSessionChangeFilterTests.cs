using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class MediaSessionChangeFilterTests
{
    [Fact]
    public void AutomaticModeObservesEverySessionAndCurrentSessionChange()
    {
        Assert.True(MediaSessionChangeFilter.ShouldNotifyForSession(string.Empty, "Chrome"));
        Assert.True(MediaSessionChangeFilter.ShouldNotifyForCurrentSession(string.Empty));
    }

    [Fact]
    public void FixedSourceModeIgnoresOtherSessionsAndCurrentSessionChanges()
    {
        const string appleMusic = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";

        Assert.True(MediaSessionChangeFilter.ShouldNotifyForSession(appleMusic, appleMusic.ToLowerInvariant()));
        Assert.False(MediaSessionChangeFilter.ShouldNotifyForSession(appleMusic, "Chrome"));
        Assert.False(MediaSessionChangeFilter.ShouldNotifyForCurrentSession(appleMusic));
    }
}
