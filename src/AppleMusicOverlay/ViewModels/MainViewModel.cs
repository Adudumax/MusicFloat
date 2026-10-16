using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService;
    private readonly OverlaySettings _settings;
    private TrackInfo? _currentTrack;
    private string _statusText = LocalizationService.Current.Text("CurrentStatusWaiting");
    private string _statusResourceKey = "CurrentStatusWaiting";
    private object[] _statusResourceArgs = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(OverlaySettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Load();
        CaptureSources.Add(MediaSessionSourceOption.Automatic);
    }

    public OverlaySettings Settings => _settings;

    public ObservableCollection<MediaSessionSourceOption> CaptureSources { get; } = new();

    public string CaptureSourceAppUserModelId
    {
        get => Settings.CaptureSourceAppUserModelId;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(
                    Settings.CaptureSourceAppUserModelId,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            Settings.CaptureSourceAppUserModelId = normalized;
            OnPropertyChanged();
        }
    }

    public TrackInfo? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            _currentTrack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentArtist));
            OnPropertyChanged(nameof(TransportButtonText));
        }
    }

    public string CurrentTitle => CurrentTrack?.Title ?? LocalizationService.Current.Text("NoTrackTitle");

    public string CurrentArtist => CurrentTrack?.Artist ?? LocalizationService.Current.Text("NoTrackArtist");

    public string TransportButtonText => CurrentTrack?.IsPlaying == true
        ? LocalizationService.Current.Text("Pause")
        : LocalizationService.Current.Text("Play");

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public void ApplyTrack(TrackInfo? track)
    {
        UpdateCaptureSourceTrack(track);
        if (HasSameVisibleTrackState(_currentTrack, track))
        {
            _currentTrack = track;
            return;
        }

        CurrentTrack = track;
        if (track == null)
        {
            SetLocalizedStatus("CurrentStatusNoPlayingMusic");
            return;
        }

        SetLocalizedStatus("CurrentStatusConnectedTemplate", track.SourceAppId);
    }

    public void Save()
    {
        _settingsService.Save(_settings);
    }

    public void SetStatus(string statusText)
    {
        _statusResourceKey = string.Empty;
        _statusResourceArgs = [];
        StatusText = statusText;
    }

    public void SetLocalizedStatus(string resourceKey, params object[] args)
    {
        _statusResourceKey = resourceKey;
        _statusResourceArgs = args;
        StatusText = args.Length == 0
            ? LocalizationService.Current.Text(resourceKey)
            : LocalizationService.Current.Format(resourceKey, args);
    }

    public void RefreshLocalizedText()
    {
        if (!string.IsNullOrWhiteSpace(_statusResourceKey))
        {
            StatusText = _statusResourceArgs.Length == 0
                ? LocalizationService.Current.Text(_statusResourceKey)
                : LocalizationService.Current.Format(_statusResourceKey, _statusResourceArgs);
        }

        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentArtist));
        OnPropertyChanged(nameof(TransportButtonText));
    }

    public void ReplaceCaptureSources(IEnumerable<MediaSessionCandidate> sessions)
    {
        string selected = CaptureSourceAppUserModelId;
        CaptureSources.Clear();
        CaptureSources.Add(MediaSessionSourceOption.Automatic);

        foreach (MediaSessionCandidate session in sessions.OrderBy(session => session.SourceAppUserModelId))
        {
            CaptureSources.Add(MediaSessionSourceOption.FromCandidate(session));
        }

        if (!string.IsNullOrWhiteSpace(selected) &&
            CaptureSources.All(source => !source.SourceAppUserModelId.Equals(selected, StringComparison.OrdinalIgnoreCase)))
        {
            CaptureSources.Add(new MediaSessionSourceOption(selected, $"{selected}（{LocalizationService.Current.Text("UndetectedSuffix")}）"));
        }

        Settings.CaptureSourceAppUserModelId = selected;
        OnPropertyChanged(nameof(CaptureSourceAppUserModelId));
        OnPropertyChanged(nameof(CaptureSources));
    }

    private void UpdateCaptureSourceTrack(TrackInfo? track)
    {
        if (track == null)
        {
            return;
        }

        foreach (MediaSessionSourceOption source in CaptureSources)
        {
            if (!source.SourceAppUserModelId.Equals(track.SourceAppId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            source.UpdateTrack(track);
            return;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static bool HasSameVisibleTrackState(TrackInfo? current, TrackInfo? next)
    {
        if (current == null || next == null)
        {
            return current == next;
        }

        return current.Title == next.Title &&
               current.Artist == next.Artist &&
               current.SourceAppId == next.SourceAppId &&
               current.IsPlaying == next.IsPlaying;
    }
}

public sealed class MediaSessionSourceOption : INotifyPropertyChanged
{
    private string _displayName;

    public MediaSessionSourceOption(string sourceAppUserModelId, string displayName)
    {
        SourceAppUserModelId = sourceAppUserModelId;
        _displayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceAppUserModelId { get; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public static MediaSessionSourceOption Automatic => new(string.Empty, LocalizationService.Current.Text("AutoSelect"));

    public static MediaSessionSourceOption FromCandidate(MediaSessionCandidate candidate)
    {
        return Create(candidate.SourceAppUserModelId, candidate.Title);
    }

    public void UpdateTrack(TrackInfo track)
    {
        if (!SourceAppUserModelId.Equals(track.SourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisplayName = CreateDisplayName(track.SourceAppId, track.Title);
    }

    private static MediaSessionSourceOption Create(string sourceAppUserModelId, string title)
    {
        return new MediaSessionSourceOption(
            sourceAppUserModelId,
            CreateDisplayName(sourceAppUserModelId, title));
    }

    private static string CreateDisplayName(string sourceAppUserModelId, string title)
    {
        string displayTitle = string.IsNullOrWhiteSpace(title)
            ? LocalizationService.Current.Text("UnknownMedia")
            : title.Trim();
        string source = SimplifySourceName(sourceAppUserModelId);
        return $"{source} · {displayTitle}";
    }

    private static string SimplifySourceName(string sourceAppUserModelId)
    {
        if (sourceAppUserModelId.Contains("edge", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Edge";
        }

        if (sourceAppUserModelId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Chrome";
        }

        return sourceAppUserModelId;
    }
}
