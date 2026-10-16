using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;
using AppleMusicOverlay.Views;

namespace AppleMusicOverlay;

public partial class MainWindow : Window, IHotkeySnapshotRegistrar
{
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";
    private const double NormalWindowFrameRadius = 20;
    private static readonly TimeSpan CurrentNavigationPromptDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan OverlayNavigationPromptDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan OverlayPositionResultPromptDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan FavoriteInProgressPromptDelay = TimeSpan.FromMilliseconds(7000);
    private static readonly TimeSpan FavoriteStarTransitionDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FavoriteOptimisticFillDelay = TimeSpan.FromMilliseconds(180);

    private enum OverlayRenderMode
    {
        Hidden,
        LayeredRuntime,
        WpfSettingsPreview,
        WpfPositionEdit
    }

    private readonly MainViewModel _viewModel;
    private readonly IMediaSessionService _mediaService;
    private readonly IMediaSessionSourceService _sourceService;
    private readonly TrackMonitor _trackMonitor;
    private readonly OverlayWindow _overlayWindow;
    private readonly LayeredOverlayWindow _layeredOverlayWindow;
    private TrackInfo? _layeredOverlayTrack;
    private readonly ShadowWindow _shadowWindow;
    private readonly InstalledFontService _installedFontService = new();
    private readonly AppleMusicFavoriteService _favoriteService = new();
    private readonly CloudMusicFavoriteStateService _cloudMusicFavoriteStateService;
    private readonly CloudMusicFavoriteCommandService _cloudMusicFavoriteCommandService;
    private readonly CloudMusicFavoriteStateMonitor _cloudMusicFavoriteStateMonitor;
    private readonly OverlayFavoriteStatusCoordinator _overlayFavoriteStatusCoordinator;
    private OverlayTrackFontAvailability _trackFontAvailability = InstalledFontService.DetectForTesting([]);
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly GamepadInputService _gamepadService;
    private readonly GamepadShortcutRuntime _gamepadShortcutRuntime = new();
    private readonly object _gamepadRuntimeLock = new();
    private GamepadBindingSet _gamepadRuntimeBindings = new();
    private readonly TrayIconService _trayIconService;
    private bool _isExiting;
    private bool _isSessionEnding;
    private bool _hasCleanedUpForExit;
    private bool _isRefreshingSources;
    private bool _isRefreshingOverlayTrackFontOptions;
    private TextBox? _capturingHotkeyBox;
    private string? _capturingHotkeyOriginalText;
    private string? _pendingHotkeyText;
    private KeyboardHotkeyConflict? _keyboardHotkeyConflict;
    private TextBox? _capturingGamepadBox;
    private AppAction? _capturingGamepadAction;
    private GamepadDeviceKind _capturingGamepadKind;
    private GamepadBindingCaptureSession? _gamepadCaptureSession;
    private readonly DispatcherTimer _hotkeyCaptureAutoHideTimer = new();
    private readonly DispatcherTimer _gamepadCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _overlaySettingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _settingsPreviewSettleTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly HashSet<Key> _pressedHotkeyKeys = new();
    private OverlayRenderMode _overlayRenderMode = OverlayRenderMode.Hidden;
    private OverlayFavoriteVisualState _layeredOverlayFavoriteState = OverlayFavoriteVisualState.Unavailable;
    private string? _pendingOverlayTrackIdentity;
    private string? _pendingCloudMusicFavoriteIdentity;
    private Task? _activeFavoriteUiRequest;
    private DateTimeOffset _lastGamepadCaptureUpdate = DateTimeOffset.Now;
    private DateTimeOffset _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
    private bool _gamepadRuntimeCaptureActive;
    private bool _isRefreshingCloudMusicFavoriteHotkeyEditor;
    private bool IsKeyboardHotkeyInputSuppressed => _capturingHotkeyBox != null || _keyboardHotkeyConflict != null;
    private LocalizationService Localizer => LocalizationService.Current;
    private string HotkeyCapturePrompt => Localizer.Text("HotkeyCapturePrompt");
    private string HotkeyUnsetText => Localizer.Text("HotkeyUnset");
    private string HotkeyEditingStatus => Localizer.Text("HotkeyEditingStatus");
    private string HotkeyNeedModifierStatus => Localizer.Text("HotkeyNeedModifier");
    private string HotkeyNeedMainKeyStatus => Localizer.Text("HotkeyNeedMainKey");

    private sealed record KeyboardHotkeyConflict(TextBox Box, AppAction TargetAction, AppAction ConflictAction, string HotkeyText);
    private sealed record HotkeyChoice(string DisplayName, uint Value)
    {
        public override string ToString() => DisplayName;
    }

    private static readonly IReadOnlyList<HotkeyChoice> CloudMusicFavoriteModifierChoices =
    [
        new("Ctrl", 0x0002),
        new("Alt", 0x0001),
        new("Shift", 0x0004),
        new("Win", 0x0008),
        new("Ctrl+Alt", 0x0003),
        new("Ctrl+Shift", 0x0006),
        new("Ctrl+Win", 0x000A),
        new("Alt+Shift", 0x0005),
        new("Alt+Win", 0x0009),
        new("Shift+Win", 0x000C),
        new("Ctrl+Alt+Shift", 0x0007),
        new("Ctrl+Alt+Win", 0x000B),
        new("Ctrl+Shift+Win", 0x000E),
        new("Alt+Shift+Win", 0x000D),
        new("Ctrl+Alt+Shift+Win", 0x000F)
    ];

    private static readonly IReadOnlyList<HotkeyChoice> CloudMusicFavoriteKeyChoices =
        CreateCloudMusicFavoriteKeyChoices();

    public MainWindow()
    {
        LogStartup("ctor: before InitializeComponent");
        InitializeComponent();
        LogStartup("ctor: after InitializeComponent");
        ApplyAppIcon();
        _viewModel = new MainViewModel(new OverlaySettingsService());
        Localizer.SetLanguage(_viewModel.Settings.LanguageCode);
        Localizer.LanguageChanged += LocalizationService_LanguageChanged;
        _trackFontAvailability = _installedFontService.Detect();
        RefreshOverlayTrackFontOptions(saveInvalidSelection: true);
        LogStartup("ctor: view model ready");
        var mediaService = new SmtcMediaSessionService();
        _mediaService = mediaService;
        _sourceService = mediaService;
        ApplyPreferredSource();
        LogStartup("ctor: media service ready");
        _trackMonitor = new TrackMonitor(_mediaService);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.SetTrackFontAvailability(_trackFontAvailability);
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _layeredOverlayWindow = new LayeredOverlayWindow(Dispatcher);
        _cloudMusicFavoriteStateService = new CloudMusicFavoriteStateService();
        _cloudMusicFavoriteCommandService = new CloudMusicFavoriteCommandService(
            _cloudMusicFavoriteStateService);
        _cloudMusicFavoriteStateMonitor = new CloudMusicFavoriteStateMonitor(_cloudMusicFavoriteStateService);
        _cloudMusicFavoriteStateMonitor.StateChanged += CloudMusicFavoriteStateMonitor_StateChanged;
        var favoriteStateReader = new CompositeOverlayFavoriteStateReader(
            new AppleMusicOverlayFavoriteStateReader(_favoriteService.ReadCurrentStatusAsync),
            _cloudMusicFavoriteStateService);
        _overlayFavoriteStatusCoordinator = new OverlayFavoriteStatusCoordinator(favoriteStateReader);
        _shadowWindow = new ShadowWindow();
        LogStartup("ctor: overlay ready");
        _hotkeyService = new GlobalHotkeyService(this);
        _gamepadService = new GamepadInputService();
        _gamepadService.DevicesChanged += GamepadService_DevicesChanged;
        _gamepadService.SelectedButtonsChanged += GamepadService_SelectedButtonsChanged;
        _trayIconService = new TrayIconService(this, () => _ = ShowCurrentTrackOverlayAsync(), () => RequestApplicationExit(), Localizer);
        LogStartup("ctor: tray ready");
        DataContext = _viewModel;
        _viewModel.RefreshLocalizedText();
        _trayIconService.UpdateText();
        LanguageCombo.ItemsSource = LocalizationService.SupportedLanguages;
        LanguageCombo.SelectedValue = _viewModel.Settings.LanguageCode;
        InitializeCloudMusicFavoriteHotkeyEditor();
        UpdateDisplaySecondsValueText();
        DisplayHotkeyBoxValues();
        DisplayGamepadBindingBoxValues();
        HotkeyNavigationCard.ExpansionCollapsed += (_, _) =>
        {
            HotkeyCaptureStatusText.Text = string.Empty;
            HotkeyCaptureActionsPanel.Visibility = Visibility.Collapsed;
        };
        _hotkeyCaptureAutoHideTimer.Tick += (_, _) =>
        {
            _hotkeyCaptureAutoHideTimer.Stop();
            HideHotkeyCapturePanel();
        };
        _gamepadCaptureTimer.Tick += (_, _) => UpdateGamepadCaptureFromCurrentButtons();
        _overlaySettingsSaveDebounceTimer.Tick += (_, _) =>
        {
            _overlaySettingsSaveDebounceTimer.Stop();
            SaveOverlaySettingsNow();
        };
        _settingsPreviewSettleTimer.Tick += (_, _) =>
        {
            _settingsPreviewSettleTimer.Stop();
            CompleteSettingsPreviewLayeredHandoff();
        };

        Activated += (_, _) =>
        {
            _shadowWindow.ApplyActiveState(true);
            SyncShadowWindow();
        };
        Deactivated += (_, _) =>
        {
            _shadowWindow.ApplyActiveState(false);
            SyncShadowWindow();
        };
        LocationChanged += (_, _) => SyncShadowWindow();
        SizeChanged += (_, _) => SyncShadowWindow();
        IsVisibleChanged += (_, _) => SyncShadowWindow();
        StateChanged += (_, _) =>
        {
            UpdateMaximizeButtonGlyph();
            ApplyWindowShellState();
            SyncShadowWindow();
        };
        SourceInitialized += (_, _) => LogStartup($"source initialized: {new WindowInteropHelper(this).Handle}");
        ContentRendered += (_, _) =>
        {
            LogStartup($"content rendered: visible={IsVisible} state={WindowState}");
            SyncShadowWindow();
        };
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        _trackMonitor.TrackRead += (_, track) =>
        {
            _cloudMusicFavoriteStateMonitor.UpdateTrack(track);
            PostToDispatcher(() => ApplyTrackRead(track));
        };
        _trackMonitor.TrackChanged += (_, track) => PostToDispatcher(() => _ = ShowOverlayTrackAsync(track));
        _trackMonitor.TrackRefreshed += (_, track) => PostToDispatcher(() => UpdateOverlayTrack(track));
        _trackMonitor.TrackUnavailable += (_, _) => PostToDispatcher(HideRuntimeOverlayForUnavailableSource);
        _trackMonitor.TrackRestored += (_, track) => PostToDispatcher(() => RestorePersistentOverlay(track));
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;
        UpdateMaximizeButtonGlyph();
        ApplyWindowShellState();
        LogStartup("ctor: complete");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            base.OnClosing(e);
            return;
        }

        FlushOverlaySettingsAutoSave();
        if (_viewModel.Settings.CloseToTray)
        {
            e.Cancel = true;
            _shadowWindow.Hide();
            Hide();
            _viewModel.SetLocalizedStatus("CurrentStatusMinimizedToTray");
            return;
        }

