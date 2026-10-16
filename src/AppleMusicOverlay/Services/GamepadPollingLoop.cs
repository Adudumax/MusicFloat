namespace AppleMusicOverlay.Services;

internal static class GamepadPollingLoop
{
    internal static async Task RunAsync(
        Action read,
        Action recover,
        TimeSpan readInterval,
        CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
    {
        using var timer = new PeriodicTimer(readInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                read();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A stale/disconnected WinRT controller must not permanently kill input processing.
                await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                try { recover(); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
    }
}
