using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public enum OverlayTrackChangeDisplayDecision
{
    Show,
    UpdateVisible,
    CacheOnly
}

public static class OverlayTrackChangeDisplayPolicy
{
    public static bool ShouldRestorePersistentOverlay(bool keepVisible, bool isOverlayVisible, TrackInfo track)
    {
        return keepVisible && !isOverlayVisible && track.CoverBytes is { Length: > 0 };
    }

    public static OverlayTrackChangeDisplayDecision Decide(
        bool showOnTrackChange,
        bool pauseOverlay,
        bool isOverlayVisible)
    {
        if (showOnTrackChange)
        {
            return OverlayTrackChangeDisplayDecision.Show;
        }

        if (isOverlayVisible)
        {
            return OverlayTrackChangeDisplayDecision.UpdateVisible;
        }

        return pauseOverlay
            ? OverlayTrackChangeDisplayDecision.Show
            : OverlayTrackChangeDisplayDecision.CacheOnly;
    }

    public static bool ShouldHideStaleCloudMusicOverlay(
        TrackInfo? currentTrack,
        TrackInfo? displayedTrack)
    {
        return currentTrack != null &&
               displayedTrack != null &&
               OverlayFavoritePresentation.IsSupportedCloudMusicSource(currentTrack.SourceAppId) &&
               currentTrack.CoverBytes is not { Length: > 0 } &&
               !TrackIdentity.Create(currentTrack).Equals(
                   TrackIdentity.Create(displayedTrack),
                   StringComparison.Ordinal);
    }

    public static TrackInfo SelectLatestCloudMusicTrack(
        TrackInfo requestedTrack,
        TrackInfo? latestTrack)
    {
        return OverlayFavoritePresentation.IsSupportedCloudMusicSource(requestedTrack.SourceAppId) &&
               latestTrack != null &&
               TrackIdentity.Create(latestTrack).Equals(
                   TrackIdentity.Create(requestedTrack),
                   StringComparison.Ordinal)
            ? latestTrack
            : requestedTrack;
    }
}
