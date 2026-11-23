using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

// A paused AirPlay mirror means the iPhone display is off. Touch input cannot
// reach a sleeping phone, so the first press becomes an explicit wake request
// instead of a tap that would be silently dropped.
public partial class MainWindow
{
    private bool TryHandleScreenOffPointer(PreviewPointerEventArgs e, string? udid)
    {
        if (udid is null || !_viewModel.IsWirelessScreenOff(udid)) return false;
        switch (e.Kind)
        {
            case PreviewPointerKind.ButtonDown when e.Button == 1:
            case PreviewPointerKind.TouchDown:
                RequestScreenWake(udid);
                return true;
            case PreviewPointerKind.Move:
            case PreviewPointerKind.Wheel:
            case PreviewPointerKind.TouchMove:
                // The retained last frame has nothing that accepts input.
                return true;
            default:
                // Releases and resets still run so no contact stays pressed.
                return false;
        }
    }

    private void RequestScreenWake(string udid)
    {
        if (!_viewModel.TryBeginScreenWake()) return;
        _ = WakeScreenForDeviceAsync(udid);
    }

    private async Task WakeScreenForDeviceAsync(string udid)
    {
        try
        {
            var sent = await _viewModel.WakeScreenAsync(udid);
            _viewModel.AddDiagnosticLog(AppLog.Event("wireless_screen_wake",
                ("device", AppLog.Device(udid)), ("sent", sent)));
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("wireless_screen_wake_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error))));
        }
        finally { _viewModel.CompleteScreenWake(); }
    }
}
