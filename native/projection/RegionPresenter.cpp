#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <cstdio>
#include <cwchar>
#include <cmath>
#include <algorithm>
#include <sstream>
#include <iomanip>
#include <utility>
#include "ProjectionPolicy.h"
#include "ProjectionCursor.h"
#include "ProjectionInput.h"

namespace {
using projection::Rectangle;
template<class T> class Ptr {
    T* p = nullptr;
public:
    Ptr() = default;
    ~Ptr() { reset(); }
    Ptr(const Ptr&) = delete;
    Ptr& operator=(const Ptr&) = delete;
    Ptr(Ptr&& other) noexcept : p(other.p) { other.p = nullptr; }
    Ptr& operator=(Ptr&& other) noexcept { if (this != &other) { reset(); p = other.p; other.p = nullptr; } return *this; }
    T* get() const { return p; }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
    T** put() { reset(); return &p; }
    void reset() { if (p) { p->Release(); p = nullptr; } }
};

std::string Utf8(const std::wstring& text) {
    if (text.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return "<invalid UTF-16>";
    std::string output(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), &output[0], count, nullptr, nullptr);
    return output;
}
std::string Json(const std::string& value) {
    std::ostringstream out;
    out << '"';
    for (unsigned char c : value) {
        if (c == '"' || c == '\\') out << '\\' << c;
        else if (c < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<unsigned>(c) << std::dec;
        else out << c;
    }
    return out.str() + '"';
}
std::string Hr(HRESULT hr) {
    char buffer[16]; std::snprintf(buffer, sizeof(buffer), "0x%08lX", static_cast<unsigned long>(hr)); return buffer;
}
std::string RectJson(const Rectangle& r) {
    std::ostringstream out; out << "{\"x\":" << r.x << ",\"y\":" << r.y << ",\"width\":" << r.width << ",\"height\":" << r.height << '}'; return out.str();
}
std::string LuidString(LUID luid) {
    char buffer[32]; std::snprintf(buffer, sizeof(buffer), "%08lX:%08lX", static_cast<unsigned long>(luid.HighPart), static_cast<unsigned long>(luid.LowPart)); return buffer;
}
bool SameLuid(LUID a, LUID b) { return a.HighPart == b.HighPart && a.LowPart == b.LowPart; }
Rectangle Rect(const RECT& r) {
    const int64_t width = static_cast<int64_t>(r.right) - r.left, height = static_cast<int64_t>(r.bottom) - r.top;
    if (width <= 0 || width > INT32_MAX || height <= 0 || height > INT32_MAX) return {};
    return {r.left, r.top, static_cast<int32_t>(width), static_cast<int32_t>(height)};
}

static const char kPresenterShader[] =
            "struct V { float4 p:SV_Position; float2 uv:TEXCOORD0; };"
            "V vs(uint i:SV_VertexID) { V o; float2 uv=float2((i<<1)&2,i&2); o.uv=uv; o.p=float4(uv.x*2-1,1-uv.y*2,0,1); return o; }"
            "Texture2D image:register(t0); SamplerState samp:register(s0);"
            "Texture2D<uint4> pointerImage:register(t1);"
            "cbuffer Color:register(b0) { float whiteScale; uint convertScRgb; float2 padding; };"
            "cbuffer Cursor:register(b1) { int2 pointerPosition; uint2 pointerSize; uint pointerType; uint pointerVisible; float2 imageSize; };"
            "float3 encodeSrgb(float3 c) { c=saturate(c); float3 a=12.92*c; float3 b=1.055*pow(c,1.0/2.4)-0.055;"
            "return float3(c.r<=0.0031308?a.r:b.r,c.g<=0.0031308?a.g:b.g,c.b<=0.0031308?a.b:b.b); }"
            "float4 ps(V i):SV_Target { float3 c=image.Sample(samp,i.uv).rgb; if(convertScRgb!=0) c=encodeSrgb(c*whiteScale);"
            "int2 q=int2(floor(i.uv*imageSize))-pointerPosition;"
            "if(pointerVisible!=0 && all(q>=0) && all(q<int2(pointerSize))) { uint4 p=pointerImage.Load(int3(q,0)); uint3 b=uint3(round(saturate(c)*255));"
            "if(pointerType==1) c=float3((b & p.aaa)^p.rgb)/255;"
            "else if(pointerType==4) c=float3(p.a==255 ? b^p.rgb : p.rgb)/255;"
            "else c=lerp(c,float3(p.rgb)/255,float(p.a)/255); } return float4(c,1); }";

struct Options {
    bool list = false, selfTest = false, cursorGpuTest = false, fixture = false, verify = false, allowSdrConversion = false;
    bool interactive = false, entryCenter = false, clickToEnter = false;
    HWND sourceWindow = nullptr;
    HWND returnWindow = nullptr;
    DWORD sourcePid = 0;
    std::wstring sourceDevice, report, releaseBeginName, releaseAckName;
    Rectangle destination, crop;
    int seconds = 0, fixtureInputStallMs = 0;
};
bool ParseOptions(int argc, wchar_t** argv, Options& options, std::string& error) {
    bool source = false, destination = false, seconds = false, report = false;
    for (int i = 1; i < argc; ++i) {
        const std::wstring argument = argv[i];
        if (argument == L"--list-outputs" && !options.list) { options.list = true; continue; }
        if (argument == L"--self-test" && !options.selfTest) { options.selfTest = true; continue; }
        if (argument == L"--self-test-cursor-gpu" && !options.cursorGpuTest) { options.cursorGpuTest = true; continue; }
        if (argument == L"--verify-fixture-colors" && !options.verify) { options.verify = true; continue; }
        if (argument == L"--allow-sdr-conversion" && !options.allowSdrConversion) { options.allowSdrConversion = true; continue; }
        if (argument == L"--interactive-entry-center" && !options.entryCenter) { options.entryCenter = true; continue; }
        if (argument == L"--interactive-click-to-enter" && !options.clickToEnter) { options.clickToEnter = true; continue; }
        if (i + 1 >= argc) { error = "Missing value or repeated/unknown option: " + Utf8(argument); return false; }
        const std::wstring value = argv[++i];
        uint64_t identifier = 0;
        if (argument == L"--source-device" && !source) { options.sourceDevice = value; source = true; }
        else if (argument == L"--report" && !report && !value.empty()) { options.report = value; report = true; }
        else if (argument == L"--destination" && !destination && projection::ParseRectangle(value, options.destination)) destination = true;
        else if (argument == L"--fixture-source-rect" && !options.fixture && projection::ParseRectangle(value, options.crop)) options.fixture = true;
        else if (argument == L"--seconds" && !seconds && projection::ParseInteger(value, options.seconds) && options.seconds >= 1 && options.seconds <= 600) seconds = true;
        else if (argument == L"--fixture-input-stall-ms" && !options.fixtureInputStallMs && projection::ParseInteger(value,options.fixtureInputStallMs) && options.fixtureInputStallMs>=2800 && options.fixtureInputStallMs<=3500) {}
        else if (argument == L"--interactive-source-hwnd" && !options.sourceWindow && projection_cursor::ParseUnsigned(value,UINTPTR_MAX,true,identifier)) options.sourceWindow = reinterpret_cast<HWND>(static_cast<UINT_PTR>(identifier));
        else if (argument == L"--interactive-source-pid" && !options.sourcePid && projection_cursor::ParseUnsigned(value,MAXDWORD,false,identifier)) options.sourcePid = static_cast<DWORD>(identifier);
        else if (argument == L"--interactive-return-hwnd" && !options.returnWindow && projection_cursor::ParseUnsigned(value,UINTPTR_MAX,true,identifier)) options.returnWindow = reinterpret_cast<HWND>(static_cast<UINT_PTR>(identifier));
        else if (argument == L"--lease-release-begin-event" && options.releaseBeginName.empty() && projection_input::ValidLeaseReleaseEventName(value,L"Begin")) options.releaseBeginName = value;
        else if (argument == L"--lease-release-ack-event" && options.releaseAckName.empty() && projection_input::ValidLeaseReleaseEventName(value,L"Ack")) options.releaseAckName = value;
        else { error = "Invalid, repeated, or unknown option: " + Utf8(argument); return false; }
    }
    if (options.list || options.selfTest || options.cursorGpuTest) {
        if (argc != 2) { error = "--list-outputs, --self-test and --self-test-cursor-gpu must each be used alone."; return false; }
        return true;
    }
    if (!source || !destination || !seconds || !report || options.sourceDevice.empty()) {
        error = "Required: --source-device \\\\.\\DISPLAYn --destination x,y,width,height --seconds 1..600 --report path. Optional owned-test crop: --fixture-source-rect x,y,width,height [--verify-fixture-colors].";
        return false;
    }
    if (options.verify && !options.fixture) { error = "Color verification is only allowed with an explicit owned-fixture crop."; return false; }
    if (options.allowSdrConversion && !options.fixture) { error = "SDR conversion from an advanced-color source is only allowed with an explicit owned-fixture crop in this prototype."; return false; }
    if ((options.sourceWindow != nullptr) != (options.sourcePid != 0)) { error = "Interactive projection requires both --interactive-source-hwnd and --interactive-source-pid."; return false; }
    options.interactive = options.sourceWindow != nullptr;
    if (options.returnWindow && !options.interactive) { error = "A return window requires an explicit interactive source."; return false; }
    if (options.entryCenter && (!options.interactive || !options.fixture)) { error = "Center entry requires an explicit interactive source and owned-fixture crop."; return false; }
    if (options.clickToEnter && (!options.interactive || options.entryCenter)) { error = "Click-to-enter requires an explicit interactive source and cannot use center entry."; return false; }
    if (options.releaseBeginName.empty() != options.releaseAckName.empty()) { error = "A lease release gate requires both --lease-release-begin-event and --lease-release-ack-event."; return false; }
    if (!options.releaseBeginName.empty() && !options.interactive) { error = "A lease release gate requires an explicit interactive source."; return false; }
    if (options.fixtureInputStallMs && (!options.fixture || !options.interactive || options.seconds<6)) {
        error = "An input watchdog stall requires an explicit owned-fixture crop, interactive source and at least six seconds."; return false;
    }
    return true;
}

struct Output {
    Ptr<IDXGIAdapter1> adapter;
    Ptr<IDXGIOutput> output;
    DXGI_ADAPTER_DESC1 adapterDesc{};
    DXGI_OUTPUT_DESC desc{};
    DXGI_OUTPUT_DESC1 extended{};
    bool colorKnown = false;
};
HRESULT EnumerateOutputs(std::vector<Output>& result) {
    Ptr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(factory.put()));
    if (FAILED(hr)) return hr;
    for (UINT ai = 0; ; ++ai) {
        Ptr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(ai, adapter.put());
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(hr)) return hr;
        DXGI_ADAPTER_DESC1 adapterDesc{};
        if (FAILED(hr = adapter->GetDesc1(&adapterDesc))) return hr;
        for (UINT oi = 0; ; ++oi) {
            Output item;
            hr = adapter->EnumOutputs(oi, item.output.put());
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return hr;
            if (FAILED(hr = adapter->QueryInterface(__uuidof(IDXGIAdapter1), reinterpret_cast<void**>(item.adapter.put())))) return hr;
            item.adapterDesc = adapterDesc;
            if (FAILED(hr = item.output->GetDesc(&item.desc))) return hr;
            Ptr<IDXGIOutput6> six;
            if (SUCCEEDED(item.output->QueryInterface(__uuidof(IDXGIOutput6), reinterpret_cast<void**>(six.put())))) item.colorKnown = SUCCEEDED(six->GetDesc1(&item.extended));
            result.emplace_back(std::move(item));
        }
    }
    return S_OK;
}
std::string OutputJson(const Output& output) {
    std::ostringstream out;
    out << "{\"device\":" << Json(Utf8(output.desc.DeviceName)) << ",\"adapterLuid\":" << Json(LuidString(output.adapterDesc.AdapterLuid))
        << ",\"adapter\":" << Json(Utf8(output.adapterDesc.Description)) << ",\"attached\":" << (output.desc.AttachedToDesktop ? "true" : "false")
        << ",\"bounds\":" << RectJson(Rect(output.desc.DesktopCoordinates)) << ",\"rotation\":" << static_cast<unsigned>(output.desc.Rotation)
        << ",\"colorSpaceKnown\":" << (output.colorKnown ? "true" : "false") << ",\"colorSpace\":";
    if (output.colorKnown) out << static_cast<unsigned>(output.extended.ColorSpace); else out << "null";
    out << ",\"sdr\":" << (output.colorKnown && output.extended.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? "true" : "false") << '}';
    return out.str();
}
bool SupportedColorAndRotation(const Output& output, bool source, bool allowSdrConversion, std::string& error) {
    if (output.desc.Rotation != DXGI_MODE_ROTATION_IDENTITY && output.desc.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED) { error = "Rotated displays are unsupported in this projection prototype."; return false; }
    if (!output.colorKnown) { error = "Cannot confirm the output color space; this prototype requires a known output color space."; return false; }
    // DWM can compose an explicitly SDR swapchain onto an HDR destination. The
    // source is different: no implicit loss of HDR is allowed outside the
    // explicitly opted-in owned fixture. This is never HDR-preserving capture.
    if (source && output.extended.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 && !allowSdrConversion) {
        error = "HDR or advanced-color source requires explicit owned-fixture --allow-sdr-conversion. Display settings were not changed."; return false;
    }
    return true;
}

