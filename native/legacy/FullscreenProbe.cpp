#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cwchar>
#include <cstdlib>
#include <cmath>
#include <string>
#include "RegionProtocol.h"
#include "MonitorProbe.h"

// A real DXGI fullscreen client. Scripted verification cannot enter fullscreen
// until DisplayWeave's per-process hook acknowledges readiness. Never use this to
// establish compatibility for an unrelated application or graphics API.
namespace {
constexpr UINT kTimer = 1;
constexpr UINT kEnterFullscreen = WM_APP + 1;
constexpr UINT kLeaveFullscreen = WM_APP + 2;
constexpr UINT kSaveReport = WM_APP + 3;
constexpr UINT kQuit = WM_APP + 4;
const GUID kTexture2D = {0x6f15aaf2, 0xd208, 0x4e89, {0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c}};

struct Snapshot {
    RECT rect{};
    RECT client{};
    RECT monitor{};
    LONG_PTR style = 0;
    LONG_PTR exStyle = 0;
    BOOL fullscreen = FALSE;
    BOOL windowed = TRUE;
    BOOL maximized = FALSE;
    HRESULT getState = E_PENDING;
    HRESULT getDesc = E_PENDING;
    UINT width = 0;
    UINT height = 0;
};

HWND window = nullptr;
ID3D11Device* device = nullptr;
ID3D11DeviceContext* context = nullptr;
IDXGISwapChain* swapChain = nullptr;
ID3D11RenderTargetView* target = nullptr;
D3D_DRIVER_TYPE driver = D3D_DRIVER_TYPE_UNKNOWN;
std::wstring reportPath;
RECT expected{};
bool scripted = false;
bool sentMessagesOnly = false;
bool smoke = false;
bool automatic = false;
bool autoAttachUnchanged = false;
bool autoExitRestored = false;
bool maximizeRegionMatches = false;
bool maximizeFramePreserved = false;
bool maximizeRestoreRestored = false;
bool modeMatchedAtEntry = false;
bool hookReadyAtEntry = false;
bool fullscreenVerified = false;
bool releaseRestored = false;
bool finished = false;
bool inResize = false;
bool requestMade = false;
int exitCode = 0;
int initialX = CW_USEDEFAULT;
int initialY = CW_USEDEFAULT;
unsigned phase = 0;
DWORD delayMs = 5000;
ULONGLONG started = 0;
ULONGLONG changed = 0;
unsigned long long frames = 0;
unsigned long long failedPresents = 0;
unsigned long long fullscreenStartFrames = 0;
unsigned long long fullscreenFrames = 0;
HRESULT enterResult = E_PENDING;
HRESULT leaveResult = E_PENDING;
HRESULT lastPresent = E_PENDING;
Snapshot initial, before, during, after, maximized, maximizeRestored, released;
LONG transitionBaseline = 0;
LONG ownTransitions = 0;
LONG ownResizes = 0;
const char* resultMessage = "running";

struct HookStatus {
    bool active = false;
    bool ready = false;
    LONG transitions = 0;
    LONG resizes = 0;
    LONG mode = -1;
};

bool ReadHookStatus(HookStatus& result) {
    wchar_t name[96];
    swprintf(name, 96, L"Local\\DisplayWeave_%lu", GetCurrentProcessId());
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!mapping) return false;
    // The controller creates its mapping before injection, so existence alone
    // must never authorize a real fullscreen request. Avoid Interlocked reads
    // because this view deliberately has no write access.
    const auto* shared = static_cast<const RegionProtocol*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(RegionProtocol)));
    bool valid = false;
    if (shared) {
        const LONG sequence = shared->sequence;
        valid = !(sequence & 1) && shared->magic == RS_PROTOCOL_MAGIC && shared->version == RS_PROTOCOL_VERSION
            && shared->size == sizeof(RegionProtocol) && shared->targetPid == GetCurrentProcessId();
        HookStatus status;
        status.active = shared->active != 0;
        status.ready = status.active && shared->hookReady == 1
            && static_cast<DWORD>(shared->acknowledgedPid) == GetCurrentProcessId()
            && (shared->capabilities & (RS_CAP_USER32 | RS_CAP_DXGI)) == (RS_CAP_USER32 | RS_CAP_DXGI)
            && static_cast<DWORD>(GetTickCount() - shared->heartbeatTick) < RS_HEARTBEAT_TIMEOUT_MS;
        status.transitions = shared->dxgiTransitions;
        status.resizes = shared->resizeIntercepts;
        status.mode = shared->mode;
        MemoryBarrier();
        valid = valid && sequence == shared->sequence;
        if (valid) result = status;
        UnmapViewOfFile(shared);
    }
    CloseHandle(mapping);
    return valid;
}

