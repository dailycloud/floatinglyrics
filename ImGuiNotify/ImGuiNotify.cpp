#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dwmapi.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include "imgui/imgui.h"
#include "imgui/backends/imgui_impl_win32.h"
#include "imgui/backends/imgui_impl_dx11.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

using NotifyCallback = void(__stdcall*)();

static CRITICAL_SECTION g_lock;
static bool g_lockInit = false;
static HANDLE g_thread = nullptr;
static HANDLE g_readyEvent = nullptr;
static volatile LONG g_running = 0;

static NotifyCallback g_onUpdate = nullptr;
static NotifyCallback g_onClosed = nullptr;

static HWND g_hwnd = nullptr;
static ID3D11Device* g_device = nullptr;
static ID3D11DeviceContext* g_context = nullptr;
static IDXGISwapChain* g_swapChain = nullptr;
static ID3D11RenderTargetView* g_rtv = nullptr;

static bool g_active = false;
static bool g_userClosed = false;
static bool g_visible = false;
static std::string g_versionText;
static ULONGLONG g_animStartTick = 0;

static constexpr int kWinWidth = 246;
static constexpr int kWinHeight = 96;
static constexpr int kMarginRight = 18;
static constexpr int kMarginBottom = 18;
static constexpr ULONGLONG kMsPerChar = 28;

static void EnsureLock()
{
    if (!g_lockInit)
    {
        InitializeCriticalSection(&g_lock);
        g_lockInit = true;
    }
}

static bool IsDesktopWindowClass(HWND wnd)
{
    if (!wnd)
    {
        return false;
    }

    wchar_t cls[64]{};
    int len = GetClassNameW(wnd, cls, 64);
    if (len <= 0)
    {
        return false;
    }

    return wcscmp(cls, L"Progman") == 0 ||
           wcscmp(cls, L"WorkerW") == 0 ||
           wcscmp(cls, L"Shell_TrayWnd") == 0;
}

static bool IsWindowCloaked(HWND wnd)
{
    BOOL cloaked = FALSE;
    if (SUCCEEDED(DwmGetWindowAttribute(wnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))))
    {
        return cloaked != FALSE;
    }

    return false;
}

static bool IsFloatingLyricsWindow(HWND wnd)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(wnd, &pid);
    return pid == GetCurrentProcessId();
}

static bool IsDesktopAreaExposed(const RECT& targetRect)
{
    HWND fg = GetForegroundWindow();
    if (fg && !IsDesktopWindowClass(fg) && !IsFloatingLyricsWindow(fg))
    {
        wchar_t fgCls[64]{};
        GetClassNameW(fg, fgCls, 64);
        if (wcscmp(fgCls, L"Windows.UI.Core.CoreWindow") == 0 ||
            wcscmp(fgCls, L"XamlExplorerHostPopupWindow") == 0)
        {
            return false;
        }
    }

    struct EnumState
    {
        RECT target;
        bool covered;
    } state{ targetRect, false };

    EnumWindows([](HWND wnd, LPARAM lp) -> BOOL
    {
        auto* s = reinterpret_cast<EnumState*>(lp);
        if (wnd == g_hwnd || !IsWindowVisible(wnd) || IsIconic(wnd))
        {
            return TRUE;
        }

        if (IsDesktopWindowClass(wnd) || IsFloatingLyricsWindow(wnd) || IsWindowCloaked(wnd))
        {
            return TRUE;
        }

        LONG_PTR ex = GetWindowLongPtrW(wnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_TOPMOST) == 0)
        {
            return TRUE;
        }

        RECT wr{};
        if (!GetWindowRect(wnd, &wr))
        {
            return TRUE;
        }

        if (wr.right <= wr.left || wr.bottom <= wr.top)
        {
            return TRUE;
        }

        RECT inter{};
        if (IntersectRect(&inter, &wr, &s->target))
        {
            int iw = inter.right - inter.left;
            int ih = inter.bottom - inter.top;
            if (iw > 8 && ih > 8)
            {
                s->covered = true;
                return FALSE;
            }
        }

        return TRUE;
    }, reinterpret_cast<LPARAM>(&state));

    return !state.covered;
}

static RECT ComputeTargetRect()
{
    RECT work{};
    if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0))
    {
        work = { 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN) };
    }

    RECT r{};
    r.right = work.right - kMarginRight;
    r.bottom = work.bottom - kMarginBottom;
    r.left = r.right - kWinWidth;
    r.top = r.bottom - kWinHeight;
    return r;
}

static void CreateRenderTarget()
{
    ID3D11Texture2D* backBuffer = nullptr;
    if (SUCCEEDED(g_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) && backBuffer)
    {
        g_device->CreateRenderTargetView(backBuffer, nullptr, &g_rtv);
        backBuffer->Release();
    }
}

static void CleanupDeviceD3D()
{
    if (g_rtv)
    {
        g_rtv->Release();
        g_rtv = nullptr;
    }

    if (g_swapChain)
    {
        g_swapChain->Release();
        g_swapChain = nullptr;
    }

    if (g_context)
    {
        g_context->Release();
        g_context = nullptr;
    }

    if (g_device)
    {
        g_device->Release();
        g_device = nullptr;
    }
}