struct WhiteLevel {
    bool known = false;
    UINT value = 0;
    LUID targetAdapter{};
    UINT targetId = 0;
};
HRESULT ReadSdrWhiteLevel(const std::wstring& deviceName, LUID sourceAdapter, WhiteLevel& level) {
    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        UINT32 pathCount = 0, modeCount = 0;
        LONG result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&pathCount,&modeCount);
        if (result != ERROR_SUCCESS) return HRESULT_FROM_WIN32(result);
        if (!pathCount || pathCount > 128 || modeCount > 384) return E_UNEXPECTED;
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
        std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
        result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&pathCount,paths.data(),&modeCount,modes.data(),nullptr);
        if (result == ERROR_INSUFFICIENT_BUFFER) continue;
        if (result != ERROR_SUCCESS) return HRESULT_FROM_WIN32(result);
        unsigned matches = 0;
        DISPLAYCONFIG_PATH_TARGET_INFO selected{};
        for (UINT32 i = 0; i < pathCount; ++i) {
            if (!SameLuid(paths[i].sourceInfo.adapterId,sourceAdapter)) continue;
            DISPLAYCONFIG_SOURCE_DEVICE_NAME name{};
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            name.header.size = sizeof(name);
            name.header.adapterId = paths[i].sourceInfo.adapterId;
            name.header.id = paths[i].sourceInfo.id;
            result = DisplayConfigGetDeviceInfo(&name.header);
            if (result != ERROR_SUCCESS) return HRESULT_FROM_WIN32(result);
            if (_wcsicmp(name.viewGdiDeviceName,deviceName.c_str()) == 0) { selected = paths[i].targetInfo; ++matches; }
        }
        // A cloned source can have multiple different SDR brightness levels.
        // This fixture requires one unambiguous source-to-target mapping.
        if (matches != 1) return E_UNEXPECTED;
        DISPLAYCONFIG_SDR_WHITE_LEVEL white{};
        white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        white.header.size = sizeof(white);
        white.header.adapterId = selected.adapterId;
        white.header.id = selected.id;
        result = DisplayConfigGetDeviceInfo(&white.header);
        if (result != ERROR_SUCCESS) return HRESULT_FROM_WIN32(result);
        double scale = 0;
        if (!projection::SdrWhiteScale(white.SDRWhiteLevel,scale)) return E_UNEXPECTED;
        level = {true,white.SDRWhiteLevel,selected.adapterId,selected.id};
        return S_OK;
    }
    return HRESULT_FROM_WIN32(ERROR_RETRY);
}

