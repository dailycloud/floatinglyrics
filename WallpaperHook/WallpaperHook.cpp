#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d9.h>
#include <dxgi1_2.h>
#include <cstdint>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")

struct SharedWallpaperHeader
{
    volatile LONG FrameSeq;
    UINT32 Width;
    UINT32 Height;
    UINT32 Format;
    INT32 OriginX;
    INT32 OriginY;
    UINT64 SharedHandle;
};

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using D3D9PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
using D3D9PresentExFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9Ex*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
using D3D9SwapPresentFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DSwapChain9*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
using Direct3DCreate9ExFn = HRESULT(WINAPI*)(UINT, IDirect3D9Ex**);

static HMODULE g_module = nullptr;
static PresentFn g_origPresent = nullptr;
static Present1Fn g_origPresent1 = nullptr;
static void** g_swapChainVtable = nullptr;
static PresentFn g_origFlipPresent = nullptr;
static Present1Fn g_origFlipPresent1 = nullptr;
static void** g_flipSwapChainVtable = nullptr;
static D3D9PresentFn g_origD3D9Present = nullptr;
static D3D9PresentExFn g_origD3D9PresentEx = nullptr;
static D3D9SwapPresentFn g_origD3D9SwapPresent = nullptr;
static void** g_d3d9DevVtable = nullptr;
static void** g_d3d9SwapVtable = nullptr;
static volatile LONG g_hookInstalled = 0;
static volatile LONG g_unloading = 0;
static volatile LONG g_inHookCount = 0;

static CRITICAL_SECTION g_lock;
static ID3D11Device* g_lastDevice = nullptr;
static ID3D11Texture2D* g_sharedTex = nullptr;
static HANDLE g_sharedHandle = nullptr;
static UINT32 g_sharedW = 0;
static UINT32 g_sharedH = 0;
static DXGI_FORMAT g_sharedFmt = DXGI_FORMAT_UNKNOWN;

static IDirect3DDevice9* g_lastD3D9Device = nullptr;
static IDirect3DSurface9* g_d3d9SharedSurf = nullptr;
static IDirect3DQuery9* g_d3d9FlushQuery = nullptr;
static HANDLE g_d3d9SharedHandle = nullptr;
static UINT32 g_d3d9W = 0;
static UINT32 g_d3d9H = 0;

static HANDLE g_mapFile = nullptr;
static SharedWallpaperHeader* g_header = nullptr;
static HANDLE g_frameEvent = nullptr;
static HANDLE g_stopEvent = nullptr;

static void ReleaseSharedResources()
{
    if (g_sharedTex)
    {
        g_sharedTex->Release();
        g_sharedTex = nullptr;
    }

    g_sharedHandle = nullptr;
    g_sharedW = 0;
    g_sharedH = 0;
    g_sharedFmt = DXGI_FORMAT_UNKNOWN;
    g_lastDevice = nullptr;

    if (g_d3d9FlushQuery)
    {
        g_d3d9FlushQuery->Release();
        g_d3d9FlushQuery = nullptr;
    }

    if (g_d3d9SharedSurf)
    {
        g_d3d9SharedSurf->Release();
        g_d3d9SharedSurf = nullptr;
    }

    g_d3d9SharedHandle = nullptr;
    g_d3d9W = 0;
    g_d3d9H = 0;
    g_lastD3D9Device = nullptr;
}

static void EnsureIpcObjects()
{
    if (!g_mapFile)
    {
        g_mapFile = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            sizeof(SharedWallpaperHeader),
            L"Local\\FloatingLyrics_WE_Shared");
        if (g_mapFile)
        {
            g_header = static_cast<SharedWallpaperHeader*>(
                MapViewOfFile(g_mapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedWallpaperHeader)));
            if (g_header)
            {
                ZeroMemory(g_header, sizeof(SharedWallpaperHeader));
            }
        }
    }

    if (!g_frameEvent)
    {
        g_frameEvent = CreateEventW(nullptr, FALSE, FALSE, L"Local\\FloatingLyrics_WE_FrameEvent");
    }

    if (!g_stopEvent)
    {
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, L"Local\\FloatingLyrics_WE_StopEvent");
    }
}

