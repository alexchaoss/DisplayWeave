#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <algorithm>
#include <cerrno>
#include <climits>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <string>
#include <vector>

// Owned renderer/input fixture only. It never hooks another process, sends input,
// changes cursor clipping, or changes a display mode explicitly. Exclusive mode
// is attempted only when the caller supplies --exclusive.
namespace {
const GUID kFactoryId = {0x7b7166ec, 0x21c7, 0x44ae, {0xb2, 0x1a, 0xc9, 0xae, 0x32, 0x1a, 0xe3, 0x69}};
const GUID kTextureId = {0x6f15aaf2, 0xd208, 0x4e89, {0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c}};
const unsigned kGrayLevels[4] = {32, 64, 128, 192};

RECT GrayPatchRect(UINT width, UINT height, unsigned index) {
    return {
        static_cast<LONG>(static_cast<unsigned long long>(width) * index / 4),
        static_cast<LONG>(static_cast<unsigned long long>(height) * 9 / 20),
        static_cast<LONG>(static_cast<unsigned long long>(width) * (index + 1) / 4),
        static_cast<LONG>(static_cast<unsigned long long>(height) * 11 / 20)
    };
}

struct Options {
    RECT rect{80, 80, 720, 440};
    unsigned seconds = 30;
    std::wstring report;
    std::wstring outputDevice;
    bool exclusive = false;
};
struct OutputInfo {
    std::wstring device;
    RECT desktop{};
    bool attached = false;
    UINT rotation = 0;
    LUID adapter{};
};
struct Snapshot {
    RECT window{}, client{}, clip{};
    POINT cursor{};
    HWND foreground = nullptr;
    LONG_PTR style = 0, exStyle = 0;
    BOOL fullscreen = FALSE;
    HRESULT stateHr = E_PENDING;
    HRESULT outputHr = E_PENDING;
    bool windowValid = false, cursorValid = false, clipValid = false;
    std::wstring output;
};
struct InputCounts {
    unsigned long long rawMouse = 0, rawKeyboard = 0, rawRelative = 0, rawAbsolute = 0;
    unsigned long long rawForeground = 0, rawBackground = 0, rawErrors = 0;
    unsigned long long rawVirtualDesktop = 0, rawButtonPackets = 0, rawWheelPackets = 0;
    long long rawDx = 0, rawDy = 0;
    LONG lastAbsoluteX = 0, lastAbsoluteY = 0;
    unsigned long long mouseMoves = 0, mouseDown = 0, mouseUp = 0, wheel = 0;
    unsigned long long keyDown = 0, keyUp = 0, chars = 0, activate = 0, deactivate = 0;
    unsigned long long fixtureMouseMoves = 0, fixtureKeyDown = 0, fixtureKeyUp = 0;
    POINT lastLegacyScreen{};
};
// Explicitly tagged SendInput events from our owned fixture harness. They are
// synthetic observations, never counted as proof of physical-device delivery.
constexpr ULONG_PTR kFixtureInputTag = 0x52535054;

Options options;
InputCounts input;
HWND window = nullptr;
ID3D11Device* device = nullptr;
ID3D11DeviceContext* context = nullptr;
IDXGISwapChain* swapChain = nullptr;
IDXGIOutput* selectedOutput = nullptr;
ID3D11RenderTargetView* renderTarget = nullptr;
ID3D11VertexShader* vertexShader = nullptr;
ID3D11PixelShader* pixelShader = nullptr;
ID3D11Buffer* colorBuffer = nullptr;
std::vector<OutputInfo> outputs;
UINT bufferWidth = 0, bufferHeight = 0;
bool running = true, readyWritten = false, finished = false, rawRegistered = false;
bool focusRequested = false, focusRequestSucceeded = false;
bool sawRegionHook = false;
int resultCode = 0;
std::string reason = "running";
HRESULT graphicsHr = E_PENDING, enterHr = E_PENDING, leaveHr = E_PENDING;
HRESULT lastPresentHr = E_PENDING, resizeHr = S_OK;
unsigned long long frames = 0, successfulPresents = 0, failedPresents = 0, occludedPresents = 0;
unsigned long long foregroundChanges = 0, foregroundOwnFrames = 0;
unsigned displayChanges = 0;
ULONGLONG started = 0;
HWND lastForeground = nullptr;
Snapshot initial, lastRunning, restored;
WINDOWPLACEMENT normalPlacement{sizeof(WINDOWPLACEMENT)};
LONG_PTR normalStyle = 0, normalExStyle = 0;

template <typename T> void Release(T*& value) { if (value) { value->Release(); value = nullptr; } }

void EnableDpiAwareness() {
    typedef BOOL (WINAPI *SetContext)(HANDLE);
    SetContext setContext = reinterpret_cast<SetContext>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    if (!setContext || !setContext(reinterpret_cast<HANDLE>(static_cast<INT_PTR>(-4)))) SetProcessDPIAware();
}

bool Integer(const wchar_t* text, long long minimum, long long maximum, long long& value) {
    if (!text || !*text) return false;
    const wchar_t* digit = text;
    if (*digit == L'-') ++digit;
    if (!*digit) return false;
    for (const wchar_t* p = digit; *p; ++p) if (*p < L'0' || *p > L'9') return false;
    errno = 0;
    wchar_t* end = nullptr;
    value = wcstoll(text, &end, 10);
    return !errno && end && !*end && value >= minimum && value <= maximum;
}

bool ParseRect(const wchar_t* text, RECT& rect) {
    std::wstring source(text);
    long long values[4]{};
    size_t begin = 0;
    for (unsigned i = 0; i < 4; ++i) {
        size_t end = source.find(L',', begin);
        if ((i < 3 && end == std::wstring::npos) || (i == 3 && end != std::wstring::npos)) return false;
        std::wstring part = source.substr(begin, end == std::wstring::npos ? end : end - begin);
        if (!Integer(part.c_str(), i < 2 ? INT_MIN : 1, INT_MAX, values[i])) return false;
        begin = end + 1;
    }
    if (values[0] + values[2] > INT_MAX || values[1] + values[3] > INT_MAX) return false;
    rect = {static_cast<LONG>(values[0]), static_cast<LONG>(values[1]),
        static_cast<LONG>(values[0] + values[2]), static_cast<LONG>(values[1] + values[3])};
    return true;
}

bool Parse(int count, wchar_t** args, std::wstring& error) {
    unsigned seen = 0;
    for (int i = 1; i < count; ++i) {
        std::wstring flag(args[i]);
        unsigned bit = flag == L"--rect" ? 1 : flag == L"--seconds" ? 2 : flag == L"--report" ? 4
            : flag == L"--exclusive" ? 8 : flag == L"--output-device" ? 16 : 0;
        if (!bit || (seen & bit)) { error = L"Unknown or repeated option: " + flag; return false; }
        seen |= bit;
        if (bit == 8) { options.exclusive = true; continue; }
        if (++i >= count || !args[i][0] || wcsncmp(args[i], L"--", 2) == 0) { error = L"Missing value for " + flag; return false; }
        if (bit == 1 && !ParseRect(args[i], options.rect)) { error = L"--rect requires x,y,width,height with positive dimensions."; return false; }
        if (bit == 2) {
            long long value = 0;
            if (!Integer(args[i], 1, 3600, value)) { error = L"--seconds must be between 1 and 3600."; return false; }
            options.seconds = static_cast<unsigned>(value);
        }
        if (bit == 4) options.report = args[i];
        if (bit == 16) options.outputDevice = args[i];
    }
    if (options.report.empty()) { error = L"--report is required."; return false; }
    if (!options.outputDevice.empty() && !options.exclusive) { error = L"--output-device requires --exclusive."; return false; }
    return true;
}

void JsonString(FILE* file, const std::wstring& text) {
    fputc('"', file);
    // Escape UTF-16 code units: valid JSON preserves non-ASCII device/path names.
    for (wchar_t value : text) {
        unsigned code = static_cast<unsigned short>(value);
        if (code == '"' || code == '\\') { fputc('\\', file); fputc(static_cast<int>(code), file); }
        else if (code >= 32 && code < 127) fputc(static_cast<int>(code), file);
        else fprintf(file, "\\u%04x", code);
    }
    fputc('"', file);
}
void RectJson(FILE* file, const RECT& rect) {
    fprintf(file, "{\"x\":%ld,\"y\":%ld,\"width\":%lld,\"height\":%lld}", rect.left, rect.top,
        static_cast<long long>(rect.right) - rect.left, static_cast<long long>(rect.bottom) - rect.top);
}
void HandleJson(FILE* file, HWND value) { fprintf(file, "\"0x%llx\"", static_cast<unsigned long long>(reinterpret_cast<UINT_PTR>(value))); }
const char* Boolean(bool value) { return value ? "true" : "false"; }

void ObserveModules() {
    sawRegionHook = sawRegionHook || GetModuleHandleW(L"RegionHook.dll");
}

Snapshot Capture() {
    Snapshot value;
    value.windowValid = window && IsWindow(window);
    if (value.windowValid) {
        GetWindowRect(window, &value.window);
        GetClientRect(window, &value.client);
        POINT origin{0, 0};
        ClientToScreen(window, &origin);
        OffsetRect(&value.client, origin.x, origin.y);
        value.style = GetWindowLongPtrW(window, GWL_STYLE);
        value.exStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
    }
    value.foreground = GetForegroundWindow();
    value.cursorValid = GetCursorPos(&value.cursor) != FALSE;
    value.clipValid = GetClipCursor(&value.clip) != FALSE;
    if (swapChain) {
        IDXGIOutput* output = nullptr;
        value.stateHr = swapChain->GetFullscreenState(&value.fullscreen, &output);
        value.outputHr = output ? S_OK : swapChain->GetContainingOutput(&output);
        if (output) {
            DXGI_OUTPUT_DESC desc{};
            value.outputHr = output->GetDesc(&desc);
            if (SUCCEEDED(value.outputHr)) value.output = desc.DeviceName;
            output->Release();
        }
    }
    return value;
}

void SnapshotJson(FILE* file, const Snapshot& value) {
    fprintf(file, "{\"windowValid\":%s,\"window\":", Boolean(value.windowValid)); RectJson(file, value.window);
    fprintf(file, ",\"client\":"); RectJson(file, value.client);
    fprintf(file, ",\"foregroundHwnd\":"); HandleJson(file, value.foreground);
    fprintf(file, ",\"foregroundIsSource\":%s,\"style\":%llu,\"exStyle\":%llu,\"exclusive\":%s,\"fullscreenHr\":%ld,\"outputHr\":%ld,\"outputDevice\":",
        Boolean(value.foreground == window), static_cast<unsigned long long>(value.style), static_cast<unsigned long long>(value.exStyle),
        Boolean(value.fullscreen != FALSE), static_cast<long>(value.stateHr), static_cast<long>(value.outputHr));
    JsonString(file, value.output);
    fprintf(file, ",\"cursorValid\":%s,\"cursor\":{\"x\":%ld,\"y\":%ld},\"clipValid\":%s,\"clip\":",
        Boolean(value.cursorValid), value.cursor.x, value.cursor.y, Boolean(value.clipValid));
    RectJson(file, value.clip); fputc('}', file);
}

void OutputsJson(FILE* file) {
    fputc('[', file);
    for (size_t i = 0; i < outputs.size(); ++i) {
        const OutputInfo& output = outputs[i];
        if (i) fputc(',', file);
        fprintf(file, "{\"device\":"); JsonString(file, output.device);
        fprintf(file, ",\"desktop\":"); RectJson(file, output.desktop);
        fprintf(file, ",\"attached\":%s,\"rotation\":%u,\"adapterLuidLow\":%lu,\"adapterLuidHigh\":%ld}",
            Boolean(output.attached), output.rotation, output.adapter.LowPart, output.adapter.HighPart);
    }
    fputc(']', file);
}

bool SaveReport(const std::wstring& path, bool ready) {
    ObserveModules();
    Snapshot current = Capture();
    std::wstring temporary = path + L".tmp";
    FILE* file = _wfopen(temporary.c_str(), L"wb");
    if (!file) return false;
    fprintf(file, "{\n\"schema\":1,\"pid\":%lu,\"hwnd\":", GetCurrentProcessId()); HandleJson(file, window);
    fprintf(file, ",\"hwndValue\":%llu,\"ready\":%s,\"finished\":%s,\"success\":%s,\"reason\":\"%s\",\n\"window\":",
        static_cast<unsigned long long>(reinterpret_cast<UINT_PTR>(window)), Boolean(ready), Boolean(finished),
        Boolean(resultCode == 0 && successfulPresents > 0), reason.c_str());
    RectJson(file, current.window);
    fprintf(file, ",\"client\":"); RectJson(file, current.client);
    fprintf(file, ",\"foregroundHwnd\":"); HandleJson(file, current.foreground);
    fprintf(file, ",\"requestedExclusive\":%s,\"exclusive\":%s,\"outputDevice\":",
        Boolean(options.exclusive), Boolean(current.fullscreen != FALSE)); JsonString(file, current.output);
    fprintf(file, ",\"requestedOutputDevice\":"); JsonString(file, options.outputDevice);
    fprintf(file, ",\"regionHookLoaded\":%s,\"measurementScope\":\"own ordinary API calls; other loaded software can affect reported state\",\n",
        Boolean(sawRegionHook));
    fprintf(file, "\"frame\":%llu,\"frames\":%llu,\"successfulPresents\":%llu,\"failedPresents\":%llu,\"occludedPresents\":%llu,\"elapsedMs\":%llu,\n",
        frames, frames, successfulPresents, failedPresents, occludedPresents, started ? GetTickCount64() - started : 0);
    fprintf(file, "\"graphicsHr\":%ld,\"enterFullscreenHr\":%ld,\"leaveFullscreenHr\":%ld,\"lastPresentHr\":%ld,\"resizeHr\":%ld,\"rawRegistered\":%s,\"focusRequested\":%s,\"focusRequestSucceeded\":%s,\n",
        static_cast<long>(graphicsHr), static_cast<long>(enterHr), static_cast<long>(leaveHr), static_cast<long>(lastPresentHr),
        static_cast<long>(resizeHr), Boolean(rawRegistered), Boolean(focusRequested), Boolean(focusRequestSucceeded));
    fprintf(file, "\"foregroundChanges\":%llu,\"foregroundOwnFrames\":%llu,\"displayChanges\":%u,\"input\":{",
        foregroundChanges, foregroundOwnFrames, displayChanges);
    fprintf(file, "\"rawMouse\":%llu,\"rawKeyboard\":%llu,\"rawRelative\":%llu,\"rawAbsolute\":%llu,\"rawForeground\":%llu,\"rawBackground\":%llu,\"rawErrors\":%llu,\"rawVirtualDesktop\":%llu,\"rawButtonPackets\":%llu,\"rawWheelPackets\":%llu,",
        input.rawMouse, input.rawKeyboard, input.rawRelative, input.rawAbsolute, input.rawForeground, input.rawBackground,
        input.rawErrors, input.rawVirtualDesktop, input.rawButtonPackets, input.rawWheelPackets);
    fprintf(file, "\"rawRelativeDx\":%lld,\"rawRelativeDy\":%lld,\"lastRawAbsoluteX\":%ld,\"lastRawAbsoluteY\":%ld,\"legacyMouseMoves\":%llu,\"legacyMouseDown\":%llu,\"legacyMouseUp\":%llu,\"legacyWheel\":%llu,\"legacyKeyDown\":%llu,\"legacyKeyUp\":%llu,\"legacyChars\":%llu,\"activations\":%llu,\"deactivations\":%llu,\"lastLegacyScreen\":{\"x\":%ld,\"y\":%ld}},\n",
        input.rawDx, input.rawDy, input.lastAbsoluteX, input.lastAbsoluteY, input.mouseMoves, input.mouseDown, input.mouseUp,
        input.wheel, input.keyDown, input.keyUp, input.chars, input.activate, input.deactivate, input.lastLegacyScreen.x, input.lastLegacyScreen.y);
    fprintf(file, "\"syntheticFixtureInput\":{\"tag\":%llu,\"mouseMoves\":%llu,\"keyDown\":%llu,\"keyUp\":%llu,\"provesHardwareInput\":false},\n",
        static_cast<unsigned long long>(kFixtureInputTag), input.fixtureMouseMoves, input.fixtureKeyDown, input.fixtureKeyUp);
    fprintf(file, "\"pattern\":{\"quadrantsRgb\":[[255,0,0],[0,255,0],[0,0,255],[255,255,0]],\"frameBits\":32,\"frameBitOrder\":\"left-to-right least significant first\",\"markerTopHeight\":%u,\"animationBottomHeight\":%u,\"bufferWidth\":%u,\"bufferHeight\":%u,\"grayPatches\":[",
        std::min<UINT>(16, bufferHeight / 8), std::min<UINT>(12, bufferHeight / 8), bufferWidth, bufferHeight);
    for (unsigned index = 0; index < 4; ++index) {
        if (index) fputc(',', file);
        fprintf(file, "{\"centerNormalized\":{\"x\":%.3f,\"y\":0.5},\"boundsNormalized\":{\"x\":%.2f,\"y\":0.45,\"width\":0.25,\"height\":0.10},\"rgb\":[%u,%u,%u],\"pixels\":",
            (index + 0.5) / 4.0, index / 4.0, kGrayLevels[index], kGrayLevels[index], kGrayLevels[index]);
        RectJson(file, GrayPatchRect(bufferWidth, bufferHeight, index));
        fputc('}', file);
    }
    fputs("]},\n", file);
    fprintf(file, "\"topology\":"); OutputsJson(file);
    fprintf(file, ",\n\"initial\":"); SnapshotJson(file, initial);
    fprintf(file, ",\n\"running\":"); SnapshotJson(file, lastRunning);
    fprintf(file, ",\n\"restored\":"); SnapshotJson(file, restored);
    fprintf(file, ",\n\"current\":"); SnapshotJson(file, current);
    fputs("\n}\n", file);
    bool okay = !ferror(file);
    if (fclose(file) != 0) okay = false;
    return okay && MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

void RawInput(LPARAM parameter, WPARAM code) {
    RAWINPUT value{};
    UINT size = sizeof(value);
    UINT copied = GetRawInputData(reinterpret_cast<HRAWINPUT>(parameter), RID_INPUT, &value, &size, sizeof(RAWINPUTHEADER));
    if (copied == static_cast<UINT>(-1) || copied < sizeof(RAWINPUTHEADER)) { ++input.rawErrors; return; }
    if (GET_RAWINPUT_CODE_WPARAM(code) == RIM_INPUT) ++input.rawForeground; else ++input.rawBackground;
    if (value.header.dwType == RIM_TYPEMOUSE) {
        ++input.rawMouse;
        const RAWMOUSE& mouse = value.data.mouse;
        if (mouse.usFlags & MOUSE_MOVE_ABSOLUTE) {
            ++input.rawAbsolute; input.lastAbsoluteX = mouse.lLastX; input.lastAbsoluteY = mouse.lLastY;
        } else { ++input.rawRelative; input.rawDx += mouse.lLastX; input.rawDy += mouse.lLastY; }
        if (mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) ++input.rawVirtualDesktop;
        if (mouse.usButtonFlags & 0x03ff) ++input.rawButtonPackets;
        if (mouse.usButtonFlags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)) ++input.rawWheelPackets;
    } else if (value.header.dwType == RIM_TYPEKEYBOARD) ++input.rawKeyboard;
}

LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_INPUT:
        RawInput(lparam, wparam);
        return DefWindowProcW(hwnd, message, wparam, lparam); // Required foreground raw-input cleanup.
    case WM_MOUSEMOVE: {
        ++input.mouseMoves;
        if(static_cast<ULONG_PTR>(GetMessageExtraInfo()) == kFixtureInputTag) ++input.fixtureMouseMoves;
        POINT point{static_cast<short>(LOWORD(lparam)), static_cast<short>(HIWORD(lparam))};
        ClientToScreen(hwnd, &point); input.lastLegacyScreen = point; break;
    }
    case WM_LBUTTONDOWN: case WM_RBUTTONDOWN: case WM_MBUTTONDOWN: case WM_XBUTTONDOWN: ++input.mouseDown; break;
    case WM_LBUTTONUP: case WM_RBUTTONUP: case WM_MBUTTONUP: case WM_XBUTTONUP: ++input.mouseUp; break;
    case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL: ++input.wheel; break;
    case WM_KEYDOWN: case WM_SYSKEYDOWN:
        ++input.keyDown;
        if(static_cast<ULONG_PTR>(GetMessageExtraInfo()) == kFixtureInputTag) ++input.fixtureKeyDown;
        if (wparam == VK_ESCAPE) { reason = "escape"; running = false; return 0; }
        break;
    case WM_KEYUP: case WM_SYSKEYUP:
        ++input.keyUp;
        if(static_cast<ULONG_PTR>(GetMessageExtraInfo()) == kFixtureInputTag) ++input.fixtureKeyUp;
        break;
    case WM_CHAR: ++input.chars; break;
    case WM_ACTIVATE: if (LOWORD(wparam) == WA_INACTIVE) ++input.deactivate; else ++input.activate; break;
    case WM_DISPLAYCHANGE: ++displayChanges; break;
    case WM_ERASEBKGND: return 1;
    case WM_CLOSE: reason = "window_closed"; running = false; return 0;
    case WM_DESTROY: running = false; PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

HRESULT SelectOutput(IDXGIFactory* factory, IDXGIAdapter** selectedAdapter) {
    HMONITOR nearest = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    for (UINT i = 0; ; ++i) {
        IDXGIAdapter* adapter = nullptr;
        HRESULT hr = factory->EnumAdapters(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(hr)) return hr;
        DXGI_ADAPTER_DESC adapterDesc{};
        adapter->GetDesc(&adapterDesc);
        for (UINT j = 0; ; ++j) {
            IDXGIOutput* output = nullptr;
            hr = adapter->EnumOutputs(j, &output);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) { adapter->Release(); return hr; }
            DXGI_OUTPUT_DESC desc{};
            hr = output->GetDesc(&desc);
            if (SUCCEEDED(hr)) {
                outputs.push_back({desc.DeviceName, desc.DesktopCoordinates, desc.AttachedToDesktop != FALSE,
                    static_cast<UINT>(desc.Rotation), adapterDesc.AdapterLuid});
                bool match = options.outputDevice.empty() ? desc.Monitor == nearest : _wcsicmp(options.outputDevice.c_str(), desc.DeviceName) == 0;
                if (match && desc.AttachedToDesktop && !selectedOutput) {
                    selectedOutput = output; selectedOutput->AddRef();
                    *selectedAdapter = adapter; adapter->AddRef();
                }
            }
            output->Release();
        }
        adapter->Release();
    }
    return selectedOutput ? S_OK : DXGI_ERROR_NOT_FOUND;
}

