namespace AppleMusicOverlay.Services;

public static class MediaSessionChangeFilter
{
    public static bool ShouldNotifyForSession(
        string? preferredSourceAppUserModelId,
        string? changedSourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
        {
            return true;
        }

        return string.Equals(
            preferredSourceAppUserModelId.Trim(),
            changedSourceAppUserModelId?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldNotifyForCurrentSession(string? preferredSourceAppUserModelId)
    {
        return string.IsNullOrWhiteSpace(preferredSourceAppUserModelId);
    }
}
