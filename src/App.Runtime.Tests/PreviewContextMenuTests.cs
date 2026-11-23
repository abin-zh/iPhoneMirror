using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Real HWND validation for the reserved drag strip. No phone is needed.
    private static int RunPreviewDragBarHitTest()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var type = typeof(Windows.NativePreviewWindow);
        var constructor = type.GetConstructors(KeyboardTestMembers).Single();
        var parameters = constructor.GetParameters();
        var values = parameters.Select(p => p.DefaultValue).ToArray();
        object[] required = [390u, 844u, "Drag bar hit test", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, values, required.Length);
        var pointers = new List<Controls.PreviewPointerEventArgs>();
        values[Array.FindIndex(parameters, p => p.Name == "isUsbControlEnabled")] = (Func<bool>)(() => true);
        values[Array.FindIndex(parameters, p => p.Name == "pointerInput")] = (Action<Controls.PreviewPointerEventArgs>)pointers.Add;
        using var preview = (Windows.NativePreviewWindow)constructor.Invoke(values);
        try
        {
            KeyboardCall(preview, "ShowInitially");
            SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => preview.Handle));
            nint Message(int message, nint point = 0, nint wParam = 0) =>
                (nint)KeyboardCall(preview, "WindowProcedure", preview.Handle, message, wParam, point, false)!;
            static nint Pack(int x, int y) => (nint)((y << 16) | (x & 0xffff));
            InteractionAssert(UnfixGetWindowRect(preview.Handle, out var rect), "Missing native window bounds.");
            var centerX = (rect.Left + rect.Right) / 2;
            var centerY = (rect.Top + rect.Bottom) / 2;

            InteractionAssert(Message(0x84, Pack(centerX, rect.Top + 4)) == 2,
                "The reserved drag strip must report the caption hit test.");
            InteractionAssert(Message(0x84, Pack(centerX, centerY)) == 1,
                "The picture must report the client hit test so touches reach the phone.");
            InteractionAssert(Message(0x84, Pack(rect.Right - 2, centerY)) == 11,
                "The right edge must still resize the window.");
            InteractionAssert(Message(0x84, Pack(centerX, rect.Bottom - 2)) == 15,
                "The bottom edge must still resize the window.");
            InteractionAssert(preview.AllowsControlInput,
                "Control input must stay admitted without any explicit fix step.");

            pointers.Clear();
            Message(0xA0, Pack(centerX, rect.Top + 4), 2);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            var bar = KeyboardField(preview, "_dragBar") as System.Windows.Window;
            InteractionAssert(bar is not null && bar.IsVisible,
                "Hovering the drag strip must reveal the drag bar.");
            InteractionAssert(pointers.Count == 0,
                "Hovering the drag strip must not forward input to the device.");

            pointers.Clear();
            Message(0x201, Pack(centerX, centerY));
            InteractionAssert(pointers.Any(p => p.Kind == Controls.PreviewPointerKind.ButtonDown),
                "A press inside the picture must reach the phone.");

            KeyboardCall(preview, "HideDragBar");
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert(Message(0x84, Pack(centerX, rect.Top + 4)) == 1,
                "Fullscreen must leave the whole surface to the phone.");
            KeyboardCall(preview, "ToggleFullScreen");
            Console.WriteLine("PASS drag bar: strip is caption, picture is client, edges still resize, hover reveals the bar.");
            return 0;
        }
        finally
        {
            preview.Dispose();
            app.Shutdown();
        }
    }

    private static int RunPreviewContextMenuTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", InteractionMembers)!.SetValue(app, true);
        app.InitializeComponent();
        try
        {
            TestPreviewContextMenus();
            TestDragBarTouchControl();
            TestMappingAdmission(app);
            TestMappingAdmission(app, absolute: true);
        }
        finally { app.Shutdown(); }
        return 0;
    }

    private static void TestPreviewContextMenus()
    {
        var settings = ((App)Application.Current).UpdateSettings;
        var previousHomeKey = settings.BluetoothHomeShortcutVirtualKey;
        var previousHomeModifiers = settings.BluetoothHomeShortcutModifiers;
        settings.BluetoothHomeShortcutModifiers = 0;
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Windows.NativePreviewWindow", true)!;
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = constructor.GetParameters();
        var values = parameters.Select(p => p.DefaultValue).ToArray();
        object?[] required = [390u, 844u, "Context menu regression", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, values, required.Length);
        void Set(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
        var controlEnabled = false;
        var touchEnabled = false;
        Set("isReverseControlEnabled", (Func<nint, bool>)(_ => controlEnabled));
        Set("isUsbControlEnabled", (Func<bool>)(() => touchEnabled));
        var pointerType = parameters.Single(p => p.Name == "pointerInput").ParameterType;
        var pointer = Expression.Parameter(pointerType.GenericTypeArguments[0]);
        var inputs = new List<object>();
        Set("pointerInput", Expression.Lambda(pointerType,
            Expression.Call(Expression.Constant(inputs), typeof(List<object>).GetMethod("Add")!,
                Expression.Convert(pointer, typeof(object))), pointer).Compile());

        // Real HWND and WPF popup, with inert transport callbacks. No phone is needed.
        using var preview = (IDisposable)constructor.Invoke(values);
        var handle = (nint)type.GetProperty("Handle", InteractionMembers)!.GetValue(preview)!;
        var menu = (ContextMenu)InteractionField(preview, "_contextMenu")!;
        var procedure = type.GetMethod("WindowProcedure", InteractionMembers)!;
        var checks = 0;
        foreach (var layout in new[] { "normal", "fullscreen" })
        {
            if (layout == "fullscreen") type.GetMethod("ToggleFullScreen", InteractionMembers)!.Invoke(preview, null);
            foreach (var route in new[]
            {
                (Name: "wired/wireless touch, unbound", Control: true, Touch: true, Menu: true, Bound: false),
                (Name: "touch-only callback, unbound", Control: false, Touch: true, Menu: true, Bound: false),
                (Name: "wired/wireless touch, default Home binding", Control: true, Touch: true, Menu: true, Bound: true),
                (Name: "touch-only callback, default Home binding", Control: false, Touch: true, Menu: true, Bound: true),
                (Name: "Bluetooth", Control: true, Touch: false, Menu: false, Bound: true),
                (Name: "Bluetooth, unbound", Control: true, Touch: false, Menu: false, Bound: false),
                (Name: "view-only", Control: false, Touch: false, Menu: true, Bound: true),
            })
            {
                controlEnabled = route.Control;
                touchEnabled = route.Touch;
                ((App)Application.Current).UpdateSettings.BluetoothHomeShortcutVirtualKey =
                    route.Bound ? (int)Services.KeyboardShortcut.MouseRight : 0;
                foreach (var message in new[] { 0x0204, 0x0205, 0x007B, 0x00A4, 0x00A5 })
                {
                    inputs.Clear();
                    object[] args = [handle, message, (nint)0, (nint)0, false];
                    procedure.Invoke(preview, args);
                    InteractionAssert((bool)args[4] && menu.IsOpen == route.Menu,
                        $"{route.Name}/{layout}: message 0x{message:X4} must be handled with menu open={route.Menu}");
                    if (route.Menu)
                        InteractionAssert(!inputs.Any(input =>
                            input.GetType().GetProperty("Kind", InteractionMembers)!.GetValue(input)!
                                .ToString() is "ButtonDown" or "ButtonUp"),
                            $"{route.Name}/{layout}: opening the menu must not forward a mouse button to the phone");
                    menu.IsOpen = false;
                    DrainDispatcher();
                    checks++;
                }
            }
            // Restore normal cursor negotiation before native window transitions.
            controlEnabled = touchEnabled = false;
        }
        type.GetMethod("ToggleFullScreen", InteractionMembers)!.Invoke(preview, null);
        settings.BluetoothHomeShortcutVirtualKey = previousHomeKey;
        settings.BluetoothHomeShortcutModifiers = previousHomeModifiers;
        Console.WriteLine($"Preview context menu: {checks} routing checks passed across touch, Bluetooth and view-only modes.");
    }

    private static void TestDragBarTouchControl()
    {
        var type = typeof(Windows.NativePreviewWindow);
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = constructor.GetParameters();
        foreach (var mode in new[] { "wired", "wireless", "absolute-bluetooth" })
        {
            var absolute = mode == "absolute-bluetooth";
            var values = parameters.Select(p => p.DefaultValue).ToArray();
            object[] required = [390u, 844u, "Drag bar touch regression", (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            void Set(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
            var enabled = false;
            var pointers = new List<Controls.PreviewPointerEventArgs>();
            var keys = new List<Controls.PreviewKeyboardEventArgs>();
            Set("isUsbControlEnabled", (Func<bool>)(() => !absolute && enabled));
            Set("isReverseControlEnabled", (Func<nint, bool>)(_ => mode != "wired" && enabled));
            Set("pointerInput", (Action<Controls.PreviewPointerEventArgs>)pointers.Add);
            Set("keyboardInput", (Action<Controls.PreviewKeyboardEventArgs>)keys.Add);
            using var preview = (Windows.NativePreviewWindow)constructor.Invoke(values);
            preview.BluetoothWindowMouseEnabled = absolute;
            SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => preview.Handle));
            nint Message(int message, nint point = 0) =>
                (nint)KeyboardCall(preview, "WindowProcedure", preview.Handle, message, (nint)0, point, false)!;
            enabled = true;
            if (absolute) preview.PrepareForBluetoothControl();
            else preview.PrepareForUsbControl();
            var rectArgs = new object?[] { preview.Handle, null };
            type.GetMethod("GetWindowRect", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, rectArgs);
            var rect = rectArgs[1]!;
            var centerX = ((int)KeyboardField(rect, "Left") + (int)KeyboardField(rect, "Right")) / 2;
            var centerY = ((int)KeyboardField(rect, "Top") + (int)KeyboardField(rect, "Bottom")) / 2;
            var packedCenter = (nint)((centerY << 16) | (centerX & 0xffff));
            InteractionAssert(Message(0x84, packedCenter) == 1 && preview.AllowsControlInput,
                $"{mode}: the picture must stay controllable without an explicit fix step");
            Message(0x201, packedCenter);
            InteractionAssert(pointers.Any(p => p.Kind == Controls.PreviewPointerKind.ButtonDown),
                $"{mode}: the initial press must reach the phone");
            if (absolute)
            {
                foreach (var side in new[] { 1, 2 })
                {
                    var beforeSide = pointers.Count;
                    KeyboardCall(preview, "WindowProcedure", preview.Handle, 0x020B,
                        (nint)(side << 16), (nint)0, false);
                    InteractionAssert(InteractionGetCapture() != preview.Handle,
                        "absolute Bluetooth side buttons must leave Windows capture free during a left drag");
                    KeyboardCall(preview, "WindowProcedure", preview.Handle, 0x020C,
                        (nint)(side << 16), (nint)0, false);
                    InteractionAssert(pointers.Skip(beforeSide).All(p => p.Kind != Controls.PreviewPointerKind.Reset),
                        "releasing a side button must not reset an active absolute Bluetooth drag");
                }
            }
            KeyboardCall(preview, "ToggleFullScreen");
            Message(0x201, packedCenter);
            pointers.Clear(); keys.Clear();
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert(pointers.Any(p => p.Kind == Controls.PreviewPointerKind.Reset) &&
                keys.Any(k => k.Kind == Controls.PreviewKeyboardKind.Reset) &&
                (byte)KeyboardField(preview, "_capturedMouseButtons") == 0,
                $"{mode}: returning from fullscreen must release held inputs");
            pointers.Clear(); keys.Clear();
            Message(0x201, packedCenter); Message(0x202, packedCenter);
            InteractionAssert(preview.AllowsControlInput && pointers.Count == 2,
                $"{mode}: touch input must resume after leaving fullscreen");
            enabled = false;
        }
        Console.WriteLine("PASS drag bar wired/wireless/absolute Bluetooth: input never paused; fullscreen exit releases held state.");
    }

    private static void TestMappingAdmission(App app, bool absolute = false)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (ViewModels.MainViewModel)KeyboardField(main, "_viewModel");
        var manager = (Services.MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (System.Collections.IDictionary)KeyboardField(manager, "_windows");
        var deviceCtor = typeof(Models.DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (Models.DeviceViewModel)deviceCtor.Invoke(["unfix-mapping-phone", "Test phone", "iPhone13,2", "18.0", "USB", "",
            Enum.Parse(deviceCtor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        var ctor = typeof(Windows.NativePreviewWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = ctor.GetParameters();
        var values = parameters.Select(p => p.DefaultValue).ToArray();
        object[] required = [390u, 844u, "Mapping admission", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, values, required.Length);
        values[Array.FindIndex(parameters, p => p.Name == "pointerInput")] = (Action<Controls.PreviewPointerEventArgs>)(_ => { });
        values[Array.FindIndex(parameters, p => p.Name == "isUsbControlEnabled")] = (Func<bool>)(() => !absolute);
        values[Array.FindIndex(parameters, p => p.Name == "isReverseControlEnabled")] = (Func<nint, bool>)(_ => absolute);
        using var preview = (Windows.NativePreviewWindow)ctor.Invoke(values);
        preview.BluetoothWindowMouseEnabled = absolute;
        windows[device.Udid] = preview;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => preview.Handle));
        try
        {
            if (absolute) manager.PrepareBluetoothControlWindow(device.Udid);
            else preview.PrepareForUsbControl();
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "the control preview must admit key mappings");
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "fullscreen must admit mappings on the control surface");
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "leaving fullscreen must keep mapping admission");
            Console.WriteLine("PASS key mappings: admitted in windowed and fullscreen layouts without a fix step.");
        }
        finally
        {
            windows.Remove(device.Udid);
            CloseWorkspaceTestWindow(main);
        }
    }
}