HRESULT CreateTarget() {
    ID3D11Texture2D* buffer = nullptr;
    HRESULT hr = swapChain->GetBuffer(0, kTextureId, reinterpret_cast<void**>(&buffer));
    if (FAILED(hr)) return hr;
    D3D11_TEXTURE2D_DESC desc{}; buffer->GetDesc(&desc);
    bufferWidth = desc.Width; bufferHeight = desc.Height;
    hr = device->CreateRenderTargetView(buffer, nullptr, &renderTarget);
    buffer->Release();
    return hr;
}

HRESULT CreateShaders() {
    const char source[] =
        "cbuffer Paint:register(b0){float4 color;}"
        "float4 vs(uint id:SV_VertexID):SV_POSITION{"
        "float2 p=id==0?float2(-1,-1):(id==1?float2(-1,3):float2(3,-1));return float4(p,0,1);}"
        "float4 ps():SV_TARGET{return color;}";
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    HRESULT hr = D3DCompile(source, sizeof(source) - 1, "ProjectionProbe", nullptr, nullptr, "vs", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    Release(errors);
    if (SUCCEEDED(hr)) hr = device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &vertexShader);
    Release(code);
    if (FAILED(hr)) return hr;
    hr = D3DCompile(source, sizeof(source) - 1, "ProjectionProbe", nullptr, nullptr, "ps", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    Release(errors);
    if (SUCCEEDED(hr)) hr = device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &pixelShader);
    Release(code);
    if (FAILED(hr)) return hr;
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = 16; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    return device->CreateBuffer(&desc, nullptr, &colorBuffer);
}

