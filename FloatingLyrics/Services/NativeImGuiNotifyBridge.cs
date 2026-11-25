using System.Runtime.InteropServices;

namespace FloatingLyrics.Services;

internal static class NativeImGuiNotifyBridge
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeNotifyCallback();

    private static NativeNotifyCallback? onUpdateDelegate;
    private static NativeNotifyCallback? onClosedDelegate;
    private static bool initialized;

    public static void Initialize(Action onUpdateClicked, Action onClosed)
    {
        if (initialized)
        {
            return;
        }

        try
        {
            onUpdateDelegate = () => onUpdateClicked();
            onClosedDelegate = () => onClosed();
            InitImGuiNotify(onUpdateDelegate, onClosedDelegate);
            initialized = true;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"ImGuiNotify: init failed {ex.Message}");
        }
    }

    public static void Show(string version)
    {
        if (!initialized)
        {
            return;
        }

        try
        {
            ShowImGuiUpdateNotification(version ?? string.Empty);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"ImGuiNotify: show failed {ex.Message}");
        }
    }

    public static void Hide()
    {
        if (!initialized)
        {
            return;
        }

        try
        {
            HideImGuiUpdateNotification();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"ImGuiNotify: hide failed {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        if (!initialized)
        {
            return;
        }

        initialized = false;
        try
        {
            ShutdownImGuiNotify();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"ImGuiNotify: shutdown failed {ex.Message}");
        }
    }

    [DllImport("ImGuiNotify.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void InitImGuiNotify(NativeNotifyCallback onUpdate, NativeNotifyCallback onClosed);

    [DllImport("ImGuiNotify.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern void ShowImGuiUpdateNotification(string versionUtf16);

    [DllImport("ImGuiNotify.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void HideImGuiUpdateNotification();

    [DllImport("ImGuiNotify.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void ShutdownImGuiNotify();
}
