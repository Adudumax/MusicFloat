using System.Buffers.Binary;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackMonitorTests
{
    [Fact]
    public async Task PollOnceRaisesChangedForNewTracksAndRefreshesCoverUpdates()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var same = first with { CoverBytes = [9], IsPlaying = false };
        var second = first with { Title = "Second", CoverBytes = [2] };
        var service = new FakeMediaSessionService(first, first, same, second, second, null);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var reads = new List<TrackInfo?>();
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackRead += (_, track) => reads.Add(track);
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(4, reads.Count);
        Assert.Equal([first, second], changes);
        Assert.Equal([same], refreshes);
    }

    [Fact]
    public async Task PollOnceRefreshesTrackAfterIdentityChangesBeforeRaisingChanged()
    {
        byte[] staleCover = [1];
        byte[] freshCover = [2];
        var first = new TrackInfo("First", "Artist", staleCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithStaleCover = new TrackInfo("Second", "Artist", staleCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithFreshCover = new TrackInfo("Second", "Artist", freshCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, secondWithStaleCover, secondWithFreshCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, secondWithFreshCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceUsesSettledTrackWhenIdentityChangesDuringSettle()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var transitional = new TrackInfo("Loading", "", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var settled = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, transitional, settled);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, settled], changes);
    }

    [Fact]
    public async Task PollOnceDoesNotRaiseChangedForUnknownTransitionBeforeRealTrack()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var unknown = new TrackInfo("Unknown Track", "Unknown Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, unknown, unknown, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var reads = new List<TrackInfo?>();
        var changes = new List<TrackInfo>();
        monitor.TrackRead += (_, track) => reads.Add(track);
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, second], changes);
        Assert.DoesNotContain(unknown, reads);
    }

    [Fact]
    public async Task CurrentTrackKeepsLastStableTrackDuringUnknownTransition()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var unknown = new TrackInfo("Unknown Track", "Unknown Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, unknown, unknown, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);

        Assert.Null(monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(first, monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(first, monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(second, monitor.CurrentTrack);
    }

    [Fact]
    public async Task PollOnceRefreshesWhenArtistArrivesAfterTitle()
    {
        byte[] cover = [1];
        var withoutArtist = new TrackInfo("Song", "Unknown Artist", cover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var normalizedWithoutArtist = withoutArtist with { Artist = string.Empty };
        var withArtist = withoutArtist with { Artist = "Artist" };
        var service = new FakeMediaSessionService(withoutArtist, withoutArtist, withArtist, withArtist);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([normalizedWithoutArtist], changes);
        Assert.Equal([withArtist], refreshes);
    }

    [Fact]
    public async Task PollOnceDoesNotShowPreviousCoverWhenNewTrackInitiallyKeepsOldCover()
    {
        byte[] oldCover = [1];
        byte[] freshCover = [2];
        var first = new TrackInfo("First", "Artist", oldCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithOldCover = new TrackInfo("Second", "Artist", oldCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithFreshCover = secondWithOldCover with { CoverBytes = freshCover };
        var service = new FakeMediaSessionService(first, secondWithOldCover, secondWithOldCover, secondWithFreshCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.FromMilliseconds(40));
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, secondWithFreshCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task CloudMusicAllowsSharedHighResolutionArtworkAfterSettling()
    {
        byte[] oldCover = CreateBmp(768, 768);
        var first = new TrackInfo(
            "First",
            "Artist",
            oldCover,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var secondWithOldCover = new TrackInfo(
            "Second",
            "Artist",
            oldCover,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(4),
            true);
        var service = new FakeMediaSessionService(
            first,
            first,
            secondWithOldCover,
            secondWithOldCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, secondWithOldCover], changes);
        Assert.Equal("Second", monitor.CurrentTrack?.Title);
        Assert.Equal(oldCover, monitor.CurrentTrack?.CoverBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public async Task AlbumTracksSharingArtworkCanMoveForwardAndBackWithoutFreezing(int settleMilliseconds)
    {
        var first = new TrackInfo("Album Track 1", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = first with { Title = "Album Track 2" };
        var third = first with { Title = "Album Track 3" };
        var service = new MutableMediaSessionService(first);
        using var monitor = new TrackMonitor(service, settleDelay: TimeSpan.FromMilliseconds(settleMilliseconds));
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        foreach (TrackInfo track in new[] { first, second, third, second, first })
        {
            service.Track = track;
            await monitor.PollOnceAsync();
            Assert.Equal(track, monitor.CurrentTrack);
        }
        await monitor.PollOnceAsync();
        Assert.Equal([first, second, third, second, first], changes);
    }

    [Fact]
    public async Task ArtworkArrivingAfterSharedCoverSettleRefreshesWithoutSecondTrackChange()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = first with { Title = "Second" };
        var updated = second with { CoverBytes = [2] };
        var service = new MutableMediaSessionService(first);
        using var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);
        await monitor.PollOnceAsync();
        service.Track = second;
        await monitor.PollOnceAsync();
        service.Track = updated;
        await monitor.PollOnceAsync();
        Assert.Equal([first, second], changes);
        Assert.Equal([updated], refreshes);
        Assert.Equal(updated, monitor.CurrentTrack);
    }

    [Fact]
    public async Task SharedArtworkStopsFullCoverReadsAfterBoundedFollowup()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new MutableMediaSessionService(first);
        using var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        await monitor.PollOnceAsync();
        service.Track = first with { Title = "Second" };
        await monitor.PollOnceAsync();
        for (int i = 0; i < 8; i++) await monitor.PollOnceAsync();
        Assert.All(service.IncludeCoverRequests.TakeLast(5), includeCover => Assert.False(includeCover));
    }

    [Fact]
    public async Task CloudMusicBrieflyWaitsForHighResolutionCoverBeforeFirstDisplay()
    {
        byte[] lowResolutionCover = CreateBmp(162, 162);
        byte[] highResolutionCover = CreateBmp(768, 768);
        var lowResolutionTrack = new TrackInfo(
            "Song",
            "Artist",
            lowResolutionCover,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var highResolutionTrack = lowResolutionTrack with
        {
            CoverBytes = highResolutionCover
        };
        var service = new FakeMediaSessionService(
            lowResolutionTrack,
            highResolutionTrack);
        var monitor = new TrackMonitor(
            service,
            settleDelay: TimeSpan.FromMilliseconds(120));
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();

        Assert.Equal([highResolutionTrack], changes);
    }

    [Fact]
    public async Task CloudMusicNeverPublishesLowResolutionArtworkAtTheSettleDeadline()
    {
        byte[] staleLowResolutionCover = CreateBmp(162, 162);
        var track = new TrackInfo(
            "New Song",
            "New Artist",
            staleLowResolutionCover,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        var service = new FakeMediaSessionService(track, track, track);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var reads = new List<TrackInfo?>();
        monitor.TrackChanged += (_, changedTrack) => changes.Add(changedTrack);
        monitor.TrackRead += (_, readTrack) => reads.Add(readTrack);

        await monitor.PollOnceAsync();

        Assert.Empty(changes);
        Assert.Single(reads);
        Assert.Equal("New Song", reads[0]?.Title);
        Assert.Null(reads[0]?.CoverBytes);
    }

    [Fact]
    public async Task CloudMusicPublishesLateHdCoverAsFirstCompleteDisplay()
    {
        byte[] lowResolutionCover = CreateBmp(162, 162);
        byte[] highResolutionCover = CreateBmp(768, 768);
        var withoutHdCover = new TrackInfo(
            "Song",
            "Artist",
            lowResolutionCover,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
        TrackInfo withHdCover = withoutHdCover with { CoverBytes = highResolutionCover };
        var service = new FakeMediaSessionService(
            withoutHdCover,
            withoutHdCover,
            withHdCover,
            withHdCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, changedTrack) => changes.Add(changedTrack);
        monitor.TrackRefreshed += (_, refreshedTrack) => refreshes.Add(refreshedTrack);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([withHdCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceRaisesChangedOnlyAfterFirstTrackReceivesCover()
    {
        var withoutCover = new TrackInfo("Song", "Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var withCover = withoutCover with { CoverBytes = [2] };
        var service = new FakeMediaSessionService(withoutCover, withoutCover, withCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([withCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceReportsNullWhenMediaSessionFails()
    {
        var monitor = new TrackMonitor(new ThrowingMediaSessionService());
        var reads = new List<TrackInfo?>();
        monitor.TrackRead += (_, track) => reads.Add(track);

        await monitor.PollOnceAsync();

        Assert.Equal([null], reads);
    }

    [Fact]
    public async Task TransientMissingSessionKeepsCurrentTrackAndDoesNotReplaySameTrackChange()
    {
        var track = new TrackInfo("Song", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(track, track, null, track);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        int unavailableCount = 0;
        monitor.TrackChanged += (_, changedTrack) => changes.Add(changedTrack);
        monitor.TrackUnavailable += (_, _) => unavailableCount++;

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Equal(track, monitor.CurrentTrack);
        await monitor.PollOnceAsync();

        Assert.Equal(0, unavailableCount);
        Assert.Equal([track], changes);
    }

    [Fact]
    public async Task ConsecutiveMissingSessionReadsConfirmUnavailableWithoutReplayingSameTrackChange()
    {
        var track = new TrackInfo("Song", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(track, track, null, null, track, track);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        int unavailableCount = 0;
        monitor.TrackChanged += (_, changedTrack) => changes.Add(changedTrack);
        monitor.TrackUnavailable += (_, _) => unavailableCount++;

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Equal(track, monitor.CurrentTrack);
        await monitor.PollOnceAsync();
        Assert.Null(monitor.CurrentTrack);
        await monitor.PollOnceAsync();

        Assert.Equal(1, unavailableCount);
        Assert.Equal(track, monitor.CurrentTrack);
        Assert.Equal([track], changes);
    }

    [Fact]
    public async Task DifferentTrackAfterConfirmedUnavailableStillRaisesTrackChanged()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(4), true);
        var service = new FakeMediaSessionService(first, first, null, null, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, changedTrack) => changes.Add(changedTrack);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(second, monitor.CurrentTrack);
        Assert.Equal([first, second], changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameTrackRecoveryRaisesRestoredWithoutAutomaticTrackChangeEvenWhenArtistArrives(bool artistArrives)
    {
        var first = new TrackInfo("Song", artistArrives ? "" : "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var restored = first with { Artist = "Artist" };
        var service = new FakeMediaSessionService(first, first, null, null, null, restored, restored);
        using var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var recoveries = new List<TrackInfo>();
        int unavailable = 0;
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRestored += (_, track) => recoveries.Add(track);
        monitor.TrackUnavailable += (_, _) => unavailable++;

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(1, unavailable);
        Assert.Equal([first], changes);
        Assert.Equal([restored], recoveries);
        Assert.Equal(restored, monitor.CurrentTrack);
    }

    [Fact]
    public async Task MediaReadFailureDoesNotReportConfirmedSessionUnavailable()
    {
        var monitor = new TrackMonitor(new ThrowingMediaSessionService());
        int unavailableCount = 0;
        monitor.TrackUnavailable += (_, _) => unavailableCount++;

        await monitor.PollOnceAsync();

        Assert.Equal(0, unavailableCount);
    }

    [Fact]
    public async Task RecoveryWaitsForCoverAndStillNotifiesWhenSameCoverArrivesLater()
    {
        var track = new TrackInfo("Song", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(track, track, null, null, track with { CoverBytes = null }, track);
        using var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var restored = new List<TrackInfo>();
        monitor.TrackRestored += (_, value) => restored.Add(value);
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Empty(restored);
        await monitor.PollOnceAsync();
        Assert.Equal([track], restored);
    }

    [Fact]
    public async Task StartWaitsForPollIntervalBeforeFirstAutomaticRead()
    {
        var service = new CountingMediaSessionService();
        using var monitor = new TrackMonitor(service, TimeSpan.FromSeconds(10));

        monitor.Start();
        await Task.Delay(50);

        Assert.Equal(0, service.ReadCount);
    }

    [Fact]
    public async Task PollOnceSkipsCoverReadAfterStableTrackIsKnown()
    {
        var track = new TrackInfo("Song", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new ReadOptionsMediaSessionService(track, track, track);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([true, true, false], service.IncludeCoverRequests);
    }

    [Fact]
    public async Task PollOnceRefetchesCoverImmediatelyWhenMetadataOnlyReadFindsNewTrack()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new ReadOptionsMediaSessionService(first, first, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, second], changes);
        Assert.Equal([true, true, false, true], service.IncludeCoverRequests);
    }

    [Fact]
    public async Task StartPollsImmediatelyWhenMediaSessionSignalsTrackChange()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new NotifyingReadOptionsMediaSessionService(first, first, second, second);
        using var monitor = new TrackMonitor(
            service,
            pollInterval: TimeSpan.FromSeconds(30),
            stablePollInterval: TimeSpan.FromSeconds(30),
            settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        monitor.Start();
        service.RaiseMediaSessionChanged();
        await service.WaitForReadCountAsync(4, TimeSpan.FromSeconds(1));

        Assert.Equal([first, second], changes);
        Assert.Equal([true, true, false, true], service.IncludeCoverRequests);
    }

    private sealed class FakeMediaSessionService : IMediaSessionService
    {
        private readonly Queue<TrackInfo?> _tracks;

        public FakeMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_tracks.Count == 0 ? null : _tracks.Dequeue());
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MutableMediaSessionService(TrackInfo track) : IMediaSessionService
    {
        public TrackInfo Track { get; set; } = track;
        public List<bool> IncludeCoverRequests { get; } = new();
        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default) =>
            GetCurrentTrackAsync(MediaSessionReadOptions.Full, cancellationToken);
        public Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(options.IncludeCover);
            return Task.FromResult<TrackInfo?>(options.IncludeCover ? Track : Track with { CoverBytes = null });
        }
        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static byte[] CreateBmp(int width, int height)
    {
        int rowStride = ((width * 3) + 3) & ~3;
        int pixelBytes = checked(rowStride * height);
        byte[] bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34, 4), pixelBytes);
        return bytes;
    }

    private sealed class ThrowingMediaSessionService : IMediaSessionService
    {
        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("SMTC unavailable");
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingMediaSessionService : IMediaSessionService
    {
        public int ReadCount { get; private set; }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<TrackInfo?>(null);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReadOptionsMediaSessionService : IMediaSessionService
    {
        private readonly Queue<TrackInfo?> _tracks;

        public ReadOptionsMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public List<bool> IncludeCoverRequests { get; } = new();

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(true);
            return DequeueAsync(includeCover: true);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(options.IncludeCover);
            return DequeueAsync(options.IncludeCover);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private Task<TrackInfo?> DequeueAsync(bool includeCover)
        {
            TrackInfo? track = _tracks.Count == 0 ? null : _tracks.Dequeue();
            return Task.FromResult(track == null || includeCover ? track : track with { CoverBytes = null });
        }
    }

    private sealed class NotifyingReadOptionsMediaSessionService : IMediaSessionService, IMediaSessionChangeNotifier
    {
        private readonly Queue<TrackInfo?> _tracks;
        private TaskCompletionSource<int> _readCountChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NotifyingReadOptionsMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public event EventHandler? MediaSessionChanged;

        public List<bool> IncludeCoverRequests { get; } = new();

        public int ReadCount { get; private set; }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(true);
            return DequeueAsync(includeCover: true);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(options.IncludeCover);
            return DequeueAsync(options.IncludeCover);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RaiseMediaSessionChanged()
        {
            MediaSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task WaitForReadCountAsync(int expectedReadCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (ReadCount < expectedReadCount)
            {
                Task waitTask = _readCountChanged.Task;
                await waitTask.WaitAsync(cts.Token);
            }
        }

        private Task<TrackInfo?> DequeueAsync(bool includeCover)
        {
            ReadCount++;
            _readCountChanged.TrySetResult(ReadCount);
            _readCountChanged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackInfo? track = _tracks.Count == 0 ? null : _tracks.Dequeue();
            return Task.FromResult(track == null || includeCover ? track : track with { CoverBytes = null });
        }
    }
}