HRESULT CreateGraphics() {
    IDXGIFactory* factory = nullptr;
    HRESULT hr = CreateDXGIFactory(kFactoryId, reinterpret_cast<void**>(&factory));
    if (FAILED(hr)) return hr;
    IDXGIAdapter* adapter = nullptr;
    hr = SelectOutput(factory, &adapter);
    if (SUCCEEDED(hr)) {
        D3D_FEATURE_LEVEL level;
        // Hardware only: a software fallback would hide a failed GPU experiment.
        hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, &level, &context);
    }
    Release(adapter);
    if (SUCCEEDED(hr)) {
        RECT client{}; GetClientRect(window, &client);
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = std::max<LONG>(1, client.right);
        desc.BufferDesc.Height = std::max<LONG>(1, client.bottom);
        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.OutputWindow = window;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        // No ALLOW_MODE_SWITCH / ResizeTarget: this fixture does not request a
        // physical resolution or refresh-rate change even in exclusive mode.
        hr = factory->CreateSwapChain(device, &desc, &swapChain);
        if (SUCCEEDED(hr)) factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
    }
    factory->Release();
    if (SUCCEEDED(hr)) hr = CreateShaders();
    if (SUCCEEDED(hr)) hr = CreateTarget();
    return hr;
}

void Paint(UINT x, UINT y, UINT width, UINT height, const float color[4]) {
    if (!width || !height) return;
    D3D11_VIEWPORT viewport{static_cast<float>(x), static_cast<float>(y), static_cast<float>(width), static_cast<float>(height), 0, 1};
    context->RSSetViewports(1, &viewport);
    context->UpdateSubresource(colorBuffer, 0, nullptr, color, 0, 0);
    context->Draw(3, 0);
}

