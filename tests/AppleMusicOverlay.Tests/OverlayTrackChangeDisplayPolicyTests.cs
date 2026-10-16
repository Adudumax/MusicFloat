using AppleMusicOverlay.Services;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Tests;

public sealed class OverlayTrackChangeDisplayPolicyTests
{
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void SourceRecoveryOnlyReshowsHiddenPersistentOverlayWithReadyCover(bool keepVisible, bool visible, bool hasCover, bool expected)
    {
        var track = new TrackInfo("Song", "Artist", hasCover ? [1] : null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        Assert.Equal(expected, OverlayTrackChangeDisplayPolicy.ShouldRestorePersistentOverlay(keepVisible, visible, track));
    }

    [Fact]
    public void EnabledSettingPreservesAutomaticShowBehavior()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: true,
            pauseOverlay: false,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.Show, decision);
    }

    [Fact]
    public void ManualOnlyModeCachesTrackWhenOverlayIsHidden()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: false,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.CacheOnly, decision);
    }

    [Fact]
    public void ManualOnlyModeUpdatesAnAlreadyVisibleOverlay()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: false,
            isOverlayVisible: true);

        Assert.Equal(OverlayTrackChangeDisplayDecision.UpdateVisible, decision);
    }

    [Fact]
    public void ManualOnlyModeShowsPersistentOverlayWhenItIsNotVisibleYet()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: true,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.Show, decision);
    }

    [Fact]
    public void ManualOnlyModeUpdatesVisiblePersistentOverlayWithoutRestartingIt()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: true,
            isOverlayVisible: true);

        Assert.Equal(OverlayTrackChangeDisplayDecision.UpdateVisible, decision);
    }

    [Fact]
    public void CloudMusicNewTrackWithoutHdCoverHidesThePreviousOverlay()
    {
        var previous = new TrackInfo(
            "Previous",
            "Artist",
            [1],
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var current = new TrackInfo(
            "Current",
            "Artist",
            null,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(4),
            true);

        Assert.True(OverlayTrackChangeDisplayPolicy.ShouldHideStaleCloudMusicOverlay(
            current,
            previous));
    }

    [Fact]
    public void CloudMusicDoesNotHideWhenTheHdCoverIsReady()
    {
        var previous = new TrackInfo(
            "Previous",
            "Artist",
            [1],
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var current = new TrackInfo(
            "Current",
            "Artist",
            [2],
            "cloudmusic.exe",
            TimeSpan.FromMinutes(4),
            true);

        Assert.False(OverlayTrackChangeDisplayPolicy.ShouldHideStaleCloudMusicOverlay(
            current,
            previous));
    }

    [Fact]
    public void AppleMusicNeverUsesTheCloudMusicStaleOverlayRule()
    {
        var previous = new TrackInfo(
            "Previous",
            "Artist",
            [1],
            "AppleMusic",
            TimeSpan.FromMinutes(3),
            true);
        var current = new TrackInfo(
            "Current",
            "Artist",
            null,
            "AppleMusic",
            TimeSpan.FromMinutes(4),
            true);

        Assert.False(OverlayTrackChangeDisplayPolicy.ShouldHideStaleCloudMusicOverlay(
            current,
            previous));
    }

    [Fact]
    public void LatestMatchingTrackPreservesAnHdCoverThatArrivedDuringAsyncWork()
    {
        var requested = new TrackInfo(
            "Song",
            "Artist",
            null,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        TrackInfo latest = requested with { CoverBytes = [1, 2, 3] };

        TrackInfo selected = OverlayTrackChangeDisplayPolicy.SelectLatestCloudMusicTrack(
            requested,
            latest);

        Assert.Same(latest, selected);
    }

    [Fact]
    public void LatestDifferentTrackNeverReplacesTheRequestedTrack()
    {
        var requested = new TrackInfo(
            "Requested",
            "Artist",
            null,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var latest = new TrackInfo(
            "Different",
            "Artist",
            [1, 2, 3],
            "cloudmusic.exe",
            TimeSpan.FromMinutes(4),
            true);

        TrackInfo selected = OverlayTrackChangeDisplayPolicy.SelectLatestCloudMusicTrack(
            requested,
            latest);

        Assert.Same(requested, selected);
    }

    [Fact]
    public void AppleMusicNeverUsesTheCloudMusicLateCoverMergeRule()
    {
        var requested = new TrackInfo(
            "Song",
            "Artist",
            null,
            "AppleMusic",
            TimeSpan.FromMinutes(3),
            true);
        TrackInfo latest = requested with { CoverBytes = [1, 2, 3] };

        TrackInfo selected = OverlayTrackChangeDisplayPolicy.SelectLatestCloudMusicTrack(
            requested,
            latest);

        Assert.Same(requested, selected);
    }
}