static bool CreateDeviceD3D(HWND hWnd)
{
    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 2;
    sd.BufferDesc.Width = 0;
    sd.BufferDesc.Height = 0;
    sd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    D3D_FEATURE_LEVEL featureLevel{};
    const D3D_FEATURE_LEVEL levels[2] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0,
        levels,
        2,
        D3D11_SDK_VERSION,
        &sd,
        &g_swapChain,
        &g_device,
        &featureLevel,
        &g_context);
    if (FAILED(hr))
    {
        return false;
    }

    CreateRenderTarget();
    return g_rtv != nullptr;
}

static LRESULT WINAPI NotifyWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
    {
        return 1;
    }

    switch (msg)
    {
    case WM_SIZE:
        if (g_device && wParam != SIZE_MINIMIZED)
        {
            if (g_rtv)
            {
                g_rtv->Release();
                g_rtv = nullptr;
            }
            g_swapChain->ResizeBuffers(0, LOWORD(lParam), HIWORD(lParam), DXGI_FORMAT_UNKNOWN, 0);
            CreateRenderTarget();
        }
        return 0;
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

static void ApplyImGuiStyle()
{
    ImGui::StyleColorsDark();
    ImGuiStyle& style = ImGui::GetStyle();
    style.WindowRounding = 0.0f;
    style.ChildRounding = 0.0f;
    style.FrameRounding = 0.0f;
    style.WindowBorderSize = 1.0f;
    style.FrameBorderSize = 1.0f;
    style.WindowPadding = ImVec2(10.0f, 8.0f);
    style.FramePadding = ImVec2(8.0f, 3.0f);
    style.ItemSpacing = ImVec2(6.0f, 6.0f);
    style.WindowTitleAlign = ImVec2(0.0f, 0.5f);

    ImVec4* colors = style.Colors;
    colors[ImGuiCol_WindowBg] = ImVec4(0.05f, 0.06f, 0.08f, 0.94f);
    colors[ImGuiCol_Border] = ImVec4(0.18f, 0.25f, 0.36f, 1.00f);
    colors[ImGuiCol_TitleBg] = ImVec4(0.11f, 0.28f, 0.60f, 1.00f);
    colors[ImGuiCol_TitleBgActive] = ImVec4(0.11f, 0.28f, 0.60f, 1.00f);
    colors[ImGuiCol_Text] = ImVec4(0.92f, 0.95f, 0.98f, 1.00f);
    colors[ImGuiCol_Button] = ImVec4(0.14f, 0.29f, 0.54f, 1.00f);
    colors[ImGuiCol_ButtonHovered] = ImVec4(0.20f, 0.40f, 0.72f, 1.00f);
    colors[ImGuiCol_ButtonActive] = ImVec4(0.10f, 0.22f, 0.44f, 1.00f);
}

static DWORD WINAPI RenderThreadProc(LPVOID)
{
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.style = CS_CLASSDC;
    wc.lpfnWndProc = NotifyWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FloatingLyricsImGuiNotifyWnd";
    RegisterClassExW(&wc);

    RECT target = ComputeTargetRect();
    g_hwnd = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        wc.lpszClassName,
        L"",
        WS_POPUP,
        target.left,
        target.top,
        kWinWidth,
        kWinHeight,
        nullptr,
        nullptr,
        wc.hInstance,
        nullptr);

    if (!g_hwnd || !CreateDeviceD3D(g_hwnd))
    {
        CleanupDeviceD3D();
        if (g_hwnd)
        {
            DestroyWindow(g_hwnd);
            g_hwnd = nullptr;
        }
        UnregisterClassW(wc.lpszClassName, wc.hInstance);
        if (g_readyEvent) SetEvent(g_readyEvent);
        return 1;
    }

    DWM_BLURBEHIND bb{};
    bb.dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION;
    bb.fEnable = TRUE;
    bb.hRgnBlur = CreateRectRgn(0, 0, -1, -1);
    DwmEnableBlurBehindWindow(g_hwnd, &bb);
    if (bb.hRgnBlur)
    {
        DeleteObject(bb.hRgnBlur);
    }

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.Fonts->AddFontDefault();
    ApplyImGuiStyle();

    ImGui_ImplWin32_Init(g_hwnd);
    ImGui_ImplDX11_Init(g_device, g_context);

    if (g_readyEvent)
    {
        SetEvent(g_readyEvent);
    }

    MSG msg{};
    while (InterlockedCompareExchange(&g_running, 0, 0) != 0)
    {
        while (PeekMessageW(&msg, nullptr, 0U, 0U, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
            if (msg.message == WM_QUIT)
            {
                InterlockedExchange(&g_running, 0);
            }
        }

        if (InterlockedCompareExchange(&g_running, 0, 0) == 0)
        {
            break;
        }

        bool active = false;
        bool userClosed = false;
        std::string versionText;
        ULONGLONG animStart = 0;

        EnterCriticalSection(&g_lock);
        active = g_active;
        userClosed = g_userClosed;
        versionText = g_versionText;
        animStart = g_animStartTick;
        LeaveCriticalSection(&g_lock);

        RECT rect = ComputeTargetRect();
        bool shouldShow = active && !userClosed && IsDesktopAreaExposed(rect);

        if (!shouldShow)
        {
            if (g_visible)
            {
                ShowWindow(g_hwnd, SW_HIDE);
                g_visible = false;
            }
            Sleep(60);
            continue;
        }

        if (!g_visible)
        {
            SetWindowPos(
                g_hwnd,
                HWND_TOPMOST,
                rect.left,
                rect.top,
                kWinWidth,
                kWinHeight,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            g_visible = true;
        }

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        ImGui::SetNextWindowPos(ImVec2(0.0f, 0.0f), ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(static_cast<float>(kWinWidth), static_cast<float>(kWinHeight)), ImGuiCond_Always);

        bool open = true;
        ImGuiWindowFlags flags =
            ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoMove |
            ImGuiWindowFlags_NoCollapse |
            ImGuiWindowFlags_NoSavedSettings;

        if (ImGui::Begin("Notification", &open, flags))
        {
            std::string bodyFull = versionText.empty() ? "New ver." : ("New ver. " + versionText);
            ULONGLONG elapsed = GetTickCount64() - animStart;
            size_t typed = static_cast<size_t>(elapsed / kMsPerChar);
            bool done = typed >= bodyFull.size();
            size_t count = done ? bodyFull.size() : typed;

            std::string bodyShown = bodyFull.substr(0, count);
            if (!done)
            {
                bodyShown.push_back('_');
            }

            ImGui::TextUnformatted(bodyShown.c_str());

            if (done)
            {
                float btnW = 62.0f;
                float btnH = ImGui::GetFrameHeight();
                float availW = ImGui::GetContentRegionAvail().x;
                ImGui::SetCursorPosY(kWinHeight - btnH - ImGui::GetStyle().WindowPadding.y);
                ImGui::SetCursorPosX(ImGui::GetCursorPosX() + (availW > btnW ? availW - btnW : 0.0f));
                if (ImGui::Button("update", ImVec2(btnW, 0.0f)))
                {
                    if (g_onUpdate)
                    {
                        g_onUpdate();
                    }
                }
            }
        }
        ImGui::End();

        if (!open)
        {
            EnterCriticalSection(&g_lock);
            g_userClosed = true;
            LeaveCriticalSection(&g_lock);
            ShowWindow(g_hwnd, SW_HIDE);
            g_visible = false;
            if (g_onClosed)
            {
                g_onClosed();
            }
            continue;
        }

        ImGui::Render();
        const float clearColor[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
        g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_context->ClearRenderTargetView(g_rtv, clearColor);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
        g_swapChain->Present(1, 0);
    }

    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    CleanupDeviceD3D();
    if (g_hwnd)
    {
        DestroyWindow(g_hwnd);
        g_hwnd = nullptr;
    }
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return 0;
}

extern "C" __declspec(dllexport) void __stdcall InitImGuiNotify(NotifyCallback onUpdate, NotifyCallback onClosed)
{
    EnsureLock();
    g_onUpdate = onUpdate;
    g_onClosed = onClosed;

    if (InterlockedCompareExchange(&g_running, 1, 0) == 0)
    {
        g_readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        g_thread = CreateThread(nullptr, 0, RenderThreadProc, nullptr, 0, nullptr);
        if (g_readyEvent)
        {
            WaitForSingleObject(g_readyEvent, 2000);
            CloseHandle(g_readyEvent);
            g_readyEvent = nullptr;
        }
    }
}

extern "C" __declspec(dllexport) void __stdcall ShowImGuiUpdateNotification(const wchar_t* versionUtf16)
{
    EnsureLock();
    std::string utf8;
    if (versionUtf16 && versionUtf16[0] != L'\0')
    {
        int len = WideCharToMultiByte(CP_UTF8, 0, versionUtf16, -1, nullptr, 0, nullptr, nullptr);
        if (len > 1)
        {
            utf8.resize(len - 1);
            WideCharToMultiByte(CP_UTF8, 0, versionUtf16, -1, &utf8[0], len, nullptr, nullptr);
        }
    }

    EnterCriticalSection(&g_lock);
    bool changed = !g_active || g_userClosed || g_versionText != utf8;
    g_versionText = utf8;
    g_active = true;
    g_userClosed = false;
    if (changed)
    {
        g_animStartTick = GetTickCount64();
    }
    LeaveCriticalSection(&g_lock);
}

extern "C" __declspec(dllexport) void __stdcall HideImGuiUpdateNotification()
{
    EnsureLock();
    EnterCriticalSection(&g_lock);
    g_active = false;
    LeaveCriticalSection(&g_lock);
}

extern "C" __declspec(dllexport) void __stdcall ShutdownImGuiNotify()
{
    if (InterlockedExchange(&g_running, 0) != 0)
    {
        if (g_hwnd)
        {
            PostMessageW(g_hwnd, WM_QUIT, 0, 0);
        }
        if (g_thread)
        {
            WaitForSingleObject(g_thread, 2000);
            CloseHandle(g_thread);
            g_thread = nullptr;
        }
    }
}