static DXGI_FORMAT NormalizeFormat(DXGI_FORMAT fmt)
{
    switch (fmt)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    default:
        return fmt;
    }
}

static void CaptureSwapChainFrame(IDXGISwapChain* swapChain)
{
    if (InterlockedCompareExchange(&g_unloading, 0, 0) != 0)
    {
        return;
    }

    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(swapChain->GetDesc(&desc)))
    {
        return;
    }

    if (desc.BufferDesc.Width < 200 || desc.BufferDesc.Height < 200)
    {
        return;
    }

    ID3D11Texture2D* backBuffer = nullptr;
    if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer))) || !backBuffer)
    {
        return;
    }

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    backBuffer->GetDevice(&device);
    if (device)
    {
        device->GetImmediateContext(&context);
    }

    if (!device || !context)
    {
        if (context) context->Release();
        if (device) device->Release();
        backBuffer->Release();
        return;
    }

    D3D11_TEXTURE2D_DESC bbDesc{};
    backBuffer->GetDesc(&bbDesc);
    DXGI_FORMAT targetFmt = NormalizeFormat(bbDesc.Format);

    EnterCriticalSection(&g_lock);
    EnsureIpcObjects();

    if (!g_sharedTex ||
        g_lastDevice != device ||
        g_sharedW != bbDesc.Width ||
        g_sharedH != bbDesc.Height ||
        g_sharedFmt != targetFmt)
    {
        ReleaseSharedResources();

        D3D11_TEXTURE2D_DESC sd{};
        sd.Width = bbDesc.Width;
        sd.Height = bbDesc.Height;
        sd.MipLevels = 1;
        sd.ArraySize = 1;
        sd.Format = targetFmt;
        sd.SampleDesc.Count = 1;
        sd.SampleDesc.Quality = 0;
        sd.Usage = D3D11_USAGE_DEFAULT;
        sd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        sd.CPUAccessFlags = 0;
        sd.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

        if (SUCCEEDED(device->CreateTexture2D(&sd, nullptr, &g_sharedTex)) && g_sharedTex)
        {
            IDXGIResource* dxgiRes = nullptr;
            if (SUCCEEDED(g_sharedTex->QueryInterface(__uuidof(IDXGIResource), reinterpret_cast<void**>(&dxgiRes))) && dxgiRes)
            {
                HANDLE hShared = nullptr;
                if (SUCCEEDED(dxgiRes->GetSharedHandle(&hShared)) && hShared)
                {
                    g_sharedHandle = hShared;
                    g_sharedW = bbDesc.Width;
                    g_sharedH = bbDesc.Height;
                    g_sharedFmt = targetFmt;
                    g_lastDevice = device;
                }
                else
                {
                    ReleaseSharedResources();
                }

                dxgiRes->Release();
            }
            else
            {
                ReleaseSharedResources();
            }
        }
    }

    if (g_sharedTex && g_sharedHandle && g_header)
    {
        context->CopyResource(g_sharedTex, backBuffer);
        context->Flush();

        RECT winRect{};
        if (desc.OutputWindow && GetWindowRect(desc.OutputWindow, &winRect))
        {
            g_header->OriginX = winRect.left;
            g_header->OriginY = winRect.top;
        }
        else
        {
            g_header->OriginX = 0;
            g_header->OriginY = 0;
        }

        g_header->Width = g_sharedW;
        g_header->Height = g_sharedH;
        g_header->Format = static_cast<UINT32>(g_sharedFmt);
        g_header->SharedHandle = reinterpret_cast<UINT64>(g_sharedHandle);
        InterlockedIncrement(&g_header->FrameSeq);

        if (g_frameEvent)
        {
            SetEvent(g_frameEvent);
        }
    }

    LeaveCriticalSection(&g_lock);

    context->Release();
    device->Release();
    backBuffer->Release();
}

static HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
{
    InterlockedIncrement(&g_inHookCount);
    if ((flags & DXGI_PRESENT_TEST) == 0)
    {
        CaptureSwapChainFrame(swapChain);
    }

    void** vt = *reinterpret_cast<void***>(swapChain);
    PresentFn orig = (vt == g_flipSwapChainVtable && g_origFlipPresent) ? g_origFlipPresent : g_origPresent;
    HRESULT hr = orig(swapChain, syncInterval, flags);
    InterlockedDecrement(&g_inHookCount);
    return hr;
}

static HRESULT STDMETHODCALLTYPE HookedPresent1(
    IDXGISwapChain1* swapChain,
    UINT syncInterval,
    UINT flags,
    const DXGI_PRESENT_PARAMETERS* params)
{
    InterlockedIncrement(&g_inHookCount);
    if ((flags & DXGI_PRESENT_TEST) == 0)
    {
        CaptureSwapChainFrame(swapChain);
    }

    void** vt = *reinterpret_cast<void***>(swapChain);
    Present1Fn orig = (vt == g_flipSwapChainVtable && g_origFlipPresent1) ? g_origFlipPresent1 : g_origPresent1;
    HRESULT hr = orig(swapChain, syncInterval, flags, params);
    InterlockedDecrement(&g_inHookCount);
    return hr;
}

static void CaptureD3D9Frame(IDirect3DDevice9* dev, IDirect3DSwapChain9* swapChain, HWND destOverride)
{
    if (InterlockedCompareExchange(&g_unloading, 0, 0) != 0 || !dev)
    {
        return;
    }

    IDirect3DSurface9* backBuffer = nullptr;
    HRESULT hr = swapChain
        ? swapChain->GetBackBuffer(0, D3DBACKBUFFER_TYPE_MONO, &backBuffer)
        : dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &backBuffer);
    if (FAILED(hr) || !backBuffer)
    {
        return;
    }

    D3DSURFACE_DESC desc{};
    if (FAILED(backBuffer->GetDesc(&desc)) || desc.Width < 200 || desc.Height < 200)
    {
        backBuffer->Release();
        return;
    }

    IDirect3DDevice9Ex* devEx = nullptr;
    if (FAILED(dev->QueryInterface(__uuidof(IDirect3DDevice9Ex), reinterpret_cast<void**>(&devEx))) || !devEx)
    {
        backBuffer->Release();
        return;
    }

    EnterCriticalSection(&g_lock);
    EnsureIpcObjects();

    if (!g_d3d9SharedSurf ||
        g_lastD3D9Device != dev ||
        g_d3d9W != desc.Width ||
        g_d3d9H != desc.Height)
    {
        ReleaseSharedResources();
        HANDLE hShared = nullptr;
        if (SUCCEEDED(devEx->CreateRenderTarget(
                desc.Width,
                desc.Height,
                D3DFMT_A8R8G8B8,
                D3DMULTISAMPLE_NONE,
                0,
                FALSE,
                &g_d3d9SharedSurf,
                &hShared)) &&
            g_d3d9SharedSurf &&
            hShared)
        {
            g_d3d9SharedHandle = hShared;
            g_d3d9W = desc.Width;
            g_d3d9H = desc.Height;
            g_lastD3D9Device = dev;
            dev->CreateQuery(D3DQUERYTYPE_EVENT, &g_d3d9FlushQuery);
        }
        else
        {
            ReleaseSharedResources();
        }
    }

    if (g_d3d9SharedSurf && g_d3d9SharedHandle && g_header)
    {
        if (SUCCEEDED(dev->StretchRect(backBuffer, nullptr, g_d3d9SharedSurf, nullptr, D3DTEXF_NONE)))
        {
            if (g_d3d9FlushQuery)
            {
                g_d3d9FlushQuery->Issue(D3DISSUE_END);
                g_d3d9FlushQuery->GetData(nullptr, 0, D3DGETDATA_FLUSH);
            }

            HWND targetWnd = destOverride;
            if (!targetWnd && swapChain)
            {
                D3DPRESENT_PARAMETERS pp{};
                if (SUCCEEDED(swapChain->GetPresentParameters(&pp)))
                {
                    targetWnd = pp.hDeviceWindow;
                }
            }
            if (!targetWnd)
            {
                D3DDEVICE_CREATION_PARAMETERS cp{};
                if (SUCCEEDED(dev->GetCreationParameters(&cp)))
                {
                    targetWnd = cp.hFocusWindow;
                }
            }

            RECT winRect{};
            if (targetWnd && GetWindowRect(targetWnd, &winRect))
            {
                g_header->OriginX = winRect.left;
                g_header->OriginY = winRect.top;
            }
            else
            {
                g_header->OriginX = 0;
                g_header->OriginY = 0;
            }

            g_header->Width = g_d3d9W;
            g_header->Height = g_d3d9H;
            g_header->Format = static_cast<UINT32>(DXGI_FORMAT_B8G8R8A8_UNORM);
            g_header->SharedHandle = reinterpret_cast<UINT64>(g_d3d9SharedHandle);
            InterlockedIncrement(&g_header->FrameSeq);

            if (g_frameEvent)
            {
                SetEvent(g_frameEvent);
            }
        }
    }

    LeaveCriticalSection(&g_lock);

    devEx->Release();
    backBuffer->Release();
}

