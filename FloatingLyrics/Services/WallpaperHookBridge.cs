using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Windows.Graphics;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace FloatingLyrics.Services;

internal sealed unsafe class WallpaperHookBridge
{
    private static readonly string[] ProcessNames = ["wallpaper64", "wallpaper32", "webwallpaper64", "webwallpaper32"];
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11DeviceIid = new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");
    private static readonly Guid DxgiSurfaceIid = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");

    private const uint ProcessInjectAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct SharedWallpaperHeader
    {
        public int FrameSeq;
        public uint Width;
        public uint Height;
        public uint Format;
        public int OriginX;
        public int OriginY;
        public ulong SharedHandle;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiInterfaceDelegate(nint thisPtr, ref Guid iid, out nint p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenSharedResourceDelegate(nint thisPtr, nint hResource, ref Guid returnedInterface, out nint ppResource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint hProcess, nint lpThreadAttributes, nuint dwStackSize, nint lpStartAddress, nint lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);

    private readonly HashSet<int> injectedPids = [];
    private DispatcherQueue? dispatcherQueue;
    private MemoryMappedFile? mappedFile;
    private MemoryMappedViewAccessor? viewAccessor;
    private EventWaitHandle? frameEvent;
    private CancellationTokenSource? waitCts;
    private Task? waitTask;
    private ulong openedHandle;
    private uint openedWidth;
    private uint openedHeight;
    private int uiUpdateScheduled;
    private bool isEnabled = true;

    public CanvasBitmap? LiveBitmap { get; private set; }
    public PointInt32 LiveOrigin { get; private set; }

    public event Action? FrameUpdated;

    public void Start(DispatcherQueue queue)
    {
        dispatcherQueue = queue;
        EnsureConnected();
    }

    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled)
        {
            return;
        }

        isEnabled = enabled;
        if (enabled)
        {
            EnsureConnected();
        }
    }

    public void EnsureConnected()
    {
        if (dispatcherQueue is null)
        {
            return;
        }

        TryInjectHook();
        if (mappedFile is null)
        {
            TryOpenIpc();
        }
    }

    public void Stop()
    {
        waitCts?.Cancel();
        waitCts?.Dispose();
        waitCts = null;
        waitTask = null;

        try
        {
            if (EventWaitHandle.TryOpenExisting("Local\\FloatingLyrics_WE_StopEvent", out var stopEvt))
            {
                using (stopEvt)
                {
                    stopEvt.Set();
                }
            }
        }
        catch
        {
        }

        viewAccessor?.Dispose();
        viewAccessor = null;
        mappedFile?.Dispose();
        mappedFile = null;
        frameEvent?.Dispose();
        frameEvent = null;

        LiveBitmap?.Dispose();
        LiveBitmap = null;
        openedHandle = 0;
        openedWidth = 0;
        openedHeight = 0;
        injectedPids.Clear();
        dispatcherQueue = null;
    }