bool HookReady() {
    HookStatus status;
    return ReadHookStatus(status) && status.ready;
}

Snapshot Capture() {
    Snapshot snapshot;
    GetWindowRect(window, &snapshot.rect);
    GetClientRect(window, &snapshot.client);
    snapshot.style = GetWindowLongPtrW(window, GWL_STYLE);
    snapshot.exStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
    snapshot.maximized = IsZoomed(window);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &info)) snapshot.monitor = info.rcMonitor;
    if (swapChain) {
        snapshot.getState = swapChain->GetFullscreenState(&snapshot.fullscreen, nullptr);
        DXGI_SWAP_CHAIN_DESC description{};
        snapshot.getDesc = swapChain->GetDesc(&description);
        if (SUCCEEDED(snapshot.getDesc)) {
            snapshot.windowed = description.Windowed;
            snapshot.width = description.BufferDesc.Width;
            snapshot.height = description.BufferDesc.Height;
        }
    }
    return snapshot;
}

void WriteRect(FILE* file, const RECT& rect) {
    fprintf(file, "{\"x\":%ld,\"y\":%ld,\"width\":%ld,\"height\":%ld}",
        rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
}

void WriteSnapshot(FILE* file, const Snapshot& snapshot) {
    fprintf(file, "{\"window\":"); WriteRect(file, snapshot.rect);
    fprintf(file, ",\"client\":"); WriteRect(file, snapshot.client);
    fprintf(file, ",\"reported_monitor\":"); WriteRect(file, snapshot.monitor);
    fprintf(file, ",\"style\":%llu,\"ex_style\":%llu,\"get_state_hr\":%ld,\"reported_fullscreen\":%s,"
        "\"get_desc_hr\":%ld,\"description_windowed\":%s,\"maximized\":%s,\"buffer_width\":%u,\"buffer_height\":%u}",
        static_cast<unsigned long long>(snapshot.style), static_cast<unsigned long long>(snapshot.exStyle),
        static_cast<long>(snapshot.getState), snapshot.fullscreen ? "true" : "false",
        static_cast<long>(snapshot.getDesc), snapshot.windowed ? "true" : "false", snapshot.maximized ? "true" : "false", snapshot.width, snapshot.height);
}

bool Near(LONG a, LONG b) { return std::abs(static_cast<long long>(a) - b) <= 2; }
bool RegionMatches(const Snapshot& snapshot) {
    return Near(snapshot.rect.left, expected.left) && Near(snapshot.rect.top, expected.top)
        && Near(snapshot.rect.right, expected.right) && Near(snapshot.rect.bottom, expected.bottom);
}
bool RegionMatches() { return RegionMatches(during); }
bool Restored() {
    return after.getState == S_OK && !after.fullscreen
        && Near(after.rect.left, before.rect.left) && Near(after.rect.top, before.rect.top)
        && Near(after.rect.right, before.rect.right) && Near(after.rect.bottom, before.rect.bottom)
        && after.style == before.style && after.exStyle == before.exStyle;
}

bool MatchesInitialNormalWindow(const Snapshot& snapshot) {
    return snapshot.getState == S_OK && !snapshot.fullscreen && !snapshot.maximized
        && snapshot.getDesc == S_OK && snapshot.windowed
        && Near(snapshot.rect.left, initial.rect.left) && Near(snapshot.rect.top, initial.rect.top)
        && Near(snapshot.rect.right, initial.rect.right) && Near(snapshot.rect.bottom, initial.rect.bottom)
        && snapshot.style == initial.style && snapshot.exStyle == initial.exStyle;
}
bool OriginalPlacementRestored() { return MatchesInitialNormalWindow(released); }

bool SaveReport() {
    if (reportPath.empty()) return false;
    FILE* file = _wfopen(reportPath.c_str(), L"wb");
    if (!file) return false;
    fprintf(file, "{\n  \"pid\":%lu,\n  \"hwnd\":%llu,\n  \"scripted\":%s,\n  \"finished\":%s,\n"
        "  \"success\":%s,\n  \"message\":\"%s\",\n  \"driver\":\"%s\",\n"
        "  \"hook_ready\":%s,\n  \"fullscreen_requested\":%s,\n  \"enter_hr\":%ld,\n  \"leave_hr\":%ld,\n"
        "  \"presented_frames\":%llu,\n  \"fullscreen_frames\":%llu,\n  \"failed_presents\":%llu,\n"
        "  \"last_present_hr\":%ld,\n  \"region_matches\":%s,\n  \"restored\":%s,\n  \"expected\":",
        GetCurrentProcessId(), reinterpret_cast<unsigned long long>(window), scripted ? "true" : "false", finished ? "true" : "false",
        finished && exitCode == 0 ? "true" : "false", resultMessage,
        driver == D3D_DRIVER_TYPE_HARDWARE ? "hardware" : driver == D3D_DRIVER_TYPE_WARP ? "warp" : "unknown",
        HookReady() ? "true" : "false", requestMade ? "true" : "false", static_cast<long>(enterResult), static_cast<long>(leaveResult),
        frames, fullscreenFrames, failedPresents, static_cast<long>(lastPresent), RegionMatches() ? "true" : "false", Restored() ? "true" : "false");
    WriteRect(file, expected);
    fprintf(file, ",\n  \"hook_ready_at_entry\":%s,\n"
        "  \"dxgiTransitions\":%ld,\n  \"resizeIntercepts\":%ld,\n  \"fullscreen_verified\":%s,\n  \"releaseRestored\":%s",
        hookReadyAtEntry ? "true" : "false", ownTransitions, ownResizes,
        fullscreenVerified ? "true" : "false", releaseRestored ? "true" : "false");
    fprintf(file, ",\n  \"mode\":\"%s\",\n  \"modeMatchedAtEntry\":%s,\n  \"autoAttachUnchanged\":%s,\n"
        "  \"autoExitRestored\":%s,\n  \"maximizeRegionMatches\":%s,\n  \"maximizeFramePreserved\":%s,\n"
        "  \"maximizeRestoreRestored\":%s",
        automatic ? "AUTO" : "FORCE", modeMatchedAtEntry ? "true" : "false", autoAttachUnchanged ? "true" : "false",
        autoExitRestored ? "true" : "false", maximizeRegionMatches ? "true" : "false",
        maximizeFramePreserved ? "true" : "false", maximizeRestoreRestored ? "true" : "false");
    fprintf(file, ",\n  \"initial\":"); WriteSnapshot(file, initial);
    fprintf(file, ",\n  \"before\":"); WriteSnapshot(file, before);
    fprintf(file, ",\n  \"fullscreen\":"); WriteSnapshot(file, during);
    fprintf(file, ",\n  \"after\":"); WriteSnapshot(file, after);
    fprintf(file, ",\n  \"maximized\":"); WriteSnapshot(file, maximized);
    fprintf(file, ",\n  \"maximizeRestored\":"); WriteSnapshot(file, maximizeRestored);
    fprintf(file, ",\n  \"released\":"); WriteSnapshot(file, released);
    fprintf(file, "\n}\n");
    return fclose(file) == 0;
}

void SaveReady() {
    initial = Capture();
    if (reportPath.empty()) return;
    const std::wstring path = reportPath + L".ready.json";
    FILE* file = _wfopen(path.c_str(), L"wb");
    if (!file) return;
    fprintf(file, "{\"pid\":%lu,\"hwnd\":%llu,\"ready\":true,\"mapping\":\"Local\\\\DisplayWeave_%lu\"}\n",
        GetCurrentProcessId(), reinterpret_cast<unsigned long long>(window), GetCurrentProcessId());
    fclose(file);
}

bool RequestRelease() {
    const std::wstring path = reportPath + L".await-release.json";
    FILE* file = _wfopen(path.c_str(), L"wb");
    if (!file) return false;
    fprintf(file, "{\"pid\":%lu,\"awaitingRelease\":true}\n", GetCurrentProcessId());
    return fclose(file) == 0;
}

void ReleaseTarget() {
    if (context) context->OMSetRenderTargets(0, nullptr, nullptr);
    if (target) { target->Release(); target = nullptr; }
}

HRESULT CreateTarget() {
    ID3D11Texture2D* buffer = nullptr;
    HRESULT hr = swapChain->GetBuffer(0, kTexture2D, reinterpret_cast<void**>(&buffer));
    if (SUCCEEDED(hr)) {
        hr = device->CreateRenderTargetView(buffer, nullptr, &target);
        buffer->Release();
    }
    return hr;
}

void ResizeTarget(UINT width, UINT height) {
    if (!swapChain || !width || !height || inResize) return;
    inResize = true;
    ReleaseTarget();
    swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
    CreateTarget();
    inResize = false;
}

HRESULT CreateGraphics() {
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = 640;
    description.BufferDesc.Height = 360;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 1;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    description.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    D3D_FEATURE_LEVEL level;
    driver = D3D_DRIVER_TYPE_HARDWARE;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, driver, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &description, &swapChain, &device, &level, &context);
    if (FAILED(hr)) {
        driver = D3D_DRIVER_TYPE_WARP;
        hr = D3D11CreateDeviceAndSwapChain(nullptr, driver, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &description, &swapChain, &device, &level, &context);
    }
    if (FAILED(hr)) return hr;
    IDXGIFactory* factory = nullptr;
    const GUID factoryId = {0x7b7166ec, 0x21c7, 0x44ae, {0xb2, 0x1a, 0xc9, 0xae, 0x32, 0x1a, 0xe3, 0x69}};
    if (SUCCEEDED(swapChain->GetParent(factoryId, reinterpret_cast<void**>(&factory)))) {
        factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        factory->Release();
    }
    return CreateTarget();
}