static HRESULT STDMETHODCALLTYPE HookedD3D9Present(
    IDirect3DDevice9* dev,
    const RECT* src,
    const RECT* dst,
    HWND destWnd,
    const RGNDATA* dirty)
{
    InterlockedIncrement(&g_inHookCount);
    CaptureD3D9Frame(dev, nullptr, destWnd);
    HRESULT hr = g_origD3D9Present(dev, src, dst, destWnd, dirty);
    InterlockedDecrement(&g_inHookCount);
    return hr;
}

static HRESULT STDMETHODCALLTYPE HookedD3D9PresentEx(
    IDirect3DDevice9Ex* dev,
    const RECT* src,
    const RECT* dst,
    HWND destWnd,
    const RGNDATA* dirty,
    DWORD flags)
{
    InterlockedIncrement(&g_inHookCount);
    CaptureD3D9Frame(dev, nullptr, destWnd);
    HRESULT hr = g_origD3D9PresentEx(dev, src, dst, destWnd, dirty, flags);
    InterlockedDecrement(&g_inHookCount);
    return hr;
}

static HRESULT STDMETHODCALLTYPE HookedD3D9SwapPresent(
    IDirect3DSwapChain9* swapChain,
    const RECT* src,
    const RECT* dst,
    HWND destWnd,
    const RGNDATA* dirty,
    DWORD flags)
{
    InterlockedIncrement(&g_inHookCount);
    IDirect3DDevice9* dev = nullptr;
    if (SUCCEEDED(swapChain->GetDevice(&dev)) && dev)
    {
        CaptureD3D9Frame(dev, swapChain, destWnd);
        dev->Release();
    }

    HRESULT hr = g_origD3D9SwapPresent(swapChain, src, dst, destWnd, dirty, flags);
    InterlockedDecrement(&g_inHookCount);
    return hr;
}