        base.OnClosing(e);
    }

    public void RequestApplicationExit(bool isSessionEnding = false)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _isSessionEnding = isSessionEnding;
        _trayIconService.PrepareForExit();

        if (Dispatcher.CheckAccess())
        {
            Close();
            return;
        }

        PostToDispatcher(Close);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogStartup("loaded: begin");
        await RefreshSourcesAsync();
        LogStartup("loaded: after refresh sources");
        RegisterHotkeys();
        LogStartup("loaded: after register hotkeys");
        _gamepadService.Start();
        UpdateGamepadDeviceUi();
        RefreshGamepadRuntimeBindings();
        LogStartup("loaded: after gamepad service start");
        _trackMonitor.Start();
        await _trackMonitor.PollOnceAsync();
        LogStartup("loaded: complete");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync(showNavigationFeedback: true);
        await _trackMonitor.PollOnceAsync();
    }

    private async void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync(showNavigationFeedback: true);
    }

    private async void CaptureSourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingSources)
        {
            return;
        }

        ApplyPreferredSource();
        _viewModel.Save();
        await _trackMonitor.PollOnceAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await WaitForActiveFavoriteRequestAsync();
        await _mediaService.PreviousAsync();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await WaitForActiveFavoriteRequestAsync();
        await _mediaService.NextAsync();
    }

    private async void ShowCurrentTrack_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowCurrentTrackOverlayAsync())
        {
            ShowOverlayNavigationPrompt("OverlayShownTitle", "OverlayShownMessage", CurrentNavigationPromptDelay);
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedValue is not string languageCode ||
            languageCode == _viewModel.Settings.LanguageCode)
        {
            return;
        }

        _viewModel.Settings.LanguageCode = LocalizationService.NormalizeLanguageCode(languageCode);
        Localizer.SetLanguage(_viewModel.Settings.LanguageCode);
        _viewModel.Save();
        ShowGeneralNavigationPrompt("GeneralLanguageChangedTitle", "GeneralLanguageChangedMessage", CurrentNavigationPromptDelay);
    }

    private void GeneralSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || !IsLoaded)
        {
            return;
        }

        _viewModel.Save();
        ShowGeneralNavigationPrompt("GeneralSavedTitle", "GeneralSavedMessage", OverlayNavigationPromptDelay);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        _viewModel.RefreshLocalizedText();
        UpdateDisplaySecondsValueText();
        DisplayHotkeyBoxValues();
        DisplayGamepadBindingBoxValues();
        UpdateGamepadDeviceUi();
        _trayIconService.UpdateText();
        if (LanguageCombo.SelectedValue as string != _viewModel.Settings.LanguageCode)
        {
            LanguageCombo.SelectedValue = _viewModel.Settings.LanguageCode;
        }

        RefreshOverlayTrackFontOptions(saveInvalidSelection: false);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw when the mouse state changes during window chrome hit testing.
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateHotkeyEdits(out string validationMessage))
        {
            ShowHotkeyCapturePanel(validationMessage, isConflict: true);
            return;
        }

        ApplyPendingHotkeyEdits();
        _viewModel.Save();
        ApplyPreferredSource();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        bool registered = RegisterHotkeys();
        DisplayHotkeyBoxValues();

        if (registered)
        {
            ShowHotkeyCapturePanel(Localizer.Text("HotkeySaved"), autoHide: true, autoHideMilliseconds: 2200);
        }
        else
        {
            ShowHotkeyCapturePanel(Localizer.Text("HotkeyRegisterPartialFailed"), isConflict: true);
        }
    }

    private void OverlaySettingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        UpdateDisplaySecondsValueText();
        _overlayWindow.ApplySettings(_viewModel.Settings, animateScale: false);
        if (_overlayRenderMode == OverlayRenderMode.WpfPositionEdit)
        {
            QueueOverlaySettingsAutoSave(debounce: true);
            return;
        }

        if (!ReferenceEquals(sender, DisplaySecondsSlider))
        {
            ShowWpfSettingsPreview(animateScale: false);
        }
        else if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            QueueSettingsPreviewLayeredHandoff();
        }

        QueueOverlaySettingsAutoSave(debounce: true);
    }

    private void OverlayTextOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track != null)
        {
            ShowWpfSettingsPreview(animateScale: false);
        }

        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void OverlayAutoShow_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || !IsLoaded || sender is not CheckBox autoShowToggle)
        {
            return;
        }

        _viewModel.Settings.ShowOverlayOnTrackChange = autoShowToggle.IsChecked == true;
        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void PauseOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || sender is not CheckBox pauseOverlayToggle)
        {
            return;
        }

        bool isPaused = pauseOverlayToggle.IsChecked == true;
        _viewModel.Settings.PauseOverlay = isPaused;
        _overlayWindow.ApplySettings(_viewModel.Settings, applyPauseOverlayMode: false);
        if (isPaused)
        {
            TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
            if (track != null)
            {
                _ = ShowOverlayTrackAsync(track);
            }
        }
        else
        {
            _layeredOverlayWindow.HideImmediately();
            _layeredOverlayTrack = null;
            _overlayRenderMode = OverlayRenderMode.Hidden;
            _settingsPreviewSettleTimer.Stop();
            _overlayWindow.HideOverlayVisualImmediately();
        }

        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void MouseAutoHide_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || sender is not CheckBox autoHideToggle)
        {
            return;
        }

        _viewModel.Settings.AutoHideOnMouseNear = autoHideToggle.IsChecked == true;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            QueueSettingsPreviewLayeredHandoff();
        }
        else
        {
            RefreshLayeredOverlaySnapshot();
        }

        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void OverlayTrackFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_overlayWindow == null || _isRefreshingOverlayTrackFontOptions)
        {
            return;
        }

        string selectedFont = OverlayTrackFontCombo.SelectedValue as string ?? OverlayTrackFontIds.Default;
        string normalizedFont = _trackFontAvailability.NormalizeSelection(selectedFont);
        _viewModel.Settings.OverlayTrackFont = normalizedFont;
        if (!Equals(OverlayTrackFontCombo.SelectedValue, normalizedFont))
        {
            OverlayTrackFontCombo.SelectedValue = normalizedFont;
        }

        _overlayWindow.ApplyTrackFontSetting(_viewModel.Settings);
        ShowWpfSettingsPreview(animateScale: false);
        QueueOverlaySettingsAutoSave(debounce: false);
        ShowOverlayNavigationPrompt("OverlayFontUpdatedTitle", "OverlayFontUpdatedMessage", OverlayNavigationPromptDelay);
    }

    private void RefreshOverlayTrackFontOptions(bool saveInvalidSelection)
    {
        if (OverlayTrackFontCombo == null)
        {
            return;
        }

        string selectedFont = _trackFontAvailability.NormalizeSelection(_viewModel.Settings.OverlayTrackFont);
        bool correctedSelection = !selectedFont.Equals(_viewModel.Settings.OverlayTrackFont, StringComparison.Ordinal);
        if (correctedSelection)
        {
            _viewModel.Settings.OverlayTrackFont = selectedFont;
        }

        _isRefreshingOverlayTrackFontOptions = true;
        try
        {
            OverlayTrackFontCombo.ItemsSource = _trackFontAvailability.CreateOptions();
            OverlayTrackFontCombo.SelectedValue = selectedFont;
        }
        finally
        {
            _isRefreshingOverlayTrackFontOptions = false;
        }

        if (correctedSelection && saveInvalidSelection)
        {
            _viewModel.Save();
        }
    }

    private void PositionOverlay_Click(object sender, RoutedEventArgs e)
    {
        ShowPersistentOverlayNavigationPrompt("OverlayAdjustingTitle", "OverlayAdjustingMessage");
        _settingsPreviewSettleTimer.Stop();
        _overlayRenderMode = OverlayRenderMode.WpfPositionEdit;
        _overlayWindow.BeginPositionEdit(_viewModel.Settings, SaveOverlayPositionSettingsNow, HandleOverlayPositionEditCompleted);
        _layeredOverlayWindow.HideImmediately();
        _layeredOverlayTrack = null;
    }

    private void RefreshGamepads_Click(object sender, RoutedEventArgs e)
    {
        _gamepadService.RefreshDevices();
    }

    private void GamepadDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GamepadDeviceSelector.SelectedItem is GamepadDeviceInfo device)
        {
            if (_capturingGamepadBox != null && _gamepadService.SelectedRuntimeId != device.RuntimeId)
            {
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
            }

            _gamepadService.SelectDevice(device.RuntimeId);
            RefreshGamepadRuntimeBindings();
        }
    }

    private void GamepadService_DevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateGamepadDeviceUi();
            if (_capturingGamepadBox != null && _gamepadService.SelectedDevice == null)
            {
                HandleGamepadCaptureDeviceDisconnected();
            }

            if (_gamepadService.SelectedDevice == null)
            {
                ResetGamepadRuntime();
            }
            else
            {
                RefreshGamepadRuntimeBindings();
            }
        });
    }

    private void GamepadService_SelectedButtonsChanged(object? sender, GamepadButtonsChangedEventArgs e)
    {
        HandleGamepadShortcutButtons(e.Buttons);
        _ = Dispatcher.InvokeAsync(UpdateGamepadCaptureFromCurrentButtons, DispatcherPriority.Input);
    }

    private void UpdateGamepadDeviceUi()
    {
        IReadOnlyList<GamepadDeviceInfo> devices = _gamepadService.Devices;
        GamepadDeviceInfo? selected = _gamepadService.SelectedDevice ?? devices.FirstOrDefault();
        bool hasDevice = selected != null;
        bool hasMultipleDevices = devices.Count > 1;

        GamepadStatusTitleText.Text = hasDevice
            ? hasMultipleDevices ? Localizer.Text("ConnectedMultipleGamepads") : selected!.StatusText
            : Localizer.Text("GamepadNotDetected");
        GamepadStatusDescriptionText.Text = hasDevice
            ? Localizer.Format("CurrentDeviceTemplate", selected!.DisplayName)
            : Localizer.Text("GamepadNotDetectedDescription");

        GamepadDeviceSelector.ItemsSource = devices;
        GamepadDeviceSelector.SelectedItem = selected;
        GamepadDeviceSelector.Visibility = hasMultipleDevices ? Visibility.Visible : Visibility.Collapsed;
        DisplayGamepadBindingBoxValues();
        AnimateGamepadConnectionState(hasDevice);
    }

    private void AnimateGamepadConnectionState(bool connected)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Duration duration = TimeSpan.FromMilliseconds(connected ? 180 : 140);
        GamepadConnectedDot.BeginAnimation(OpacityProperty, new DoubleAnimation(connected ? 1 : 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);

        if (connected && GamepadBindingsHost.Visibility != Visibility.Visible)
        {
            GamepadBindingsHost.Visibility = Visibility.Visible;
        }

        GamepadBindingsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(connected ? 1 : 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        GamepadBindingsHostScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(connected ? 1 : 0.96, duration) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);

        if (!connected)
        {
            var collapseAnimation = new DoubleAnimation(GamepadBindingsHost.Opacity, 0, duration) { EasingFunction = ease };
            collapseAnimation.Completed += (_, _) =>
            {
                if (_gamepadService.Devices.Count == 0)
                {
                    GamepadBindingsHost.Visibility = Visibility.Collapsed;
                }
            };
            GamepadBindingsHost.BeginAnimation(OpacityProperty, collapseAnimation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void GamepadBindingBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || !TryGetAppAction(box.Tag as string, out AppAction action))
        {
            return;
        }

        e.Handled = true;
        BeginGamepadBindingCapture(box, action);
        box.Focusable = true;
        box.Focus();
    }

    private void GamepadBindingBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingGamepadBox == null)
        {
            return;
        }

        if (NormalizeKey(e) == Key.Escape)
        {
            e.Handled = true;
            CancelGamepadCapture(hidePanel: true, restoreBox: true);
            Keyboard.ClearFocus();
        }
    }

    private void ClearGamepadBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetAppAction(button.Tag as string, out AppAction action))
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        bindings.Clear(action);
        SaveGamepadBindings();
        DisplayGamepadBindingBoxValues();
    }

    private void BeginGamepadBindingCapture(TextBox box, AppAction action)
    {
        if (_capturingHotkeyBox != null)
        {
            RestoreHotkeyBox(_capturingHotkeyBox);
            ClearHotkeyCapture(hidePanel: false);
        }

        if (_capturingGamepadBox != null && !ReferenceEquals(_capturingGamepadBox, box))
        {
            CancelGamepadCapture(hidePanel: false, restoreBox: true);
        }

        GamepadDeviceInfo? selected = _gamepadService.SelectedDevice;
        _capturingGamepadBox = box;
        _capturingGamepadAction = action;
        _capturingGamepadKind = selected?.Kind ?? GamepadDeviceKind.Compatible;
        SetGamepadRuntimeCaptureActive(true);
        box.Focusable = true;
        box.Text = Localizer.Text("Listening");
        ShowHotkeyCapturePanel(
            Localizer.Text("GamepadListenInstruction"),
            title: Localizer.Text("GamepadHotkeyEditTitle"));

        if (selected == null || !selected.HasStandardGamepad)
        {
            _gamepadCaptureSession = null;
            ShowHotkeyCapturePanel(
                selected == null ? Localizer.Text("GamepadNoAvailableDevice") : Localizer.Text("GamepadUnsupportedDevice"),
                isConflict: true,
                title: Localizer.Text("GamepadHotkeyEditTitle"));
            return;
        }

        _gamepadCaptureSession = new GamepadBindingCaptureSession(_capturingGamepadKind, action, GetCurrentGamepadBindings());
        _lastGamepadCaptureUpdate = DateTimeOffset.Now;
        _gamepadCaptureTimer.Stop();
        _gamepadCaptureTimer.Start();
        UpdateGamepadCaptureFromCurrentButtons();
    }

    private void UpdateGamepadCaptureFromCurrentButtons()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan elapsed = now - _lastGamepadCaptureUpdate;
        _lastGamepadCaptureUpdate = now;
        _gamepadCaptureSession.Update(_gamepadService.CurrentSelectedButtons, elapsed);
        RenderGamepadCaptureState();
    }

    private void HandleGamepadShortcutButtons(IReadOnlySet<GamepadButton> buttons)
    {
        AppAction? action;
        lock (_gamepadRuntimeLock)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan elapsed = now - _lastGamepadRuntimeUpdate;
            _lastGamepadRuntimeUpdate = now;

            action = _gamepadShortcutRuntime.Update(
                buttons,
                elapsed,
                _gamepadRuntimeBindings,
                isCaptureActive: _gamepadRuntimeCaptureActive);
        }

        if (action != null)
        {
            _ = Dispatcher.InvokeAsync(
                () => _ = ExecuteAppActionAsync(action.Value),
                DispatcherPriority.Normal);
        }
    }

    private void RenderGamepadCaptureState()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        string detectedText = _gamepadCaptureSession.DisplayText;
        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.WaitingForNeutral:
                _capturingGamepadBox.Text = Localizer.Text("Listening");
                ShowHotkeyCapturePanel(Localizer.Text("GamepadReleaseAllButtons"), title: Localizer.Text("GamepadHotkeyEditTitle"));
                break;
            case GamepadCaptureState.Listening:
                _capturingGamepadBox.Text = Localizer.Text("Listening");
                ShowHotkeyCapturePanel(Localizer.Text("GamepadListenInstruction"), title: Localizer.Text("GamepadHotkeyEditTitle"));
                break;
            case GamepadCaptureState.Capturing:
                _capturingGamepadBox.Text = string.IsNullOrWhiteSpace(detectedText) ? Localizer.Text("Listening") : detectedText;
                ShowHotkeyCapturePanel(Localizer.Format("GamepadDetectedTemplate", detectedText), title: Localizer.Text("GamepadHotkeyEditTitle"));
                break;
            case GamepadCaptureState.TooManyButtons:
                _capturingGamepadBox.Text = Localizer.Text("ClickToBind");
                ShowHotkeyCapturePanel(
                    Localizer.Text("GamepadTooManyButtons"),
                    isConflict: true,
                    title: Localizer.Text("GamepadHotkeyEditTitle"),
                    primaryAction: Localizer.Text("Retry"),
                    secondaryAction: Localizer.Text("Cancel"));
                break;
            case GamepadCaptureState.SingleButtonWarning:
                _capturingGamepadBox.Text = detectedText;
                ShowHotkeyCapturePanel(
                    Localizer.Text("GamepadSingleButtonWarning"),
                    isConflict: true,
                    title: Localizer.Text("GamepadHotkeyEditTitle"),
                    primaryAction: Localizer.Text("UseAnyway"),
                    secondaryAction: Localizer.Text("Retry"));
                break;
            case GamepadCaptureState.Conflict:
                _capturingGamepadBox.Text = detectedText;
                ShowHotkeyCapturePanel(
                    _gamepadCaptureSession.Message,
                    isConflict: true,
                    title: Localizer.Text("GamepadHotkeyEditTitle"),
                    primaryAction: Localizer.Text("ReplaceOriginalBinding"),
                    secondaryAction: Localizer.Text("Cancel"));
                break;
            case GamepadCaptureState.Completed:
                CompleteGamepadCaptureAndSave();
                break;
            case GamepadCaptureState.DeviceDisconnected:
                ShowHotkeyCapturePanel(Localizer.Text("GamepadDisconnectedCapture"), isConflict: true, title: Localizer.Text("GamepadHotkeyEditTitle"));
                _gamepadCaptureTimer.Stop();
                break;
            case GamepadCaptureState.Cancelled:
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
                break;
        }
    }

    private void CompleteGamepadCaptureAndSave()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        _gamepadCaptureSession.ApplyCompleted(bindings);
        SaveGamepadBindings();
        DisplayGamepadBindingBoxValues();
        string savedText = _gamepadCaptureSession.PendingBinding == null
            ? string.Empty
            : GamepadBindingFormatter.Format(_gamepadCaptureSession.PendingBinding, _capturingGamepadKind);
        ShowHotkeyCapturePanel(Localizer.Format("AutoSavedTemplate", savedText), autoHide: true, autoHideMilliseconds: 650, title: Localizer.Text("GamepadHotkeyEditTitle"));
        ClearGamepadCaptureState(restoreBox: false);
    }

    private void GamepadCapturePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_keyboardHotkeyConflict != null)
        {
            HandleKeyboardHotkeyConflictPrimary();
            return;
        }

        if (_gamepadCaptureSession == null)
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.SingleButtonWarning:
                _gamepadCaptureSession.ConfirmSingleButton();
                RenderGamepadCaptureState();
                break;
            case GamepadCaptureState.Conflict:
                _gamepadCaptureSession.ReplaceConflict(bindings);
                SaveGamepadBindings();
                DisplayGamepadBindingBoxValues();
                string savedText = _gamepadCaptureSession.PendingBinding == null
                    ? string.Empty
                    : GamepadBindingFormatter.Format(_gamepadCaptureSession.PendingBinding, _capturingGamepadKind);
                ShowHotkeyCapturePanel(Localizer.Format("AutoSavedTemplate", savedText), autoHide: true, autoHideMilliseconds: 650, title: Localizer.Text("GamepadHotkeyEditTitle"));
                ClearGamepadCaptureState(restoreBox: false);
                break;
            case GamepadCaptureState.TooManyButtons:
                _gamepadCaptureSession.Retry();
                _lastGamepadCaptureUpdate = DateTimeOffset.Now;
                _gamepadCaptureTimer.Start();
                RenderGamepadCaptureState();
                break;
        }
    }

    private void GamepadCaptureSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_keyboardHotkeyConflict != null)
        {
            HandleKeyboardHotkeyConflictSecondary();
            return;
        }

        if (_gamepadCaptureSession == null)
        {
            return;
        }

        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.SingleButtonWarning:
                _gamepadCaptureSession.Retry();
                _lastGamepadCaptureUpdate = DateTimeOffset.Now;
                _gamepadCaptureTimer.Start();
                RenderGamepadCaptureState();
                break;
            default:
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
                break;
        }
    }

    private void HandleKeyboardHotkeyConflictPrimary()
    {
        if (_keyboardHotkeyConflict == null)
        {
            return;
        }

        KeyboardHotkeyConflict conflict = _keyboardHotkeyConflict;
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.ApplyReplacingConflict(
            _viewModel.Settings,
            conflict.TargetAction,
            conflict.ConflictAction,
            conflict.HotkeyText,
            this);

        if (!result.Success)
        {
            ShowHotkeyCapturePanel(result.Message, isConflict: true, title: Localizer.Text("KeyboardHotkeyEditTitle"));
            DisplayHotkeyBoxValues();
            conflict.Box.Text = HotkeyCapturePrompt;
            _pendingHotkeyText = null;
            _keyboardHotkeyConflict = null;
            return;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        conflict.Box.Text = DisplayHotkeyText(GetHotkeySetting(conflict.Box.Tag as string));
        _capturingHotkeyOriginalText = conflict.Box.Text;
        _pendingHotkeyText = null;
        _keyboardHotkeyConflict = null;
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650, title: Localizer.Text("KeyboardHotkeyEditTitle"));
        ClearHotkeyCapture(hidePanel: false);
        Keyboard.ClearFocus();
    }

    private void HandleKeyboardHotkeyConflictSecondary()
    {
        _keyboardHotkeyConflict = null;
        DisplayHotkeyBoxValues();
        ClearHotkeyCapture();
        Keyboard.ClearFocus();
    }

    private void HandleGamepadCaptureDeviceDisconnected()
    {
        if (_gamepadCaptureSession == null)
        {
            return;
        }

        _gamepadCaptureSession.Disconnect();
        RenderGamepadCaptureState();
    }

    private void CancelGamepadCapture(bool hidePanel, bool restoreBox)
    {
        if (_gamepadCaptureSession != null)
        {
            _gamepadCaptureSession.Cancel();
        }

        ClearGamepadCaptureState(restoreBox);
        if (restoreBox)
        {
            DisplayGamepadBindingBoxValues();
        }

        if (hidePanel)
        {
            HideHotkeyCapturePanel();
        }
    }

    private void ClearGamepadCaptureState(bool restoreBox)
    {
        TextBox? previousBox = _capturingGamepadBox;
        if (restoreBox && previousBox != null)
        {
            previousBox.Text = DisplayGamepadBindingText(_capturingGamepadAction);
        }

        if (previousBox != null)
        {
            previousBox.Focusable = false;
        }

        _capturingGamepadBox = null;
        _capturingGamepadAction = null;
        _gamepadCaptureSession = null;
        _gamepadCaptureTimer.Stop();
        ResumeGamepadRuntimeAfterCapture(_gamepadService.CurrentSelectedButtons);
    }

    private void DisplayGamepadBindingBoxValues()
    {
        if (GamepadPreviousBox == null)
        {
            return;
        }

        GamepadPreviousBox.Text = DisplayGamepadBindingText(AppAction.PreviousTrack);
        GamepadNextBox.Text = DisplayGamepadBindingText(AppAction.NextTrack);
        GamepadToggleBox.Text = DisplayGamepadBindingText(AppAction.TogglePlayPause);
        GamepadShowCurrentBox.Text = DisplayGamepadBindingText(AppAction.ShowCurrentTrack);
        GamepadFavoriteBox.Text = DisplayGamepadBindingText(AppAction.FavoriteCurrentTrack);
    }

    private string DisplayGamepadBindingText(AppAction? action)
    {
        if (action == null)
        {
            return Localizer.Text("ClickToBind");
        }

        GamepadBinding binding = GetCurrentGamepadBindings().GetBinding(action.Value);
        return binding.IsEmpty ? Localizer.Text("ClickToBind") : GamepadBindingFormatter.Format(binding, GetCurrentGamepadKind());
    }

    private GamepadBindingSet GetCurrentGamepadBindings()
    {
        return GetGamepadBindingsForKind(GetCurrentGamepadKind());
    }

    private GamepadBindingSet GetGamepadBindingsForKind(GamepadDeviceKind kind)
    {
        return kind switch
        {
            GamepadDeviceKind.Xbox => _viewModel.Settings.XboxGamepadBindings,
            GamepadDeviceKind.DualSense => _viewModel.Settings.DualSenseGamepadBindings,
            _ => _viewModel.Settings.CompatibleGamepadBindings
        };
    }

    private GamepadDeviceKind GetCurrentGamepadKind()
    {
        return _gamepadService.SelectedDevice?.Kind ?? _capturingGamepadKind;
    }

    private void SaveGamepadBindings()
    {
        _viewModel.Save();
        RefreshGamepadRuntimeBindings();
    }

    private void RefreshGamepadRuntimeBindings()
    {
        GamepadBindingSet bindings = _gamepadService.SelectedDevice is { } device
            ? CloneGamepadBindings(GetGamepadBindingsForKind(device.Kind))
            : new GamepadBindingSet();

        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeBindings = bindings;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void ResetGamepadRuntime()
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeBindings = new GamepadBindingSet();
            _gamepadRuntimeCaptureActive = false;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void SetGamepadRuntimeCaptureActive(bool isActive)
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeCaptureActive = isActive;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void ResumeGamepadRuntimeAfterCapture(IReadOnlySet<GamepadButton> currentButtons)
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeCaptureActive = false;
            _gamepadShortcutRuntime.ResumeAfterCapture(currentButtons);
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private static GamepadBindingSet CloneGamepadBindings(GamepadBindingSet source)
    {
        return new GamepadBindingSet
        {
            Previous = CloneGamepadBinding(source.Previous),
            Next = CloneGamepadBinding(source.Next),
            Toggle = CloneGamepadBinding(source.Toggle),
            ShowCurrent = CloneGamepadBinding(source.ShowCurrent),
            Favorite = CloneGamepadBinding(source.Favorite)
        };
    }

    private static GamepadBinding CloneGamepadBinding(GamepadBinding source)
    {
        return GamepadBinding.FromButtons(source.Buttons);
    }

    private static bool TryGetAppAction(string? value, out AppAction action)
    {
        return Enum.TryParse(value, ignoreCase: false, out action) && GamepadBindingActions.SupportedActions.Contains(action);
    }

    private static bool TryGetKeyboardAction(string? key, out AppAction action)
    {
        switch (key)
        {
            case "KeyboardPrevious":
                action = AppAction.PreviousTrack;
                return true;
            case "KeyboardNext":
                action = AppAction.NextTrack;
                return true;
            case "KeyboardToggle":
                action = AppAction.TogglePlayPause;
                return true;
            case "KeyboardTestOverlay":
                action = AppAction.ShowCurrentTrack;
                return true;
            case "KeyboardFavorite":
                action = AppAction.FavoriteCurrentTrack;
                return true;
            default:
                action = default;
                return false;
        }
    }

    private void QueueOverlaySettingsAutoSave(bool debounce)
    {
        if (debounce)
        {
            _overlaySettingsSaveDebounceTimer.Stop();
            _overlaySettingsSaveDebounceTimer.Start();
            return;
        }

        _overlaySettingsSaveDebounceTimer.Stop();
        _ = Dispatcher.InvokeAsync(() => SaveOverlaySettingsNow(), DispatcherPriority.Background);
    }

    private void FlushOverlaySettingsAutoSave()
    {
        if (!_overlaySettingsSaveDebounceTimer.IsEnabled)
        {
            return;
        }

        _overlaySettingsSaveDebounceTimer.Stop();
        SaveOverlaySettingsNow(showNavigationFeedback: false);
    }

    private void SaveOverlaySettingsNow(bool showNavigationFeedback = true)
    {
        _viewModel.Save();
        if (showNavigationFeedback)
        {
            ShowOverlayNavigationPrompt("OverlaySavedTitle", "OverlaySavedMessage", OverlayNavigationPromptDelay);
        }
    }

    private void SaveOverlayPositionSettingsNow()
    {
        SaveOverlaySettingsNow(showNavigationFeedback: false);
    }

    private void UpdateDisplaySecondsValueText()
    {
        if (DisplaySecondsValueText != null && DisplaySecondsSlider != null)
        {
            DisplaySecondsValueText.Text = Localizer.Format("SecondsSuffix", DisplaySecondsSlider.Value);
        }
    }

    private void ShowCurrentNavigationPrompt(string titleKey, string messageKey, TimeSpan autoCollapseDelay)
    {
        ClearInactiveTransientNavigationPrompts(CurrentNavigationCard);
        CurrentNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay);
    }

    private void ShowOverlayNavigationPrompt(string titleKey, string messageKey, TimeSpan autoCollapseDelay)
    {
        ClearInactiveTransientNavigationPrompts(OverlayNavigationCard);
        OverlayNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay);
    }

    private void ShowPersistentOverlayNavigationPrompt(string titleKey, string messageKey)
    {
        ClearInactiveTransientNavigationPrompts(OverlayNavigationCard);
        OverlayNavigationCard.ShowPersistentPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey));
    }

    private void ShowGeneralNavigationPrompt(string titleKey, string messageKey, TimeSpan autoCollapseDelay)
    {
        ClearInactiveTransientNavigationPrompts(GeneralNavigationCard);
        GeneralNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay);
    }

    private void ClearInactiveTransientNavigationPrompts(ExpandableNavigationCard activeCard)
    {
        if (!ReferenceEquals(activeCard, CurrentNavigationCard) && !CurrentNavigationCard.IsPromptPersistent)
        {
            CurrentNavigationCard.ClearPrompt();
        }

        if (!ReferenceEquals(activeCard, OverlayNavigationCard) && !OverlayNavigationCard.IsPromptPersistent)
        {
            OverlayNavigationCard.ClearPrompt();
        }

        if (!ReferenceEquals(activeCard, GeneralNavigationCard) && !GeneralNavigationCard.IsPromptPersistent)
        {
            GeneralNavigationCard.ClearPrompt();
        }
    }

    private void ShowCurrentRefreshResult(int sessionCount)
    {
        if (sessionCount > 0)
        {
            ShowCurrentNavigationPrompt("CurrentRefreshCompleteTitle", "CurrentRefreshCompleteMessage", CurrentNavigationPromptDelay);
            return;
        }

        ShowCurrentNavigationPrompt("CurrentNoMusicTitle", "CurrentNoMusicMessage", CurrentNavigationPromptDelay);
    }

    private void HandleOverlayPositionEditCompleted(OverlayPositionEditResult result)
    {
        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        RefreshLayeredOverlaySnapshot();

        if (result == OverlayPositionEditResult.Saved)
        {
            ShowOverlayNavigationPrompt("OverlayPositionSavedTitle", "OverlayPositionSavedMessage", OverlayPositionResultPromptDelay);
            return;
        }

        ShowOverlayNavigationPrompt("OverlayPositionCancelledTitle", "OverlayPositionCancelledMessage", OverlayPositionResultPromptDelay);
    }

    private void DeleteHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        TextBox? box = GetHotkeyBox(button.Tag as string);
        if (box == null)
        {
            return;
        }

        if (ReferenceEquals(box, _capturingHotkeyBox))
        {
            ClearHotkeyCapture();
        }

        if (!TryGetKeyboardAction(button.Tag as string, out AppAction action))
        {
            return;
        }

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_viewModel.Settings, action, string.Empty, this);
        if (!result.Success)
        {
            ShowHotkeyCapturePanel(result.Message, isConflict: true);
            DisplayHotkeyBoxValues();
            return;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650);
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        if (!ReferenceEquals(_capturingHotkeyBox, box))
        {
            if (_capturingHotkeyBox != null)
            {
                RestoreHotkeyBox(_capturingHotkeyBox);
                ClearHotkeyCapture();
            }

            BeginHotkeyCapture(box);
        }

        box.Focusable = true;
        box.Focus();
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_keyboardHotkeyConflict != null)
        {
            return;
        }

        if (sender is TextBox box && ReferenceEquals(box, _capturingHotkeyBox))
        {
            RestoreHotkeyBox(box);
            ClearHotkeyCapture();
        }
    }

    private void CloudMusicNativeFavoriteHotkeyBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isExiting || _isRefreshingCloudMusicFavoriteHotkeyEditor ||
            CloudMusicNativeFavoriteModifierBox.SelectedItem is not HotkeyChoice modifiers ||
            CloudMusicNativeFavoriteKeyBox.SelectedItem is not HotkeyChoice mainKey)
        {
            return;
        }

        TrySaveCloudMusicNativeFavoriteHotkey(
            $"{modifiers.DisplayName}+{mainKey.DisplayName}");
    }

    private bool TrySaveCloudMusicNativeFavoriteHotkey(string candidateText)
    {
        string candidate = NormalizeCloudMusicNativeHotkeyText(
            candidateText);
        string current = _viewModel.Settings.CloudMusicFavoriteHotkey;
        if (candidate.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            DisplayCloudMusicFavoriteHotkeyEditorValue();
            return true;
        }

        if (!HotkeyParser.TryParse(candidate, out HotkeyDefinition nativeHotkey))
        {
            DisplayCloudMusicFavoriteHotkeyEditorValue();
            ShowHotkeyCapturePanel(
                Localizer.Text("CloudMusicNativeFavoriteHotkeyInvalid"),
                isConflict: true,
                title: Localizer.Text("CloudMusicNativeFavoriteHotkey"));
            return false;
        }

        foreach ((AppAction action, string hotkeyText) in
                 KeyboardHotkeyBindingManager.CreateSnapshot(_viewModel.Settings))
        {
            if (!HotkeyParser.TryParse(hotkeyText, out HotkeyDefinition musicFloatHotkey) ||
                musicFloatHotkey != nativeHotkey)
            {
                continue;
            }

            DisplayCloudMusicFavoriteHotkeyEditorValue();
            ShowHotkeyCapturePanel(
                Localizer.Format(
                    "CloudMusicNativeFavoriteHotkeyConflictTemplate",
                    GamepadBindingActions.GetLabel(action)),
                isConflict: true,
                title: Localizer.Text("CloudMusicNativeFavoriteHotkey"));
            return false;
        }

        _viewModel.Settings.CloudMusicFavoriteHotkey = candidate;
        _viewModel.Save();
        DisplayCloudMusicFavoriteHotkeyEditorValue();
        ShowHotkeyCapturePanel(
            Localizer.Format(
                "CloudMusicNativeFavoriteHotkeySavedTemplate",
                candidate),
            autoHide: true,
            autoHideMilliseconds: 1200,
            title: Localizer.Text("CloudMusicNativeFavoriteHotkey"));
        return true;
    }

    private void InitializeCloudMusicFavoriteHotkeyEditor()
    {
        _isRefreshingCloudMusicFavoriteHotkeyEditor = true;
        try
        {
            CloudMusicNativeFavoriteModifierBox.ItemsSource =
                CloudMusicFavoriteModifierChoices;
            CloudMusicNativeFavoriteKeyBox.ItemsSource =
                CloudMusicFavoriteKeyChoices;
        }
        finally
        {
            _isRefreshingCloudMusicFavoriteHotkeyEditor = false;
        }
    }

    private void DisplayCloudMusicFavoriteHotkeyEditorValue()
    {
        if (!HotkeyParser.TryParse(
                _viewModel.Settings.CloudMusicFavoriteHotkey,
                out HotkeyDefinition hotkey))
        {
            return;
        }

        _isRefreshingCloudMusicFavoriteHotkeyEditor = true;
        try
        {
            CloudMusicNativeFavoriteModifierBox.SelectedItem =
                CloudMusicFavoriteModifierChoices.FirstOrDefault(
                    choice => choice.Value == hotkey.Modifiers);
            CloudMusicNativeFavoriteKeyBox.SelectedItem =
                CloudMusicFavoriteKeyChoices.FirstOrDefault(
                    choice => choice.Value == hotkey.VirtualKey);
        }
        finally
        {
            _isRefreshingCloudMusicFavoriteHotkeyEditor = false;
        }
    }

    private static IReadOnlyList<HotkeyChoice> CreateCloudMusicFavoriteKeyChoices()
    {
        var choices = new List<HotkeyChoice>();
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            choices.Add(new HotkeyChoice(letter.ToString(), letter));
        }

        for (char digit = '0'; digit <= '9'; digit++)
        {
            choices.Add(new HotkeyChoice(digit.ToString(), digit));
        }

        for (int functionKey = 1; functionKey <= 24; functionKey++)
        {
            choices.Add(new HotkeyChoice(
                $"F{functionKey}",
                (uint)(0x70 + functionKey - 1)));
        }

        choices.AddRange(
        [
            new HotkeyChoice("Space", 0x20),
            new HotkeyChoice("Enter", 0x0D),
            new HotkeyChoice("Esc", 0x1B),
            new HotkeyChoice("Left", 0x25),
            new HotkeyChoice("Up", 0x26),
            new HotkeyChoice("Right", 0x27),
            new HotkeyChoice("Down", 0x28),
            new HotkeyChoice("PageUp", 0x21),
            new HotkeyChoice("PageDown", 0x22),
            new HotkeyChoice("Home", 0x24),
            new HotkeyChoice("End", 0x23),
            new HotkeyChoice("Insert", 0x2D),
            new HotkeyChoice("Delete", 0x2E)
        ]);

        return choices;
    }

    private static string NormalizeCloudMusicNativeHotkeyText(string? text)
    {
        return string.Join(
            "+",
            (text ?? string.Empty).Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        Key key = NormalizeKey(e);
        if (key == Key.Escape)
        {
            if (_keyboardHotkeyConflict != null)
            {
                DisplayHotkeyBoxValues();
            }
            else
            {
                RestoreHotkeyBox(box);
            }

            ClearHotkeyCapture();
            Keyboard.ClearFocus();
            return;
        }

        if (_keyboardHotkeyConflict != null)
        {
            return;
        }

        if (key == Key.None)
        {
            return;
        }

        if (e.IsRepeat && _pressedHotkeyKeys.Contains(key))
        {
            return;
        }

        _pressedHotkeyKeys.Add(key);
        UpdateHotkeyCapture(box);
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        if (_keyboardHotkeyConflict != null)
        {
            return;
        }

        Key key = NormalizeKey(e);
        if (key != Key.None)
        {
            _pressedHotkeyKeys.Remove(key);
        }

        if (_pressedHotkeyKeys.Count == 0)
        {
            if (TryCommitPendingHotkey(box))
            {
                ClearHotkeyCapture(hidePanel: false);
                Keyboard.ClearFocus();
            }
            else if (_keyboardHotkeyConflict == null)
            {
                box.Text = HotkeyCapturePrompt;
            }
            return;
        }

        UpdateHotkeyCapture(box);
    }

    private void BeginHotkeyCapture(TextBox box)
    {
        if (_capturingGamepadBox != null)
        {
            CancelGamepadCapture(hidePanel: false, restoreBox: true);
        }

        if (_capturingHotkeyBox != null && !ReferenceEquals(_capturingHotkeyBox, box))
        {
            RestoreHotkeyBox(_capturingHotkeyBox);
            ClearHotkeyCapture();
        }

        _capturingHotkeyBox = box;
        _capturingHotkeyOriginalText = box.Text;
        _pendingHotkeyText = null;
        _keyboardHotkeyConflict = null;
        _pressedHotkeyKeys.Clear();
        box.Focusable = true;
        box.Text = HotkeyCapturePrompt;
        box.SelectAll();
        ShowHotkeyCapturePanel(HotkeyEditingStatus);
    }

    private void RestoreHotkeyBox(TextBox box)
    {
        if (box.Text == HotkeyCapturePrompt || box.Text == Localizer.Text("HotkeyPressCombination") || _pressedHotkeyKeys.Count > 0)
        {
            box.Text = GetHotkeySetting(box.Tag as string);
        }
    }

    private void ClearHotkeyCapture(bool hidePanel = true)
    {
        TextBox? previousBox = _capturingHotkeyBox;
        _capturingHotkeyBox = null;
        _capturingHotkeyOriginalText = null;
        _pendingHotkeyText = null;
        _keyboardHotkeyConflict = null;
        _pressedHotkeyKeys.Clear();
        if (previousBox != null)
        {
            previousBox.Focusable = false;
        }

        if (hidePanel)
        {
            HideHotkeyCapturePanel();
        }

        RegisterHotkeys();
    }

    private void UpdateHotkeyCapture(TextBox box)
    {
        string pressedText = FormatPressedKeys(_pressedHotkeyKeys);
        box.Text = string.IsNullOrWhiteSpace(pressedText) ? HotkeyCapturePrompt : pressedText;

        if (TryCreateHotkeyText(_pressedHotkeyKeys, out string hotkeyText))
        {
            _pendingHotkeyText = hotkeyText;
            ShowHotkeyCapturePanel(Localizer.Format("HotkeyDetectedTemplate", hotkeyText));
            return;
        }

        ShowHotkeyCapturePanel(_pressedHotkeyKeys.Any(IsModifierKey)
            ? (_pendingHotkeyText == null ? HotkeyNeedMainKeyStatus : Localizer.Format("HotkeyReleaseToSaveTemplate", _pendingHotkeyText))
            : HotkeyNeedModifierStatus);
    }

    private bool TryCommitPendingHotkey(TextBox box)
    {
        if (string.IsNullOrWhiteSpace(_pendingHotkeyText))
        {
            ShowHotkeyCapturePanel(HotkeyNeedModifierStatus);
            return false;
        }

        box.Text = _pendingHotkeyText;
        if (!TryGetKeyboardAction(box.Tag as string, out AppAction action))
        {
            return false;
        }

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_viewModel.Settings, action, _pendingHotkeyText, this);
        if (!result.Success)
        {
            if (result.FailureKind == HotkeyApplyFailureKind.InternalConflict && result.ConflictAction != null)
            {
                _keyboardHotkeyConflict = new KeyboardHotkeyConflict(box, action, result.ConflictAction.Value, result.HotkeyText);
                ShowHotkeyCapturePanel(
                    result.Message,
                    isConflict: true,
                    title: Localizer.Text("KeyboardHotkeyEditTitle"),
                    primaryAction: Localizer.Text("ReplaceOriginalBinding"),
                    secondaryAction: Localizer.Text("Cancel"));
                box.Text = result.HotkeyText;
                _pendingHotkeyText = null;
                return false;
            }

            ShowHotkeyCapturePanel(result.Message, isConflict: true, title: Localizer.Text("KeyboardHotkeyEditTitle"));
            box.Text = HotkeyCapturePrompt;
            _pendingHotkeyText = null;
            DisplayHotkeyBoxValues();
            return false;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        box.Text = DisplayHotkeyText(GetHotkeySetting(box.Tag as string));
        _capturingHotkeyOriginalText = box.Text;
        _pendingHotkeyText = null;
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650);
        return true;
    }

    private bool TryCommitRegisteredHotkeyCandidate(string hotkeyText)
    {
        if (_capturingHotkeyBox == null || _keyboardHotkeyConflict != null)
        {
            return false;
        }

        _pressedHotkeyKeys.Clear();
        _pendingHotkeyText = hotkeyText;
        _capturingHotkeyBox.Text = hotkeyText;
        if (!TryCommitPendingHotkey(_capturingHotkeyBox))
        {
            return false;
        }

        ClearHotkeyCapture(hidePanel: false);
        Keyboard.ClearFocus();
        return true;
    }

    private void SetHotkeySetting(string? key, string hotkeyText)
    {
        switch (key)
        {
            case "KeyboardPrevious":
                _viewModel.Settings.KeyboardPrevious = hotkeyText;
                break;
            case "KeyboardNext":
                _viewModel.Settings.KeyboardNext = hotkeyText;
                break;
            case "KeyboardToggle":
                _viewModel.Settings.KeyboardToggle = hotkeyText;
                break;
            case "KeyboardTestOverlay":
                _viewModel.Settings.KeyboardTestOverlay = hotkeyText;
                break;
            case "KeyboardFavorite":
                _viewModel.Settings.KeyboardFavorite = hotkeyText;
                break;
        }
    }

    private void ApplyPendingHotkeyEdits()
    {
        ApplyHotkeyEdit("KeyboardPrevious", KeyboardPreviousBox.Text);
        ApplyHotkeyEdit("KeyboardNext", KeyboardNextBox.Text);
        ApplyHotkeyEdit("KeyboardToggle", KeyboardToggleBox.Text);
        ApplyHotkeyEdit("KeyboardTestOverlay", KeyboardTestOverlayBox.Text);
        ApplyHotkeyEdit("KeyboardFavorite", KeyboardFavoriteBox.Text);
    }

    private bool ValidateHotkeyEdits(out string message)
    {
        (string FirstLabel, string SecondLabel, string Hotkey)? duplicate = FindDuplicateHotkey();
        if (duplicate != null)
        {
            message = Localizer.Format("HotkeyDuplicateTemplate", duplicate.Value.Hotkey, duplicate.Value.FirstLabel, duplicate.Value.SecondLabel);
            return false;
        }

        message = string.Empty;
        return true;
    }

    private (string FirstLabel, string SecondLabel, string Hotkey)? FindDuplicateHotkey()
    {
        Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string hotkey) in GetPendingHotkeyEdits())
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                continue;
            }

            if (seen.TryGetValue(hotkey, out string? existingKey))
            {
                return (GetHotkeyLabel(existingKey), GetHotkeyLabel(key), hotkey);
            }

            seen[hotkey] = key;
        }

        return null;
    }

    private Dictionary<string, string> GetPendingHotkeyEdits()
    {
        return new Dictionary<string, string>
        {
            ["KeyboardPrevious"] = NormalizeCapturedHotkeyText(KeyboardPreviousBox.Text, _viewModel.Settings.KeyboardPrevious),
            ["KeyboardNext"] = NormalizeCapturedHotkeyText(KeyboardNextBox.Text, _viewModel.Settings.KeyboardNext),
            ["KeyboardToggle"] = NormalizeCapturedHotkeyText(KeyboardToggleBox.Text, _viewModel.Settings.KeyboardToggle),
            ["KeyboardTestOverlay"] = NormalizeCapturedHotkeyText(KeyboardTestOverlayBox.Text, _viewModel.Settings.KeyboardTestOverlay),
            ["KeyboardFavorite"] = NormalizeCapturedHotkeyText(KeyboardFavoriteBox.Text, _viewModel.Settings.KeyboardFavorite)
        };
    }

    private void ApplyHotkeyEdit(string key, string text)
    {
        SetHotkeySetting(key, NormalizeCapturedHotkeyText(text, GetHotkeySetting(key)));
    }

    private string GetHotkeySetting(string? key)
    {
        return key switch
        {
            "KeyboardPrevious" => _viewModel.Settings.KeyboardPrevious,
            "KeyboardNext" => _viewModel.Settings.KeyboardNext,
            "KeyboardToggle" => _viewModel.Settings.KeyboardToggle,
            "KeyboardTestOverlay" => _viewModel.Settings.KeyboardTestOverlay,
            "KeyboardFavorite" => _viewModel.Settings.KeyboardFavorite,
            _ => string.Empty
        };
    }

    private string? FindHotkeyConflict(string? currentKey, string hotkeyText)
    {
        Dictionary<string, string> hotkeys = new()
        {
            ["KeyboardPrevious"] = GetHotkeyBoxText("KeyboardPrevious"),
            ["KeyboardNext"] = GetHotkeyBoxText("KeyboardNext"),
            ["KeyboardToggle"] = GetHotkeyBoxText("KeyboardToggle"),
            ["KeyboardTestOverlay"] = GetHotkeyBoxText("KeyboardTestOverlay"),
            ["KeyboardFavorite"] = GetHotkeyBoxText("KeyboardFavorite")
        };

        foreach ((string key, string existing) in hotkeys)
        {
            if (key == currentKey)
            {
                continue;
            }

            if (existing.Equals(hotkeyText, StringComparison.OrdinalIgnoreCase))
            {
                return key switch
                {
                    "KeyboardPrevious" => Localizer.Text("PreviousTrack"),
                    "KeyboardNext" => Localizer.Text("NextTrack"),
                    "KeyboardToggle" => Localizer.Text("TogglePlayPause"),
                    "KeyboardTestOverlay" => Localizer.Text("TestOverlay"),
                    "KeyboardFavorite" => Localizer.Text("FavoriteCurrentTrack"),
                    _ => Localizer.Text("OtherAction")
                };
            }
        }

        return null;
    }

    private string GetHotkeyBoxText(string key)
    {
        return key switch
        {
            "KeyboardPrevious" => NormalizeCapturedHotkeyText(KeyboardPreviousBox.Text, _viewModel.Settings.KeyboardPrevious),
            "KeyboardNext" => NormalizeCapturedHotkeyText(KeyboardNextBox.Text, _viewModel.Settings.KeyboardNext),
            "KeyboardToggle" => NormalizeCapturedHotkeyText(KeyboardToggleBox.Text, _viewModel.Settings.KeyboardToggle),
            "KeyboardTestOverlay" => NormalizeCapturedHotkeyText(KeyboardTestOverlayBox.Text, _viewModel.Settings.KeyboardTestOverlay),
            "KeyboardFavorite" => NormalizeCapturedHotkeyText(KeyboardFavoriteBox.Text, _viewModel.Settings.KeyboardFavorite),
            _ => string.Empty
        };
    }

    private string NormalizeCapturedHotkeyText(string text, string fallback)
    {
        return string.IsNullOrWhiteSpace(text) || text == HotkeyCapturePrompt || text == HotkeyUnsetText ? string.Empty : text.Trim();
    }

    private TextBox? GetHotkeyBox(string? key)
    {
        return key switch
        {
            "KeyboardPrevious" => KeyboardPreviousBox,
            "KeyboardNext" => KeyboardNextBox,
            "KeyboardToggle" => KeyboardToggleBox,
            "KeyboardTestOverlay" => KeyboardTestOverlayBox,
            "KeyboardFavorite" => KeyboardFavoriteBox,
            _ => null
        };
    }

    private static string GetHotkeyLabel(string key)
    {
        return key switch
        {
            "KeyboardPrevious" => LocalizationService.Current.Text("PreviousTrack"),
            "KeyboardNext" => LocalizationService.Current.Text("NextTrack"),
            "KeyboardToggle" => LocalizationService.Current.Text("TogglePlayPause"),
            "KeyboardTestOverlay" => LocalizationService.Current.Text("TestOverlay"),
            "KeyboardFavorite" => LocalizationService.Current.Text("FavoriteCurrentTrack"),
            _ => LocalizationService.Current.Text("OtherAction")
        };
    }

    private void DisplayHotkeyBoxValues()
    {
        KeyboardPreviousBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardPrevious);
        KeyboardNextBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardNext);
        KeyboardToggleBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardToggle);
        KeyboardTestOverlayBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardTestOverlay);
        KeyboardFavoriteBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardFavorite);
        DisplayCloudMusicFavoriteHotkeyEditorValue();
    }

    private string DisplayHotkeyText(string hotkeyText)
    {
        return string.IsNullOrWhiteSpace(hotkeyText) ? HotkeyUnsetText : hotkeyText;
    }

    private void ShowHotkeyCapturePanel(
        string statusText,
        bool isConflict = false,
        bool autoHide = false,
        int autoHideMilliseconds = 0,
        string? title = null,
        string? primaryAction = null,
        string? secondaryAction = null)
    {
        _hotkeyCaptureAutoHideTimer.Stop();
        HotkeyCaptureTitleText.Text = isConflict ? Localizer.Text("HotkeyPromptTitle") : Localizer.Text("HotkeyEditTitle");
        HotkeyCaptureStatusText.Text = statusText;
        if (!string.IsNullOrWhiteSpace(title))
        {
            HotkeyCaptureTitleText.Text = title;
        }

        bool hasActions = !string.IsNullOrWhiteSpace(primaryAction) || !string.IsNullOrWhiteSpace(secondaryAction);
        HotkeyCaptureActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        GamepadCapturePrimaryButton.Visibility = string.IsNullOrWhiteSpace(primaryAction) ? Visibility.Collapsed : Visibility.Visible;
        GamepadCaptureSecondaryButton.Visibility = string.IsNullOrWhiteSpace(secondaryAction) ? Visibility.Collapsed : Visibility.Visible;
        GamepadCapturePrimaryButton.Content = primaryAction ?? string.Empty;
        GamepadCaptureSecondaryButton.Content = secondaryAction ?? string.Empty;
        HotkeyCaptureTitleText.Foreground = isConflict
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xB4))
            : (Brush)FindResource("TextPrimaryBrush");
        HotkeyCaptureStatusText.Foreground = isConflict
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0xC4))
            : (Brush)FindResource("TextSecondaryBrush");
        BeginHotkeyCapturePanelAnimation(true);

        if (autoHide)
        {
            _hotkeyCaptureAutoHideTimer.Interval = TimeSpan.FromMilliseconds(autoHideMilliseconds <= 0 ? 2200 : autoHideMilliseconds);
            _hotkeyCaptureAutoHideTimer.Start();
        }
    }

    private void HideHotkeyCapturePanel()
    {
        _hotkeyCaptureAutoHideTimer.Stop();
        BeginHotkeyCapturePanelAnimation(false);
    }

    private void BeginHotkeyCapturePanelAnimation(bool show)
    {
        HotkeyNavigationCard.IsExpanded = show;
        if (show)
        {
            HotkeyNavigationCard.RefreshExpandedContentHeight();
        }
    }

    private void HotkeyPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelHotkeyCaptureIfClickOutside(e.OriginalSource as DependencyObject);
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelHotkeyCaptureIfClickOutside(e.OriginalSource as DependencyObject);
    }

    private void CancelHotkeyCaptureIfClickOutside(DependencyObject? source)
    {
        if (IsClickInsideCurrentHotkeyBox(source))
        {
            return;
        }

        if (_capturingHotkeyBox != null)
        {
            if (_keyboardHotkeyConflict != null)
            {
                DisplayHotkeyBoxValues();
            }
            else
            {
                RestoreHotkeyBox(_capturingHotkeyBox);
            }

            ClearHotkeyCapture();
            Keyboard.ClearFocus();
        }

        if (_capturingGamepadBox != null)
        {
            CancelGamepadCapture(hidePanel: true, restoreBox: true);
            Keyboard.ClearFocus();
        }
    }

    private bool IsClickInsideCurrentHotkeyBox(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _capturingHotkeyBox) ||
                ReferenceEquals(source, _capturingGamepadBox) ||
                ReferenceEquals(source, HotkeyCapturePanel) ||
                ReferenceEquals(source, HotkeyCaptureActionsPanel))
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static Key NormalizeKey(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
    }

    private static bool TryCreateHotkeyText(IEnumerable<Key> keys, out string hotkeyText)
    {
        Key[] keyArray = keys.ToArray();
        bool hasModifier = keyArray.Any(IsModifierKey);
        Key? mainKey = keyArray.FirstOrDefault(key => !IsModifierKey(key));
        if (!hasModifier || mainKey is null or Key.None)
        {
            hotkeyText = string.Empty;
            return false;
        }

        var modifiers = ModifierKeys.None;
        if (keyArray.Any(key => key is Key.LeftCtrl or Key.RightCtrl))
        {
            modifiers |= ModifierKeys.Control;
        }

        if (keyArray.Any(key => key is Key.LeftShift or Key.RightShift))
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (keyArray.Any(key => key is Key.LeftAlt or Key.RightAlt))
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (keyArray.Any(key => key is Key.LWin or Key.RWin))
        {
            modifiers |= ModifierKeys.Windows;
        }

        hotkeyText = FormatHotkey(modifiers, mainKey.Value);
        return true;
    }

    private static string FormatPressedKeys(IEnumerable<Key> keys)
    {
        List<string> parts = new();
        Key[] keyArray = keys.ToArray();
        if (keyArray.Any(key => key is Key.LeftCtrl or Key.RightCtrl))
        {
            parts.Add("Ctrl");
        }

        if (keyArray.Any(key => key is Key.LeftShift or Key.RightShift))
        {
            parts.Add("Shift");
        }

        if (keyArray.Any(key => key is Key.LeftAlt or Key.RightAlt))
        {
            parts.Add("Alt");
        }

        if (keyArray.Any(key => key is Key.LWin or Key.RWin))
        {
            parts.Add("Win");
        }

        parts.AddRange(keyArray.Where(key => !IsModifierKey(key)).Select(FormatKey));
        return string.Join("+", parts);
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        int keyValue = (int)key;
        if (keyValue >= (int)Key.A && keyValue <= (int)Key.Z)
        {
            return key.ToString();
        }

        if (keyValue >= (int)Key.D0 && keyValue <= (int)Key.D9)
        {
            return (keyValue - (int)Key.D0).ToString();
        }

        if (keyValue >= (int)Key.NumPad0 && keyValue <= (int)Key.NumPad9)
        {
            return (keyValue - (int)Key.NumPad0).ToString();
        }

        return key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            _ => key.ToString()
        };
    }

    private bool RegisterHotkeys()
    {
        return RegisterHotkeySnapshotDirect(KeyboardHotkeyBindingManager.CreateSnapshot(_viewModel.Settings));
    }

    public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        IReadOnlyDictionary<AppAction, string> previous = KeyboardHotkeyBindingManager.CreateSnapshot(_viewModel.Settings);
        if (RegisterHotkeySnapshotDirect(hotkeys))
        {
            return true;
        }

        RegisterHotkeySnapshotDirect(previous);
        return false;
    }

    private bool RegisterHotkeySnapshotDirect(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        _hotkeyService.Clear();
        foreach ((AppAction action, string hotkeyText) in hotkeys)
        {
            if (!RegisterOptionalHotkey(action, hotkeyText))
            {
                _hotkeyService.Clear();
                return false;
            }
        }

        return true;
    }

    private bool RegisterOptionalHotkey(AppAction action, string hotkeyText)
    {
        return string.IsNullOrWhiteSpace(hotkeyText) || _hotkeyService.Register(action, hotkeyText);
    }

    private async Task<int> RefreshSourcesAsync(bool showNavigationFeedback = false)
    {
        if (showNavigationFeedback)
        {
            ShowCurrentNavigationPrompt("CurrentRefreshInProgressTitle", "CurrentRefreshInProgressMessage", TimeSpan.FromMilliseconds(1600));
        }

        try
        {
            _isRefreshingSources = true;
            IReadOnlyList<MediaSessionCandidate> sessions = await _sourceService.ListSessionsAsync();
            _viewModel.ReplaceCaptureSources(sessions);
            ApplyPreferredSource();
            if (sessions.Count == 0)
            {
                _viewModel.SetLocalizedStatus("CurrentStatusNoMediaSource");
            }
            else
            {
                _viewModel.SetLocalizedStatus("CurrentStatusMediaSourceCountTemplate", sessions.Count);
            }
            if (showNavigationFeedback)
            {
                ShowCurrentRefreshResult(sessions.Count);
            }

            return sessions.Count;
        }
        catch
        {
            _viewModel.SetLocalizedStatus("CurrentStatusMediaRefreshFailed");
            if (showNavigationFeedback)
            {
                ShowCurrentNavigationPrompt("CurrentNoMusicTitle", "CurrentNoMusicMessage", CurrentNavigationPromptDelay);
            }

            return 0;
        }
        finally
        {
            _isRefreshingSources = false;
        }
    }

    private void ApplyPreferredSource()
    {
        _sourceService.PreferredSourceAppUserModelId = _viewModel.CaptureSourceAppUserModelId;
    }

    private async void HotkeyService_HotkeyPressed(object? sender, GlobalHotkeyEventArgs e)
    {
        if (IsKeyboardHotkeyInputSuppressed)
        {
            TryCommitRegisteredHotkeyCandidate(e.HotkeyText);
            return;
        }

        await ExecuteAppActionAsync(e.Action, e.HotkeyText);
    }

    private async Task ExecuteAppActionAsync(
        AppAction action,
        string? triggeringHotkey = null)
    {
        switch (action)
        {
            case AppAction.PreviousTrack:
                await WaitForActiveFavoriteRequestAsync();
                await _mediaService.PreviousAsync();
                break;
            case AppAction.NextTrack:
                await WaitForActiveFavoriteRequestAsync();
                await _mediaService.NextAsync();
                break;
            case AppAction.TogglePlayPause:
                await _mediaService.TogglePlayPauseAsync();
                break;
            case AppAction.ShowCurrentTrack:
                await ShowCurrentTrackOverlayAsync();
                break;
            case AppAction.FavoriteCurrentTrack:
                await FavoriteCurrentTrackAsync(triggeringHotkey);
                break;
        }
    }

    private async Task WaitForActiveFavoriteRequestAsync()
    {
        Task? activeRequest = _activeFavoriteUiRequest;
        if (activeRequest is { IsCompleted: false })
        {
            await activeRequest;
        }
    }

    private Task FavoriteCurrentTrackAsync(string? triggeringHotkey = null)
    {
        if (_activeFavoriteUiRequest is { IsCompleted: false } activeRequest)
        {
            return activeRequest;
        }

        _activeFavoriteUiRequest = FavoriteCurrentTrackCoreAsync(triggeringHotkey);
        return _activeFavoriteUiRequest;
    }

    private async Task FavoriteCurrentTrackCoreAsync(string? triggeringHotkey)
    {
        TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            await _trackMonitor.PollOnceAsync();
            track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        }

        if (track == null)
        {
            ShowCurrentNavigationPrompt("FavoriteNoTrackTitle", "FavoriteNoTrackMessage", CurrentNavigationPromptDelay);
            return;
        }

        bool isAppleMusic = OverlayFavoritePresentation.IsSupportedAppleMusicSource(track.SourceAppId);
        bool isCloudMusic = OverlayFavoritePresentation.IsSupportedCloudMusicSource(track.SourceAppId);
        if (!isAppleMusic && !isCloudMusic)
        {
            ShowCurrentNavigationPrompt("FavoriteUnsupportedTitle", "FavoriteUnsupportedMessage", CurrentNavigationPromptDelay);
            return;
        }

        OverlayFavoriteVisualState previousState = GetCachedFavoriteState(track);
        if (previousState == OverlayFavoriteVisualState.Unavailable)
        {
            previousState = OverlayFavoriteVisualState.NotFavorite;
        }

        if (isCloudMusic && previousState == OverlayFavoriteVisualState.Favorite)
        {
            ShowCurrentNavigationPrompt(
                "CloudMusicUnfavoriteInProgressTitle",
                "CloudMusicUnfavoriteInProgressMessage",
                FavoriteInProgressPromptDelay);
        }
        else
        {
            ShowCurrentNavigationPrompt(
                "FavoriteInProgressTitle",
                "FavoriteInProgressMessage",
                FavoriteInProgressPromptDelay);
        }

        _overlayFavoriteStatusCoordinator.CancelPending();
        _pendingOverlayTrackIdentity = null;
        if (isCloudMusic)
        {
            await FavoriteCloudMusicCurrentTrackAsync(
                track,
                previousState,
                triggeringHotkey);
            return;
        }

        Task<AppleMusicFavoriteResult> favoriteRequest = _favoriteService.FavoriteCurrentTrackAsync();
        await ShowOptimisticFavoriteFeedbackAsync(track, previousState);

        AppleMusicFavoriteResult result = await favoriteRequest;
        if (result.Kind is not AppleMusicFavoriteResultKind.Added and
            not AppleMusicFavoriteResultKind.AlreadyFavorite)
        {
            await RollBackOptimisticFavoriteAsync(track, previousState);
            ShowFavoriteFailureNotification();
        }
    }

    private async Task FavoriteCloudMusicCurrentTrackAsync(
        TrackInfo track,
        OverlayFavoriteVisualState previousState,
        string? triggeringHotkey)
    {
        string trackIdentity = TrackIdentity.Create(track);
        _pendingCloudMusicFavoriteIdentity = trackIdentity;
        try
        {
            Task<CloudMusicFavoriteCommandResult> favoriteRequest =
                _cloudMusicFavoriteCommandService.FavoriteCurrentTrackAsync(
                    track,
                    _viewModel.Settings.CloudMusicFavoriteHotkey,
                    isStillCurrent: () => IsCurrentTrack(track),
                    triggeringHotkey: triggeringHotkey);
            await ShowOptimisticCloudMusicToggleFeedbackAsync(track, previousState);

            CloudMusicFavoriteCommandResult result = await favoriteRequest;
            if (result.Kind == CloudMusicFavoriteCommandResultKind.Added)
            {
                _cloudMusicFavoriteStateMonitor.SetKnownState(
                    track,
                    OverlayFavoriteVisualState.Favorite);
                if (previousState == OverlayFavoriteVisualState.Favorite)
                {
                    await ShowOptimisticFavoriteFeedbackAsync(
                        track,
                        OverlayFavoriteVisualState.NotFavorite);
                }

                return;
            }

            if (result.Kind == CloudMusicFavoriteCommandResultKind.Removed)
            {
                _cloudMusicFavoriteStateMonitor.SetKnownState(
                    track,
                    OverlayFavoriteVisualState.NotFavorite);
                if (previousState != OverlayFavoriteVisualState.Favorite)
                {
                    await ShowFavoriteOverlayStateAsync(
                        track,
                        OverlayFavoriteVisualState.NotFavorite,
                        animateTransition: true);
                }

                return;
            }

            await RollBackOptimisticFavoriteAsync(track, previousState);
            ShowCloudMusicFavoriteFailureNotification(result.Kind);
        }
        finally
        {
            if (string.Equals(
                    _pendingCloudMusicFavoriteIdentity,
                    trackIdentity,
                    StringComparison.Ordinal))
            {
                _pendingCloudMusicFavoriteIdentity = null;
            }
        }
    }

    private async Task ShowOptimisticFavoriteFeedbackAsync(
        TrackInfo track,
        OverlayFavoriteVisualState previousState)
    {
        await ShowFavoriteOverlayStateAsync(track, previousState, animateTransition: false);
        if (previousState != OverlayFavoriteVisualState.NotFavorite)
        {
            return;
        }

        await Task.Delay(FavoriteOptimisticFillDelay);
        if (IsCurrentTrack(track))
        {
            await PlayFavoriteStarMotionAsync(track);
        }
    }

    private async Task ShowOptimisticCloudMusicToggleFeedbackAsync(
        TrackInfo track,
        OverlayFavoriteVisualState previousState)
    {
        if (previousState == OverlayFavoriteVisualState.Favorite)
        {
            await ShowFavoriteOverlayStateAsync(
                track,
                OverlayFavoriteVisualState.NotFavorite,
                animateTransition: true);
            return;
        }

        await ShowOptimisticFavoriteFeedbackAsync(track, previousState);
    }

    private void CloudMusicFavoriteStateMonitor_StateChanged(
        object? sender,
        CloudMusicFavoriteStateChangedEventArgs e)
    {
        PostToDispatcher(() => _ = ApplyCloudMusicFavoriteStateChangeAsync(e));
    }

    private async Task ApplyCloudMusicFavoriteStateChangeAsync(
        CloudMusicFavoriteStateChangedEventArgs e)
    {
        if (!IsCurrentTrack(e.Track))
        {
            return;
        }

        if (string.Equals(
                _pendingCloudMusicFavoriteIdentity,
                TrackIdentity.Create(e.Track),
                StringComparison.Ordinal))
        {
            return;
        }

        _overlayFavoriteStatusCoordinator.CancelPending();
        _pendingOverlayTrackIdentity = null;
        if (e.CurrentState == OverlayFavoriteVisualState.Favorite)
        {
            await ShowOptimisticFavoriteFeedbackAsync(
                e.Track,
                OverlayFavoriteVisualState.NotFavorite);
            return;
        }

        await ShowFavoriteOverlayStateAsync(
            e.Track,
            OverlayFavoriteVisualState.NotFavorite,
            animateTransition: true);
    }

    private async Task PlayFavoriteStarMotionAsync(TrackInfo track)
    {
        if (!IsCurrentTrack(track))
        {
            return;
        }

        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = OverlayFavoriteVisualState.Favorite;
        _overlayWindow.ApplySettings(_viewModel.Settings);

        if (_overlayRenderMode is OverlayRenderMode.WpfSettingsPreview or OverlayRenderMode.WpfPositionEdit)
        {
            await _overlayWindow.PlayFavoriteStarMotionAsync(track);
            return;
        }

        IReadOnlyList<OverlaySnapshot> motionSnapshots = _overlayWindow.CreateFavoriteMotionSnapshots(track);
        if (motionSnapshots.Count == 0 || !IsCurrentTrack(track))
        {
            return;
        }

        OverlaySnapshotTransition[] transitions = motionSnapshots
            .Zip(
                OverlayFavoriteMotion.Frames,
                (snapshot, frame) => new OverlaySnapshotTransition(
                    snapshot,
                    frame.Duration,
                    frame.Easing == OverlayFavoriteMotionEasing.EaseIn))
            .ToArray();
        OverlaySnapshot finalSnapshot = motionSnapshots[^1];
        _overlayWindow.HideOverlayVisualImmediately();
        if (_layeredOverlayWindow.IsVisible)
        {
            await _layeredOverlayWindow.TransitionSnapshotSequenceAsync(
                transitions,
                _viewModel.Settings.DisplaySeconds,
                _viewModel.Settings.PauseOverlay);
        }
        else
        {
            _layeredOverlayWindow.ShowSnapshotImmediately(
                finalSnapshot,
                _viewModel.Settings.DisplaySeconds,
                _viewModel.Settings.PauseOverlay);
        }

        if (!IsCurrentTrack(track))
        {
            return;
        }

        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        ConfigureLayeredOverlayRuntime(finalSnapshot);
    }

    private async Task ShowFavoriteOverlayStateAsync(
        TrackInfo track,
        OverlayFavoriteVisualState favoriteState,
        bool animateTransition)
    {
        if (favoriteState == OverlayFavoriteVisualState.Unavailable || !IsCurrentTrack(track))
        {
            return;
        }

        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = favoriteState;
        _overlayWindow.ApplySettings(_viewModel.Settings);

        if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            _overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false, favoriteState);
            QueueSettingsPreviewLayeredHandoff();
            return;
        }

        if (_overlayRenderMode == OverlayRenderMode.WpfPositionEdit)
        {
            _overlayWindow.UpdateTrack(track, favoriteState);
            return;
        }

        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, favoriteState);
        _overlayWindow.HideOverlayVisualImmediately();
        if (_layeredOverlayWindow.IsVisible)
        {
            await _layeredOverlayWindow.TransitionSnapshotAsync(
                snapshot,
                _viewModel.Settings.DisplaySeconds,
                _viewModel.Settings.PauseOverlay,
                animateTransition ? FavoriteStarTransitionDuration : TimeSpan.Zero);
        }
        else
        {
            await _layeredOverlayWindow.ShowSnapshotAsync(
                snapshot,
                _viewModel.Settings.DisplaySeconds,
                _viewModel.Settings.PauseOverlay);
        }

        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        ConfigureLayeredOverlayRuntime(snapshot);
    }

    private async Task RollBackOptimisticFavoriteAsync(
        TrackInfo track,
        OverlayFavoriteVisualState previousState)
    {
        if (!IsCurrentTrack(track))
        {
            return;
        }

        _layeredOverlayFavoriteState = previousState;
        bool hasVisibleOverlay = _layeredOverlayWindow.IsVisible ||
                                 _overlayRenderMode is OverlayRenderMode.WpfSettingsPreview or OverlayRenderMode.WpfPositionEdit;
        if (hasVisibleOverlay)
        {
            await ShowFavoriteOverlayStateAsync(track, previousState, animateTransition: true);
        }
    }

    private void ShowFavoriteFailureNotification()
    {
        ShowFavoriteFailureNotification("FavoriteFailedTitle", "FavoriteFailedMessage");
    }

    private void ShowCloudMusicFavoriteFailureNotification(
        CloudMusicFavoriteCommandResultKind resultKind)
    {
        (string titleKey, string messageKey) = resultKind switch
        {
            CloudMusicFavoriteCommandResultKind.TriggerReleaseTimedOut =>
                ("CloudMusicTriggerReleaseTimedOutTitle", "CloudMusicTriggerReleaseTimedOutMessage"),
            CloudMusicFavoriteCommandResultKind.StateUnavailable =>
                ("CloudMusicStateUnavailableTitle", "CloudMusicStateUnavailableMessage"),
            CloudMusicFavoriteCommandResultKind.DispatchFailed =>
                ("CloudMusicDispatchFailedTitle", "CloudMusicDispatchFailedMessage"),
            CloudMusicFavoriteCommandResultKind.ConfirmationTimedOut =>
                ("CloudMusicConfirmationTimedOutTitle", "CloudMusicConfirmationTimedOutMessage"),
            CloudMusicFavoriteCommandResultKind.TrackChanged =>
                ("CloudMusicTrackChangedTitle", "CloudMusicTrackChangedMessage"),
            _ => ("FavoriteFailedTitle", "FavoriteFailedMessage")
        };
        ShowFavoriteFailureNotification(titleKey, messageKey);
    }

    private void ShowFavoriteFailureNotification(
        string titleKey,
        string messageKey)
    {
        ShowCurrentNavigationPrompt(titleKey, messageKey, CurrentNavigationPromptDelay);
        if (!IsVisible || WindowState == WindowState.Minimized || !IsActive)
        {
            _trayIconService.ShowNotification(
                Localizer.Text(titleKey),
                Localizer.Text(messageKey));
        }
    }

    private bool IsCurrentTrack(TrackInfo expectedTrack)
    {
        TrackInfo? currentTrack = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack ?? _layeredOverlayTrack;
        return currentTrack != null &&
               TrackIdentity.Create(currentTrack).Equals(TrackIdentity.Create(expectedTrack), StringComparison.Ordinal);
    }

    private async Task<bool> ShowCurrentTrackOverlayAsync()
    {
        TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            await _trackMonitor.PollOnceAsync();
            track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        }

        if (track == null)
        {
            _viewModel.SetLocalizedStatus("CurrentStatusNoCurrentTrack");
            return false;
        }

        OverlayFavoriteVisualState immediateState = GetCachedFavoriteState(track);
        await ShowResolvedOverlayTrackAsync(track, immediateState);
        _ = RefreshFavoriteStateAfterImmediateShowAsync(track);
        return true;
    }

    private OverlayFavoriteVisualState GetCachedFavoriteState(TrackInfo track)
    {
        return _layeredOverlayTrack != null &&
               TrackIdentity.Create(_layeredOverlayTrack).Equals(TrackIdentity.Create(track), StringComparison.Ordinal)
            ? _layeredOverlayFavoriteState
            : OverlayFavoriteVisualState.Unavailable;
    }

    private async Task RefreshFavoriteStateAfterImmediateShowAsync(TrackInfo track)
    {
        OverlayFavoriteResolution? resolution = await _overlayFavoriteStatusCoordinator.ResolveAsync(track);
        TrackInfo latestTrack = OverlayTrackChangeDisplayPolicy.SelectLatestCloudMusicTrack(
            track,
            _trackMonitor.CurrentTrack);
        if (resolution == null ||
            resolution.State == OverlayFavoriteVisualState.Unavailable ||
            !IsCurrentTrack(latestTrack) ||
            GetCachedFavoriteState(latestTrack) == resolution.State)
        {
            return;
        }

        _layeredOverlayTrack = latestTrack;
        _layeredOverlayFavoriteState = resolution.State;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(latestTrack, resolution.State);
        _overlayWindow.HideOverlayVisualImmediately();
        if (_layeredOverlayWindow.IsVisible)
        {
            _layeredOverlayWindow.UpdateSnapshot(snapshot);
            ConfigureLayeredOverlayRuntime(snapshot);
        }
    }

    private async Task ShowOverlayTrackAsync(TrackInfo track)
    {
        string trackIdentity = TrackIdentity.Create(track);
        _pendingOverlayTrackIdentity = trackIdentity;
        OverlayFavoriteResolution? resolution = await _overlayFavoriteStatusCoordinator.ResolveAsync(track);
        if (resolution == null || !string.Equals(_pendingOverlayTrackIdentity, trackIdentity, StringComparison.Ordinal))
        {
            return;
        }

        _pendingOverlayTrackIdentity = null;
        if (_overlayRenderMode == OverlayRenderMode.WpfPositionEdit)
        {
            UpdateVisibleResolvedOverlayTrack(resolution.Track, resolution.State);
            return;
        }

        bool isOverlayVisible = _layeredOverlayWindow.IsVisible ||
                                _overlayRenderMode is OverlayRenderMode.WpfSettingsPreview or OverlayRenderMode.WpfPositionEdit;
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            _viewModel.Settings.ShowOverlayOnTrackChange,
            _viewModel.Settings.PauseOverlay,
            isOverlayVisible);

        switch (decision)
        {
            case OverlayTrackChangeDisplayDecision.Show:
                await ShowResolvedOverlayTrackAsync(resolution.Track, resolution.State);
                break;
            case OverlayTrackChangeDisplayDecision.UpdateVisible:
                UpdateVisibleResolvedOverlayTrack(resolution.Track, resolution.State);
                break;
            case OverlayTrackChangeDisplayDecision.CacheOnly:
                CacheResolvedOverlayTrack(resolution.Track, resolution.State);
                break;
        }
    }

    private void ApplyTrackRead(TrackInfo? track)
    {
        _viewModel.ApplyTrack(track);
        if (!OverlayTrackChangeDisplayPolicy.ShouldHideStaleCloudMusicOverlay(
                track,
                _layeredOverlayTrack))
        {
            return;
        }

        _pendingOverlayTrackIdentity = null;
        _overlayWindow.HideOverlayVisualImmediately();
        _layeredOverlayWindow.HideImmediately();
        _overlayRenderMode = OverlayRenderMode.Hidden;
        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = OverlayFavoriteVisualState.Unavailable;
    }

    private void RestorePersistentOverlay(TrackInfo track)
    {
        if (OverlayTrackChangeDisplayPolicy.ShouldRestorePersistentOverlay(
                _viewModel.Settings.PauseOverlay,
                _layeredOverlayWindow.IsVisible || _overlayRenderMode is OverlayRenderMode.WpfSettingsPreview or OverlayRenderMode.WpfPositionEdit,
                track) && IsCurrentTrack(track))
        {
            _ = ShowOverlayTrackAsync(track);
        }
    }

    private void HideRuntimeOverlayForUnavailableSource()
    {
        _pendingOverlayTrackIdentity = null;
        _layeredOverlayTrack = null;
        _layeredOverlayFavoriteState = OverlayFavoriteVisualState.Unavailable;

        if (_overlayRenderMode is OverlayRenderMode.WpfSettingsPreview or OverlayRenderMode.WpfPositionEdit)
        {
            return;
        }

        _overlayWindow.HideOverlayVisualImmediately();
        _layeredOverlayWindow.HideImmediately();
        _overlayRenderMode = OverlayRenderMode.Hidden;
    }

    private void UpdateVisibleResolvedOverlayTrack(TrackInfo track, OverlayFavoriteVisualState favoriteState)
    {
        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = favoriteState;
        if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            _overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false, favoriteState);
            QueueSettingsPreviewLayeredHandoff();
            return;
        }

        if (_overlayRenderMode == OverlayRenderMode.WpfPositionEdit)
        {
            _overlayWindow.ApplySettings(_viewModel.Settings, animateScale: false);
            _overlayWindow.UpdateTrack(track, favoriteState);
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, favoriteState);
        _overlayWindow.HideOverlayVisualImmediately();
        if (_layeredOverlayWindow.IsVisible)
        {
            _layeredOverlayWindow.UpdateSnapshot(snapshot);
            _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
            ConfigureLayeredOverlayRuntime(snapshot);
            return;
        }

        CacheResolvedOverlayTrack(track, favoriteState);
    }

    private void CacheResolvedOverlayTrack(TrackInfo track, OverlayFavoriteVisualState favoriteState)
    {
        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = favoriteState;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _overlayWindow.UpdateTrack(track, favoriteState);
        _overlayRenderMode = OverlayRenderMode.Hidden;
    }

    private async Task ShowResolvedOverlayTrackAsync(TrackInfo track, OverlayFavoriteVisualState favoriteState)
    {
        _layeredOverlayTrack = track;
        _layeredOverlayFavoriteState = favoriteState;
        if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            _overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false, favoriteState);
            QueueSettingsPreviewLayeredHandoff();
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, favoriteState);
        _overlayWindow.HideOverlayVisualImmediately();
        await _layeredOverlayWindow.ShowSnapshotAsync(snapshot, _viewModel.Settings.DisplaySeconds, _viewModel.Settings.PauseOverlay);
        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        ConfigureLayeredOverlayRuntime(snapshot);
    }

    private void UpdateOverlayTrack(TrackInfo track)
    {
        if (string.Equals(_pendingOverlayTrackIdentity, TrackIdentity.Create(track), StringComparison.Ordinal))
        {
            return;
        }

        _layeredOverlayTrack = track;
        if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)
        {
            _overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false, _layeredOverlayFavoriteState);
            QueueSettingsPreviewLayeredHandoff();
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, _layeredOverlayFavoriteState);
        if (_layeredOverlayWindow.IsVisible || _viewModel.Settings.PauseOverlay)
        {
            _overlayWindow.HideOverlayVisualImmediately();
            _layeredOverlayWindow.UpdateSnapshot(snapshot);
            _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
            ConfigureLayeredOverlayRuntime(snapshot);
            return;
        }

        _overlayRenderMode = OverlayRenderMode.Hidden;
        _overlayWindow.UpdateTrack(track, _layeredOverlayFavoriteState);
    }

    private void ShowWpfSettingsPreview(bool animateScale)
    {
        TrackInfo? track = _layeredOverlayTrack ?? _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            _overlayWindow.ApplySettings(_viewModel.Settings, animateScale);
            return;
        }

        _layeredOverlayTrack = track;
        _overlayRenderMode = OverlayRenderMode.WpfSettingsPreview;
        _overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale, _layeredOverlayFavoriteState);
        _layeredOverlayWindow.HideImmediately();
        _layeredOverlayWindow.ConfigurePointerAutoHide(enabled: false);
        QueueSettingsPreviewLayeredHandoff();
    }

    private void QueueSettingsPreviewLayeredHandoff()
    {
        _settingsPreviewSettleTimer.Stop();
        _settingsPreviewSettleTimer.Start();
    }

    private void CompleteSettingsPreviewLayeredHandoff()
    {
        if (_overlayRenderMode != OverlayRenderMode.WpfSettingsPreview)
        {
            return;
        }

        TrackInfo? track = _layeredOverlayTrack ?? _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            _overlayRenderMode = OverlayRenderMode.Hidden;
            _layeredOverlayWindow.ConfigurePointerAutoHide(enabled: false);
            _overlayWindow.HideOverlayVisualImmediately();
            return;
        }

        _layeredOverlayTrack = track;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, _layeredOverlayFavoriteState);
        _layeredOverlayWindow.ShowSnapshotImmediately(snapshot, _viewModel.Settings.DisplaySeconds, _viewModel.Settings.PauseOverlay);
        _overlayWindow.HideOverlayVisualImmediately();
        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        ConfigureLayeredOverlayRuntime(snapshot);
    }

    private void RefreshLayeredOverlaySnapshot()
    {
        TrackInfo? track = _layeredOverlayTrack ?? _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            _layeredOverlayWindow.ConfigurePointerAutoHide(enabled: false);
            return;
        }

        if (!_layeredOverlayWindow.IsVisible && !_viewModel.Settings.PauseOverlay)
        {
            _overlayWindow.ApplySettings(_viewModel.Settings);
            return;
        }

        _layeredOverlayTrack = track;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        OverlaySnapshot snapshot = _overlayWindow.CreateSnapshot(track, _layeredOverlayFavoriteState);
        _overlayWindow.HideOverlayVisualImmediately();
        if (_layeredOverlayWindow.IsVisible)
        {
            _layeredOverlayWindow.UpdateSnapshot(snapshot);
        }
        else
        {
            _ = _layeredOverlayWindow.ShowSnapshotAsync(snapshot, _viewModel.Settings.DisplaySeconds, keepVisible: true);
        }

        _overlayRenderMode = OverlayRenderMode.LayeredRuntime;
        ConfigureLayeredOverlayRuntime(snapshot);
    }

    private void ConfigureLayeredOverlayRuntime(OverlaySnapshot snapshot)
    {
        _layeredOverlayWindow.ConfigurePointerAutoHide(_viewModel.Settings.PauseOverlay && _viewModel.Settings.AutoHideOnMouseNear);
    }

    private void ExitApplication()
    {
        RequestApplicationExit();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateMaximizeButtonGlyph()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;
    }

    private void ApplyWindowShellState()
    {
        WindowFrame.Margin = new Thickness(0);
        WindowFrame.Effect = null;

        if (WindowState == WindowState.Maximized)
        {
            WindowFrame.CornerRadius = new CornerRadius(0);
            WindowFrame.BorderThickness = new Thickness(0);
            return;
        }

        WindowFrame.CornerRadius = new CornerRadius(NormalWindowFrameRadius);
        WindowFrame.BorderThickness = new Thickness(1);
    }

    private void SyncShadowWindow()
    {
        if (!IsVisible || WindowState is WindowState.Minimized or WindowState.Maximized)
        {
            _shadowWindow.Hide();
            return;
        }

        _shadowWindow.SyncWith(this);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        LogStartup("closed");
        CleanupForExit();
    }

    private void CleanupForExit()
    {
        if (_hasCleanedUpForExit)
        {
            return;
        }

        _hasCleanedUpForExit = true;
        _hotkeyCaptureAutoHideTimer.Stop();
        _gamepadCaptureTimer.Stop();
        _overlaySettingsSaveDebounceTimer.Stop();
        _settingsPreviewSettleTimer.Stop();
        _overlayRenderMode = OverlayRenderMode.Hidden;

        if (!_isSessionEnding)
        {
            FlushOverlaySettingsAutoSave();
        }

        SafeDispose(_trackMonitor);
        _cloudMusicFavoriteStateMonitor.StateChanged -= CloudMusicFavoriteStateMonitor_StateChanged;
        SafeDispose(_cloudMusicFavoriteStateMonitor);
        SafeDispose(_hotkeyService);
        SafeDispose(_gamepadService);
        SafeDispose(_overlayFavoriteStatusCoordinator);
        _trayIconService.PrepareForExit();
        SafeDispose(_trayIconService);
        SafeDispose(_layeredOverlayWindow);
        SafeClose(_overlayWindow);
        SafeClose(_shadowWindow);
    }

    private void PostToDispatcher(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SafeClose(Window window)
    {
        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicFloat.ico");
            if (File.Exists(iconPath))
            {
                Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
            }
        }
        catch
        {
        }
    }

    [Conditional("DEBUG")]
    private static void LogStartup(string message)
    {
#if DEBUG
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "musicfloat-startup.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
#endif
    }
}
