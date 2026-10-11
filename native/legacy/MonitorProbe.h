#pragma once
#include "RegionLayoutProtocol.h"
#include <vector>

// Isolated fixture: every swap chain stays windowed. No game or unrelated
// process is used to validate the USER32 virtual-monitor engine.
namespace monitor_probe {
HWND hwnd;
std::wstring report;
RECT original{}, physical{};
LONG_PTR originalStyle, originalExStyle;
WINDOWPLACEMENT originalPlacement{sizeof(WINDOWPLACEMENT)};
HMONITOR originalMonitor, cachedVirtual;
int physicalCount, failures = 0, checks = 0, lastPhase = 0;
bool filtered = false, deferMessages = false, quit = false;
ID3D11Device* device;
ID3D11DeviceContext* context;
IDXGISwapChain* swap;
unsigned long long frames = 0, failedPresents = 0, entryFrames = 0;
std::string details;
bool Equal(const RECT& a, const RECT& b) { return EqualRect(&a, &b) != FALSE; }
bool Close(const RECT& a, const RECT& b) {
    return abs(a.left-b.left)<=2 && abs(a.top-b.top)<=2 &&
        abs(a.right-b.right)<=2 && abs(a.bottom-b.bottom)<=2;
}
RECT Box(const RegionBox& b) { return {b.x,b.y,b.x+b.width,b.y+b.height}; }
void Check(bool ok, const char* name) {
    ++checks; if (!ok) ++failures;
    details += ok ? "PASS " : "FAIL "; details += name; details += '\n';
}
void Save() {
    FILE* f = _wfopen((report + L".tmp").c_str(), L"wb");
    if (!f) return;
    fprintf(f, "{\"phase\":%d,\"checks\":%d,\"failures\":%d,\"success\":%s,\"frames\":%llu,\"failedPresents\":%llu}\n",
        lastPhase,checks,failures,failures ? "false":"true",frames,failedPresents);
    fclose(f); MoveFileExW((report+L".tmp").c_str(),report.c_str(),MOVEFILE_REPLACE_EXISTING);
    f = _wfopen((report + L".txt").c_str(),L"wb");
    if (f) { fwrite(details.data(),1,details.size(),f); fclose(f); }
}
bool Status(RegionSnapshot& state, RegionLayoutSnapshot& layout, LONG& caps) {
    wchar_t name[96]; swprintf(name,96,L"Local\\DisplayWeave_%lu",GetCurrentProcessId());
    HANDLE map = OpenFileMappingW(FILE_MAP_READ,FALSE,name);
    if (!map) return false;
    auto shared = static_cast<const RegionProtocol*>(MapViewOfFile(map,FILE_MAP_READ,0,0,sizeof(RegionProtocol)));
    bool ok = false;
    if (shared) {
        LONG seq = shared->sequence;
        state.targetPid=shared->targetPid; state.controllerPid=shared->controllerPid;
        state.hwnd=reinterpret_cast<HWND>(static_cast<uintptr_t>(shared->targetHwnd));
        state.monitor=reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(shared->targetMonitor));
        state.x=shared->x; state.y=shared->y; state.width=shared->width; state.height=shared->height;
        state.active=shared->active != 0; state.mode=shared->mode; state.heartbeat=shared->heartbeatTick;
        caps=shared->capabilities;
        ok=!(seq&1) && shared->magic==RS_PROTOCOL_MAGIC && shared->version==2 && shared->size==96 &&
            shared->targetPid==GetCurrentProcessId() && shared->hookReady==1 && shared->acknowledgedPid==static_cast<LONG>(GetCurrentProcessId());
        MemoryBarrier(); ok=ok && seq==shared->sequence;
        UnmapViewOfFile(shared);
    }
    CloseHandle(map);
    swprintf(name,96,L"Local\\DisplayWeave_Layout_%lu",GetCurrentProcessId());
    map=OpenFileMappingW(FILE_MAP_READ,FALSE,name);
    if (!map) return false;
    auto regions=static_cast<const RegionLayoutProtocol*>(MapViewOfFile(map,FILE_MAP_READ,0,0,sizeof(RegionLayoutProtocol)));
    if (regions) {
        LONG seq=regions->sequence;
        layout.targetPid=regions->targetPid; layout.controllerPid=regions->controllerPid; layout.count=regions->count;
        for(int i=0;i<RS_LAYOUT_MAX_REGIONS;++i) layout.regions[i]=regions->regions[i];
        MemoryBarrier(); ok=ok && !(seq&1) && seq==regions->sequence && regions->magic==RS_LAYOUT_MAGIC &&
            regions->version==RS_LAYOUT_VERSION && regions->size==sizeof(RegionLayoutProtocol) &&
            layout.targetPid==state.targetPid && layout.controllerPid==state.controllerPid && RS_ValidLayout(layout);
        UnmapViewOfFile(regions);
    } else ok=false;
    CloseHandle(map); return ok;
}
struct Monitor { HMONITOR handle; RECT rect; };
BOOL CALLBACK Collect(HMONITOR handle,HDC,LPRECT rect,LPARAM data) {
    reinterpret_cast<std::vector<Monitor>*>(data)->push_back({handle,*rect}); return TRUE;
}
BOOL CALLBACK StopEnumeration(HMONITOR,HDC,LPRECT,LPARAM data) { ++*reinterpret_cast<int*>(data); return FALSE; }
BOOL CALLBACK NestedInformation(HMONITOR handle,HDC,LPRECT rect,LPARAM data) {
    MONITORINFO info{sizeof(info)};
    if(!GetMonitorInfoW(handle,&info) || !Equal(info.rcMonitor,*rect)) *reinterpret_cast<bool*>(data)=false;
    return TRUE;
}
void VerifyMonitors(const RegionSnapshot& state, const RegionLayoutSnapshot& layout) {
    std::vector<Monitor> all;
    Check(EnumDisplayMonitors(nullptr,nullptr,Collect,reinterpret_cast<LPARAM>(&all))!=FALSE,"enumeration succeeds");
    Check(all.size()==static_cast<size_t>(physicalCount-1+layout.count),"regions replace exactly one physical monitor");
    Check(GetSystemMetrics(SM_CMONITORS)==static_cast<int>(all.size()),"monitor count agrees with enumeration");
    Check(GetSystemMetrics(SM_CXSCREEN)==state.width && GetSystemMetrics(SM_CYSCREEN)==state.height,"primary metrics use selected region");
    int primaryCount=0;
    for(const auto& m:all) {
        MONITORINFO info{sizeof(MONITORINFO)};
        Check(GetMonitorInfoW(m.handle,&info) && Equal(info.rcMonitor,m.rect),"enumerated handles return matching bounds");
        if(info.dwFlags & MONITORINFOF_PRIMARY) ++primaryCount;
    }
    Check(primaryCount==1,"exactly one primary monitor");
    bool nested=true;
    Check(EnumDisplayMonitors(nullptr,nullptr,NestedInformation,reinterpret_cast<LPARAM>(&nested)) && nested,
        "monitor callbacks can query virtual monitor information");
    for(int i=0;i<layout.count;++i) {
        RECT rect=Box(layout.regions[i]); POINT point{(rect.left+rect.right)/2,(rect.top+rect.bottom)/2};
        HMONITOR handle=MonitorFromPoint(point,MONITOR_DEFAULTTONULL);
        MONITORINFOEXW wide{}; wide.cbSize=sizeof(wide);
        MONITORINFOEXA ansi{}; ansi.cbSize=sizeof(ansi);
        Check(handle && GetMonitorInfoW(handle,reinterpret_cast<MONITORINFO*>(&wide)) && Equal(wide.rcMonitor,rect),"point resolves correct virtual region");
        Check(GetMonitorInfoA(handle,reinterpret_cast<MONITORINFO*>(&ansi)) && Equal(ansi.rcMonitor,rect) && ansi.szDevice[0],"ANSI monitor information and device name");
        Check(MonitorFromRect(&rect,MONITOR_DEFAULTTONULL)==handle,"rectangle resolves matching handle");
        std::vector<Monitor> clipped;
        Check(EnumDisplayMonitors(nullptr,&rect,Collect,reinterpret_cast<LPARAM>(&clipped)) && clipped.size()==1 && clipped[0].handle==handle,"clipped enumeration honors region");
        if(i==0) cachedVirtual=handle;
    }
    RECT selected{state.x,state.y,state.x+state.width,state.y+state.height};
    MONITORINFO info{sizeof(MONITORINFO)};
    Check(GetMonitorInfoW(MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST),&info) && Equal(info.rcMonitor,selected),"window resolves selected region");
    int called=0;
    BOOL stopped=EnumDisplayMonitors(nullptr,nullptr,StopEnumeration,reinterpret_cast<LPARAM>(&called));
    Check(!stopped && called==1,"enumeration obeys callback cancellation");
    POINT outside{-1000000,-1000000};
    Check(MonitorFromPoint(outside,MONITOR_DEFAULTTONULL)==nullptr,"outside point respects DEFAULTTONULL");
    Check(MonitorFromPoint(outside,MONITOR_DEFAULTTONEAREST)!=nullptr,"outside point supports nearest monitor");
}
void Phase(int number) {
    lastPhase=number;
    RegionSnapshot state{}; RegionLayoutSnapshot layout{}; LONG caps=0;
    bool status=Status(state,layout,caps);
    RECT current{}; GetWindowRect(hwnd,&current);
    RECT selected{state.x,state.y,state.x+state.width,state.y+state.height};
    if(number!=9 && number!=10) Check(status && (caps & 7)==5,"USER32 and virtual-monitor capabilities without DXGI");
    switch(number) {
    case 1:
        Check(Close(current,original) && GetWindowLongPtrW(hwnd,GWL_STYLE)==originalStyle,"automatic attachment preserves ordinary window");
        VerifyMonitors(state,layout);
        { HDC dc=GetDC(hwnd); RECT client{}; GetClientRect(hwnd,&client); std::vector<Monitor> visible;
          Check(dc && EnumDisplayMonitors(dc,nullptr,Collect,reinterpret_cast<LPARAM>(&visible)) && visible.size()==1 &&
              Equal(visible[0].rect,client),"window DC enumeration uses client coordinates");
          if(dc) ReleaseDC(hwnd,dc); }
        break;
    case 2: ShowWindow(hwnd,SW_MAXIMIZE); break;
    case 11: {
        WINDOWPLACEMENT place{sizeof(place)}; GetWindowPlacement(hwnd,&place);
        place.showCmd=SW_MAXIMIZE;
        Check(SetWindowPlacement(hwnd,&place)!=FALSE,"explicit placement maximize succeeds"); break;
    }
    case 3: {
        RECT client{}; GetClientRect(hwnd,&client); POINT origin{0,0}; ClientToScreen(hwnd,&origin);
        RECT clientScreen{origin.x,origin.y,origin.x+client.right,origin.y+client.bottom};
        Check(IsZoomed(hwnd) && Close(current,selected) && clientScreen.left>=selected.left &&
            clientScreen.top>=selected.top && clientScreen.right<=selected.right && clientScreen.bottom<=selected.bottom,
            "maximize frame fits selected region and client stays inside");
        Check((GetWindowLongPtrW(hwnd,GWL_STYLE)&WS_OVERLAPPEDWINDOW)==(originalStyle&WS_OVERLAPPEDWINDOW),"maximize preserves normal frame");
        ShowWindow(hwnd,SW_RESTORE); break;
    }
    case 4:
        Check(Close(current,original) && !IsZoomed(hwnd),"restore returns ordinary bounds");
        SetWindowLongPtrW(hwnd,GWL_STYLE,originalStyle & ~static_cast<LONG_PTR>(WS_OVERLAPPEDWINDOW));
        SetWindowPos(hwnd,nullptr,physical.left,physical.top,physical.right-physical.left,physical.bottom-physical.top,SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
        entryFrames=frames; break;
    case 5:
        Check(Close(current,selected),"physical-size borderless request is constrained to region");
        Check(frames>entryFrames && failedPresents==0,"windowed Direct3D continues presenting");
        { BOOL full=TRUE; DXGI_SWAP_CHAIN_DESC desc{};
          Check(SUCCEEDED(swap->GetFullscreenState(&full,nullptr)) && !full && SUCCEEDED(swap->GetDesc(&desc)) && desc.Windowed,"DXGI state remains real windowed state"); }
        break;
    case 6:
        Check(Close(current,selected),"live region change moves fullscreen window");
        VerifyMonitors(state,layout); break;
    case 7:
        Check(layout.count==2,"live layout change received");
        VerifyMonitors(state,layout); break;
    case 8:
        Check(state.mode==RS_MODE_FORCE && Close(current,selected),"manual force fills selected region"); break;
    case 9:
    case 10: {
        Check(Close(current,original) && GetWindowLongPtrW(hwnd,GWL_STYLE)==originalStyle,"release restores original bounds and frame");
        std::vector<Monitor> all; EnumDisplayMonitors(nullptr,nullptr,Collect,reinterpret_cast<LPARAM>(&all));
        Check(static_cast<int>(all.size())==physicalCount,"release returns physical monitor enumeration");
        MONITORINFO info{sizeof(MONITORINFO)};
        Check(cachedVirtual && GetMonitorInfoW(cachedVirtual,&info) && Equal(info.rcMonitor,physical),"cached virtual handle resolves physical monitor after release");
        Check(failedPresents==0,"no failed presents during lifecycle"); break;
    }
    default: Check(false,"unknown test phase");
    }
    Save();
}
void Render() {
    if(!swap) return;
    ID3D11Texture2D* texture=nullptr; ID3D11RenderTargetView* target=nullptr;
    if(SUCCEEDED(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&texture)))) {
        if(SUCCEEDED(device->CreateRenderTargetView(texture,nullptr,&target))) {
            float color[4]{0.06f,0.18f,0.25f+0.05f*static_cast<float>((frames/30)%4),1};
            context->ClearRenderTargetView(target,color); target->Release();
        }
        texture->Release();
    }
    HRESULT hr=swap->Present(0,0); if(FAILED(hr)) ++failedPresents; else ++frames;
}
LRESULT CALLBACK Proc(HWND w,UINT m,WPARAM wp,LPARAM lp) {
    if(m==WM_APP+100) { Phase(static_cast<int>(wp)); return 1; }
    if(m==WM_CLOSE) { quit=true; DestroyWindow(w); return 0; }
    if(m==WM_DESTROY) { quit=true; PostQuitMessage(0); return 0; }
    return DefWindowProcW(w,m,wp,lp);
}
int Run(HINSTANCE instance) {
    int argc=0; auto argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    bool requested=false, valid=true;
    for(int i=1;i<argc;++i) if(wcscmp(argv[i],L"--verify-monitor")==0) requested=true;
    if(!requested) { LocalFree(argv); return -1; }
    for(int i=1;i<argc;++i) {
        if(wcscmp(argv[i],L"--verify-monitor")==0) continue;
        if(wcscmp(argv[i],L"--report")==0 && i+1<argc) report=argv[++i];
        else if(wcscmp(argv[i],L"--sent-messages-only")==0) filtered=true;
        else if(wcscmp(argv[i],L"--defer-messages")==0) deferMessages=true;
        else valid=false;
    }
    LocalFree(argv); if(!valid || report.empty()) return 64;
    POINT origin{0,0}; originalMonitor=MonitorFromPoint(origin,MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO mi{sizeof(mi)}; if(!GetMonitorInfoW(originalMonitor,&mi)) return 65;
    physical=mi.rcMonitor; physicalCount=GetSystemMetrics(SM_CMONITORS);
    WNDCLASSW wc{}; wc.hInstance=instance; wc.lpfnWndProc=Proc; wc.lpszClassName=L"DisplayWeaveMonitorProbe";
    wc.hCursor=LoadCursorW(nullptr,MAKEINTRESOURCEW(32512)); if(!RegisterClassW(&wc)) return 66;
    int x=physical.left+(physical.right-physical.left)/3+50, y=physical.top+140;
    hwnd=CreateWindowExW(0,wc.lpszClassName,L"DisplayWeave monitor test",WS_OVERLAPPEDWINDOW,x,y,640,360,nullptr,nullptr,instance,nullptr);
    if(!hwnd) return 67;
    DXGI_SWAP_CHAIN_DESC desc{}; desc.BufferDesc.Width=640; desc.BufferDesc.Height=360; desc.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count=1; desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount=1; desc.OutputWindow=hwnd; desc.Windowed=TRUE; desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
    HRESULT hr=D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&device,nullptr,&context);
    if(FAILED(hr)) hr=D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&device,nullptr,&context);
    if(FAILED(hr)) { DestroyWindow(hwnd); return 68; }
    ShowWindow(hwnd,SW_SHOWNOACTIVATE); UpdateWindow(hwnd);
    GetWindowRect(hwnd,&original); GetWindowPlacement(hwnd,&originalPlacement);
    originalStyle=GetWindowLongPtrW(hwnd,GWL_STYLE); originalExStyle=GetWindowLongPtrW(hwnd,GWL_EXSTYLE);
    FILE* f=_wfopen((report+L".ready.json").c_str(),L"wb");
    if(!f) return 69;
    fprintf(f,"{\"pid\":%lu,\"hwnd\":\"%llx\",\"x\":%ld,\"y\":%ld,\"width\":%ld,\"height\":%ld}\n",GetCurrentProcessId(),static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(hwnd)),physical.left,physical.top,physical.right-physical.left,physical.bottom-physical.top);
    fclose(f); ULONGLONG start=GetTickCount64();
    while(!quit && GetTickCount64()-start<90000) {
        if(deferMessages) {
            RegionSnapshot state{}; RegionLayoutSnapshot layout{}; LONG caps=0;
            if(!Status(state,layout,caps) || (caps&7)!=5) { Sleep(16); continue; }
            deferMessages=false;
        }
        MSG msg{};
        if(filtered) PeekMessageW(&msg,nullptr,0xBFFF,0xBFFF,PM_REMOVE);
        else while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) { if(msg.message==WM_QUIT) quit=true; TranslateMessage(&msg); DispatchMessageW(&msg); }
        Render(); Sleep(16);
    }
    Save(); context->ClearState(); swap->Release(); context->Release(); device->Release();
    return failures ? 1:0;
}
}
