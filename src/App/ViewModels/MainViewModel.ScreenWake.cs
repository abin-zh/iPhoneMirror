using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

// The iPhone side button is a toggle: one pulse wakes a sleeping display and
// locks a lit one. Wake requests therefore stay behind the explicit sender
// pause signal and are coalesced, so a burst of clicks cannot bounce the
// phone display on and off.
internal sealed partial class MainViewModel
{
    private const int ScreenWakePulseHoldMs = 500;
    private const int ScreenWakeRepeatDelayMs = 2000;
    private long _screenWakeHandledAt;
    private int _screenWakeInFlight;

    // A paused AirPlay mirror is the only reliable "display off" signal the
    // host receives: iOS pauses the mirror stream when the sender sleeps or
    // locks. Stalled or static frames deliberately do not qualify.
    internal bool IsWirelessScreenOff(string? udid)
    {
        if (_disposed || string.IsNullOrWhiteSpace(udid) ||
            !DeviceViewModel.IsWirelessUdid(udid)) return false;
        return _sessions.TryGet(udid, out var state) &&
            state.HasSession && state.MirrorPaused;
    }

    // Reports whether the current route could deliver the side-button pulse,
    // so the paused caption only promises a wake the app can actually send.
    internal bool CanWakeScreen(string? udid) =>
        !_disposed && !string.IsNullOrWhiteSpace(udid) && AutomationHumanAllowed(udid) &&
        (GetReadyUsbControlBridge(udid) is not null ||
            (BluetoothControlIsInputEnabled && IsBluetoothControlTarget(udid)));

    internal bool TryBeginScreenWake()
    {
        if (Interlocked.CompareExchange(ref _screenWakeInFlight, 1, 0) != 0) return false;
        if (Environment.TickCount64 - Interlocked.Read(ref _screenWakeHandledAt) <
            ScreenWakeRepeatDelayMs)
        {
            Interlocked.Exchange(ref _screenWakeInFlight, 0);
            return false;
        }
        return true;
    }

    internal void CompleteScreenWake()
    {
        Interlocked.Exchange(ref _screenWakeHandledAt, Environment.TickCount64);
        Interlocked.Exchange(ref _screenWakeInFlight, 0);
    }

    // Sends one hardware side-button pulse through the live reverse-control
    // route. AirPlay is receive-only, so a sleeping phone can only be reached
    // through the USB or wireless CoreDevice HID bridge.
    internal async Task<bool> WakeScreenAsync(string? udid, Func<bool>? canSend = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(udid) || !AutomationHumanAllowed(udid))
            return false;
        var route = CaptureDirectKeyboardRoute(udid);
        if (route?.SendButtonAsync is { } send)
        {
            await send(CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                CoreDeviceTouchProtocol.IndigoLock, "down", canSend);
            try { await Task.Delay(ScreenWakePulseHoldMs); }
            finally
            {
                // iOS treats a very short side-button pulse as bounce noise
                // and the release must be attempted even if the guard expired.
                await send(CoreDeviceTouchProtocol.IndigoConsumerUsagePage,
                    CoreDeviceTouchProtocol.IndigoLock, "up", null);
            }
            return true;
        }
        if (BluetoothControlIsInputEnabled && IsBluetoothControlTarget(udid))
        {
            await SendBluetoothConsumerShortcutAsync(CoreDeviceTouchProtocol.IndigoLock,
                ScreenWakePulseHoldMs, udid, canSend);
            return true;
        }
        return false;
    }
}
