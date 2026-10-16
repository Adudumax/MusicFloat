using System.Security.Cryptography;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class TrackMonitor : IDisposable
{
    private static readonly TimeSpan SettleProbeInterval = TimeSpan.FromMilliseconds(40);
    private const int StableCoverRefreshPolls = 30;
    private const int SharedCoverRefreshPolls = 3;
    private const int MissingSessionConfirmationReads = 2;
    private readonly IMediaSessionService _mediaSessionService;
    private readonly IMediaSessionChangeNotifier? _changeNotifier;
    private readonly object _wakeLock = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _stablePollInterval;
    private readonly TimeSpan _settleDelay;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lastTrackKey;
    private string? _lastCoverKey;
    private string? _pendingTrackKey;
    private TrackInfo? _lastStableTrack;
    private TrackInfo? _lastPublishedTrack;
    private bool _isUnavailable;
    private bool _restoreNotificationPending;
    private int _stableMetadataOnlyPolls;
    private int _sharedCoverRefreshPollsRemaining;
    private int _consecutiveMissingSessionReads;
    private bool _disposed;

    public event EventHandler<TrackInfo?>? TrackRead;

    public event EventHandler<TrackInfo>? TrackChanged;

    public event EventHandler<TrackInfo>? TrackRefreshed;

    public event EventHandler? TrackUnavailable;
    public event EventHandler<TrackInfo>? TrackRestored;

    public TrackInfo? CurrentTrack => _lastStableTrack;

    public TrackMonitor(
        IMediaSessionService mediaSessionService,
        TimeSpan? pollInterval = null,
        TimeSpan? stablePollInterval = null,
        TimeSpan? settleDelay = null)
    {
        _mediaSessionService = mediaSessionService;
        _changeNotifier = mediaSessionService as IMediaSessionChangeNotifier;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(300);
        _stablePollInterval = stablePollInterval ?? TimeSpan.FromSeconds(1);
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(120);
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_cts != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        if (_changeNotifier != null)
        {
            _changeNotifier.MediaSessionChanged += ChangeNotifier_MediaSessionChanged;
        }

        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _pollGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await PollCoreAsync(cancellationToken);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollCoreAsync(CancellationToken cancellationToken)
    {
        TrackInfo? track;
        bool includeCover = ShouldIncludeCoverOnNextRead();
        try
        {
            track = await ReadCurrentTrackAsync(includeCover, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _consecutiveMissingSessionReads = 0;
            TrackRead?.Invoke(this, _lastStableTrack);
            return;
        }

        if (track == null)
        {
            if (_lastStableTrack != null &&
                ++_consecutiveMissingSessionReads < MissingSessionConfirmationReads)
            {
                TrackRead?.Invoke(this, _lastStableTrack);
                return;
            }

            _consecutiveMissingSessionReads = 0;
            _pendingTrackKey = null;
            _lastStableTrack = null;
            _stableMetadataOnlyPolls = 0;
            _sharedCoverRefreshPollsRemaining = 0;
            TrackRead?.Invoke(this, null);
            if (!_isUnavailable)
            {
                _isUnavailable = true;
                _restoreNotificationPending = true;
                TrackUnavailable?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        _consecutiveMissingSessionReads = 0;
        track = NormalizeCoverForSource(NormalizeTrack(track));
        bool alreadySettledTrack = false;
        if (!includeCover && HasTrackIdentityChanged(track))
        {
            track = NormalizeCoverForSource(
                NormalizeTrack(await ReadSettledTrackAsync(track, cancellationToken)));
            alreadySettledTrack = true;
        }
        else
        {
            track = RestoreStableCoverIfMetadataOnly(track);
        }

        string trackKey = TrackIdentity.Create(track);
        string coverKey = CreateCoverKey(track);
        bool pendingTrackReady = trackKey == _pendingTrackKey && HasCover(track);
        bool isCloudMusicTrack =
            OverlayFavoritePresentation.IsSupportedCloudMusicSource(track.SourceAppId);
        bool metadataUpdated = IsMetadataRefresh(track) &&
                               !(isCloudMusicTrack && !HasCover(track));
        bool trackChanged = (trackKey != _lastTrackKey || pendingTrackReady) && !metadataUpdated;
        bool refreshed = metadataUpdated ||
                         (trackKey == _lastTrackKey && coverKey != _lastCoverKey && track.CoverBytes is { Length: > 0 });

        if (!trackChanged && !refreshed)
        {
            PublishRead(track);
            return;
        }

        if (trackChanged)
        {
            if (!alreadySettledTrack ||
                (_settleDelay > TimeSpan.Zero && !IsReadyToDisplay(track, _lastCoverKey)))
            {
                track = NormalizeCoverForSource(
                    NormalizeTrack(await SettleTrackAsync(
                        track,
                        _lastCoverKey,
                        cancellationToken)));
            }

            if (!IsUsableTrack(track))
            {
                TrackRead?.Invoke(this, _lastStableTrack);
                return;
            }

            trackKey = TrackIdentity.Create(track);
            coverKey = CreateCoverKey(track);
            if (IsMetadataRefresh(track))
            {
                _lastTrackKey = trackKey;
                _lastCoverKey = coverKey;
                PublishRead(track);
                TrackRefreshed?.Invoke(this, track);
                return;
            }

            if (!HasCover(track))
            {
                _pendingTrackKey = trackKey;
                if (OverlayFavoritePresentation.IsSupportedCloudMusicSource(track.SourceAppId))
                {
                    _lastTrackKey = trackKey;
                    _lastCoverKey = string.Empty;
                    PublishRead(track);
                    return;
                }

                TrackRead?.Invoke(this, _lastStableTrack);
                return;
            }

            if (trackKey == _lastTrackKey)
            {
                bool wasWaitingForFirstCover = trackKey == _pendingTrackKey;
                _pendingTrackKey = null;
                if (coverKey != _lastCoverKey && track.CoverBytes is { Length: > 0 })
                {
                    _sharedCoverRefreshPollsRemaining = 0;
                    _lastCoverKey = coverKey;
                    PublishRead(track);
                    if (wasWaitingForFirstCover)
                    {
                        TrackChanged?.Invoke(this, track);
                    }
                    else
                    {
                        TrackRefreshed?.Invoke(this, track);
                    }
                }
                else
                {
                    PublishRead(track);
                }

                return;
            }

            // Different songs may legitimately share album artwork. After the bounded
            // settle window, publish the new identity and briefly check for late artwork.
            _sharedCoverRefreshPollsRemaining = coverKey == _lastCoverKey ? SharedCoverRefreshPolls : 0;
            _lastTrackKey = trackKey;
            _lastCoverKey = coverKey;
            _pendingTrackKey = null;
            PublishRead(track);
            TrackChanged?.Invoke(this, track);
            return;
        }

        PublishRead(track);
        _sharedCoverRefreshPollsRemaining = 0;
        _lastTrackKey = trackKey;
        _lastCoverKey = coverKey;
        TrackRefreshed?.Invoke(this, track);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cts?.Cancel();
        if (_changeNotifier != null)
        {
            _changeNotifier.MediaSessionChanged -= ChangeNotifier_MediaSessionChanged;
        }

        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        _disposed = true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task delayTask = Task.Delay(GetNextPollInterval(), cancellationToken);
                Task wakeTask = GetWakeSignalTask();
                Task completedTask = await Task.WhenAny(delayTask, wakeTask);
                if (completedTask == delayTask)
                {
                    await delayTask;
                }
                else
                {
                    ResetWakeSignal(wakeTask);
                }

                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TrackInfo> SettleTrackAsync(TrackInfo initialTrack, string? previousCoverKey, CancellationToken cancellationToken)
    {
        if (_settleDelay <= TimeSpan.Zero)
        {
            return await ReadSettledTrackAsync(initialTrack, cancellationToken);
        }

        if (IsReadyToDisplay(initialTrack, previousCoverKey))
        {
            return initialTrack;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _settleDelay;
        TrackInfo bestTrack = initialTrack;
        while (DateTimeOffset.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            TimeSpan delay = remaining < SettleProbeInterval ? remaining : SettleProbeInterval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            bestTrack = NormalizeCoverForSource(
                NormalizeTrack(await ReadSettledTrackAsync(bestTrack, cancellationToken)));
            if (IsReadyToDisplay(bestTrack, previousCoverKey))
            {
                return bestTrack;
            }
        }

        return bestTrack;
    }

    private async Task<TrackInfo> ReadSettledTrackAsync(TrackInfo initialTrack, CancellationToken cancellationToken)
    {
        try
        {
            TrackInfo? settledTrack = await ReadCurrentTrackAsync(includeCover: true, cancellationToken);
            if (settledTrack != null)
            {
                return settledTrack;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return initialTrack;
    }

    private Task<TrackInfo?> ReadCurrentTrackAsync(bool includeCover, CancellationToken cancellationToken)
    {
        _stableMetadataOnlyPolls = includeCover ? 0 : _stableMetadataOnlyPolls + 1;
        if (includeCover && _sharedCoverRefreshPollsRemaining > 0)
        {
            _sharedCoverRefreshPollsRemaining--;
        }
        return _mediaSessionService.GetCurrentTrackAsync(
            includeCover ? MediaSessionReadOptions.Full : MediaSessionReadOptions.MetadataOnly,
            cancellationToken);
    }

    private bool ShouldIncludeCoverOnNextRead()
    {
        return _lastStableTrack == null ||
               _pendingTrackKey != null ||
               _sharedCoverRefreshPollsRemaining > 0 ||
               _stableMetadataOnlyPolls >= StableCoverRefreshPolls;
    }

    private TimeSpan GetNextPollInterval()
    {
        return _lastStableTrack != null && _pendingTrackKey == null
            ? _stablePollInterval
            : _pollInterval;
    }

    private Task GetWakeSignalTask()
    {
        lock (_wakeLock)
        {
            return _wakeSignal.Task;
        }
    }

    private void ResetWakeSignal(Task completedWakeTask)
    {
        lock (_wakeLock)
        {
            if (ReferenceEquals(_wakeSignal.Task, completedWakeTask))
            {
                _wakeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void ChangeNotifier_MediaSessionChanged(object? sender, EventArgs e)
    {
        lock (_wakeLock)
        {
            _wakeSignal.TrySetResult();
        }
    }

    private static string CreateCoverKey(TrackInfo track)
    {
        if (track.CoverBytes is not { Length: > 0 } coverBytes)
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(coverBytes));
    }

    private void PublishRead(TrackInfo track)
    {
        bool sameTrack = HasSameSourceAndTitle(_lastPublishedTrack, track);
        bool restoredSameTrack = _restoreNotificationPending && sameTrack && HasCover(track);
        if (restoredSameTrack || !sameTrack)
        {
            _restoreNotificationPending = false;
        }
        _lastStableTrack = track;
        _lastPublishedTrack = track;
        _isUnavailable = false;
        TrackRead?.Invoke(this, track);
        if (restoredSameTrack)
        {
            TrackRestored?.Invoke(this, track);
        }
    }

    private TrackInfo RestoreStableCoverIfMetadataOnly(TrackInfo track)
    {
        if (track.CoverBytes is { Length: > 0 } ||
            _lastStableTrack?.CoverBytes is not { Length: > 0 } stableCover)
        {
            return track;
        }

        string trackKey = TrackIdentity.Create(track);
        if (!trackKey.Equals(_lastTrackKey, StringComparison.Ordinal))
        {
            return track;
        }

        return track with { CoverBytes = stableCover };
    }

    private bool HasTrackIdentityChanged(TrackInfo track)
    {
        return _lastTrackKey == null ||
               !TrackIdentity.Create(track).Equals(_lastTrackKey, StringComparison.Ordinal);
    }

    private static TrackInfo NormalizeTrack(TrackInfo track)
    {
        string title = track.Title.Trim();
        string artist = IsPlaceholderText(track.Artist) ? string.Empty : track.Artist.Trim();
        return track with { Title = title, Artist = artist };
    }

    private static TrackInfo NormalizeCoverForSource(TrackInfo track)
    {
        if (!OverlayFavoritePresentation.IsSupportedCloudMusicSource(track.SourceAppId) ||
            track.CoverBytes is not { Length: > 0 } coverBytes ||
            CoverImageQuality.MeetsMinimumResolution(
                coverBytes,
                CloudMusicCoverResolver.MinimumHighResolutionPixels))
        {
            return track;
        }

        return track with { CoverBytes = null };
    }

    private static bool IsReadyToDisplay(TrackInfo track, string? previousCoverKey)
    {
        bool hasCurrentCover =
            IsUsableTrack(track) &&
            HasCover(track) &&
            (string.IsNullOrEmpty(previousCoverKey) || CreateCoverKey(track) != previousCoverKey);
        if (!hasCurrentCover)
        {
            return false;
        }

        return !OverlayFavoritePresentation.IsSupportedCloudMusicSource(track.SourceAppId) ||
               CoverImageQuality.MeetsMinimumResolution(
                   track.CoverBytes!,
                   CloudMusicCoverResolver.MinimumHighResolutionPixels);
    }

    private static bool HasCover(TrackInfo track)
    {
        return track.CoverBytes is { Length: > 0 };
    }

    private static bool IsUsableTrack(TrackInfo track)
    {
        return !IsPlaceholderText(track.Title);
    }

    private bool IsMetadataRefresh(TrackInfo track)
    {
        return HasSameSourceAndTitle(_lastPublishedTrack, track) &&
               !_lastPublishedTrack!.Artist.Equals(track.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameSourceAndTitle(TrackInfo? previous, TrackInfo track)
    {
        return previous != null &&
               previous.SourceAppId.Equals(track.SourceAppId, StringComparison.OrdinalIgnoreCase) &&
               previous.Title.Equals(track.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Unknown Track", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrackMonitor));
        }
    }
}