bool Render() {
    RECT client{};
    if (!GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0 || IsIconic(window)) return true;
    UINT width = static_cast<UINT>(client.right), height = static_cast<UINT>(client.bottom);
    if (width != bufferWidth || height != bufferHeight) {
        context->OMSetRenderTargets(0, nullptr, nullptr);
        Release(renderTarget);
        resizeHr = swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
        if (FAILED(resizeHr)) return false;
        resizeHr = CreateTarget();
        if (FAILED(resizeHr)) return false;
    }
    context->OMSetRenderTargets(1, &renderTarget, nullptr);
    context->IASetInputLayout(nullptr);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(vertexShader, nullptr, 0);
    context->PSSetShader(pixelShader, nullptr, 0);
    context->PSSetConstantBuffers(0, 1, &colorBuffer);
    const float red[]{1, 0, 0, 1}, green[]{0, 1, 0, 1}, blue[]{0, 0, 1, 1}, yellow[]{1, 1, 0, 1};
    const float white[]{1, 1, 1, 1}, black[]{0, 0, 0, 1};
    UINT halfWidth = width / 2, halfHeight = height / 2;
    Paint(0, 0, halfWidth, halfHeight, red);
    Paint(halfWidth, 0, width - halfWidth, halfHeight, green);
    Paint(0, halfHeight, halfWidth, height - halfHeight, blue);
    Paint(halfWidth, halfHeight, width - halfWidth, height - halfHeight, yellow);
    // Midtones detect incorrect SDR-white scaling or transfer functions, which
    // saturated primaries cannot. These are SDR byte values in an UNORM target;
    // the desktop compositor performs any output-color conversion.
    for (unsigned index = 0; index < 4; ++index) {
        const float gray = kGrayLevels[index] / 255.0f;
        const float color[]{gray, gray, gray, 1};
        const RECT patch = GrayPatchRect(width, height, index);
        Paint(patch.left, patch.top, patch.right - patch.left, patch.bottom - patch.top, color);
    }
    UINT markerHeight = std::min<UINT>(16, height / 8);
    Paint(0, 0, width, markerHeight, black);
    unsigned sequence = static_cast<unsigned>(frames + 1);
    for (UINT bit = 0; bit < 32; ++bit) {
        UINT left = static_cast<UINT>(static_cast<unsigned long long>(bit) * width / 32);
        UINT right = static_cast<UINT>(static_cast<unsigned long long>(bit + 1) * width / 32);
        if ((sequence >> bit) & 1) Paint(left, 0, right - left, markerHeight, white);
    }
    UINT animationHeight = std::min<UINT>(12, height / 8);
    UINT markerWidth = std::min<UINT>(24, width);
    UINT markerX = static_cast<UINT>((frames * 3) % (width - markerWidth + 1));
    Paint(markerX, height - animationHeight, markerWidth, animationHeight, white);
    ++frames;
    lastPresentHr = swapChain->Present(1, 0);
    if (lastPresentHr == DXGI_STATUS_OCCLUDED) ++occludedPresents;
    else if (SUCCEEDED(lastPresentHr)) ++successfulPresents;
    else { ++failedPresents; return false; }
    HWND foreground = GetForegroundWindow();
    if (foreground != lastForeground) { ++foregroundChanges; lastForeground = foreground; }
    if (foreground == window) ++foregroundOwnFrames;
    return true;
}