struct Metrics {
    uint64_t captured = 0, presented = 0, desktopFramesSkipped = 0, captureTimeouts = 0, captureErrors = 0, presentErrors = 0, presentBusy = 0, occluded = 0, recoveries = 0;
    uint64_t framesReleased = 0;
    std::vector<double> ages;
    double ageTotal = 0, ageMaximum = 0;
    uint64_t ageCount = 0;
    HRESULT lastCapture = S_OK, lastPresent = S_OK;
};
struct FixtureResult {
    bool requested = false, scheduled = false, completed = false, passed = false;
    unsigned pixels[4][3]{};
    unsigned grayPixels[4][3]{};
    bool quadrantsPassed = false, graysPassed = false;
    std::string error;
};
// clickToEnter makes the projected image accept a left click (it is otherwise
// click-through). The click is real user input to this process, which is what
// lets Windows grant the source focus without any focus-lock bypass.
struct WindowState { bool close = false, topologyChanged = false, clickToEnter = false, clicked = false; };
LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    WindowState* state = reinterpret_cast<WindowState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        state = static_cast<WindowState*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
    }
    switch (message) {
        case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
        case WM_NCHITTEST: return state && state->clickToEnter ? HTCLIENT : HTTRANSPARENT;
        case WM_LBUTTONUP: if (state && state->clickToEnter) state->clicked = true; return 0;
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: { PAINTSTRUCT paint{}; BeginPaint(hwnd, &paint); EndPaint(hwnd, &paint); return 0; }
        case WM_DISPLAYCHANGE: case WM_DPICHANGED: if (state) state->topologyChanged = true; return 0;
        case WM_CLOSE: if (state) state->close = true; return 0;
        case WM_DESTROY: if (state) state->close = true; return 0;
        default: return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
void Pump(projection_input::Session* input) {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { if (input && input->HandleMessage(message)) continue; TranslateMessage(&message); DispatchMessageW(&message); }
}
bool WriteReport(const std::wstring& path, const std::string& report);

class Presenter {
    const Options& options;
    Rectangle sourceBounds, crop;
    LUID adapterLuid{};
    LUID destinationLuid{};
    std::wstring destinationName;
    Rectangle destinationBounds;
    unsigned sourceColorSpace = 0, destinationColorSpace = 0;
    bool convertedSource = false, explicitSdrColorSpace = false;
    bool shaderConvertsScRgb = false;
    DXGI_FORMAT captureFormat = DXGI_FORMAT_UNKNOWN;
    WhiteLevel sourceWhite;
    float whiteScale = 1;
    ULONGLONG lastWhiteCheck = 0;
    std::string captureApi;
    bool duplicationDescriptionObserved = false, frameDescriptionObserved = false;
    DXGI_OUTDUPL_DESC duplicationDescription{};
    D3D11_TEXTURE2D_DESC frameDescription{};
    WindowState state;
    HWND window = nullptr;
    Ptr<ID3D11Device> device;
    Ptr<ID3D11DeviceContext> context;
    Ptr<IDXGIOutputDuplication> duplication;
    Ptr<IDXGISwapChain1> swapchain;
    Ptr<ID3D11Texture2D> backbuffer, cropTexture, staging;
    Ptr<ID3D11RenderTargetView> target;
    Ptr<ID3D11ShaderResourceView> view;
    Ptr<ID3D11VertexShader> vertexShader;
    Ptr<ID3D11PixelShader> pixelShader;
    Ptr<ID3D11SamplerState> sampler;
    Ptr<ID3D11Buffer> colorConstants;
    Ptr<ID3D11Texture2D> cursorTexture;
    Ptr<ID3D11ShaderResourceView> cursorView;
    Ptr<ID3D11Buffer> cursorConstants;
    projection_cursor::Shape cursorShape;
    DXGI_OUTDUPL_POINTER_SHAPE_INFO pointerDescription{};
    POINT pointerPosition{};
    bool pointerStateKnown = false, pointerVisible = false;
    uint64_t cursorUpdates = 0, cursorDrawnFrames = 0;
    projection_input::Session input;
    HANDLE releaseBegin = nullptr, releaseAck = nullptr;
    bool inputAttempted = false, inputReadyWritten = false;
    unsigned entryClicks = 0;
    std::string entryWait;
    bool fixtureInputStallStarted = false, fixtureInputStallCompleted = false;
    ULONGLONG fixtureInputStallElapsed = 0;
    Ptr<ID3D11Query> readbackComplete;
    projection::Fit fit;
    LARGE_INTEGER frequency{};
    ULONGLONG verificationStarted = 0;
    bool acquired = false;
    bool shown = false;
    ULONGLONG started = 0;
    HRESULT errorHr = S_OK;
    std::string error;
    int code = 0;

    bool Fail(const char* stage, HRESULT hr, int exitCode = 4) {
        if (error.empty()) { error = stage; errorHr = hr; code = exitCode; }
        input.Release("Projection stopped after a renderer failure.");
        return false;
    }
    bool CreateDuplication(IDXGIOutput* output) {
        HRESULT hr = E_NOINTERFACE;
        {
            Ptr<IDXGIOutput5> five;
            hr = output->QueryInterface(__uuidof(IDXGIOutput5), reinterpret_cast<void**>(five.put()));
            if (SUCCEEDED(hr)) {
                const DXGI_FORMAT formats[] = {DXGI_FORMAT_R16G16B16A16_FLOAT,DXGI_FORMAT_B8G8R8A8_UNORM};
                hr = five->DuplicateOutput1(device.get(), 0, convertedSource ? 2 : 1, convertedSource ? formats : formats+1, duplication.put());
                if (SUCCEEDED(hr)) captureApi = convertedSource ? "DuplicateOutput1 (explicit FP16/BGRA fixture formats)" : "DuplicateOutput1 (SDR BGRA)";
            }
            // Only older API/format support permits the ordinary SDR fallback;
            // availability, permissions and device errors are reported directly.
            if (FAILED(hr) && hr != E_NOINTERFACE && hr != E_NOTIMPL && hr != E_INVALIDARG && hr != DXGI_ERROR_UNSUPPORTED)
                return Fail("Desktop Duplication 1 could not start for the selected output.", hr);
        }
        if (!duplication) {
        Ptr<IDXGIOutput1> one;
        hr = output->QueryInterface(__uuidof(IDXGIOutput1), reinterpret_cast<void**>(one.put()));
        if (FAILED(hr)) return Fail("Source output does not expose Desktop Duplication.", hr);
        // Some tested HDR systems also return FP16 from the older API despite
        // its documented BGRA behavior. Always inspect the actual descriptor.
        hr = one->DuplicateOutput(device.get(), duplication.put());
        if (FAILED(hr)) return Fail("Desktop Duplication could not start for the selected source output.", hr);
        captureApi = convertedSource ? "DuplicateOutput (explicit HDR fixture fallback)" : "DuplicateOutput (SDR fallback)";
        }
        duplication->GetDesc(&duplicationDescription);
        pointerStateKnown = false; pointerVisible = false; cursorShape = {};
        duplicationDescriptionObserved = true;
        const DXGI_OUTDUPL_DESC& desc = duplicationDescription;
        const bool allowedFp16 = options.allowSdrConversion && options.fixture && convertedSource && desc.ModeDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        if ((desc.ModeDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && !allowedFp16) || desc.ModeDesc.Width != static_cast<UINT>(sourceBounds.width) || desc.ModeDesc.Height != static_cast<UINT>(sourceBounds.height) ||
            (desc.Rotation != DXGI_MODE_ROTATION_IDENTITY && desc.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED)) {
            std::ostringstream reason;
            reason << "Desktop Duplication description differs from the supported capture geometry/format: observed "
                << desc.ModeDesc.Width << 'x' << desc.ModeDesc.Height << ", DXGI format " << static_cast<unsigned>(desc.ModeDesc.Format)
                << ", rotation " << static_cast<unsigned>(desc.Rotation) << "; expected " << sourceBounds.width << 'x' << sourceBounds.height
                << ", BGRA format " << static_cast<unsigned>(DXGI_FORMAT_B8G8R8A8_UNORM) << (convertedSource ? " or explicitly enabled FP16 format10" : "") << ". No frame was acquired.";
            return Fail(reason.str().c_str(), E_UNEXPECTED);
        }
        if (captureFormat != DXGI_FORMAT_UNKNOWN && captureFormat != desc.ModeDesc.Format) return Fail("Capture pixel format changed during recovery; restart projection.",DXGI_ERROR_ACCESS_LOST);
        captureFormat = desc.ModeDesc.Format;
        shaderConvertsScRgb = allowedFp16;
        if (shaderConvertsScRgb) {
            WhiteLevel current;
            hr = ReadSdrWhiteLevel(options.sourceDevice,adapterLuid,current);
            if (FAILED(hr)) return Fail("Cannot query an unambiguous source SDR white level for FP16 conversion.",hr);
            if (sourceWhite.known && current.value != sourceWhite.value) return Fail("Source SDR brightness changed during capture; restart the color-validation fixture.",DXGI_ERROR_ACCESS_LOST);
            sourceWhite = current;
            whiteScale = 1000.0f/sourceWhite.value;
            lastWhiteCheck = GetTickCount64();
        }
        return true;
    }
    bool ReleaseFrame() {
        if (!acquired) return true;
        acquired = false;
        HRESULT hr = duplication->ReleaseFrame();
        if (FAILED(hr)) { ++metrics.captureErrors; metrics.lastCapture = hr; return Fail("Releasing the captured frame failed.", hr); }
        ++metrics.framesReleased;
        return true;
    }
    bool CreateShaders() {
        Ptr<ID3DBlob> vs, ps, compilerErrors;
        HRESULT hr = D3DCompile(kPresenterShader, sizeof(kPresenterShader) - 1, "RegionPresenter", nullptr, nullptr, "vs", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, vs.put(), compilerErrors.put());
        if (FAILED(hr)) return Fail("Vertex shader compilation failed.", hr);
        hr = D3DCompile(kPresenterShader, sizeof(kPresenterShader) - 1, "RegionPresenter", nullptr, nullptr, "ps", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, ps.put(), compilerErrors.put());
        if (FAILED(hr)) return Fail("Pixel shader compilation failed.", hr);
        if (FAILED(hr = device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, vertexShader.put()))) return Fail("Vertex shader creation failed.", hr);
        if (FAILED(hr = device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, pixelShader.put()))) return Fail("Pixel shader creation failed.", hr);
        D3D11_SAMPLER_DESC desc{};
        desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        desc.AddressU = desc.AddressV = desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        desc.MaxLOD = D3D11_FLOAT32_MAX;
        if (FAILED(hr = device->CreateSamplerState(&desc, sampler.put()))) return Fail("Sampler creation failed.", hr);
        struct Constants { float scale; UINT convert; float padding[2]; } constants{whiteScale,shaderConvertsScRgb ? 1U : 0U,{0,0}};
        D3D11_BUFFER_DESC buffer{}; buffer.ByteWidth = sizeof(constants); buffer.Usage = D3D11_USAGE_IMMUTABLE; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = &constants;
        if (FAILED(hr = device->CreateBuffer(&buffer,&data,colorConstants.put()))) return Fail("Color-conversion constant creation failed.",hr);
        buffer.ByteWidth = 32; buffer.Usage = D3D11_USAGE_DEFAULT;
        if (FAILED(hr = device->CreateBuffer(&buffer,nullptr,cursorConstants.put()))) return Fail("Pointer constant creation failed.",hr);
        // A bound zero texture avoids undefined/unbound resource reads before
        // the first hardware-pointer shape arrives, including hidden cursors.
        projection_cursor::Shape empty; empty.type = projection_cursor::Color; empty.width = empty.height = 1; empty.rgba.resize(4);
        if (!UploadPointer(empty)) return false;
        return true;
    }
    bool UploadPointer(const projection_cursor::Shape& shape) {
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = shape.width; desc.Height = shape.height;
        desc.MipLevels = desc.ArraySize = 1; desc.Format = DXGI_FORMAT_R8G8B8A8_UINT;
        desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_IMMUTABLE; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = shape.rgba.data(); data.SysMemPitch = shape.width * 4;
        Ptr<ID3D11Texture2D> texture; Ptr<ID3D11ShaderResourceView> resource;
        HRESULT hr = device->CreateTexture2D(&desc,&data,texture.put());
        if (FAILED(hr)) return Fail("Pointer texture creation failed.",hr);
        if (FAILED(hr = device->CreateShaderResourceView(texture.get(),nullptr,resource.put()))) return Fail("Pointer texture view creation failed.",hr);
        cursorTexture = std::move(texture); cursorView = std::move(resource); return true;
    }
    bool UpdatePointer(const DXGI_OUTDUPL_FRAME_INFO& frame) {
        // Position is already the top-left of the shape relative to this output.
        // DDA HotSpot is informational: subtracting it would offset twice.
        if (frame.LastMouseUpdateTime.QuadPart || !pointerStateKnown) {
            pointerStateKnown = true; pointerVisible = frame.PointerPosition.Visible != FALSE;
            if (pointerVisible) pointerPosition = frame.PointerPosition.Position;
        }
        if (frame.PointerShapeBufferSize || (pointerVisible && cursorShape.rgba.empty())) {
            UINT required = frame.PointerShapeBufferSize;
            std::vector<uint8_t> bytes;
            DXGI_OUTDUPL_POINTER_SHAPE_INFO description{};
            HRESULT hr = S_OK;
            if (!required) {
                hr = duplication->GetFramePointerShape(0,nullptr,&required,&description);
                if (hr != DXGI_ERROR_MORE_DATA && FAILED(hr)) return Fail("Initial pointer shape query failed.",hr);
            }
            if (!required || required > projection_cursor::MaximumBytes) return Fail("Invalid or excessive Desktop Duplication pointer buffer.",E_UNEXPECTED);
            bytes.resize(required);
            hr = duplication->GetFramePointerShape(static_cast<UINT>(bytes.size()),bytes.data(),&required,&description);
            if (FAILED(hr)) return Fail("Desktop Duplication pointer shape acquisition failed.",hr);
            if (required > bytes.size()) return Fail("Pointer buffer grew during acquisition.",E_UNEXPECTED);
            projection_cursor::Shape decoded; std::string reason;
            if (!projection_cursor::Decode(description.Type,description.Width,description.Height,description.Pitch,bytes.data(),required,decoded,reason)) return Fail(reason.c_str(),E_UNEXPECTED);
            if (!UploadPointer(decoded)) return false;
            cursorShape = std::move(decoded); pointerDescription = description; ++cursorUpdates;
        }
        return true;
    }
    bool CreateResources() {
        D3D11_TEXTURE2D_DESC texture{};
        texture.Width = crop.width; texture.Height = crop.height;
        texture.MipLevels = texture.ArraySize = 1;
        texture.Format = captureFormat;
        texture.SampleDesc.Count = 1;
        texture.Usage = D3D11_USAGE_DEFAULT;
        texture.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = device->CreateTexture2D(&texture, nullptr, cropTexture.put());
        if (FAILED(hr)) return Fail("Owned capture texture allocation failed.", hr);
        if (FAILED(hr = device->CreateShaderResourceView(cropTexture.get(), nullptr, view.put()))) return Fail("Capture texture view creation failed.", hr);
        if (FAILED(hr = swapchain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backbuffer.put())))) return Fail("Presenter backbuffer acquisition failed.", hr);
        if (FAILED(hr = device->CreateRenderTargetView(backbuffer.get(), nullptr, target.put()))) return Fail("Presenter render target creation failed.", hr);
        return CreateShaders();
    }
    bool Recover() {
        if (metrics.recoveries >= 3) return Fail("Desktop Duplication recovery limit reached.", DXGI_ERROR_ACCESS_LOST);
        ++metrics.recoveries;
        duplication.reset();
        std::vector<Output> fresh;
        HRESULT hr = EnumerateOutputs(fresh);
        if (FAILED(hr)) return Fail("Output enumeration during recovery failed.", hr);
        Output* selected = nullptr;
        Output* destinationOutput = nullptr;
        unsigned selectedCount = 0, destinationCount = 0;
        for (auto& output : fresh) {
            if (!output.desc.AttachedToDesktop) continue;
            if (_wcsicmp(output.desc.DeviceName, options.sourceDevice.c_str()) == 0 && SameLuid(output.adapterDesc.AdapterLuid, adapterLuid)) { selected = &output; ++selectedCount; }
            if (projection::Contains(Rect(output.desc.DesktopCoordinates), options.destination)) { destinationOutput = &output; ++destinationCount; }
        }
        std::string reason;
        if (selectedCount != 1 || destinationCount != 1 || !selected || !destinationOutput || !projection::Equal(Rect(selected->desc.DesktopCoordinates), sourceBounds) ||
            !projection::Equal(Rect(destinationOutput->desc.DesktopCoordinates), destinationBounds) || !SameLuid(destinationOutput->adapterDesc.AdapterLuid, destinationLuid) ||
            _wcsicmp(destinationOutput->desc.DeviceName, destinationName.c_str()) != 0 ||
            !SupportedColorAndRotation(*selected, true, options.allowSdrConversion, reason) || !SupportedColorAndRotation(*destinationOutput, false, false, reason) ||
            static_cast<unsigned>(selected->extended.ColorSpace) != sourceColorSpace || static_cast<unsigned>(destinationOutput->extended.ColorSpace) != destinationColorSpace)
            return Fail("Display identity, geometry, or color changed; restart projection after choosing outputs again.", DXGI_ERROR_ACCESS_LOST);
        return CreateDuplication(selected->output.get());
    }
    bool BeginReadback() {
        D3D11_TEXTURE2D_DESC desc{}; backbuffer->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
        HRESULT hr = device->CreateTexture2D(&desc, nullptr, staging.put());
        if (FAILED(hr)) return Fail("Fixture readback allocation failed.", hr, 5);
        D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
        if (FAILED(hr = device->CreateQuery(&query, readbackComplete.put()))) return Fail("Fixture readback query creation failed.", hr, 5);
        context->CopyResource(staging.get(), backbuffer.get());
        context->End(readbackComplete.get());
        context->Flush();
        fixture.scheduled = true; verificationStarted = GetTickCount64();
        return true;
    }
    bool PollReadback() {
        if (!fixture.scheduled || fixture.completed) return true;
        const HRESULT hr = context->GetData(readbackComplete.get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr == S_FALSE) {
            if (GetTickCount64() - verificationStarted > 1500) return Fail("Fixture readback did not complete within 1500 ms.", HRESULT_FROM_WIN32(WAIT_TIMEOUT), 5);
            return true;
        }
        if (FAILED(hr)) return Fail("Fixture readback GPU synchronization failed.", hr, 5);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const HRESULT mapHr = context->Map(staging.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (FAILED(mapHr)) return Fail("Fixture readback mapping failed.", mapHr, 5);
        const unsigned expected[4][3] = {{255,0,0},{0,255,0},{0,0,255},{255,255,0}};
        fixture.quadrantsPassed = fixture.graysPassed = true;
        for (unsigned index = 0; index < 4; ++index) {
            const unsigned x = static_cast<unsigned>(fit.x + fit.width * (index % 2 ? 0.75f : 0.25f));
            const unsigned y = static_cast<unsigned>(fit.y + fit.height * (index / 2 ? 0.75f : 0.25f));
            const unsigned char* pixel = static_cast<const unsigned char*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch + x * 4;
            fixture.pixels[index][0] = pixel[2]; fixture.pixels[index][1] = pixel[1]; fixture.pixels[index][2] = pixel[0];
            for (unsigned channel = 0; channel < 3; ++channel) if (std::abs(static_cast<int>(fixture.pixels[index][channel]) - static_cast<int>(expected[index][channel])) > 8) fixture.quadrantsPassed = false;
        }
        const unsigned grayExpected[] = {32,64,128,192};
        for (unsigned index = 0; index < 4; ++index) {
            const unsigned x = static_cast<unsigned>(fit.x + fit.width * ((index+0.5f)/4.0f));
            const unsigned y = static_cast<unsigned>(fit.y + fit.height * 0.5f);
            const unsigned char* pixel = static_cast<const unsigned char*>(mapped.pData) + static_cast<size_t>(y)*mapped.RowPitch+x*4;
            fixture.grayPixels[index][0] = pixel[2]; fixture.grayPixels[index][1] = pixel[1]; fixture.grayPixels[index][2] = pixel[0];
            for (unsigned channel = 0; channel < 3; ++channel) if (std::abs(static_cast<int>(fixture.grayPixels[index][channel])-static_cast<int>(grayExpected[index])) > 8) fixture.graysPassed = false;
        }
        fixture.passed = fixture.quadrantsPassed && fixture.graysPassed;
        context->Unmap(staging.get(), 0);
        fixture.completed = true;
        staging.reset(); readbackComplete.reset();
        if (!fixture.passed) { fixture.error = "Projected fixture quadrant colors or neutral gray levels did not match within 8/255."; return Fail(fixture.error.c_str(), E_FAIL, 5); }
        return true;
    }
    bool Draw(const DXGI_OUTDUPL_FRAME_INFO& frame) {
        const float black[4] = {0,0,0,1};
        context->ClearRenderTargetView(target.get(), black);
        ID3D11RenderTargetView* targets[] = {target.get()};
        context->OMSetRenderTargets(1, targets, nullptr);
        const D3D11_VIEWPORT viewport{fit.x,fit.y,fit.width,fit.height,0,1};
        context->RSSetViewports(1, &viewport);
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertexShader.get(), nullptr, 0);
        context->PSSetShader(pixelShader.get(), nullptr, 0);
        struct CursorConstants { int x,y; UINT width,height,type,visible; float imageWidth,imageHeight; } cursor{
            projection_cursor::CropOffset(pointerPosition.x,crop.x-sourceBounds.x,crop.width),projection_cursor::CropOffset(pointerPosition.y,crop.y-sourceBounds.y,crop.height),cursorShape.width,cursorShape.height,cursorShape.type,
            pointerVisible && !cursorShape.rgba.empty() && (!fixture.requested || fixture.scheduled) ? 1U : 0U,static_cast<float>(crop.width),static_cast<float>(crop.height)};
        static_assert(sizeof(CursorConstants) == 32,"Pointer shader constant layout");
        context->UpdateSubresource(cursorConstants.get(),0,nullptr,&cursor,0,0);
        if (cursor.visible && cursor.x < crop.width && cursor.y < crop.height && static_cast<int64_t>(cursor.x)+cursor.width > 0 && static_cast<int64_t>(cursor.y)+cursor.height > 0) ++cursorDrawnFrames;
        ID3D11Buffer* constants[] = {colorConstants.get(),cursorConstants.get()}; context->PSSetConstantBuffers(0,2,constants);
        ID3D11ShaderResourceView* views[] = {view.get(),cursorView.get()}; context->PSSetShaderResources(0, 2, views);
        ID3D11SamplerState* samplers[] = {sampler.get()}; context->PSSetSamplers(0, 1, samplers);
        context->Draw(3, 0);
        ID3D11ShaderResourceView* empty[] = {nullptr,nullptr}; context->PSSetShaderResources(0, 2, empty);
        if (fixture.requested && !fixture.scheduled && metrics.captured >= 8 && !BeginReadback()) return false;
        if (!shown) { ShowWindow(window, SW_SHOWNOACTIVATE); SetWindowPos(window, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW); shown = true; }
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        if (frame.LastPresentTime.QuadPart > 0 && frame.LastPresentTime.QuadPart <= now.QuadPart) {
            const double age = static_cast<double>(now.QuadPart - frame.LastPresentTime.QuadPart) * 1000.0 / frequency.QuadPart;
            metrics.ageTotal += age; metrics.ageMaximum = std::max(metrics.ageMaximum, age); ++metrics.ageCount;
            if (metrics.ages.size() < 1000000) metrics.ages.push_back(age);
        }
        HRESULT hr = swapchain->Present(1, DXGI_PRESENT_DO_NOT_WAIT);
        metrics.lastPresent = hr;
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING) { ++metrics.presentBusy; return true; }
        if (hr == DXGI_STATUS_OCCLUDED) { ++metrics.occluded; return true; }
        if (FAILED(hr)) { ++metrics.presentErrors; return Fail("Presenting the projected frame failed.", hr); }
        ++metrics.presented;
        return true;
    }
    RECT ProjectedImage() const {
        // Exclude partly covered letterbox edge pixels from interactive entry.
        return {options.destination.x+static_cast<LONG>(std::ceil(fit.x)),options.destination.y+static_cast<LONG>(std::ceil(fit.y)),
            options.destination.x+static_cast<LONG>(std::floor(fit.x+fit.width)),options.destination.y+static_cast<LONG>(std::floor(fit.y+fit.height))};
    }
    bool EnterInputWhenReady() {
        if (!options.interactive || inputAttempted || !metrics.presented || !pointerStateKnown || (fixture.requested && !fixture.completed)) return true;
        const RECT sourceClient{crop.x,crop.y,static_cast<LONG>(projection::Right(crop)),static_cast<LONG>(projection::Bottom(crop))};
        if (options.clickToEnter) {
            // Real-application entry: wait, without failing, for a click on the
            // projected image while the source exactly fills the captured
            // output. Too little remaining lifetime never enters.
            if (!state.clicked) return true;
            state.clicked = false; ++entryClicks;
            POINT cursor{};
            if (GetTickCount64()-started+2000 >= static_cast<ULONGLONG>(options.seconds)*1000) { entryWait = "too_little_time_left"; return true; }
            if (!GetCursorPos(&cursor) || !projection_input::policy::Contains(ProjectedImage(),cursor)) { entryWait = "click_outside_image"; return true; }
            if (!projection_input::SourceClientMatches(options.sourceWindow,sourceClient)) { entryWait = "source_does_not_fill_virtual_display"; return true; }
            entryWait.clear();
        }
        inputAttempted = true;
        if (pointerVisible && cursorShape.rgba.empty()) return Fail("A visible source pointer has no usable shape; interactive input was not started.",E_UNEXPECTED,7);
        projection_input::Config config;
        config.sourceWindow = options.sourceWindow; config.ownerWindow = window; config.sourcePid = options.sourcePid;
        config.returnFocusWindow = options.returnWindow;
        // A real user's return window may have been minimized since launch;
        // fall back to the current foreground rather than abandoning entry.
        if (options.clickToEnter && config.returnFocusWindow && (!IsWindowVisible(config.returnFocusWindow) || IsIconic(config.returnFocusWindow))) config.returnFocusWindow = nullptr;
        config.leaseReleaseBegin = releaseBegin; config.leaseReleaseAck = releaseAck;
        config.leaseReleaseBeginName = options.releaseBeginName; config.leaseReleaseAckName = options.releaseAckName;
        config.sourceClient = sourceClient;
        config.projectedImage = ProjectedImage(); config.allowEntryOutsideImage = options.entryCenter;
        const ULONGLONG elapsed = GetTickCount64()-started, duration = static_cast<ULONGLONG>(options.seconds)*1000;
        if (elapsed >= duration) return Fail("Projection deadline expired before interactive input could begin.",HRESULT_FROM_WIN32(WAIT_TIMEOUT),7);
        if (options.fixtureInputStallMs && duration-elapsed<static_cast<ULONGLONG>(options.fixtureInputStallMs)+500)
            return Fail("Too little fixture lifetime remains to observe heartbeat-based watchdog recovery.",HRESULT_FROM_WIN32(WAIT_TIMEOUT),7);
        config.maximumDurationMs = static_cast<DWORD>(duration-elapsed);
        std::string reason;
        if (!input.Enter(config,reason)) return Fail(reason.c_str(),HRESULT_FROM_WIN32(input.GetStatus().error ? input.GetStatus().error : ERROR_CANCELLED),7);
        input.Tick();
        if (!input.Active()) return Fail("Interactive ownership changed before readiness could be published.",HRESULT_FROM_WIN32(ERROR_CANCELLED),7);
        RECT activeClip{};
        if (GetForegroundWindow() != options.sourceWindow || !GetClipCursor(&activeClip) || !projection_input::policy::SameRect(activeClip,config.sourceClient)) return Fail("Interactive focus or clipping changed before readiness could be published.",HRESULT_FROM_WIN32(ERROR_CANCELLED),7);
        std::ostringstream ready;
        ready << "{\"schemaVersion\":1,\"pid\":" << GetCurrentProcessId() << ",\"hwndValue\":" << reinterpret_cast<UINT_PTR>(window)
            << ",\"inputActive\":true,\"sourcePid\":" << options.sourcePid << ",\"sourceHwndValue\":" << reinterpret_cast<UINT_PTR>(options.sourceWindow)
            << ",\"source\":" << RectJson(crop) << ",\"destination\":" << RectJson(options.destination) << ",\"projectedImage\":" << RectJson(Rect(ProjectedImage()))
            << ",\"releaseShortcut\":\"Ctrl+Alt+F10\",\"inputMode\":\"modalHardwareDirect\",\"focused\":true,\"clip\":" << RectJson(Rect(activeClip))
            << ",\"guardianReady\":" << (input.GetStatus().guardianReady ? "true" : "false") << ",\"fixtureInputStallMs\":" << options.fixtureInputStallMs << '}';
        if (!WriteReport(options.report+L".active.ready.json",ready.str())) return Fail("Could not publish interactive readiness; input has been released.",HRESULT_FROM_WIN32(GetLastError()),7);
        inputReadyWritten = true;
        if(options.fixtureInputStallMs){
            // Explicit owned-fixture fault simulation only. No input heartbeat,
            // messages or capture work runs while this UI thread is stalled.
            // Readiness is published first so the external fixture can arm its
            // bounded release journal before the guardian's timeout occurs.
            if(acquired)return Fail("A watchdog stall cannot hold an acquired desktop frame.",E_UNEXPECTED,7);
            fixtureInputStallStarted=true;
            const ULONGLONG before=GetTickCount64();
            Sleep(static_cast<DWORD>(options.fixtureInputStallMs));
            fixtureInputStallElapsed=GetTickCount64()-before;fixtureInputStallCompleted=true;
            input.Tick();
        }
        return true;
    }