    private void TryInjectHook()
    {
        var sourceDllPath = Path.Combine(AppContext.BaseDirectory, "WallpaperHook.dll");
        if (!File.Exists(sourceDllPath))
        {
            DebugTrace.Write($"Wallpaper: hook dll missing at {sourceDllPath}");
            return;
        }

        var targets = new List<Process>();
        foreach (var name in ProcessNames)
        {
            var list = Process.GetProcessesByName(name);
            foreach (var p in list)
            {
                if (injectedPids.Contains(p.Id))
                {
                    p.Dispose();
                    continue;
                }

                targets.Add(p);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        var hookDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingLyrics",
            "hook");
        Directory.CreateDirectory(hookDir);
        foreach (var oldFile in Directory.EnumerateFiles(hookDir, "WallpaperHook*.dll"))
        {
            try
            {
                File.Delete(oldFile);
            }
            catch
            {
            }
        }

        string? stagedDllPath = null;

        foreach (var target in targets)
        {
            using (target)
            {
                var hasGraphics = false;
                var alreadyLoaded = false;
                try
                {
                    foreach (ProcessModule module in target.Modules)
                    {
                        var modName = module.ModuleName;
                        if (modName.StartsWith("WallpaperHook", StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyLoaded = true;
                            break;
                        }

                        if (string.Equals(modName, "dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(modName, "d3d11.dll", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(modName, "d3d9.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            hasGraphics = true;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (alreadyLoaded)
                {
                    injectedPids.Add(target.Id);
                    continue;
                }

                if (!hasGraphics)
                {
                    continue;
                }

                if (stagedDllPath is null)
                {
                    stagedDllPath = Path.Combine(hookDir, $"WallpaperHook_{Environment.ProcessId}_{Environment.TickCount64}.dll");
                    File.Copy(sourceDllPath, stagedDllPath, overwrite: true);
                }

                try
                {
                    if (EventWaitHandle.TryOpenExisting("Local\\FloatingLyrics_WE_StopEvent", out var stopEvt))
                    {
                        using (stopEvt)
                        {
                            stopEvt.Reset();
                        }
                    }
                }
                catch
                {
                }

                var hProc = OpenProcess(ProcessInjectAccess, false, target.Id);
                if (hProc == 0)
                {
                    DebugTrace.Write($"Wallpaper: OpenProcess failed pid={target.Id} err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                nint remoteMem = 0;
                nint hThread = 0;
                try
                {
                    var bytes = Encoding.Unicode.GetBytes(stagedDllPath + "\0");
                    remoteMem = VirtualAllocEx(hProc, 0, (nuint)bytes.Length, MemCommitReserve, PageReadWrite);
                    if (remoteMem == 0)
                    {
                        continue;
                    }

                    if (!WriteProcessMemory(hProc, remoteMem, bytes, (nuint)bytes.Length, out _))
                    {
                        continue;
                    }

                    var kernel32 = GetModuleHandleW("kernel32.dll");
                    var loadLib = GetProcAddress(kernel32, "LoadLibraryW");
                    if (loadLib == 0)
                    {
                        continue;
                    }

                    hThread = CreateRemoteThread(hProc, 0, 0, loadLib, remoteMem, 0, out _);
                    if (hThread == 0)
                    {
                        continue;
                    }

                    _ = WaitForSingleObject(hThread, 4000);
                    injectedPids.Add(target.Id);
                    DebugTrace.Write($"Wallpaper: hook injected into {target.ProcessName} pid={target.Id}");
                }
                finally
                {
                    if (hThread != 0)
                    {
                        CloseHandle(hThread);
                    }

                    if (remoteMem != 0)
                    {
                        VirtualFreeEx(hProc, remoteMem, 0, MemRelease);
                    }

                    CloseHandle(hProc);
                }
            }
        }
    }

    private void TryOpenIpc()
    {
        try
        {
            if (!TryOpenExistingMmf("Local\\FloatingLyrics_WE_Shared", out var mmf) || mmf is null)
            {
                return;
            }

            if (!EventWaitHandle.TryOpenExisting("Local\\FloatingLyrics_WE_FrameEvent", out var evt))
            {
                mmf.Dispose();
                return;
            }

            mappedFile = mmf;
            viewAccessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<SharedWallpaperHeader>(), MemoryMappedFileAccess.Read);
            frameEvent = evt;

            waitCts = new CancellationTokenSource();
            var token = waitCts.Token;
            waitTask = Task.Run(() => FrameWaitLoop(evt, token), token);
            DebugTrace.Write("Wallpaper: hook IPC connected");
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: hook IPC failed {ex.Message}");
        }
    }

    private static bool TryOpenExistingMmf(string name, out MemoryMappedFile? result)
    {
        try
        {
            result = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private void FrameWaitLoop(EventWaitHandle evt, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!evt.WaitOne(250))
                {
                    continue;
                }

                if (!isEnabled || token.IsCancellationRequested)
                {
                    continue;
                }

                if (Interlocked.Exchange(ref uiUpdateScheduled, 1) == 0)
                {
                    var queue = dispatcherQueue;
                    if (queue is null || !queue.TryEnqueue(ProcessSharedFrameOnUi))
                    {
                        Interlocked.Exchange(ref uiUpdateScheduled, 0);
                    }
                }
            }
            catch
            {
                break;
            }
        }
    }

    private void ProcessSharedFrameOnUi()
    {
        Interlocked.Exchange(ref uiUpdateScheduled, 0);

        if (viewAccessor is null || !isEnabled)
        {
            return;
        }

        SharedWallpaperHeader header;
        viewAccessor.Read(0, out header);

        if (header.SharedHandle == 0 || header.Width < 64 || header.Height < 64)
        {
            return;
        }

        LiveOrigin = new PointInt32(header.OriginX, header.OriginY);

        if (LiveBitmap is null ||
            openedHandle != header.SharedHandle ||
            openedWidth != header.Width ||
            openedHeight != header.Height)
        {
            if (!TryOpenSharedCanvasBitmap((nint)header.SharedHandle, out var bitmap))
            {
                return;
            }

            LiveBitmap?.Dispose();
            LiveBitmap = bitmap;
            openedHandle = header.SharedHandle;
            openedWidth = header.Width;
            openedHeight = header.Height;
            DebugTrace.Write($"Wallpaper: hook shared bitmap opened {header.Width}x{header.Height} origin={header.OriginX},{header.OriginY}");
        }

        FrameUpdated?.Invoke();
    }

    private static bool TryOpenSharedCanvasBitmap(nint sharedHandle, out CanvasBitmap? bitmap)
    {
        bitmap = null;
        nint accessPtr = 0;
        nint d3dDevPtr = 0;
        nint dxgiSurfPtr = 0;
        nint d3dSurfAbi = 0;

        try
        {
            var canvasDevice = CanvasDevice.GetSharedDevice();
            var deviceAbi = ((IWinRTObject)canvasDevice).NativeObject.ThisPtr;
            if (deviceAbi == 0)
            {
                return false;
            }

            var accessIid = Direct3DDxgiInterfaceAccessIid;
            if (Marshal.QueryInterface(deviceAbi, in accessIid, out accessPtr) != 0 || accessPtr == 0)
            {
                return false;
            }

            var accessVtable = Marshal.ReadIntPtr(accessPtr);
            var getInterfacePtr = Marshal.ReadIntPtr(accessVtable, 3 * IntPtr.Size);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetDxgiInterfaceDelegate>(getInterfacePtr);

            var d3dIid = D3D11DeviceIid;
            if (getInterface(accessPtr, ref d3dIid, out d3dDevPtr) != 0 || d3dDevPtr == 0)
            {
                return false;
            }

            var devVtable = Marshal.ReadIntPtr(d3dDevPtr);
            var openSharedPtr = Marshal.ReadIntPtr(devVtable, 28 * IntPtr.Size);
            var openShared = Marshal.GetDelegateForFunctionPointer<OpenSharedResourceDelegate>(openSharedPtr);

            var surfIid = DxgiSurfaceIid;
            var hr = openShared(d3dDevPtr, sharedHandle, ref surfIid, out dxgiSurfPtr);
            if (hr != 0 || dxgiSurfPtr == 0)
            {
                DebugTrace.Write($"Wallpaper: hook OpenSharedResource hr=0x{hr:X8}");
                return false;
            }

            hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfPtr, out d3dSurfAbi);
            if (hr != 0 || d3dSurfAbi == 0)
            {
                DebugTrace.Write($"Wallpaper: hook CreateDirect3D11Surface hr=0x{hr:X8}");
                return false;
            }

            var d3dSurface = MarshalInterface<IDirect3DSurface>.FromAbi(d3dSurfAbi);
            bitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, d3dSurface, 96f);
            return true;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: hook open shared failed {ex.Message}");
            return false;
        }
        finally
        {
            if (d3dSurfAbi != 0) Marshal.Release(d3dSurfAbi);
            if (dxgiSurfPtr != 0) Marshal.Release(dxgiSurfPtr);
            if (d3dDevPtr != 0) Marshal.Release(d3dDevPtr);
            if (accessPtr != 0) Marshal.Release(accessPtr);
        }
    }
}