void Cleanup() {
    if (swapChain) {
        lastRunning = Capture();
        leaveHr = swapChain->SetFullscreenState(FALSE, nullptr);
        if (options.exclusive && SUCCEEDED(leaveHr) && window && IsWindow(window)) {
            SetWindowLongPtrW(window, GWL_STYLE, normalStyle);
            SetWindowLongPtrW(window, GWL_EXSTYLE, normalExStyle);
            SetWindowPlacement(window, &normalPlacement);
            SetWindowPos(window, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        restored = Capture();
        if (FAILED(leaveHr)) { resultCode = 1; reason = "fullscreen_exit_failed"; }
    }
    finished = true;
    if (!SaveReport(options.report, false)) resultCode = 1;
    if (context) context->ClearState();
    Release(renderTarget); Release(colorBuffer); Release(pixelShader); Release(vertexShader);
    Release(swapChain); Release(selectedOutput); Release(context); Release(device);
    if (window && IsWindow(window)) DestroyWindow(window);
}
} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, wchar_t*, int) {
    int count = 0;
    wchar_t** args = CommandLineToArgvW(GetCommandLineW(), &count);
    std::wstring error;
    bool parsed = args && Parse(count, args, error);
    if (args) LocalFree(args);
    if (!parsed) { fwprintf(stderr, L"ProjectionProbe: %ls\n", error.c_str()); return 2; }
    EnableDpiAwareness();
    WNDCLASSW wc{};
    wc.hInstance = instance; wc.lpfnWndProc = WindowProcedure; wc.lpszClassName = L"DisplayWeaveProjectionProbe";
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    if (!RegisterClassW(&wc)) { reason = "window_class_failed"; resultCode = 1; finished = true; SaveReport(options.report, false); return 1; }
    window = CreateWindowExW(0, wc.lpszClassName, L"Projection source — Escape closes this fixture", WS_OVERLAPPEDWINDOW,
        options.rect.left, options.rect.top, options.rect.right - options.rect.left, options.rect.bottom - options.rect.top,
        nullptr, nullptr, instance, nullptr);
    if (!window) { reason = "window_creation_failed"; resultCode = 1; finished = true; SaveReport(options.report, false); return 1; }
    ShowWindow(window, SW_SHOWNORMAL);
    GetWindowPlacement(window, &normalPlacement);
    normalStyle = GetWindowLongPtrW(window, GWL_STYLE); normalExStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
    RAWINPUTDEVICE registrations[2]{};
    registrations[0].usUsagePage = 1; registrations[0].usUsage = 2; registrations[0].hwndTarget = window;
    registrations[1].usUsagePage = 1; registrations[1].usUsage = 6; registrations[1].hwndTarget = window;
    // Foreground hardware input and normal legacy messages are both retained.
    rawRegistered = RegisterRawInputDevices(registrations, 2, sizeof(RAWINPUTDEVICE)) != FALSE;
    graphicsHr = CreateGraphics();
    initial = Capture();
    if (FAILED(graphicsHr) || !rawRegistered) {
        resultCode = 1; reason = FAILED(graphicsHr) ? "graphics_initialization_failed" : "raw_input_registration_failed";
        Cleanup(); return resultCode;
    }
    if (options.exclusive) {
        focusRequested = true; focusRequestSucceeded = SetForegroundWindow(window) != FALSE;
        enterHr = swapChain->SetFullscreenState(TRUE, selectedOutput);
        Snapshot state = Capture();
        if (FAILED(enterHr) || !state.fullscreen || FAILED(state.stateHr)) {
            resultCode = 1; reason = "exclusive_entry_failed"; Cleanup(); return resultCode;
        }
    }
    started = GetTickCount64();
    ULONGLONG nextReport = started + 1000;
    while (running) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) { running = false; break; }
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        if (!running) break;
        if (GetTickCount64() - started >= static_cast<ULONGLONG>(options.seconds) * 1000) { reason = "duration_completed"; break; }
        if (!Render()) { resultCode = 1; reason = "render_failed"; break; }
        if (!readyWritten && successfulPresents > 0) {
            lastRunning = Capture();
            readyWritten = SaveReport(options.report + L".ready.json", true);
            if (!readyWritten) { resultCode = 1; reason = "ready_report_failed"; break; }
        }
        if (GetTickCount64() >= nextReport) {
            lastRunning = Capture();
            if (!SaveReport(options.report, false)) { resultCode = 1; reason = "report_write_failed"; break; }
            nextReport = GetTickCount64() + 1000;
        }
        MsgWaitForMultipleObjectsEx(0, nullptr, 1, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
    Cleanup();
    return resultCode;
}