public:
    Metrics metrics;
    FixtureResult fixture;
    explicit Presenter(const Options& value) : options(value) { fixture.requested = options.verify; }
    ~Presenter() {
        input.Release("Presenter closed.");
        ReleaseFrame();
        duplication.reset();
        if (context) { context->ClearState(); context->Flush(); }
        // The input session releases only its own temporary focus/cursor lease.
        if (window) DestroyWindow(window);
        for (HANDLE handle : {releaseBegin, releaseAck}) if (handle) CloseHandle(handle);
    }
    bool Start(Output& output, Output& destinationOutput) {
        sourceBounds = Rect(output.desc.DesktopCoordinates); crop = options.fixture ? options.crop : sourceBounds;
        if (!options.releaseBeginName.empty()) {
            // Verified before any capture or entry: the lease must already be
            // waiting for BEGIN, and its manual-reset ACK must still be clear.
            releaseBegin = OpenEventW(EVENT_MODIFY_STATE|SYNCHRONIZE, FALSE, options.releaseBeginName.c_str());
            releaseAck = OpenEventW(SYNCHRONIZE, FALSE, options.releaseAckName.c_str());
            if (!releaseBegin || !releaseAck) return Fail("Cannot open the lease release events.", HRESULT_FROM_WIN32(GetLastError()), 3);
            if (WaitForSingleObject(releaseAck, 0) != WAIT_TIMEOUT) return Fail("The lease release was already acknowledged.", HRESULT_FROM_WIN32(ERROR_INVALID_STATE), 3);
        }
        adapterLuid = output.adapterDesc.AdapterLuid;
        destinationLuid = destinationOutput.adapterDesc.AdapterLuid;
        destinationName = destinationOutput.desc.DeviceName;
        destinationBounds = Rect(destinationOutput.desc.DesktopCoordinates);
        sourceColorSpace = static_cast<unsigned>(output.extended.ColorSpace);
        destinationColorSpace = static_cast<unsigned>(destinationOutput.extended.ColorSpace);
        convertedSource = output.extended.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        started = GetTickCount64(); QueryPerformanceFrequency(&frequency);
        fit = projection::AspectFit(crop.width,crop.height,options.destination.width,options.destination.height);
        D3D_FEATURE_LEVEL actual{};
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        HRESULT hr = D3D11CreateDevice(output.adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 3, D3D11_SDK_VERSION, device.put(), &actual, context.put());
        if (FAILED(hr)) return Fail("D3D11 device creation on the source adapter failed.", hr);
        if (actual < D3D_FEATURE_LEVEL_11_0 && (crop.width > 8192 || crop.height > 8192 || options.destination.width > 8192 || options.destination.height > 8192)) return Fail("Dimensions exceed this adapter's D3D feature-level limit.", E_INVALIDARG);
        Ptr<IDXGIDevice1> dxgiDevice;
        if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice1), reinterpret_cast<void**>(dxgiDevice.put())))) dxgiDevice->SetMaximumFrameLatency(1);
        if (!CreateDuplication(output.output.get())) return false;
        WNDCLASSEXW wc{}; wc.cbSize = sizeof(wc); wc.lpfnWndProc = WindowProcedure; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"DisplayWeaveProjectionPreview";
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return Fail("Presenter window registration failed.", HRESULT_FROM_WIN32(GetLastError()));
        state.clickToEnter = options.clickToEnter;
        window = CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|(options.clickToEnter ? 0 : WS_EX_TRANSPARENT)|WS_EX_TOPMOST, wc.lpszClassName, L"DisplayWeave GPU projection prototype", WS_POPUP,
            options.destination.x, options.destination.y, options.destination.width, options.destination.height, nullptr, nullptr, wc.hInstance, &state);
        if (!window) return Fail("Presenter window creation failed.", HRESULT_FROM_WIN32(GetLastError()));
        RECT client{}; GetClientRect(window, &client);
        if (client.right != options.destination.width || client.bottom != options.destination.height) return Fail("Presenter client geometry differs from requested physical pixels.", E_UNEXPECTED);
        Ptr<IDXGIFactory2> factory;
        hr = output.adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(factory.put()));
        if (FAILED(hr)) return Fail("DXGI flip-model factory is unavailable.", hr);
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = options.destination.width; desc.Height = options.destination.height;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        if (FAILED(hr = factory->CreateSwapChainForHwnd(device.get(), window, &desc, nullptr, nullptr, swapchain.put()))) return Fail("SDR presenter swapchain creation failed.", hr);
        Ptr<IDXGISwapChain3> three;
        if (SUCCEEDED(swapchain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(three.put())))) {
            UINT supported = 0;
            if (FAILED(hr = three->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, &supported))) return Fail("Presenter SDR color-space check failed.", hr);
            if (!(supported & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) return Fail("Presenter does not support the required SDR color space.", DXGI_ERROR_UNSUPPORTED);
            if (FAILED(hr = three->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709))) return Fail("Presenter SDR color-space selection failed.", hr);
            explicitSdrColorSpace = true;
        }
        factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER|DXGI_MWA_NO_WINDOW_CHANGES);
        return CreateResources();
    }
    int Run() {
        while (error.empty() && !state.close && GetTickCount64() - started < static_cast<ULONGLONG>(options.seconds) * 1000) {
            Pump(&input);
            if (input.GetStatus().entered && !input.Active()) break;
            input.Tick();
            if (input.GetStatus().entered && !input.Active()) break;
            if (state.close) break;
            if (state.topologyChanged) {
                state.topologyChanged = false;
                if (input.Active()) { input.Release("Display topology changed."); break; }
                if (!Recover()) break;
            }
            if (shaderConvertsScRgb && GetTickCount64()-lastWhiteCheck >= 1000) {
                DISPLAYCONFIG_SDR_WHITE_LEVEL current{};
                current.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL; current.header.size = sizeof(current);
                current.header.adapterId = sourceWhite.targetAdapter; current.header.id = sourceWhite.targetId;
                const LONG result = DisplayConfigGetDeviceInfo(&current.header);
                if (result != ERROR_SUCCESS) { Fail("Source SDR white-level revalidation failed.",HRESULT_FROM_WIN32(result)); break; }
                if (current.SDRWhiteLevel != sourceWhite.value) { Fail("Source SDR brightness changed during capture; restart the color-validation fixture.",DXGI_ERROR_ACCESS_LOST); break; }
                lastWhiteCheck = GetTickCount64();
            }
            if (!PollReadback()) break;
            if (!EnterInputWhenReady()) break;
            if (input.GetStatus().entered && !input.Active()) break;
            DXGI_OUTDUPL_FRAME_INFO frame{};
            Ptr<IDXGIResource> resource;
            HRESULT hr = duplication->AcquireNextFrame(16, &frame, resource.put());
            metrics.lastCapture = hr;
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) { ++metrics.captureTimeouts; continue; }
            if (hr == DXGI_ERROR_ACCESS_LOST) { ++metrics.captureErrors; if (input.Active()) { Fail("Capture access was lost; interactive input has been released.",hr); break; } if (!Recover()) break; continue; }
            if (FAILED(hr)) { ++metrics.captureErrors; Fail("Acquiring a desktop frame failed.", hr); break; }
            acquired = true;
            ++metrics.captured;
            if (!UpdatePointer(frame)) break;
            if (frame.AccumulatedFrames > 1) metrics.desktopFramesSkipped += frame.AccumulatedFrames - 1;
            Ptr<ID3D11Texture2D> texture;
            hr = resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(texture.put()));
            if (FAILED(hr)) { Fail("Captured desktop resource is not a D3D11 texture.", hr); break; }
            D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
            frameDescription = desc; frameDescriptionObserved = true;
            if (desc.Format != captureFormat || desc.Width != static_cast<UINT>(sourceBounds.width) || desc.Height != static_cast<UINT>(sourceBounds.height)) { Fail("Captured desktop geometry or format changed.", E_UNEXPECTED); break; }
            const D3D11_BOX box{static_cast<UINT>(crop.x - sourceBounds.x), static_cast<UINT>(crop.y - sourceBounds.y), 0,
                static_cast<UINT>(crop.x - sourceBounds.x + crop.width), static_cast<UINT>(crop.y - sourceBounds.y + crop.height), 1};
            context->CopySubresourceRegion(cropTexture.get(), 0, 0, 0, 0, texture.get(), 0, &box);
            texture.reset(); resource.reset();
            if (!ReleaseFrame() || !Draw(frame) || !EnterInputWhenReady()) break;
        }
        input.Release(state.close ? "Presenter window closed." : "Projection run ended.");
        ReleaseFrame();
        if (error.empty() && metrics.presented == 0) Fail("No projected frame was presented during the bounded run.", E_FAIL);
        if (error.empty() && fixture.requested && !fixture.completed) Fail("Fixture verification did not complete during the bounded run.", E_FAIL, 5);
        if (error.empty() && options.interactive && !options.clickToEnter && !input.GetStatus().entered) Fail("Interactive input never became ready during the bounded run.",E_FAIL,7);
        if (error.empty() && input.GetStatus().releaseIncomplete) Fail("Interactive release could not restore all owned state; see the input report.",E_FAIL,7);
        return code;
    }
    int Code() const { return code; }
    std::string Report() {
        std::sort(metrics.ages.begin(), metrics.ages.end());
        auto percentile = [&](double p) { return metrics.ages.empty() ? 0.0 : metrics.ages[static_cast<size_t>(std::ceil(p * metrics.ages.size())) - 1]; };
        const double elapsed = started ? static_cast<double>(GetTickCount64() - started) : 0;
        std::ostringstream out;
        out << std::fixed << std::setprecision(3);
        out << "{\"schemaVersion\":1,\"success\":" << (error.empty() ? "true" : "false") << ",\"status\":" << Json(error.empty() ? "completed" : "failed") << ",\"reason\":" << Json(error) << ",\"hresult\":" << Json(Hr(errorHr))
            << ",\"pid\":" << GetCurrentProcessId() << ",\"backend\":\"DXGI Desktop Duplication / D3D11 SDR\",\"fixtureCrop\":" << (options.fixture ? "true" : "false")
            << ",\"colorMode\":" << Json(convertedSource ? "sdr-converted-from-hdr" : "sdr") << ",\"sourceColorSpace\":" << sourceColorSpace << ",\"destinationColorSpace\":" << destinationColorSpace
            << ",\"explicitSdrColorSpace\":" << (explicitSdrColorSpace ? "true" : "false") << ",\"captureApi\":" << Json(captureApi) << ",\"hdrPreserved\":false,\"cursorComposited\":" << (cursorDrawnFrames ? "true" : "false")
            << ",\"pointer\":{\"stateKnown\":" << (pointerStateKnown ? "true" : "false") << ",\"separatePointerVisible\":" << (pointerVisible ? "true" : "false")
            << ",\"visibilityNote\":\"A non-visible separate pointer may be hidden by the source or already included in its captured image.\",\"shapeType\":" << cursorShape.type
            << ",\"width\":" << cursorShape.width << ",\"height\":" << cursorShape.height << ",\"hotspotX\":" << pointerDescription.HotSpot.x << ",\"hotspotY\":" << pointerDescription.HotSpot.y
            << ",\"shapeUpdates\":" << cursorUpdates << ",\"compositedDraws\":" << cursorDrawnFrames << ",\"desktopCpuReadbacks\":0}"
            << ",\"captureFormat\":" << static_cast<unsigned>(captureFormat) << ",\"shaderConvertsScRgb\":" << (shaderConvertsScRgb ? "true" : "false")
            << ",\"sourceSdrWhiteLevel\":{\"known\":" << (sourceWhite.known ? "true" : "false") << ",\"rawValue\":" << sourceWhite.value
            << ",\"nits\":" << sourceWhite.value*0.08 << ",\"scale\":" << whiteScale << '}'
            << ",\"colorConversionNote\":" << Json(shaderConvertsScRgb ? "Fixture-only linear scRGB normalized by source SDR white, clipped to SDR gamut/range and encoded to sRGB; HDR is not preserved." : "SDR BGRA path; no FP16 conversion shader active.")
            << ",\"sourceDevice\":" << Json(Utf8(options.sourceDevice)) << ",\"adapterLuid\":" << Json(LuidString(adapterLuid)) << ",\"source\":" << RectJson(crop)
            << ",\"sourceOutputBounds\":" << RectJson(sourceBounds)
            << ",\"duplicationDescription\":{\"observed\":" << (duplicationDescriptionObserved ? "true" : "false")
            << ",\"width\":" << duplicationDescription.ModeDesc.Width << ",\"height\":" << duplicationDescription.ModeDesc.Height
            << ",\"format\":" << static_cast<unsigned>(duplicationDescription.ModeDesc.Format) << ",\"rotation\":" << static_cast<unsigned>(duplicationDescription.Rotation)
            << ",\"refreshNumerator\":" << duplicationDescription.ModeDesc.RefreshRate.Numerator << ",\"refreshDenominator\":" << duplicationDescription.ModeDesc.RefreshRate.Denominator
            << ",\"desktopImageInSystemMemory\":" << (duplicationDescription.DesktopImageInSystemMemory ? "true" : "false") << '}'
            << ",\"frameDescription\":{\"observed\":" << (frameDescriptionObserved ? "true" : "false") << ",\"width\":" << frameDescription.Width << ",\"height\":" << frameDescription.Height
            << ",\"format\":" << static_cast<unsigned>(frameDescription.Format) << ",\"sampleCount\":" << frameDescription.SampleDesc.Count << '}'
            << ",\"destination\":" << RectJson(options.destination) << ",\"elapsedMs\":" << elapsed << ",\"capturedFrames\":" << metrics.captured << ",\"releasedFrames\":" << metrics.framesReleased
            << ",\"presentedFrames\":" << metrics.presented << ",\"desktopFramesSkipped\":" << metrics.desktopFramesSkipped << ",\"captureTimeouts\":" << metrics.captureTimeouts
            << ",\"captureErrors\":" << metrics.captureErrors << ",\"presentErrors\":" << metrics.presentErrors << ",\"failedPresents\":" << metrics.presentErrors << ",\"presentBusy\":" << metrics.presentBusy
            << ",\"occludedPresents\":" << metrics.occluded << ",\"recoveries\":" << metrics.recoveries << ",\"lastCaptureHresult\":" << Json(Hr(metrics.lastCapture))
            << ",\"lastPresentHresult\":" << Json(Hr(metrics.lastPresent)) << ",\"desktopUpdateToPresentCallMs\":{\"samples\":" << metrics.ageCount
            << ",\"mean\":" << (metrics.ageCount ? metrics.ageTotal/metrics.ageCount : 0) << ",\"p50\":" << percentile(0.50) << ",\"p95\":" << percentile(0.95) << ",\"p99\":" << percentile(0.99) << ",\"maximum\":" << metrics.ageMaximum << '}'
            << ",\"timingNote\":\"Desktop update timestamp to present call; excludes input and physical scanout. Captured frames may repeat unchanged crop content.\""
            << ",\"fixtureVerified\":" << (fixture.completed && fixture.passed ? "true" : "false") << ",\"verificationDetails\":" << Json(fixture.requested ? (fixture.completed ? (fixture.passed ? "One GPU readback matched four RGB quadrants and four neutral gray levels within 8/255." : fixture.error) : "Verification did not complete.") : "Not requested; normal projection uses no CPU pixel readback.")
            << ",\"fixtureVerification\":{\"requested\":" << (fixture.requested ? "true" : "false") << ",\"completed\":" << (fixture.completed ? "true" : "false")
            << ",\"passed\":" << (fixture.passed ? "true" : "false") << ",\"gpuReadbacks\":" << (fixture.scheduled ? 1 : 0) << ",\"rgbSamples\":[";
        for (unsigned i = 0; i < 4; ++i) { if (i) out << ','; out << '[' << fixture.pixels[i][0] << ',' << fixture.pixels[i][1] << ',' << fixture.pixels[i][2] << ']'; }
        out << "],\"quadrantsPassed\":" << (fixture.quadrantsPassed ? "true" : "false") << ",\"graysPassed\":" << (fixture.graysPassed ? "true" : "false") << ",\"grayExpected\":[32,64,128,192],\"grayRgbSamples\":[";
        for (unsigned i = 0; i < 4; ++i) { if (i) out << ','; out << '[' << fixture.grayPixels[i][0] << ',' << fixture.grayPixels[i][1] << ',' << fixture.grayPixels[i][2] << ']'; }
        const auto& inputStatus = input.GetStatus();
        out << "]},\"fixtureInputStall\":{\"requested\":" << (options.fixtureInputStallMs ? "true" : "false") << ",\"requestedMs\":" << options.fixtureInputStallMs
            << ",\"started\":" << (fixtureInputStallStarted ? "true" : "false") << ",\"completed\":" << (fixtureInputStallCompleted ? "true" : "false") << ",\"elapsedMs\":" << fixtureInputStallElapsed
            << "},\"inputForwarding\":false,\"input\":{\"requested\":" << (options.interactive ? "true" : "false") << ",\"mode\":\"modalHardwareDirect\",\"releaseShortcut\":\"Ctrl+Alt+F10\""
            << ",\"entered\":" << (inputStatus.entered ? "true" : "false") << ",\"active\":" << (inputStatus.active ? "true" : "false") << ",\"readyPublished\":" << (inputReadyWritten ? "true" : "false")
            << ",\"guardianReady\":" << (inputStatus.guardianReady ? "true" : "false") << ",\"guardianRecovered\":" << (inputStatus.guardianRecovered ? "true" : "false")
            << ",\"guardianFocusAttempted\":" << (inputStatus.guardianFocusAttempted ? "true" : "false") << ",\"guardianFocusRestored\":" << (inputStatus.guardianFocusRestored ? "true" : "false")
            << ",\"guardianFocusSkipped\":" << (inputStatus.guardianFocusSkipped ? "true" : "false") << ",\"guardianFocusError\":" << inputStatus.guardianFocusError << ",\"guardianFocusReason\":" << Json(inputStatus.guardianFocusReason)
            << ",\"guardianFocusRequestAccepted\":" << (inputStatus.guardianFocusRequestAccepted ? "true" : "false")
            << ",\"focusRequestAttempted\":" << (inputStatus.focusRequestAttempted ? "true" : "false") << ",\"focusRequestAccepted\":" << (inputStatus.focusRequestAccepted ? "true" : "false")
            << ",\"focusRequestError\":" << inputStatus.focusRequestError << ",\"focusRequestReason\":" << Json(inputStatus.focusRequestReason)
            << ",\"clipRestored\":" << (inputStatus.clipRestored ? "true" : "false") << ",\"cursorRestored\":" << (inputStatus.cursorRestored ? "true" : "false") << ",\"focusRestored\":" << (inputStatus.focusRestored ? "true" : "false")
            << ",\"foreignClipObserved\":" << (inputStatus.foreignClipObserved ? "true" : "false") << ",\"releaseIncomplete\":" << (inputStatus.releaseIncomplete ? "true" : "false")
            << ",\"elapsedMs\":" << inputStatus.elapsedMs << ",\"error\":" << inputStatus.error << ",\"reason\":" << Json(inputStatus.reason)
            << ",\"clickToEnter\":" << (options.clickToEnter ? "true" : "false") << ",\"entryClicks\":" << entryClicks << ",\"entryWait\":" << Json(entryWait) << ",\"leaseReleaseGateRequested\":" << (options.releaseBeginName.empty() ? "false" : "true")
            << ",\"leaseReleaseConfigured\":" << (inputStatus.leaseReleaseConfigured ? "true" : "false") << ",\"leaseReleaseStarted\":" << (inputStatus.leaseReleaseStarted ? "true" : "false")
            << ",\"leaseReleaseAcknowledged\":" << (inputStatus.leaseReleaseAcknowledged ? "true" : "false") << ",\"leaseReleaseError\":" << inputStatus.leaseReleaseError
            << "},\"injection\":false,\"exclusiveFullscreenVerified\":false,\"virtualDisplayVerified\":false}";
        return out.str();
    }
};