void Render() {
    if (!target || !context || !swapChain || inResize) return;
    const double seconds = static_cast<double>(GetTickCount64() - started) / 1000.0;
    const float color[4] = {static_cast<float>(0.20 + 0.12 * std::sin(seconds)),
        static_cast<float>(0.35 + 0.20 * std::sin(seconds * 1.4)),
        static_cast<float>(0.55 + 0.25 * std::cos(seconds)), 1.0f};
    context->OMSetRenderTargets(1, &target, nullptr);
    context->ClearRenderTargetView(target, color);
    lastPresent = swapChain->Present(0, 0);
    if (lastPresent == S_OK) ++frames;
    else if (FAILED(lastPresent)) ++failedPresents;
}

bool Enter() {
    HookStatus status;
    if (!swapChain || !ReadHookStatus(status) || !status.ready) {
        SetWindowTextW(window, L"DisplayWeave Fullscreen Probe - select this app in DisplayWeave first");
        return false;
    }
    hookReadyAtEntry = true;
    modeMatchedAtEntry = status.mode == (automatic ? RS_MODE_AUTO : RS_MODE_FORCE);
    transitionBaseline = status.transitions;
    before = Capture();
    autoAttachUnchanged = automatic && MatchesInitialNormalWindow(before);
    requestMade = true;
    enterResult = swapChain->SetFullscreenState(TRUE, nullptr);
    fullscreenStartFrames = frames;
    changed = GetTickCount64();
    phase = 1;
    SetWindowTextW(window, L"DisplayWeave Fullscreen Probe - fullscreen requested; Escape exits");
    return SUCCEEDED(enterResult);
}