static bool InstallD3D9Hook()
{
    HMODULE d3d9Mod = GetModuleHandleW(L"d3d9.dll");
    if (!d3d9Mod)
    {
        return false;
    }

    auto create9Ex = reinterpret_cast<Direct3DCreate9ExFn>(GetProcAddress(d3d9Mod, "Direct3DCreate9Ex"));
    if (!create9Ex)
    {
        return false;
    }

    IDirect3D9Ex* d3d9 = nullptr;
    if (FAILED(create9Ex(D3D_SDK_VERSION, &d3d9)) || !d3d9)
    {
        return false;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = g_module;
    wc.lpszClassName = L"FloatingLyricsDummyD3D9Wnd";
    RegisterClassExW(&wc);

    HWND dummyWnd = CreateWindowExW(
        0,
        wc.lpszClassName,
        L"",
        WS_OVERLAPPEDWINDOW,
        0, 0, 100, 100,
        nullptr, nullptr, g_module, nullptr);

    bool ok = false;
    IDirect3DDevice9Ex* dev = nullptr;
    if (dummyWnd)
    {
        D3DPRESENT_PARAMETERS pp{};
        pp.Windowed = TRUE;
        pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
        pp.hDeviceWindow = dummyWnd;
        pp.BackBufferFormat = D3DFMT_A8R8G8B8;
        pp.BackBufferWidth = 100;
        pp.BackBufferHeight = 100;

        if (SUCCEEDED(d3d9->CreateDeviceEx(
                D3DADAPTER_DEFAULT,
                D3DDEVTYPE_HAL,
                dummyWnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_NOWINDOWCHANGES,
                &pp,
                nullptr,
                &dev)) &&
            dev)
        {
            void** vtable = *reinterpret_cast<void***>(dev);
            g_d3d9DevVtable = vtable;

            DWORD oldProtect = 0;
            if (VirtualProtect(&vtable[17], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
            {
                g_origD3D9Present = reinterpret_cast<D3D9PresentFn>(
                    InterlockedExchangePointer(&vtable[17], reinterpret_cast<void*>(HookedD3D9Present)));
                VirtualProtect(&vtable[17], sizeof(void*), oldProtect, &oldProtect);
                ok = true;
            }

            DWORD exProtect = 0;
            if (VirtualProtect(&vtable[121], sizeof(void*), PAGE_EXECUTE_READWRITE, &exProtect))
            {
                g_origD3D9PresentEx = reinterpret_cast<D3D9PresentExFn>(
                    InterlockedExchangePointer(&vtable[121], reinterpret_cast<void*>(HookedD3D9PresentEx)));
                VirtualProtect(&vtable[121], sizeof(void*), exProtect, &exProtect);
            }

            IDirect3DSwapChain9* sc = nullptr;
            if (SUCCEEDED(dev->GetSwapChain(0, &sc)) && sc)
            {
                void** scVtable = *reinterpret_cast<void***>(sc);
                g_d3d9SwapVtable = scVtable;
                DWORD scProtect = 0;
                if (VirtualProtect(&scVtable[3], sizeof(void*), PAGE_EXECUTE_READWRITE, &scProtect))
                {
                    g_origD3D9SwapPresent = reinterpret_cast<D3D9SwapPresentFn>(
                        InterlockedExchangePointer(&scVtable[3], reinterpret_cast<void*>(HookedD3D9SwapPresent)));
                    VirtualProtect(&scVtable[3], sizeof(void*), scProtect, &scProtect);
                }
                sc->Release();
            }

            dev->Release();
        }

        DestroyWindow(dummyWnd);
    }

    UnregisterClassW(wc.lpszClassName, g_module);
    d3d9->Release();
    return ok;
}

static bool InstallVtableHook()
{
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = g_module;
    wc.lpszClassName = L"FloatingLyricsDummyDxgiWnd";
    RegisterClassExW(&wc);

    HWND dummyWnd = CreateWindowExW(
        0,
        wc.lpszClassName,
        L"",
        WS_OVERLAPPEDWINDOW,
        0, 0, 100, 100,
        nullptr, nullptr, g_module, nullptr);
    if (!dummyWnd)
    {
        UnregisterClassW(wc.lpszClassName, g_module);
        return false;
    }

    DXGI_SWAP_CHAIN_DESC scd{};
    scd.BufferCount = 1;
    scd.BufferDesc.Width = 100;
    scd.BufferDesc.Height = 100;
    scd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.OutputWindow = dummyWnd;
    scd.SampleDesc.Count = 1;
    scd.Windowed = TRUE;
    scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    IDXGISwapChain* dummySwap = nullptr;
    ID3D11Device* dummyDev = nullptr;
    ID3D11DeviceContext* dummyCtx = nullptr;

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0,
        &fl,
        1,
        D3D11_SDK_VERSION,
        &scd,
        &dummySwap,
        &dummyDev,
        nullptr,
        &dummyCtx);

    bool ok = false;
    if (SUCCEEDED(hr) && dummySwap)
    {
        void** vtable = *reinterpret_cast<void***>(dummySwap);
        g_swapChainVtable = vtable;

        DWORD oldProtect = 0;
        if (VirtualProtect(&vtable[8], sizeof(void*) * 16, PAGE_EXECUTE_READWRITE, &oldProtect))
        {
            g_origPresent = reinterpret_cast<PresentFn>(
                InterlockedExchangePointer(&vtable[8], reinterpret_cast<void*>(HookedPresent)));

            IDXGISwapChain1* dummySwap1 = nullptr;
            if (SUCCEEDED(dummySwap->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&dummySwap1))) && dummySwap1)
            {
                void** vtable1 = *reinterpret_cast<void***>(dummySwap1);
                if (vtable1 == vtable)
                {
                    g_origPresent1 = reinterpret_cast<Present1Fn>(
                        InterlockedExchangePointer(&vtable[22], reinterpret_cast<void*>(HookedPresent1)));
                }
                dummySwap1->Release();
            }

            VirtualProtect(&vtable[8], sizeof(void*) * 16, oldProtect, &oldProtect);
            ok = true;
        }

        if (dummyDev)
        {
            IDXGIDevice* dxgiDev = nullptr;
            if (SUCCEEDED(dummyDev->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDev))) && dxgiDev)
            {
                IDXGIAdapter* adapter = nullptr;
                if (SUCCEEDED(dxgiDev->GetAdapter(&adapter)) && adapter)
                {
                    IDXGIFactory2* factory2 = nullptr;
                    if (SUCCEEDED(adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory2))) && factory2)
                    {
                        DXGI_SWAP_CHAIN_DESC1 scd1{};
                        scd1.Width = 100;
                        scd1.Height = 100;
                        scd1.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                        scd1.SampleDesc.Count = 1;
                        scd1.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                        scd1.BufferCount = 2;
                        scd1.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

                        IDXGISwapChain1* flipSwap = nullptr;
                        if (SUCCEEDED(factory2->CreateSwapChainForHwnd(dummyDev, dummyWnd, &scd1, nullptr, nullptr, &flipSwap)) && flipSwap)
                        {
                            void** flipVtable = *reinterpret_cast<void***>(flipSwap);
                            if (flipVtable && flipVtable != g_swapChainVtable)
                            {
                                g_flipSwapChainVtable = flipVtable;
                                DWORD fpProtect = 0;
                                if (VirtualProtect(&flipVtable[8], sizeof(void*) * 16, PAGE_EXECUTE_READWRITE, &fpProtect))
                                {
                                    g_origFlipPresent = reinterpret_cast<PresentFn>(
                                        InterlockedExchangePointer(&flipVtable[8], reinterpret_cast<void*>(HookedPresent)));
                                    g_origFlipPresent1 = reinterpret_cast<Present1Fn>(
                                        InterlockedExchangePointer(&flipVtable[22], reinterpret_cast<void*>(HookedPresent1)));
                                    VirtualProtect(&flipVtable[8], sizeof(void*) * 16, fpProtect, &fpProtect);
                                    ok = true;
                                }
                            }

                            flipSwap->Release();
                        }

                        factory2->Release();
                    }

                    adapter->Release();
                }

                dxgiDev->Release();
            }
        }
    }

    if (dummyCtx) dummyCtx->Release();
    if (dummyDev) dummyDev->Release();
    if (dummySwap) dummySwap->Release();
    DestroyWindow(dummyWnd);
    UnregisterClassW(wc.lpszClassName, g_module);
    return ok;
}

