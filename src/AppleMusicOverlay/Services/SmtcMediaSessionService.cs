using AppleMusicOverlay.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppleMusicOverlay.Services;

public sealed class SmtcMediaSessionService : IMediaSessionService, IMediaSessionSourceService, IMediaSessionChangeNotifier
{
    private readonly object _syncRoot = new();
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _observedSessions = new();
    private readonly CloudMusicCoverUpgradeCoordinator _cloudMusicCoverUpgradeCoordinator;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public event EventHandler? MediaSessionChanged;

    public string PreferredSourceAppUserModelId { get; set; } = string.Empty;

    public SmtcMediaSessionService()
        : this(new CloudMusicCoverResolver())
    {
    }

    public SmtcMediaSessionService(ICloudMusicCoverResolver cloudMusicCoverResolver)
    {
        _cloudMusicCoverUpgradeCoordinator =
            new CloudMusicCoverUpgradeCoordinator(cloudMusicCoverResolver);
        _cloudMusicCoverUpgradeCoordinator.CoverUpgraded +=
            (_, _) => RaiseMediaSessionChanged();
    }

    public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentTrackAsync(MediaSessionReadOptions.Full, cancellationToken);
    }

    public async Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session == null)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties properties =
            await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();

        string title = NormalizeText(properties.Title, "Unknown Track");
        string sourceAppUserModelId = NormalizeText(session.SourceAppUserModelId, "Unknown Source");
        string artist = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            sourceAppUserModelId,
            NormalizeText(properties.Artist, "Unknown Artist"),
            properties.AlbumTitle);
        TimeSpan duration = timeline.EndTime;
        byte[]? coverBytes = null;
        if (_cloudMusicCoverUpgradeCoordinator.IsSupportedSource(sourceAppUserModelId))
        {
            byte[]? highResolutionCover = _cloudMusicCoverUpgradeCoordinator.SelectCover(
                title,
                artist,
                duration,
                options.IncludeCover);
            coverBytes = highResolutionCover;
        }
        else if (options.IncludeCover)
        {
            coverBytes = await TryReadCoverBytesAsync(properties.Thumbnail, cancellationToken);
        }

        if (!_cloudMusicCoverUpgradeCoordinator.IsSupportedSource(sourceAppUserModelId))
        {
            _cloudMusicCoverUpgradeCoordinator.Reset();
        }

        return new TrackInfo(
            title,
            artist,
            coverBytes,
            sourceAppUserModelId,
            duration,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TrySkipPreviousAsync().AsTask(cancellationToken);
        }
    }

    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TrySkipNextAsync().AsTask(cancellationToken);
        }
    }

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TryTogglePlayPauseAsync().AsTask(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MediaSessionCandidate>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        SessionSnapshot snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Candidates;
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync(CancellationToken cancellationToken)
    {
        SessionSnapshot snapshot = await GetSnapshotAsync(cancellationToken);
        if (snapshot.Sessions.Count == 0)
        {
            return null;
        }

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest(snapshot.Candidates, PreferredSourceAppUserModelId);
        if (selected != null && snapshot.Sessions.TryGetValue(selected.Index, out GlobalSystemMediaTransportControlsSession? selectedSession))
        {
            return selectedSession;
        }

        if (!string.IsNullOrWhiteSpace(PreferredSourceAppUserModelId))
        {
            return null;
        }

        return snapshot.Current ?? snapshot.Sessions.Values.FirstOrDefault();
    }

    private async Task<SessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager = await GetManagerAsync(cancellationToken);

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
        ObserveSessions(sessions);
        if (sessions.Count == 0)
        {
            return new SessionSnapshot(
                new Dictionary<int, GlobalSystemMediaTransportControlsSession>(),
                [],
                null);
        }

        GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
        IReadOnlyList<MediaSessionCandidate> candidates =
            await SmtcSessionCandidateReader.ReadCandidatesAsync(
                sessions,
                current,
                ReadCandidateAsync,
                cancellationToken);

        Dictionary<int, GlobalSystemMediaTransportControlsSession> sessionByIndex = candidates
            .ToDictionary(candidate => candidate.Index, candidate => sessions[candidate.Index]);
        GlobalSystemMediaTransportControlsSession? readableCurrent = candidates.Any(candidate => candidate.IsCurrent)
            ? current
            : null;

        return new SessionSnapshot(sessionByIndex, candidates, readableCurrent);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync(CancellationToken cancellationToken)
    {
        if (_manager != null)
        {
            return _manager;
        }

        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        lock (_syncRoot)
        {
            if (_manager != null)
            {
                return _manager;
            }

            _manager = manager;
            _manager.SessionsChanged += (_, _) => RaiseMediaSessionChanged();
            _manager.CurrentSessionChanged += (_, _) =>
            {
                if (MediaSessionChangeFilter.ShouldNotifyForCurrentSession(PreferredSourceAppUserModelId))
                {
                    RaiseMediaSessionChanged();
                }
            };
            return _manager;
        }
    }

    private void ObserveSessions(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        lock (_syncRoot)
        {
            foreach (GlobalSystemMediaTransportControlsSession session in sessions)
            {
                if (!_observedSessions.Add(session))
                {
                    continue;
                }

                session.MediaPropertiesChanged += (_, _) => RaiseMediaSessionChangedForSession(session);
                session.PlaybackInfoChanged += (_, _) => RaiseMediaSessionChangedForSession(session);
            }
        }
    }

    private void RaiseMediaSessionChangedForSession(GlobalSystemMediaTransportControlsSession session)
    {
        string preferredSourceAppUserModelId = PreferredSourceAppUserModelId;
        if (string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
        {
            RaiseMediaSessionChanged();
            return;
        }

        string sourceAppUserModelId;
        try
        {
            sourceAppUserModelId = session.SourceAppUserModelId;
        }
        catch
        {
            return;
        }

        if (MediaSessionChangeFilter.ShouldNotifyForSession(
                preferredSourceAppUserModelId,
                sourceAppUserModelId))
        {
            RaiseMediaSessionChanged();
        }
    }

    private void RaiseMediaSessionChanged()
    {
        MediaSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<SmtcSessionReadResult> ReadCandidateAsync(
        GlobalSystemMediaTransportControlsSession session,
        int index,
        CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties properties =
            await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();

        return new SmtcSessionReadResult(
            NormalizeText(session.SourceAppUserModelId, "Unknown Source"),
            NormalizeText(properties.Title, "Unknown Track"),
            NormalizeText(properties.Artist, "Unknown Artist"),
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    private static async Task<byte[]?> ReadCoverBytesAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail == null)
        {
            return null;
        }

        using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
        if (stream.Size == 0 || stream.Size > int.MaxValue)
        {
            return null;
        }

        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
        IBuffer readBuffer = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None).AsTask(cancellationToken);
        byte[] bytes = new byte[readBuffer.Length];
        using DataReader reader = DataReader.FromBuffer(readBuffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]?> TryReadCoverBytesAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoverBytesAsync(thumbnail, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record SessionSnapshot(
        IReadOnlyDictionary<int, GlobalSystemMediaTransportControlsSession> Sessions,
        IReadOnlyList<MediaSessionCandidate> Candidates,
        GlobalSystemMediaTransportControlsSession? Current);
}