// This explicit diagnostic renders only tiny owned textures. It never creates
// a window, swapchain, duplication interface, virtual output or input session.
int CursorGpuTest() {
    constexpr UINT width=8,height=4;
    unsigned cases=0,pixelsChecked=0,readbacks=0;
    std::vector<std::string> failures;
    std::string adapterName;
    HRESULT last=S_OK;
    const ULONGLONG started=GetTickCount64(),deadline=started+10000;
    auto run=[&]()->bool {
        Ptr<ID3D11Device> device;Ptr<ID3D11DeviceContext> context;
        const D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_0,D3D_FEATURE_LEVEL_10_1,D3D_FEATURE_LEVEL_10_0};
        D3D_FEATURE_LEVEL actual{};
        last=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,levels,3,D3D11_SDK_VERSION,device.put(),&actual,context.put());
        if(FAILED(last)){failures.emplace_back("Hardware D3D11 device creation failed.");return false;}
        Ptr<IDXGIDevice> dxgiDevice;Ptr<IDXGIAdapter> adapter;
        if(SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice),reinterpret_cast<void**>(dxgiDevice.put()))) && SUCCEEDED(dxgiDevice->GetAdapter(adapter.put()))) {
            DXGI_ADAPTER_DESC description{};if(SUCCEEDED(adapter->GetDesc(&description)))adapterName=Utf8(description.Description);
        }
        auto okay=[&](HRESULT value,const char* reason)->bool {last=value;if(FAILED(value)){failures.emplace_back(reason);return false;}return true;};
        Ptr<ID3DBlob> vsCode,psCode,messages;
        if(!okay(D3DCompile(kPresenterShader,sizeof(kPresenterShader)-1,"RegionPresenter cursor GPU test",nullptr,nullptr,"vs","vs_4_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,vsCode.put(),messages.put()),"Production vertex shader compile failed.") ||
           !okay(D3DCompile(kPresenterShader,sizeof(kPresenterShader)-1,"RegionPresenter cursor GPU test",nullptr,nullptr,"ps","ps_4_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,psCode.put(),messages.put()),"Production pixel shader compile failed."))return false;
        Ptr<ID3D11VertexShader> vs;Ptr<ID3D11PixelShader> ps;
        if(!okay(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,vs.put()),"Vertex shader creation failed.") ||
           !okay(device->CreatePixelShader(psCode->GetBufferPointer(),psCode->GetBufferSize(),nullptr,ps.put()),"Pixel shader creation failed."))return false;
        const uint8_t backgroundRgb[]={90,150,204};
        std::vector<uint8_t> background(width*height*4);
        for(size_t i=0;i<background.size();i+=4){background[i]=204;background[i+1]=150;background[i+2]=90;background[i+3]=255;}
        D3D11_TEXTURE2D_DESC texture{};texture.Width=width;texture.Height=height;texture.MipLevels=texture.ArraySize=1;
        texture.Format=DXGI_FORMAT_B8G8R8A8_UNORM;texture.SampleDesc.Count=1;texture.Usage=D3D11_USAGE_IMMUTABLE;texture.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA sourceData{};sourceData.pSysMem=background.data();sourceData.SysMemPitch=width*4;
        Ptr<ID3D11Texture2D> source,render,staging;Ptr<ID3D11ShaderResourceView> sourceView;Ptr<ID3D11RenderTargetView> target;
        if(!okay(device->CreateTexture2D(&texture,&sourceData,source.put()),"Owned source texture creation failed.") ||
           !okay(device->CreateShaderResourceView(source.get(),nullptr,sourceView.put()),"Owned source view creation failed."))return false;
        texture.Usage=D3D11_USAGE_DEFAULT;texture.BindFlags=D3D11_BIND_RENDER_TARGET;
        if(!okay(device->CreateTexture2D(&texture,nullptr,render.put()),"Offscreen target creation failed.") ||
           !okay(device->CreateRenderTargetView(render.get(),nullptr,target.put()),"Offscreen view creation failed."))return false;
        texture.Usage=D3D11_USAGE_STAGING;texture.BindFlags=0;texture.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        if(!okay(device->CreateTexture2D(&texture,nullptr,staging.put()),"Owned readback texture creation failed."))return false;
        D3D11_SAMPLER_DESC samplerDesc{};samplerDesc.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;samplerDesc.AddressU=samplerDesc.AddressV=samplerDesc.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;samplerDesc.MaxLOD=D3D11_FLOAT32_MAX;
        Ptr<ID3D11SamplerState> sampler;
        if(!okay(device->CreateSamplerState(&samplerDesc,sampler.put()),"Sampler creation failed."))return false;
        struct ColorConstants {float scale;UINT convert;float padding[2];} color{1,0,{0,0}};
        D3D11_BUFFER_DESC buffer{};buffer.ByteWidth=sizeof(color);buffer.Usage=D3D11_USAGE_IMMUTABLE;buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        D3D11_SUBRESOURCE_DATA data{};data.pSysMem=&color;
        Ptr<ID3D11Buffer> colorBuffer,pointerBuffer;
        if(!okay(device->CreateBuffer(&buffer,&data,colorBuffer.put()),"Color constants creation failed."))return false;
        buffer.ByteWidth=32;buffer.Usage=D3D11_USAGE_DEFAULT;
        if(!okay(device->CreateBuffer(&buffer,nullptr,pointerBuffer.put()),"Pointer constants creation failed."))return false;
        D3D11_QUERY_DESC queryDesc{D3D11_QUERY_EVENT,0};Ptr<ID3D11Query> complete;
        if(!okay(device->CreateQuery(&queryDesc,complete.put()),"Readback query creation failed."))return false;
        const uint8_t colorRaw[]={200,100,50,255,255,255,255,0,0,0,255,128,0,255,0,1};
        const uint8_t monoRaw[]={0x30,0x50};
        const uint8_t maskRaw[]={0,0,255,0,255,255,255,255,0,0,0,255,12,34,56,0};
        projection_cursor::Shape colorShape,monoShape,maskShape;std::string reason;
        if(!projection_cursor::Decode(projection_cursor::Color,4,1,16,colorRaw,sizeof(colorRaw),colorShape,reason) ||
           !projection_cursor::Decode(projection_cursor::Monochrome,4,2,1,monoRaw,sizeof(monoRaw),monoShape,reason) ||
           !projection_cursor::Decode(projection_cursor::MaskedColor,4,1,16,maskRaw,sizeof(maskRaw),maskShape,reason)){failures.emplace_back(reason);return false;}
        struct Case {const char* name;const projection_cursor::Shape* shape;int x,y;bool visible;};
        const Case tests[]={
            {"color-alpha",&colorShape,2,1,true}, {"monochrome-and-xor",&monoShape,2,1,true},
            {"masked-color",&maskShape,2,1,true}, {"left-clipping",&monoShape,-2,1,true},
            {"bottom-right-clipping",&colorShape,7,3,true}, {"hidden-separate-pointer",&maskShape,2,1,false},
            {"offscreen-negative-position",&colorShape,2,-1,true}
        };
        for(const auto& test:tests) {
            if(GetTickCount64()>=deadline){last=HRESULT_FROM_WIN32(WAIT_TIMEOUT);failures.emplace_back("Overall cursor GPU test deadline reached.");return false;}
            const auto& shape=*test.shape;
            D3D11_TEXTURE2D_DESC pointerDesc{};pointerDesc.Width=shape.width;pointerDesc.Height=shape.height;pointerDesc.MipLevels=pointerDesc.ArraySize=1;
            pointerDesc.Format=DXGI_FORMAT_R8G8B8A8_UINT;pointerDesc.SampleDesc.Count=1;pointerDesc.Usage=D3D11_USAGE_IMMUTABLE;pointerDesc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA pointerData{};pointerData.pSysMem=shape.rgba.data();pointerData.SysMemPitch=shape.width*4;
            Ptr<ID3D11Texture2D> pointerTexture;Ptr<ID3D11ShaderResourceView> pointerView;
            if(!okay(device->CreateTexture2D(&pointerDesc,&pointerData,pointerTexture.put()),"Pointer texture creation failed.") ||
               !okay(device->CreateShaderResourceView(pointerTexture.get(),nullptr,pointerView.put()),"Pointer view creation failed."))return false;
            struct PointerConstants {int x,y;UINT width,height,type,visible;float imageWidth,imageHeight;} pointer{test.x,test.y,shape.width,shape.height,shape.type,test.visible?1U:0U,width,height};
            context->UpdateSubresource(pointerBuffer.get(),0,nullptr,&pointer,0,0);
            ID3D11Buffer* constants[]={colorBuffer.get(),pointerBuffer.get()};context->PSSetConstantBuffers(0,2,constants);
            ID3D11RenderTargetView* targets[]={target.get()};context->OMSetRenderTargets(1,targets,nullptr);
            D3D11_VIEWPORT viewport{0,0,width,height,0,1};context->RSSetViewports(1,&viewport);
            context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vs.get(),nullptr,0);context->PSSetShader(ps.get(),nullptr,0);
            ID3D11ShaderResourceView* views[]={sourceView.get(),pointerView.get()};context->PSSetShaderResources(0,2,views);
            ID3D11SamplerState* samplers[]={sampler.get()};context->PSSetSamplers(0,1,samplers);
            context->Draw(3,0);
            ID3D11ShaderResourceView* empty[]={nullptr,nullptr};context->PSSetShaderResources(0,2,empty);
            context->OMSetRenderTargets(0,nullptr,nullptr);context->CopyResource(staging.get(),render.get());context->End(complete.get());context->Flush();
            const ULONGLONG until=std::min(deadline,GetTickCount64()+1500);
            do {last=context->GetData(complete.get(),nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH);if(last==S_FALSE)Sleep(1);} while(last==S_FALSE && GetTickCount64()<until);
            if(last==S_FALSE){last=HRESULT_FROM_WIN32(WAIT_TIMEOUT);failures.emplace_back(std::string(test.name)+": GPU completion exceeded its deadline.");return false;}
            if(!okay(last,"GPU event readback failed."))return false;
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if(!okay(context->Map(staging.get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped),"Owned pixel readback failed."))return false;
            ++readbacks;bool passed=true;
            for(UINT y=0;y<height;++y)for(UINT x=0;x<width;++x) {
                const int qx=static_cast<int>(x)-test.x,qy=static_cast<int>(y)-test.y;
                const bool cursor=test.visible && qx>=0 && qy>=0 && qx<static_cast<int>(shape.width) && qy<static_cast<int>(shape.height);
                const uint8_t* actualPixel=static_cast<const uint8_t*>(mapped.pData)+static_cast<size_t>(y)*mapped.RowPitch+x*4;
                for(unsigned channel=0;channel<3;++channel) {
                    unsigned expected=backgroundRgb[channel];
                    if(cursor){const uint8_t* p=shape.rgba.data()+(static_cast<size_t>(qy)*shape.width+qx)*4;expected=projection_cursor::ComposeChannel(shape.type,backgroundRgb[channel],p[channel],p[3]);}
                    if(std::abs(static_cast<int>(actualPixel[2-channel])-static_cast<int>(expected))>1)passed=false;
                }
                if(actualPixel[3]!=255)passed=false;
                ++pixelsChecked;
            }
            context->Unmap(staging.get(),0);++cases;
            if(!passed)failures.emplace_back(std::string(test.name)+": GPU output differs from expected RGB/alpha.");
        }
        context->ClearState();context->Flush();return failures.empty();
    };
    const bool success=run();
    std::ostringstream report;
    report<<"{\"success\":"<<(success?"true":"false")<<",\"test\":\"owned-texture-production-cursor-shader\",\"adapter\":"<<Json(adapterName)
        <<",\"cases\":"<<cases<<",\"pixelsChecked\":"<<pixelsChecked<<",\"gpuReadbacks\":"<<readbacks<<",\"hresult\":"<<Json(Hr(last))
        <<",\"elapsedMs\":"<<(GetTickCount64()-started)<<",\"desktopCapture\":false,\"windowCreated\":false,\"inputSession\":false,\"displayMutation\":false,\"ddaPointerDeliveryVerified\":false,\"failures\":[";
    for(size_t i=0;i<failures.size();++i){if(i)report<<',';report<<Json(failures[i]);}report<<"]}";
    std::puts(report.str().c_str());return success?0:1;
}

bool WriteReport(const std::wstring& path, const std::string& report) {
    const std::wstring temporary = path + L".tmp." + std::to_wstring(GetCurrentProcessId());
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    bool okay = WriteFile(file, report.data(), static_cast<DWORD>(report.size()), &written, nullptr) && written == report.size();
    if (okay) okay = FlushFileBuffers(file) != FALSE;
    CloseHandle(file);
    if (okay) okay = MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
    if (!okay) DeleteFileW(temporary.c_str());
    return okay;
}
int ErrorReport(const Options& options, const std::string& reason, HRESULT hr, int code) {
    const std::string report = "{\"schemaVersion\":1,\"success\":false,\"status\":\"failed\",\"reason\":" + Json(reason) + ",\"hresult\":" + Json(Hr(hr)) + ",\"capturedFrames\":0,\"presentedFrames\":0,\"failedPresents\":0,\"fixtureVerified\":false}";
    std::puts(report.c_str());
    if (!options.report.empty() && !WriteReport(options.report, report)) { std::fputs("Failed to write report.\n", stderr); return 6; }
    return code;
}
bool SetPhysicalCoordinates() {
    using SetAwareness = BOOL (WINAPI*)(HANDLE);
    auto set = reinterpret_cast<SetAwareness>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    return set ? set(reinterpret_cast<HANDLE>(static_cast<INT_PTR>(-4))) != FALSE : SetProcessDPIAware() != FALSE;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    int guardianResult = 0;
    if (projection_input::TryRunGuardian(argc,argv,guardianResult)) return guardianResult;
    Options options; std::string error;
    if (!ParseOptions(argc, argv, options, error)) return ErrorReport(options, error, E_INVALIDARG, 2);
    if (options.selfTest) {
        unsigned count = 0; auto failures = projection::PolicyTests(count);
        auto cursorFailures = projection_cursor::Tests(count); failures.insert(failures.end(),cursorFailures.begin(),cursorFailures.end());
        // Option policy is tested without creating a device, window or capture session.
        auto test = [&](const std::vector<std::wstring>& args, bool expected, const char* label) {
            std::vector<wchar_t*> raw; for (const auto& arg : args) raw.push_back(const_cast<wchar_t*>(arg.c_str()));
            Options parsed; std::string reason; ++count;
            if (ParseOptions(static_cast<int>(raw.size()), raw.data(), parsed, reason) != expected) failures.emplace_back(label);
        };
        test({L"presenter",L"--self-test"},true,"standalone self test");
        test({L"presenter",L"--self-test-cursor-gpu"},true,"standalone owned-texture GPU test");
        test({L"presenter",L"--self-test-cursor-gpu",L"--self-test"},false,"GPU test cannot mix with pure tests");
        test({L"presenter",L"--list-outputs",L"--self-test"},false,"mixed discovery modes");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"1",L"--report",L"report"},true,"valid bounded arguments");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"601",L"--report",L"report"},false,"run upper bound");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"0",L"--report",L"report"},false,"zero run bound");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"1",L"--report",L"report",L"--verify-fixture-colors"},false,"readback requires fixture crop");
        test({L"presenter",L"--source-device",L"display",L"--source-device",L"other"},false,"duplicate source argument");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"1",L"--report",L"report",L"--allow-sdr-conversion"},false,"SDR conversion requires fixture crop");
        test({L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"1",L"--report",L"report",L"--allow-sdr-conversion",L"--fixture-source-rect",L"100,0,100,100"},true,"explicit fixture conversion accepted");
        const std::vector<std::wstring> normalArgs{L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",L"1",L"--report",L"report"};
        auto inputTest = [&](std::vector<std::wstring> extra, bool expected, const char* label) { auto args = normalArgs; args.insert(args.end(),extra.begin(),extra.end()); test(args,expected,label); };
        inputTest({L"--interactive-source-hwnd",L"0x1234",L"--interactive-source-pid",L"999"},true,"explicit interactive HWND and PID accepted");
        inputTest({L"--interactive-source-hwnd",L"1234"},false,"interactive requires PID");
        inputTest({L"--interactive-source-pid",L"999"},false,"interactive requires HWND");
        inputTest({L"--interactive-source-hwnd",L"0",L"--interactive-source-pid",L"999"},false,"interactive rejects null HWND");
        inputTest({L"--interactive-entry-center"},false,"center entry requires interactive source");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--interactive-entry-center"},false,"center entry requires fixture crop");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--fixture-source-rect",L"100,0,100,100",L"--interactive-entry-center"},true,"explicit fixture center entry accepted");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--interactive-source-pid",L"1000"},false,"duplicate input identity rejected");
        inputTest({L"--interactive-return-hwnd",L"1234"},false,"return HWND requires explicit interactive source");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--interactive-return-hwnd",L"0x5678"},true,"explicit input return window accepted");
        const std::wstring beginEvent = L"Local\\DisplayWeaveProjectionRelease-42-0123456789abcdef0123456789abcdef-Begin";
        const std::wstring ackEvent = L"Local\\DisplayWeaveProjectionRelease-42-0123456789abcdef0123456789abcdef-Ack";
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--interactive-click-to-enter"},true,"real-app click-to-enter accepted");
        inputTest({L"--interactive-click-to-enter"},false,"click-to-enter requires interactive source");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--fixture-source-rect",L"100,0,100,100",L"--interactive-entry-center",L"--interactive-click-to-enter"},false,"click-to-enter and center entry are exclusive");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--lease-release-begin-event",beginEvent,L"--lease-release-ack-event",ackEvent},true,"paired lease release gate accepted");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--lease-release-begin-event",beginEvent},false,"lease BEGIN requires ACK");
        inputTest({L"--lease-release-begin-event",beginEvent,L"--lease-release-ack-event",ackEvent},false,"lease release gate requires interactive source");
        inputTest({L"--interactive-source-hwnd",L"1234",L"--interactive-source-pid",L"999",L"--lease-release-begin-event",ackEvent,L"--lease-release-ack-event",beginEvent},false,"swapped lease event roles rejected");
        auto stallTest = [&](std::vector<std::wstring> extra,bool expected,const char* label,int duration=6,bool interactive=true,bool fixture=true) {
            std::vector<std::wstring> args{L"presenter",L"--source-device",L"display",L"--destination",L"0,0,100,100",L"--seconds",std::to_wstring(duration),L"--report",L"report"};
            if(interactive){args.push_back(L"--interactive-source-hwnd");args.push_back(L"1234");args.push_back(L"--interactive-source-pid");args.push_back(L"999");}
            if(fixture){args.push_back(L"--fixture-source-rect");args.push_back(L"100,0,100,100");}
            args.insert(args.end(),extra.begin(),extra.end());test(args,expected,label);
        };
        stallTest({L"--fixture-input-stall-ms",L"2800"},true,"minimum owned watchdog stall accepted");
        stallTest({L"--fixture-input-stall-ms",L"3500"},true,"maximum owned watchdog stall accepted");
        stallTest({L"--fixture-input-stall-ms",L"2799"},false,"stall below heartbeat test bound rejected");
        stallTest({L"--fixture-input-stall-ms",L"3501"},false,"stall above bounded test duration rejected");
        stallTest({L"--fixture-input-stall-ms",L"0"},false,"zero explicit stall rejected");
        stallTest({L"--fixture-input-stall-ms",L"-3500"},false,"negative stall rejected");
        stallTest({L"--fixture-input-stall-ms",L"3500"},false,"stall requires six-second run",5);
        stallTest({L"--fixture-input-stall-ms",L"3500"},false,"stall requires interactive source",6,false);
        stallTest({L"--fixture-input-stall-ms",L"3500"},false,"stall requires explicit owned crop",6,true,false);
        stallTest({L"--fixture-input-stall-ms",L"3500",L"--fixture-input-stall-ms",L"3500"},false,"duplicate stall rejected");
        stallTest({L"--fixture-input-stall-ms"},false,"missing stall value rejected");
        // Compile the exact production shaders on CPU; no D3D device, display,
        // pointer or application state is touched by this validation.
        for (const auto& entry : {std::make_pair("vs","vs_4_0"),std::make_pair("ps","ps_4_0")}) {
            Ptr<ID3DBlob> bytecode, messages; ++count;
            const HRESULT result = D3DCompile(kPresenterShader,sizeof(kPresenterShader)-1,"RegionPresenter self-test",nullptr,nullptr,entry.first,entry.second,D3DCOMPILE_OPTIMIZATION_LEVEL3,0,bytecode.put(),messages.put());
            if (FAILED(result)) { failures.emplace_back(std::string("production shader compilation: ")+entry.first); if (messages) std::fwrite(messages->GetBufferPointer(),1,messages->GetBufferSize(),stderr); }
        }
        Output color;
        color.colorKnown = true; color.extended.ColorSpace = DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020; color.desc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
        std::string colorError;
        auto colorTest = [&](bool condition, const char* name) { ++count; if (!condition) failures.emplace_back(name); };
        colorTest(!SupportedColorAndRotation(color,true,false,colorError),"HDR source rejected without explicit conversion");
        colorTest(SupportedColorAndRotation(color,true,true,colorError),"HDR source explicit conversion permitted");
        colorTest(SupportedColorAndRotation(color,false,false,colorError),"SDR window on HDR destination permitted");
        color.colorKnown = false;
        colorTest(!SupportedColorAndRotation(color,false,false,colorError),"unknown destination color rejected");
        color.colorKnown = true; color.desc.Rotation = DXGI_MODE_ROTATION_ROTATE90;
        colorTest(!SupportedColorAndRotation(color,false,false,colorError),"rotated destination rejected");
        std::printf("{\"tests\":%u,\"passed\":%u,\"failures\":[",count,count-static_cast<unsigned>(failures.size()));
        for (size_t i = 0; i < failures.size(); ++i) std::printf("%s%s",i ? "," : "",Json(failures[i]).c_str());
        std::puts("]}"); return failures.empty() ? 0 : 1;
    }
    if (options.cursorGpuTest) return CursorGpuTest();
    if (!SetPhysicalCoordinates()) return ErrorReport(options, "Could not establish physical-pixel DPI awareness.", HRESULT_FROM_WIN32(GetLastError()), 3);
    std::vector<Output> outputs;
    HRESULT hr = EnumerateOutputs(outputs);
    if (FAILED(hr)) return ErrorReport(options, "DXGI output enumeration failed.", hr, 4);
    if (options.list) {
        std::fputs("{\"schemaVersion\":1,\"identityNote\":\"Adapter LUID and device name identify the current Windows display session; re-enumerate after topology changes or restart.\",\"outputs\":[",stdout);
        for (size_t i = 0; i < outputs.size(); ++i) std::printf("%s%s",i ? "," : "",OutputJson(outputs[i]).c_str());
        std::puts("]}"); return 0;
    }
    Output* source = nullptr; Output* destination = nullptr;
    unsigned sources = 0, destinations = 0;
    for (auto& output : outputs) {
        if (!output.desc.AttachedToDesktop) continue;
        if (_wcsicmp(output.desc.DeviceName, options.sourceDevice.c_str()) == 0) { source = &output; ++sources; }
        if (projection::Contains(Rect(output.desc.DesktopCoordinates), options.destination)) { destination = &output; ++destinations; }
    }
    if (sources != 1) return ErrorReport(options, "Source device must identify exactly one attached output from --list-outputs.", E_INVALIDARG, 3);
    if (destinations != 1) return ErrorReport(options, "Destination must be contained in exactly one attached output.", E_INVALIDARG, 3);
    if (!SupportedColorAndRotation(*source,true,options.allowSdrConversion,error) || !SupportedColorAndRotation(*destination,false,false,error) ||
        !projection::ValidateProjection(Rect(source->desc.DesktopCoordinates),Rect(destination->desc.DesktopCoordinates),options.destination,options.fixture,options.crop,error))
        return ErrorReport(options,error,E_INVALIDARG,3);
    Presenter presenter(options);
    const int result = presenter.Start(*source,*destination) ? presenter.Run() : presenter.Code();
    const std::string report = presenter.Report();
    std::puts(report.c_str());
    if (!WriteReport(options.report,report)) { std::fputs("Failed to write report.\n",stderr); return 6; }
    return result;
}
