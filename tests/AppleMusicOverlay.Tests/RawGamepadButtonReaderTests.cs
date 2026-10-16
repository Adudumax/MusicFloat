using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using Windows.Gaming.Input;

namespace AppleMusicOverlay.Tests;

public sealed class RawGamepadButtonReaderTests
{
    private static readonly GameControllerButtonLabel[] ObservedDualSenseLabels =
    [
        GameControllerButtonLabel.Square, GameControllerButtonLabel.Cross, GameControllerButtonLabel.Circle, GameControllerButtonLabel.Triangle,
        GameControllerButtonLabel.LeftBumper, GameControllerButtonLabel.RightBumper, GameControllerButtonLabel.None, GameControllerButtonLabel.None,
        GameControllerButtonLabel.View, GameControllerButtonLabel.Menu, GameControllerButtonLabel.LeftStickButton, GameControllerButtonLabel.RightStickButton,
        GameControllerButtonLabel.None, GameControllerButtonLabel.None, GameControllerButtonLabel.None
    ];

    [Fact]
    public void RawOnlyDualSenseCanTriggerExistingL1L3ShortcutOnceUntilReleased()
    {
        var reader = new RawGamepadButtonReader(ObservedDualSenseLabels, dualSenseLayout: true);
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet { Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.LeftStick]) };
        bool[] buttons = new bool[15];
        buttons[4] = buttons[10] = true;
        var pressed = reader.Read(buttons, GameControllerSwitchPosition.Center, new double[6]);
        Assert.Equal(AppAction.NextTrack, runtime.Update(pressed, TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update(pressed, TimeSpan.FromMilliseconds(16), bindings));
        Array.Clear(buttons);
        Assert.Null(runtime.Update(reader.Read(buttons, GameControllerSwitchPosition.Center, []), TimeSpan.FromMilliseconds(16), bindings));
        buttons[4] = buttons[10] = true;
        Assert.Equal(AppAction.NextTrack, runtime.Update(reader.Read(buttons, GameControllerSwitchPosition.Center, []), TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void LabelsRatherThanArrayOrderDetermineFaceButtons()
    {
        var reader = new RawGamepadButtonReader([GameControllerButtonLabel.Circle, GameControllerButtonLabel.Square, GameControllerButtonLabel.Cross]);
        Assert.Equal([GamepadButton.FaceSouth], reader.Read([false, false, true], GameControllerSwitchPosition.Center, []));
    }

    [Fact]
    public void DualSenseTriggersUseAnalogHysteresisWithoutStickFalsePositives()
    {
        var reader = new RawGamepadButtonReader(ObservedDualSenseLabels, dualSenseLayout: true);
        bool[] buttons = new bool[15];
        Assert.Empty(reader.Read(buttons, GameControllerSwitchPosition.Center, [0.5, 0.5, 0.5, 0, 0, 0.5]));
        Assert.Contains(GamepadButton.LeftTrigger, reader.Read(buttons, GameControllerSwitchPosition.Center, [0.5, 0.5, 0.5, 0.7, 0, 0.5]));
        Assert.Contains(GamepadButton.LeftTrigger, reader.Read(buttons, GameControllerSwitchPosition.Center, [0.5, 0.5, 0.5, 0.4, 0, 0.5]));
        Assert.DoesNotContain(GamepadButton.LeftTrigger, reader.Read(buttons, GameControllerSwitchPosition.Center, [0.5, 0.5, 0.5, 0.2, 0, 0.5]));
    }

    [Fact]
    public void UnknownGenericButtonsAndAxesAreNotAssumedToBeDualSense()
    {
        var reader = new RawGamepadButtonReader(Enumerable.Repeat(GameControllerButtonLabel.None, 15).ToArray());
        Assert.Empty(reader.Read(Enumerable.Repeat(true, 15).ToArray(), GameControllerSwitchPosition.Center, [1, 1, 1, 1, 1, 1]));
    }

    [Fact]
    public void DiagonalHatReportsBothDirectionsAndNeutralClearsThem()
    {
        var reader = new RawGamepadButtonReader([]);
        Assert.True(reader.Read([], GameControllerSwitchPosition.UpRight, []).SetEquals([GamepadButton.DPadUp, GamepadButton.DPadRight]));
        Assert.Empty(reader.Read([], GameControllerSwitchPosition.Center, []));
    }

    [Fact]
    public async Task ReadFailureRecoversAndPollingContinues()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, recoveries = 0;
        Task loop = GamepadPollingLoop.RunAsync(
            () => { if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Disconnected controller"); success.TrySetResult(); },
            () => Interlocked.Increment(ref recoveries),
            TimeSpan.FromMilliseconds(2), cts.Token, TimeSpan.FromMilliseconds(2));
        await success.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Equal(1, recoveries);
        Assert.True(attempts >= 2);
    }
}