static void UninstallVtableHook()
{
    InterlockedExchange(&g_unloading, 1);

    if (g_swapChainVtable && g_origPresent)
    {
        DWORD oldProtect = 0;
        if (VirtualProtect(&g_swapChainVtable[8], sizeof(void*) * 16, PAGE_EXECUTE_READWRITE, &oldProtect))
        {
            InterlockedExchangePointer(&g_swapChainVtable[8], reinterpret_cast<void*>(g_origPresent));
            if (g_origPresent1)
            {
                InterlockedExchangePointer(&g_swapChainVtable[22], reinterpret_cast<void*>(g_origPresent1));
            }
            VirtualProtect(&g_swapChainVtable[8], sizeof(void*) * 16, oldProtect, &oldProtect);
        }
    }

    if (g_flipSwapChainVtable && g_origFlipPresent)
    {
        DWORD fpProtect = 0;
        if (VirtualProtect(&g_flipSwapChainVtable[8], sizeof(void*) * 16, PAGE_EXECUTE_READWRITE, &fpProtect))
        {
            InterlockedExchangePointer(&g_flipSwapChainVtable[8], reinterpret_cast<void*>(g_origFlipPresent));
            if (g_origFlipPresent1)
            {
                InterlockedExchangePointer(&g_flipSwapChainVtable[22], reinterpret_cast<void*>(g_origFlipPresent1));
            }
            VirtualProtect(&g_flipSwapChainVtable[8], sizeof(void*) * 16, fpProtect, &fpProtect);
        }
    }

    if (g_d3d9DevVtable && g_origD3D9Present)
    {
        DWORD oldProtect = 0;
        if (VirtualProtect(&g_d3d9DevVtable[17], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
        {
            InterlockedExchangePointer(&g_d3d9DevVtable[17], reinterpret_cast<void*>(g_origD3D9Present));
            VirtualProtect(&g_d3d9DevVtable[17], sizeof(void*), oldProtect, &oldProtect);
        }

        if (g_origD3D9PresentEx)
        {
            DWORD exProtect = 0;
            if (VirtualProtect(&g_d3d9DevVtable[121], sizeof(void*), PAGE_EXECUTE_READWRITE, &exProtect))
            {
                InterlockedExchangePointer(&g_d3d9DevVtable[121], reinterpret_cast<void*>(g_origD3D9PresentEx));
                VirtualProtect(&g_d3d9DevVtable[121], sizeof(void*), exProtect, &exProtect);
            }
        }
    }

    if (g_d3d9SwapVtable && g_origD3D9SwapPresent)
    {
        DWORD scProtect = 0;
        if (VirtualProtect(&g_d3d9SwapVtable[3], sizeof(void*), PAGE_EXECUTE_READWRITE, &scProtect))
        {
            InterlockedExchangePointer(&g_d3d9SwapVtable[3], reinterpret_cast<void*>(g_origD3D9SwapPresent));
            VirtualProtect(&g_d3d9SwapVtable[3], sizeof(void*), scProtect, &scProtect);
        }
    }

    for (int i = 0; i < 100 && InterlockedCompareExchange(&g_inHookCount, 0, 0) > 0; ++i)
    {
        Sleep(2);
    }
    Sleep(50);

    EnterCriticalSection(&g_lock);
    ReleaseSharedResources();
    if (g_header)
    {
        UnmapViewOfFile(g_header);
        g_header = nullptr;
    }
    if (g_mapFile)
    {
        CloseHandle(g_mapFile);
        g_mapFile = nullptr;
    }
    if (g_frameEvent)
    {
        CloseHandle(g_frameEvent);
        g_frameEvent = nullptr;
    }
    if (g_stopEvent)
    {
        CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
    }
    LeaveCriticalSection(&g_lock);
}

static DWORD WINAPI HookMainThread(LPVOID)
{
    InitializeCriticalSection(&g_lock);
    EnsureIpcObjects();

    bool dxgiOk = InstallVtableHook();
    bool d3d9Ok = InstallD3D9Hook();
    if (dxgiOk || d3d9Ok)
    {
        InterlockedExchange(&g_hookInstalled, 1);
        if (g_stopEvent)
        {
            WaitForSingleObject(g_stopEvent, INFINITE);
        }
    }

    UninstallVtableHook();
    DeleteCriticalSection(&g_lock);
    FreeLibraryAndExitThread(g_module, 0);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = hModule;
        DisableThreadLibraryCalls(hModule);
        HANDLE thread = CreateThread(nullptr, 0, HookMainThread, nullptr, 0, nullptr);
        if (thread)
        {
            CloseHandle(thread);
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(&g_unloading, 1);
    }

    return TRUE;
}