void Leave() {
    if (swapChain) leaveResult = swapChain->SetFullscreenState(FALSE, nullptr);
    changed = GetTickCount64();
    phase = 3;
}

void Finish(int code, const char* message) {
    if (finished) return;
    // Always try to leave fullscreen before destroying the swap chain/window.
    if (requestMade && swapChain && phase < 3) Leave();
    if (phase < 4) after = Capture();
    finished = true;
    exitCode = code;
    resultMessage = message;
    if (!reportPath.empty() && !SaveReport() && exitCode == 0) exitCode = 4;
    DestroyWindow(window);
}

void Tick() {
    Render();
    const ULONGLONG now = GetTickCount64();
    if (smoke && now - started >= 800) {
        Finish(frames >= 3 && failedPresents == 0 ? 0 : 3, "windowed D3D11 smoke test completed");
        return;
    }
    if (!scripted || finished) return;
    if (now - started > static_cast<ULONGLONG>(delayMs) + 15000) {
        Finish(2, "timed out waiting for hook or fullscreen verification");
        return;
    }
    if (phase == 0 && now - started >= delayMs && HookReady()) {
        if (!Enter()) Finish(2, "SetFullscreenState failed");
    } else if (phase == 1 && now - changed >= 1200) {
        during = Capture();
        changed = now;
        phase = 2;
    } else if (phase == 2 && now - changed >= 1200) {
        during = Capture();
        fullscreenFrames = frames - fullscreenStartFrames;
        Leave();
    } else if (phase == 3 && now - changed >= 400) {
        after = Capture();
        HookStatus status;
        const bool haveStatus = ReadHookStatus(status);
        ownTransitions = haveStatus ? status.transitions - transitionBaseline : 0;
        ownResizes = haveStatus ? status.resizes : 0;
        fullscreenVerified = enterResult == S_OK && during.getState == S_OK && during.fullscreen
            && during.getDesc == S_OK && !during.windowed && RegionMatches()
            && fullscreenFrames >= 3 && failedPresents == 0 && leaveResult == S_OK && Restored()
            && hookReadyAtEntry && haveStatus && status.ready && ownTransitions > 0 && modeMatchedAtEntry;
        autoExitRestored = automatic && MatchesInitialNormalWindow(after);
        if (automatic) {
            // Exercise ordinary Win32 maximization independently of DXGI.
            // The engine must retain the caption/frame and leave fullscreen false.
            phase = 5;
            changed = now;
            ShowWindow(window, SW_MAXIMIZE);
            return;
        }
        if (!RequestRelease()) {
            Finish(4, "could not write release coordination marker");
            return;
        }
        changed = now;
        phase = 4;
    } else if (phase == 5 && now - changed >= 600) {
        maximized = Capture();
        maximizeRegionMatches = RegionMatches(maximized) && maximized.maximized
            && maximized.getState == S_OK && !maximized.fullscreen;
        const LONG_PTR frameMask = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;
        maximizeFramePreserved = (maximized.style & frameMask) == (initial.style & frameMask);
        phase = 6;
        changed = now;
        ShowWindow(window, SW_RESTORE);
    } else if (phase == 6 && now - changed >= 600) {
        maximizeRestored = Capture();
        maximizeRestoreRestored = MatchesInitialNormalWindow(maximizeRestored);
        if (!RequestRelease()) {
            Finish(4, "could not write release coordination marker");
            return;
        }
        changed = now;
        phase = 4;
    } else if (phase == 4) {
        HookStatus status;
        if (ReadHookStatus(status) && !status.active) {
            released = Capture();
            releaseRestored = OriginalPlacementRestored();
            if (releaseRestored) {
                const bool success = fullscreenVerified && (!automatic || (autoAttachUnchanged
                    && autoExitRestored && maximizeRegionMatches && maximizeFramePreserved && maximizeRestoreRestored));
                Finish(success ? 0 : 3, success
                    ? (automatic ? "AUTO normal attach, fullscreen, maximize, normal restore, and OFF verified"
                                 : "fullscreen region, rendering, own DXGI interception, and OFF restoration verified")
                    : "mode verification failed; OFF restored the original window; inspect per-stage results");
                return;
            }
        }
        if (now - changed >= 2000) {
            released = Capture();
            Finish(3, "OFF did not restore the original window bounds, frame, and windowed state within two seconds");
        }
    }
}

LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED) ResizeTarget(LOWORD(lparam), HIWORD(lparam));
        return 0;
    case WM_TIMER: if (wparam == kTimer) Tick(); return 0;
    case WM_KEYDOWN:
        if (wparam == VK_ESCAPE) { Finish(scripted ? 2 : 0, "stopped by user"); return 0; }
        if (wparam == VK_F11) {
            Snapshot state = Capture();
            if (state.fullscreen) Leave(); else Enter();
            return 0;
        }
        break;
    case kEnterFullscreen: Enter(); return 0;
    case kLeaveFullscreen: during = Capture(); fullscreenFrames = frames - fullscreenStartFrames; Leave(); return 0;
    case kSaveReport: {
        Snapshot current = Capture();
        if (current.fullscreen) {
            during = current;
            fullscreenFrames = frames - fullscreenStartFrames;
        } else after = current;
        SaveReport(); return 0;
    }
    case kQuit: Finish(0, "stopped by controller"); return 0;
    case WM_CLOSE: Finish(scripted ? 2 : 0, "window closed"); return 0;
    case WM_DESTROY: KillTimer(hwnd, kTimer); PostQuitMessage(exitCode); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

bool ParseArguments() {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool valid = true;
    for (int i = 1; i < argc; ++i) {
        if ((wcscmp(argv[i], L"--verify-region") == 0 || wcscmp(argv[i], L"--verify-auto") == 0) && i + 4 < argc) {
            automatic = wcscmp(argv[i], L"--verify-auto") == 0;
            expected.left = _wtol(argv[++i]); expected.top = _wtol(argv[++i]);
            const LONG width = _wtol(argv[++i]); const LONG height = _wtol(argv[++i]);
            if (width <= 0 || height <= 0) valid = false;
            expected.right = expected.left + width; expected.bottom = expected.top + height;
            scripted = true;
        } else if (wcscmp(argv[i], L"--report") == 0 && i + 1 < argc) {
            reportPath = argv[++i];
        } else if (wcscmp(argv[i], L"--initial-position") == 0 && i + 2 < argc) {
            initialX = _wtoi(argv[++i]); initialY = _wtoi(argv[++i]);
        } else if (wcscmp(argv[i], L"--delay-ms") == 0 && i + 1 < argc) {
            const int delay = _wtoi(argv[++i]);
            if (delay < 100 || delay > 60000) valid = false; else delayMs = static_cast<DWORD>(delay);
        } else if (wcscmp(argv[i], L"--sent-messages-only") == 0) sentMessagesOnly = true;
        else if (wcscmp(argv[i], L"--smoke") == 0) smoke = true;
        else valid = false;
    }
    LocalFree(argv);
    return valid && !(scripted && smoke) && (!scripted || !reportPath.empty());
}
} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    // Physical pixels keep expected-region checks meaningful on mixed DPI setups.
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    using SetDpiContext = BOOL(WINAPI*)(HANDLE);
    auto dpi = reinterpret_cast<SetDpiContext>(GetProcAddress(user32, "SetProcessDpiAwarenessContext"));
    if (dpi) dpi(reinterpret_cast<HANDLE>(-4)); else SetProcessDPIAware();
    int monitorResult = monitor_probe::Run(instance);
    if (monitorResult >= 0) return monitorResult;
    if (!ParseArguments()) return 64;
    started = GetTickCount64();
    WNDCLASSW wc{};
    wc.lpfnWndProc = WindowProc; wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"DisplayWeaveFullscreenProbe";
    if (!RegisterClassW(&wc)) return 65;
    RECT rect{0, 0, 640, 360};
    AdjustWindowRectEx(&rect, WS_OVERLAPPEDWINDOW, FALSE, 0);
    window = CreateWindowExW(0, wc.lpszClassName, L"DisplayWeave Fullscreen Probe", WS_OVERLAPPEDWINDOW,
        initialX, initialY, rect.right - rect.left, rect.bottom - rect.top,
        nullptr, nullptr, instance, nullptr);
    if (!window) return 66;
    const HRESULT graphics = CreateGraphics();
    if (FAILED(graphics)) {
        resultMessage = "D3D11 initialization failed"; finished = true; exitCode = 67;
        SaveReport(); DestroyWindow(window);
    } else {
        ShowWindow(window, show); UpdateWindow(window);
        before = Capture(); SaveReady(); SetTimer(window, kTimer, 16, nullptr);
        MSG message{};
        if (sentMessagesOnly) {
            // Emulate a filtered game pump that never retrieves posted WM_NULL.
            // PeekMessage still delivers sent messages on the owning thread.
            // Delay rendering until readiness so a graphics driver cannot
            // accidentally pump an unfiltered queue and hide the regression.
            bool bootstrapped = false;
            ULONGLONG filteredStarted = GetTickCount64();
            while (!finished) {
                if (PeekMessageW(&message, nullptr, 0xBFFF, 0xBFFF, PM_REMOVE)) {
                    if (message.message == WM_QUIT) break;
                    TranslateMessage(&message); DispatchMessageW(&message);
                }
                HookStatus status;
                if (ReadHookStatus(status) && status.ready) bootstrapped = true;
                if (bootstrapped) Tick();
                else if (GetTickCount64() - filteredStarted > 20000) {
                    Finish(3, "filtered message loop did not receive hook readiness");
                    break;
                }
                Sleep(16);
            }
        } else while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
    }
    ReleaseTarget();
    if (swapChain) { swapChain->SetFullscreenState(FALSE, nullptr); swapChain->Release(); }
    if (context) context->Release();
    if (device) device->Release();
    return exitCode;
}
