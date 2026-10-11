// Temporary, handle-lifetime display device for the signed upstream VDD fixture.
// No driver installation, input routing or application access occurs here. One
// validated temporary transaction may restore original physical virtual-refresh
// values and normalize only the owned virtual source after arrival. Physical
// source/target modes remain unchanged. A bounded explicit release handshake
// permits only the proven refresh transition while the owned session exits.
// SwDeviceClose joins pending callbacks.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#ifdef WINVER
#undef WINVER
#endif
#define WINVER 0x0A00
#ifdef _WIN32_WINNT
#undef _WIN32_WINNT
#endif
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <cfgmgr32.h>
#include <devpropdef.h>
#include <sddl.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

// Public Software Device API ABI; Zig's bundled headers omit swdevice.h.
struct RsSoftwareDeviceInfo {
    ULONG cbSize;
    PCWSTR instanceId, hardwareIds, compatibleIds;
    const GUID* containerId;
    ULONG capabilities;
    PCWSTR description, location;
    const SECURITY_DESCRIPTOR* security;
};
using RsDeviceCallback=void (WINAPI*)(HANDLE,HRESULT,void*,PCWSTR);
using RsCreateDevice=HRESULT (WINAPI*)(PCWSTR,PCWSTR,const RsSoftwareDeviceInfo*,ULONG,const void*,RsDeviceCallback,void*,HANDLE*);
using RsCloseDevice=void (WINAPI*)(HANDLE);
struct Creation {
    HANDLE event=nullptr;
    HRESULT result=E_PENDING;
    std::wstring instance;
};
static void WINAPI Created(HANDLE,HRESULT result,void* context,PCWSTR instance) {
    Creation* state=static_cast<Creation*>(context);
    state->result=result;
    if(instance) state->instance=instance;
    SetEvent(state->event);
}
struct Topology {
    UINT32 queryFlags=QDC_ONLY_ACTIVE_PATHS;
    bool regionHookLoaded=false;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    struct Observation {
        std::wstring sourceName;
        RECT monitor={},workArea={};
        DWORD monitorFlags=0;
        bool shellObserved=false;
        // 0 means unsupported, 1 is the legacy advanced-color query, 2 exposes
        // separate Windows 11 HDR/WCG user preferences and active color mode.
        unsigned colorVersion=0;
        UINT32 colorFlags=0,colorEncoding=0,colorBits=0,activeColorMode=0;
    };
    std::vector<Observation> observations; // Same order as paths.
};
// Windows SDK ABI, omitted by this portable compiler's older wingdi.h.
// https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h
struct RsAdvancedColorInfo2 {
    DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    UINT32 value,colorEncoding,bitsPerColorChannel,activeColorMode;
};
static_assert(sizeof(RsAdvancedColorInfo2)==36,"Advanced color Info2 ABI changed");
struct MonitorCollection { std::vector<MONITORINFOEXW> values; DWORD error=ERROR_SUCCESS; };
static const DISPLAYCONFIG_SOURCE_MODE* Source(const Topology& t,const DISPLAYCONFIG_PATH_INFO& path);
static const DISPLAYCONFIG_VIDEO_SIGNAL_INFO* TargetSignal(const Topology& t,const DISPLAYCONFIG_PATH_INFO& path);
static const DISPLAYCONFIG_MODE_INFO* DesktopImage(const Topology& topology,const DISPLAYCONFIG_PATH_INFO& path);
static BOOL CALLBACK CollectMonitor(HMONITOR monitor,HDC,LPRECT,LPARAM parameter) {
    auto& collection=*reinterpret_cast<MonitorCollection*>(parameter);
    if(collection.values.size()>=128){collection.error=ERROR_INVALID_DATA;return FALSE;}
    MONITORINFOEXW info={};info.cbSize=sizeof(info);
    if(!GetMonitorInfoW(monitor,&info)){
        collection.error=GetLastError();if(!collection.error)collection.error=ERROR_GEN_FAILURE;return FALSE;
    }
    collection.values.push_back(info);return TRUE;
}
static bool UnsupportedColorQuery(LONG error) {
    return error==ERROR_NOT_SUPPORTED || error==ERROR_INVALID_PARAMETER || error==ERROR_CALL_NOT_IMPLEMENTED;
}
static LONG ObserveColor(const DISPLAYCONFIG_PATH_INFO& path,Topology::Observation& observation) {
    RsAdvancedColorInfo2 color2={};
    color2.header.type=static_cast<DISPLAYCONFIG_DEVICE_INFO_TYPE>(15); // GET_ADVANCED_COLOR_INFO_2
    color2.header.size=sizeof(color2);color2.header.adapterId=path.targetInfo.adapterId;color2.header.id=path.targetInfo.id;
    LONG result=DisplayConfigGetDeviceInfo(&color2.header);
    if(result==ERROR_SUCCESS){
        observation.colorVersion=2;observation.colorFlags=color2.value&0xfbu; // Exclude reserved bit 2.
        observation.colorEncoding=color2.colorEncoding;observation.colorBits=color2.bitsPerColorChannel;
        observation.activeColorMode=color2.activeColorMode;return ERROR_SUCCESS;
    }
    if(!UnsupportedColorQuery(result))return result;
    DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color={};
    color.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
    color.header.size=sizeof(color);color.header.adapterId=path.targetInfo.adapterId;color.header.id=path.targetInfo.id;
    result=DisplayConfigGetDeviceInfo(&color.header);
    if(result==ERROR_SUCCESS){
        observation.colorVersion=1;observation.colorFlags=color.value&0xfu;
        observation.colorEncoding=static_cast<UINT32>(color.colorEncoding);observation.colorBits=color.bitsPerColorChannel;
        return ERROR_SUCCESS;
    }
    // Unsupported is explicitly reported as unobserved, never claimed as HDR
    // validation. Other failures make the entire snapshot unavailable.
    return UnsupportedColorQuery(result)?ERROR_SUCCESS:result;
}
static LONG ObserveOutputs(Topology& topology) {
    MonitorCollection monitors;
    if(!EnumDisplayMonitors(nullptr,nullptr,CollectMonitor,reinterpret_cast<LPARAM>(&monitors)))
        return monitors.error?monitors.error:ERROR_GEN_FAILURE;
    topology.observations.clear();topology.observations.reserve(topology.paths.size());
    for(const auto& path:topology.paths){
        DISPLAYCONFIG_SOURCE_DEVICE_NAME name={};
        name.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;name.header.size=sizeof(name);
        name.header.adapterId=path.sourceInfo.adapterId;name.header.id=path.sourceInfo.id;
        LONG result=DisplayConfigGetDeviceInfo(&name.header);
        if(result!=ERROR_SUCCESS)return result;
        if(!name.viewGdiDeviceName[0] || !wmemchr(name.viewGdiDeviceName,L'\0',CCHDEVICENAME))return ERROR_INVALID_DATA;
        Topology::Observation observation;observation.sourceName=name.viewGdiDeviceName;
        for(const auto& monitor:monitors.values){
            if(_wcsicmp(monitor.szDevice,name.viewGdiDeviceName))continue;
            if(observation.shellObserved)return ERROR_INVALID_DATA;
            observation.monitor=monitor.rcMonitor;observation.workArea=monitor.rcWork;
            observation.monitorFlags=monitor.dwFlags&MONITORINFOF_PRIMARY;observation.shellObserved=true;
        }
        if(!observation.shellObserved)return ERROR_NOT_FOUND;
        const auto source=Source(topology,path);
        if(!source || !TargetSignal(topology,path) || !source->width || !source->height || source->position.x!=observation.monitor.left ||
            source->position.y!=observation.monitor.top ||
            static_cast<int64_t>(source->position.x)+source->width!=observation.monitor.right ||
            static_cast<int64_t>(source->position.y)+source->height!=observation.monitor.bottom)return ERROR_RETRY;
        result=ObserveColor(path,observation);if(result!=ERROR_SUCCESS)return result;
        topology.observations.push_back(observation);
    }
    return ERROR_SUCCESS;
}
static LONG Snapshot(Topology& value,UINT32 queryFlags=QDC_ONLY_ACTIVE_PATHS,bool* queryUnsupported=nullptr) {
    if(queryUnsupported)*queryUnsupported=false;
    value.queryFlags=queryFlags;
    // Inspect only this helper's already-loaded modules; never load a hook or
    // open another process merely to determine the observation environment.
    value.regionHookLoaded=GetModuleHandleW(L"RegionHook.dll")!=nullptr;
    for(int attempt=0;attempt<4;++attempt) {
        UINT32 paths=0,modes=0;
        LONG result=GetDisplayConfigBufferSizes(queryFlags,&paths,&modes);
        if(result!=ERROR_SUCCESS){if(queryUnsupported)*queryUnsupported=UnsupportedColorQuery(result);return result;}
        if(paths>128 || modes>384) return ERROR_INVALID_DATA;
        value.paths.resize(paths);value.modes.resize(modes);
        result=QueryDisplayConfig(queryFlags,&paths,value.paths.data(),&modes,value.modes.data(),nullptr);
        if(result==ERROR_INSUFFICIENT_BUFFER) continue;
        if(result!=ERROR_SUCCESS){if(queryUnsupported)*queryUnsupported=UnsupportedColorQuery(result);return result;}
        value.paths.resize(paths);value.modes.resize(modes);
        return paths?ObserveOutputs(value):ERROR_NOT_FOUND;
    }
    return ERROR_RETRY;
}
using SnapshotReader=LONG (*)(Topology&,UINT32,bool*);
static LONG BaselineSnapshot(Topology& value,bool& legacyFallback,LONG& modernError,SnapshotReader reader=Snapshot) {
    legacyFallback=false;bool unsupported=false;
    modernError=reader(value,0x52u,&unsupported);
    if(modernError==ERROR_SUCCESS)return ERROR_SUCCESS;
    // Only an unsupported QueryDisplayConfig/GetDisplayConfigBufferSizes mode
    // allows fallback, before creating anything. Observation/API failures do not.
    if(!unsupported)return modernError;
    value={};legacyFallback=true;
    return reader(value,QDC_ONLY_ACTIVE_PATHS,nullptr);
}
enum class SettleResult { Waiting, Ready, Expired };
struct StartupSettle {
    static constexpr ULONGLONG StableMs=750,MaximumMs=10000,MaximumSampleGapMs=500;
    ULONGLONG start=0,deadline=0,stableSince=0,lastSample=0;
    bool stable=false,sampled=false;
    StartupSettle(ULONGLONG now,ULONGLONG lifetimeDeadline):start(now){
        const ULONGLONG remaining=now<lifetimeDeadline?lifetimeDeadline-now:0;
        deadline=now+(remaining<MaximumMs?remaining:MaximumMs);
    }
    SettleResult Observe(ULONGLONG now,bool physicalMatches,bool outputOwned,bool sameOutput=true){
        if(now<start || now>=deadline)return SettleResult::Expired;
        if(!physicalMatches || !outputOwned || !sameOutput ||
            (sampled && (now<lastSample || now-lastSample>MaximumSampleGapMs)))stable=false;
        sampled=true;lastSample=now;
        if(!physicalMatches || !outputOwned)return SettleResult::Waiting;
        if(!stable){stable=true;stableSince=now;}
        return now-stableSince>=StableMs?SettleResult::Ready:SettleResult::Waiting;
    }
};
enum class ReleaseDecision { Idle, Begun, Waiting, Complete, Rejected, Expired };
struct ReleasePhase {
    static constexpr ULONGLONG MaximumMs=5000;
    bool begun=false,completed=false,transitionObserved=false;
    ULONGLONG start=0,deadline=0;unsigned transitionSamples=0;
    ReleaseDecision Observe(ULONGLONG now,ULONGLONG lifetimeDeadline,bool begin,bool complete){
        if(completed)return ReleaseDecision::Rejected;
        if(!begun){
            // Seeing both signals together cannot establish their order. The
            // harness waits for the durable BEGIN acknowledgement before exit.
            if(complete)return ReleaseDecision::Rejected;
            if(!begin)return ReleaseDecision::Idle;
            if(now>=lifetimeDeadline)return ReleaseDecision::Expired;
            begun=true;start=now;const auto remaining=lifetimeDeadline-now;
            deadline=now+(remaining<MaximumMs?remaining:MaximumMs);
            return ReleaseDecision::Begun;
        }
        if(now<start || now>=deadline)return ReleaseDecision::Expired;
        if(begin)return ReleaseDecision::Rejected; // Never renew the window.
        return complete?ReleaseDecision::Complete:ReleaseDecision::Waiting;
    }
};
struct ReleaseEvents {
    HANDLE begin=nullptr,complete=nullptr,ack=nullptr;std::wstring beginName,completeName,ackName;
    ~ReleaseEvents(){if(begin)CloseHandle(begin);if(complete)CloseHandle(complete);if(ack)CloseHandle(ack);}
    DWORD Create(){
        HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return GetLastError();
        DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);
        if(!bytes || bytes>65536){const DWORD error=GetLastError();CloseHandle(token);return error?error:ERROR_INVALID_DATA;}
        std::vector<BYTE> storage(bytes);
        if(!GetTokenInformation(token,TokenUser,storage.data(),bytes,&bytes)){const DWORD error=GetLastError();CloseHandle(token);return error;}
        CloseHandle(token);LPWSTR sid=nullptr;
        if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid,&sid))return GetLastError();
        // Explicit protected DACL, current user and SYSTEM only. Medium label
        // permits the same user's non-elevated harness to signal this helper.
        const std::wstring sddl=L"D:P(A;;0x00100002;;;SY)(A;;0x00100002;;;"+std::wstring(sid)+L")S:(ML;;NW;;;ME)";
        LocalFree(sid);PSECURITY_DESCRIPTOR descriptor=nullptr;
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr))return GetLastError();
        HMODULE bcrypt=LoadLibraryExW(L"bcrypt.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        using Random=LONG (WINAPI*)(void*,PUCHAR,ULONG,ULONG);
        auto random=bcrypt?reinterpret_cast<Random>(GetProcAddress(bcrypt,"BCryptGenRandom")):nullptr;
        unsigned char nonce[16]={};const bool randomOkay=random && random(nullptr,nonce,sizeof(nonce),2)>=0;
        if(bcrypt)FreeLibrary(bcrypt);
        if(!randomOkay){LocalFree(descriptor);return ERROR_GEN_FAILURE;}
        wchar_t hex[33]={};for(unsigned i=0;i<16;++i)swprintf(hex+i*2,3,L"%02x",nonce[i]);
        const std::wstring prefix=L"Local\\DisplayWeaveProjectionRelease-"+std::to_wstring(GetCurrentProcessId())+L"-"+hex;
        beginName=prefix+L"-Begin";completeName=prefix+L"-Complete";ackName=prefix+L"-Ack";
        SECURITY_ATTRIBUTES attributes={sizeof(attributes),descriptor,FALSE};
        SetLastError(ERROR_SUCCESS);begin=CreateEventExW(&attributes,beginName.c_str(),0,SYNCHRONIZE|EVENT_MODIFY_STATE);
        DWORD error=GetLastError();
        if(!begin || error==ERROR_ALREADY_EXISTS){LocalFree(descriptor);return error?error:ERROR_GEN_FAILURE;}
        SetLastError(ERROR_SUCCESS);complete=CreateEventExW(&attributes,completeName.c_str(),0,SYNCHRONIZE|EVENT_MODIFY_STATE);
        error=GetLastError();
        if(!complete || error==ERROR_ALREADY_EXISTS){LocalFree(descriptor);return error?error:ERROR_GEN_FAILURE;}
        SetLastError(ERROR_SUCCESS);ack=CreateEventExW(&attributes,ackName.c_str(),CREATE_EVENT_MANUAL_RESET,SYNCHRONIZE|EVENT_MODIFY_STATE);
        error=GetLastError();LocalFree(descriptor);
        if(!ack || error==ERROR_ALREADY_EXISTS)return error?error:ERROR_GEN_FAILURE;
        return ERROR_SUCCESS;
    }
};
static bool SameLuid(LUID a,LUID b){return a.LowPart==b.LowPart && a.HighPart==b.HighPart;}
static bool SameTarget(const DISPLAYCONFIG_PATH_INFO& a,const DISPLAYCONFIG_PATH_INFO& b) {
    return SameLuid(a.targetInfo.adapterId,b.targetInfo.adapterId) && a.targetInfo.id==b.targetInfo.id;
}
static const DISPLAYCONFIG_SOURCE_MODE* Source(const Topology& t,const DISPLAYCONFIG_PATH_INFO& path) {
    UINT32 index=path.sourceInfo.modeInfoIdx;
    if((t.queryFlags&0x10u) && (path.flags&0x08u)){
        // QDC_VIRTUAL_MODE_AWARE + PATH_SUPPORT_VIRTUAL_MODE: low 16 bits
        // are cloneGroupId, high 16 bits are sourceModeInfoIdx.
        index>>=16;if(index==0xffffu)return nullptr;
    }
    if(index>=t.modes.size() || t.modes[index].infoType!=DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ||
        !SameLuid(t.modes[index].adapterId,path.sourceInfo.adapterId) || t.modes[index].id!=path.sourceInfo.id) return nullptr;
    return &t.modes[index].sourceMode;
}
static const DISPLAYCONFIG_VIDEO_SIGNAL_INFO* TargetSignal(const Topology& t,const DISPLAYCONFIG_PATH_INFO& path) {
    UINT32 index=path.targetInfo.modeInfoIdx;
    if((t.queryFlags&0x10u) && (path.flags&0x08u)){
        // Low 16 bits are desktopModeInfoIdx, high 16 are targetModeInfoIdx.
        index>>=16;if(index==0xffffu)return nullptr;
    }
    if(index>=t.modes.size() || t.modes[index].infoType!=DISPLAYCONFIG_MODE_INFO_TYPE_TARGET ||
        !SameLuid(t.modes[index].adapterId,path.targetInfo.adapterId) || t.modes[index].id!=path.targetInfo.id)return nullptr;
    return &t.modes[index].targetMode.targetVideoSignalInfo;
}
static bool ColorObserved(const Topology& topology) {
    if(topology.paths.empty() || topology.observations.size()!=topology.paths.size())return false;
    for(const auto& observation:topology.observations)if(!observation.colorVersion)return false;
    return true;
}
// Return the first exact invariant difference. Keep one comparison path so the
// diagnostic cannot disagree with the fail-closed preservation decision.
static std::wstring PreserveFailure(const Topology& before,const Topology& after) {
    if(before.queryFlags!=after.queryFlags)return L"queryFlags.mismatch";
    if(before.paths.empty())return L"baseline.paths.empty";
    if(before.observations.size()!=before.paths.size())return L"baseline.observations.incomplete";
    if(after.observations.size()!=after.paths.size())return L"observed.observations.incomplete";
    for(size_t i=0;i<before.paths.size();++i) {
        const auto& old=before.paths[i];
        const std::wstring prefix=L"physical["+std::to_wstring(i)+L"] "+before.observations[i].sourceName+L": ";
        const DISPLAYCONFIG_PATH_INFO* found=nullptr;size_t foundIndex=0;
        for(size_t j=0;j<after.paths.size();++j)if(SameTarget(old,after.paths[j])){
            if(found)return prefix+L"target.identity.duplicate";found=&after.paths[j];foundIndex=j;
        }
        if(!found)return prefix+L"target.identity.missing";
        const auto sourceBefore=Source(before,old),sourceAfter=Source(after,*found);
        if(!sourceBefore || !sourceAfter)return prefix+L"sourceMode.unavailableOrInvalid";
        const auto signalBefore=TargetSignal(before,old),signalAfter=TargetSignal(after,*found);
        if(!signalBefore || !signalAfter)return prefix+L"targetSignal.unavailableOrInvalid";
#define RS_DIFFERENCE(name,a,b) if((a)!=(b))return prefix+L##name+L" (before="+std::to_wstring(a)+L", observed="+std::to_wstring(b)+L")"
        RS_DIFFERENCE("path.flags",old.flags,found->flags);
        RS_DIFFERENCE("source.statusFlags",old.sourceInfo.statusFlags,found->sourceInfo.statusFlags);
        RS_DIFFERENCE("target.statusFlags",old.targetInfo.statusFlags,found->targetInfo.statusFlags);
        // Mode-array indexes may be renumbered by Windows. Compare the resolved
        // physical desktop mapping and clone metadata, never raw array indexes.
        if((before.queryFlags&0x10u) && (old.flags&0x08u)){
            const auto oldDesktop=DesktopImage(before,old),nowDesktop=DesktopImage(after,*found);
            if(!oldDesktop || !nowDesktop)return prefix+L"desktopImage.unavailableOrInvalid";
            RS_DIFFERENCE("desktopImage.id",oldDesktop->id,nowDesktop->id);
            RS_DIFFERENCE("desktopImage.adapterLow",oldDesktop->adapterId.LowPart,nowDesktop->adapterId.LowPart);
            RS_DIFFERENCE("desktopImage.adapterHigh",oldDesktop->adapterId.HighPart,nowDesktop->adapterId.HighPart);
            RS_DIFFERENCE("source.cloneGroupId",old.sourceInfo.modeInfoIdx&0xffffu,found->sourceInfo.modeInfoIdx&0xffffu);
            DISPLAYCONFIG_DESKTOP_IMAGE_INFO a={},b={};
            static_assert(sizeof(a)<=sizeof(oldDesktop->targetMode),"Desktop image payload exceeds mode union");
            std::memcpy(&a,&oldDesktop->targetMode,sizeof(a));std::memcpy(&b,&nowDesktop->targetMode,sizeof(b));
            RS_DIFFERENCE("desktopImage.pathSourceSize.x",a.PathSourceSize.x,b.PathSourceSize.x);
            RS_DIFFERENCE("desktopImage.pathSourceSize.y",a.PathSourceSize.y,b.PathSourceSize.y);
            RS_DIFFERENCE("desktopImage.region.left",a.DesktopImageRegion.left,b.DesktopImageRegion.left);
            RS_DIFFERENCE("desktopImage.region.top",a.DesktopImageRegion.top,b.DesktopImageRegion.top);
            RS_DIFFERENCE("desktopImage.region.right",a.DesktopImageRegion.right,b.DesktopImageRegion.right);
            RS_DIFFERENCE("desktopImage.region.bottom",a.DesktopImageRegion.bottom,b.DesktopImageRegion.bottom);
            RS_DIFFERENCE("desktopImage.clip.left",a.DesktopImageClip.left,b.DesktopImageClip.left);
            RS_DIFFERENCE("desktopImage.clip.top",a.DesktopImageClip.top,b.DesktopImageClip.top);
            RS_DIFFERENCE("desktopImage.clip.right",a.DesktopImageClip.right,b.DesktopImageClip.right);
            RS_DIFFERENCE("desktopImage.clip.bottom",a.DesktopImageClip.bottom,b.DesktopImageClip.bottom);
        }
        RS_DIFFERENCE("sourceMode.width",sourceBefore->width,sourceAfter->width);
        RS_DIFFERENCE("sourceMode.height",sourceBefore->height,sourceAfter->height);
        RS_DIFFERENCE("sourceMode.pixelFormat",sourceBefore->pixelFormat,sourceAfter->pixelFormat);
        RS_DIFFERENCE("sourceMode.x",sourceBefore->position.x,sourceAfter->position.x);
        RS_DIFFERENCE("sourceMode.y",sourceBefore->position.y,sourceAfter->position.y);
        RS_DIFFERENCE("targetSignal.pixelRate",signalBefore->pixelRate,signalAfter->pixelRate);
        RS_DIFFERENCE("targetSignal.hSyncNumerator",signalBefore->hSyncFreq.Numerator,signalAfter->hSyncFreq.Numerator);
        RS_DIFFERENCE("targetSignal.hSyncDenominator",signalBefore->hSyncFreq.Denominator,signalAfter->hSyncFreq.Denominator);
        RS_DIFFERENCE("targetSignal.vSyncNumerator",signalBefore->vSyncFreq.Numerator,signalAfter->vSyncFreq.Numerator);
        RS_DIFFERENCE("targetSignal.vSyncDenominator",signalBefore->vSyncFreq.Denominator,signalAfter->vSyncFreq.Denominator);
        RS_DIFFERENCE("targetSignal.width",signalBefore->activeSize.cx,signalAfter->activeSize.cx);
        RS_DIFFERENCE("targetSignal.height",signalBefore->activeSize.cy,signalAfter->activeSize.cy);
        RS_DIFFERENCE("targetSignal.totalWidth",signalBefore->totalSize.cx,signalAfter->totalSize.cx);
        RS_DIFFERENCE("targetSignal.totalHeight",signalBefore->totalSize.cy,signalAfter->totalSize.cy);
        RS_DIFFERENCE("targetSignal.videoStandard",signalBefore->videoStandard&0x3fffffu,signalAfter->videoStandard&0x3fffffu);
        RS_DIFFERENCE("targetSignal.scanLineOrdering",signalBefore->scanLineOrdering,signalAfter->scanLineOrdering);
        RS_DIFFERENCE("source.adapterLow",old.sourceInfo.adapterId.LowPart,found->sourceInfo.adapterId.LowPart);
        RS_DIFFERENCE("source.adapterHigh",old.sourceInfo.adapterId.HighPart,found->sourceInfo.adapterId.HighPart);
        RS_DIFFERENCE("source.id",old.sourceInfo.id,found->sourceInfo.id);
        RS_DIFFERENCE("rotation",old.targetInfo.rotation,found->targetInfo.rotation);
        RS_DIFFERENCE("scaling",old.targetInfo.scaling,found->targetInfo.scaling);
        RS_DIFFERENCE("outputTechnology",old.targetInfo.outputTechnology,found->targetInfo.outputTechnology);
        RS_DIFFERENCE("scanLineOrdering",old.targetInfo.scanLineOrdering,found->targetInfo.scanLineOrdering);
        RS_DIFFERENCE("targetAvailable",old.targetInfo.targetAvailable,found->targetInfo.targetAvailable);
        RS_DIFFERENCE("refresh.numerator",old.targetInfo.refreshRate.Numerator,found->targetInfo.refreshRate.Numerator);
        RS_DIFFERENCE("refresh.denominator",old.targetInfo.refreshRate.Denominator,found->targetInfo.refreshRate.Denominator);
        const auto& a=before.observations[i];const auto& b=after.observations[foundIndex];
        if(!a.shellObserved || !b.shellObserved)return prefix+L"monitor.observation.unavailable";
        RS_DIFFERENCE("monitor.left",a.monitor.left,b.monitor.left);
        RS_DIFFERENCE("monitor.top",a.monitor.top,b.monitor.top);
        RS_DIFFERENCE("monitor.right",a.monitor.right,b.monitor.right);
        RS_DIFFERENCE("monitor.bottom",a.monitor.bottom,b.monitor.bottom);
        RS_DIFFERENCE("workArea.left",a.workArea.left,b.workArea.left);
        RS_DIFFERENCE("workArea.top",a.workArea.top,b.workArea.top);
        RS_DIFFERENCE("workArea.right",a.workArea.right,b.workArea.right);
        RS_DIFFERENCE("workArea.bottom",a.workArea.bottom,b.workArea.bottom);
        RS_DIFFERENCE("monitorFlags",a.monitorFlags,b.monitorFlags);
        RS_DIFFERENCE("colorQueryVersion",a.colorVersion,b.colorVersion);
        RS_DIFFERENCE("colorFlags",a.colorFlags,b.colorFlags);
        RS_DIFFERENCE("colorEncoding",a.colorEncoding,b.colorEncoding);
        RS_DIFFERENCE("bitsPerColorChannel",a.colorBits,b.colorBits);
        RS_DIFFERENCE("activeColorMode",a.activeColorMode,b.activeColorMode);
#undef RS_DIFFERENCE
    }
    return {};
}
static bool Preserved(const Topology& before,const Topology& after) {
    return PreserveFailure(before,after).empty();
}
using RsInterfaceProperty=CONFIGRET (WINAPI*)(LPCWSTR,const DEVPROPKEY*,DEVPROPTYPE*,PBYTE,PULONG,ULONG);
static bool ExactInstance(const std::wstring& a,const std::wstring& b) {
    return !a.empty() && !b.empty() && _wcsicmp(a.c_str(),b.c_str())==0;
}
struct Ownership {
    bool verified=false;
    LONG displayError=ERROR_SUCCESS;
    CONFIGRET configurationError=CR_SUCCESS;
    std::wstring adapterInstance;
};
static Ownership AdapterOwnership(LUID adapter,const std::wstring& createdInstance) {
    Ownership result;
    if(createdInstance.empty()){result.displayError=ERROR_INVALID_DATA;return result;}
    DISPLAYCONFIG_ADAPTER_NAME name={};name.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
    name.header.size=sizeof(name);name.header.adapterId=adapter;
    result.displayError=DisplayConfigGetDeviceInfo(&name.header);
    if(result.displayError!=ERROR_SUCCESS)return result;
    if(!name.adapterDevicePath[0] || !wmemchr(name.adapterDevicePath,L'\0',128)){
        result.displayError=ERROR_INVALID_DATA;return result;
    }
    // Ask PnP for the interface's device instance. Symbolic-link strings are
    // opaque and must not be converted to instance IDs by string replacement.
    const auto property=reinterpret_cast<RsInterfaceProperty>(GetProcAddress(GetModuleHandleW(L"cfgmgr32.dll"),"CM_Get_Device_Interface_PropertyW"));
    if(!property){result.configurationError=CR_CALL_NOT_IMPLEMENTED;return result;}
    const DEVPROPKEY instanceKey={{0x78c34fc8,0x104a,0x4aca,{0x9e,0xa4,0x52,0x4d,0x52,0x99,0x6e,0x57}},256};
    WCHAR instance[MAX_DEVICE_ID_LEN]={};ULONG bytes=sizeof(instance);DEVPROPTYPE type=0;
    result.configurationError=property(name.adapterDevicePath,&instanceKey,&type,reinterpret_cast<PBYTE>(instance),&bytes,0);
    if(result.configurationError!=CR_SUCCESS)return result;
    if(type!=DEVPROP_TYPE_STRING || bytes<sizeof(WCHAR) || bytes>sizeof(instance) || bytes%sizeof(WCHAR) ||
        instance[bytes/sizeof(WCHAR)-1]!=L'\0' || !instance[0]){
        result.displayError=ERROR_INVALID_DATA;return result;
    }
    result.adapterInstance=instance;
    DEVINST node=0;result.configurationError=CM_Locate_DevNodeW(&node,instance,CM_LOCATE_DEVNODE_NORMAL);
    if(result.configurationError!=CR_SUCCESS)return result;
    DEVINST visited[32]={};unsigned visitedCount=0;
    for(unsigned depth=0;depth<32;++depth){
        for(unsigned j=0;j<visitedCount;++j)if(visited[j]==node){result.displayError=ERROR_INVALID_DATA;return result;}
        visited[visitedCount++]=node;
        WCHAR current[MAX_DEVICE_ID_LEN]={};
        result.configurationError=CM_Get_Device_IDW(node,current,MAX_DEVICE_ID_LEN,0);
        if(result.configurationError!=CR_SUCCESS)return result;
        if(ExactInstance(current,createdInstance)){result.verified=true;return result;}
        DEVINST parent=0;result.configurationError=CM_Get_Parent(&parent,node,0);
        if(result.configurationError!=CR_SUCCESS)return result;
        node=parent;
    }
    result.displayError=ERROR_INVALID_DATA;return result;
}
enum class OutputSearch { Pending, Owned, Rejected };
using AdapterProbe=Ownership (*)(LUID,const std::wstring&);
static OutputSearch NewOwnedOutput(const Topology& before,const Topology& now,const std::wstring& createdInstance,
    std::wstring& output,Ownership& ownership,AdapterProbe probe=AdapterOwnership) {
    output.clear();ownership={};
    const DISPLAYCONFIG_PATH_INFO* candidate=nullptr;size_t candidateIndex=0;
    int newCount=0;
    for(size_t i=0;i<now.paths.size();++i) {
        const auto& path=now.paths[i];
        bool existing=false;
        for(const auto& old:before.paths) if(SameTarget(old,path)){existing=true;break;}
        if(existing) continue;
        if(++newCount!=1){ownership.displayError=ERROR_INVALID_DATA;return OutputSearch::Rejected;}
        // Connector type is descriptive, not ownership evidence. The pinned
        // upstream IDD reports HDMI. Require exact PnP ancestry below even when
        // the connector is labelled INDIRECT_WIRED or INDIRECT_VIRTUAL.
        candidate=&path;candidateIndex=i;
    }
    if(!candidate)return OutputSearch::Pending;
    ownership=probe(candidate->sourceInfo.adapterId,createdInstance);
    if(!ownership.verified)return OutputSearch::Rejected;
    if(!SameLuid(candidate->sourceInfo.adapterId,candidate->targetInfo.adapterId)){
        auto targetOwnership=probe(candidate->targetInfo.adapterId,createdInstance);
        if(!targetOwnership.verified){ownership=targetOwnership;return OutputSearch::Rejected;}
    }
    if(candidateIndex>=now.observations.size() || now.observations[candidateIndex].sourceName.empty()){
        ownership.verified=false;ownership.displayError=ERROR_INVALID_DATA;return OutputSearch::Rejected;
    }
    output=now.observations[candidateIndex].sourceName;return OutputSearch::Owned;
}
static const DISPLAYCONFIG_MODE_INFO* DesktopImage(const Topology& topology,const DISPLAYCONFIG_PATH_INFO& path){
    if(!(topology.queryFlags&0x10u) || !(path.flags&0x08u))return nullptr;
    const UINT32 index=path.targetInfo.modeInfoIdx&0xffffu;
    if(index==0xffffu || index>=topology.modes.size() ||
        topology.modes[index].infoType!=DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE)return nullptr;
    return &topology.modes[index];
}
static bool RefreshRestoreCandidate(const Topology& before,const Topology& current,const std::wstring& instance,
    Topology& request,std::wstring& reason,AdapterProbe probe=AdapterOwnership){
    request={};reason.clear();
    if(before.queryFlags!=0x52u || current.queryFlags!=0x52u){reason=L"refreshRestore.requiresAwareQuery";return false;}
    std::wstring output;Ownership ownership;
    if(NewOwnedOutput(before,current,instance,output,ownership,probe)!=OutputSearch::Owned){
        reason=L"refreshRestore.outputOwnershipNotVerified";return false;
    }
    Topology candidate=current;unsigned changed=0;
    for(const auto& path:current.paths){
        if(!(path.flags&1u) || !Source(current,path) || !TargetSignal(current,path)){
            reason=L"refreshRestore.completeActiveModesRequired";return false;
        }
    }
    for(size_t i=0;i<before.paths.size();++i){
        const auto& old=before.paths[i];size_t index=current.paths.size();
        for(size_t j=0;j<current.paths.size();++j)if(SameTarget(old,current.paths[j])){
            if(index!=current.paths.size()){reason=L"refreshRestore.physicalIdentityAmbiguous";return false;}index=j;
        }
        if(index==current.paths.size()){reason=L"refreshRestore.physicalIdentityMissing";return false;}
        const auto& now=current.paths[index];
        if(!old.targetInfo.refreshRate.Numerator || !old.targetInfo.refreshRate.Denominator ||
            !now.targetInfo.refreshRate.Numerator || !now.targetInfo.refreshRate.Denominator){
            reason=L"refreshRestore.invalidRefreshRational";return false;
        }
        if(old.flags!=now.flags || old.sourceInfo.statusFlags!=now.sourceInfo.statusFlags ||
            old.targetInfo.statusFlags!=now.targetInfo.statusFlags || i>=before.observations.size() ||
            index>=current.observations.size() || before.observations[i].sourceName!=current.observations[index].sourceName){
            reason=L"refreshRestore.nonRefreshPathMetadataChanged";return false;
        }
        if(old.flags&0x08u){
            const auto oldDesktop=DesktopImage(before,old),nowDesktop=DesktopImage(current,now);
            // The old portable SDK omits desktopImageInfo from this union;
            // compare its documented payload bytes, excluding union padding.
            static_assert(sizeof(DISPLAYCONFIG_DESKTOP_IMAGE_INFO)<=sizeof(DISPLAYCONFIG_TARGET_MODE),"Desktop image ABI exceeds mode union");
            if(!oldDesktop || !nowDesktop || oldDesktop->id!=nowDesktop->id ||
                !SameLuid(oldDesktop->adapterId,nowDesktop->adapterId) ||
                std::memcmp(&oldDesktop->targetMode,&nowDesktop->targetMode,sizeof(DISPLAYCONFIG_DESKTOP_IMAGE_INFO)) ||
                (old.sourceInfo.modeInfoIdx&0xffffu)!=(now.sourceInfo.modeInfoIdx&0xffffu)){
                reason=L"refreshRestore.desktopImageOrCloneGroupChanged";return false;
            }
        }
        if(old.targetInfo.refreshRate.Numerator!=now.targetInfo.refreshRate.Numerator ||
            old.targetInfo.refreshRate.Denominator!=now.targetInfo.refreshRate.Denominator)++changed;
        candidate.paths[index].targetInfo.refreshRate=old.targetInfo.refreshRate;
    }
    reason=PreserveFailure(before,candidate);
    if(!reason.empty())return false;
    if(!changed){reason=L"refreshRestore.notNeeded";return false;}
    request=std::move(candidate);return true;
}
static bool ReleaseTopologyAllowed(const Topology& before,const Topology& now,const std::wstring& instance,
    bool& transition,std::wstring& reason,AdapterProbe probe=AdapterOwnership,const Topology* established=nullptr){
    transition=false;Topology restored;
    if(established){
        const DISPLAYCONFIG_PATH_INFO* expected=nullptr;
        for(const auto& path:established->paths){
            bool old=false;for(const auto& physical:before.paths)if(SameTarget(path,physical))old=true;
            if(!old){if(expected){reason=L"release.establishedOutputAmbiguous";return false;}expected=&path;}
        }
        bool found=false;
        if(expected)for(const auto& path:now.paths)if(SameTarget(path,*expected) &&
            SameLuid(path.sourceInfo.adapterId,expected->sourceInfo.adapterId) && path.sourceInfo.id==expected->sourceInfo.id)found=true;
        if(!found){reason=L"release.establishedOutputChanged";return false;}
    }
    if(!RefreshRestoreCandidate(before,now,instance,restored,reason,probe)){
        if(reason==L"refreshRestore.notNeeded"){reason.clear();return true;}
        return false;
    }
    for(const auto& old:before.paths)for(const auto& current:now.paths){
        if(!SameTarget(old,current))continue;
        if(old.targetInfo.refreshRate.Numerator==current.targetInfo.refreshRate.Numerator &&
            old.targetInfo.refreshRate.Denominator==current.targetInfo.refreshRate.Denominator)continue;
        const auto signal=TargetSignal(before,old);const auto rate=current.targetInfo.refreshRate;
        if(!signal || !signal->vSyncFreq.Numerator || !signal->vSyncFreq.Denominator ||
            static_cast<uint64_t>(rate.Numerator)*signal->vSyncFreq.Denominator!=
            static_cast<uint64_t>(signal->vSyncFreq.Numerator)*rate.Denominator){
            reason=L"release.refreshDoesNotMatchUnchangedPhysicalSignal";return false;
        }
    }
    transition=true;return true;
}
using SupportedModeProbe=bool (*)(const std::wstring&,UINT32,UINT32,DISPLAYCONFIG_RATIONAL,ULONGLONG);
static bool AdvertisedMode(const std::wstring& device,UINT32 width,UINT32 height,DISPLAYCONFIG_RATIONAL refresh,ULONGLONG deadline){
    if(device.empty() || !refresh.Numerator || !refresh.Denominator)return false;
    for(DWORD index=0;index<512 && GetTickCount64()<deadline;++index){
        DEVMODEW mode={};mode.dmSize=sizeof(mode);
        if(!EnumDisplaySettingsExW(device.c_str(),index,&mode,0))break;
        const DWORD fields=DM_PELSWIDTH|DM_PELSHEIGHT|DM_DISPLAYFREQUENCY|DM_BITSPERPEL;
        if((mode.dmFields&fields)==fields && mode.dmPelsWidth==width && mode.dmPelsHeight==height && mode.dmBitsPerPel==32 &&
            static_cast<uint64_t>(mode.dmDisplayFrequency)*refresh.Denominator==refresh.Numerator &&
            (!(mode.dmFields&DM_DISPLAYORIENTATION) || mode.dmDisplayOrientation==DMDO_DEFAULT))return true;
    }
    return false;
}
static bool NormalizeOwnedVirtualCandidate(const Topology& before,const Topology& refreshRequest,const std::wstring& instance,
    ULONGLONG deadline,Topology& normalized,std::wstring& reason,AdapterProbe ownerProbe=AdapterOwnership,SupportedModeProbe modeProbe=AdvertisedMode){
    normalized={};reason.clear();
    if(refreshRequest.queryFlags!=0x52u || !Preserved(before,refreshRequest)){reason=L"virtualNormalization.physicalStateNotPreserved";return false;}
    std::wstring output;Ownership ownership;
    if(NewOwnedOutput(before,refreshRequest,instance,output,ownership,ownerProbe)!=OutputSearch::Owned){reason=L"virtualNormalization.ownershipUnverified";return false;}
    size_t index=refreshRequest.paths.size();
    for(size_t i=0;i<refreshRequest.paths.size();++i){
        bool old=false;for(const auto& prior:before.paths)if(SameTarget(prior,refreshRequest.paths[i]))old=true;
        if(!old)index=i;
    }
    if(index>=refreshRequest.paths.size()){reason=L"virtualNormalization.targetMissing";return false;}
    const auto& path=refreshRequest.paths[index];const auto source=Source(refreshRequest,path);const auto target=TargetSignal(refreshRequest,path);
    const auto desktop=DesktopImage(refreshRequest,path);
    if(!source || !target || !desktop || !(path.flags&0x08u) || path.targetInfo.rotation!=DISPLAYCONFIG_ROTATION_IDENTITY ||
        path.targetInfo.scaling!=DISPLAYCONFIG_SCALING_IDENTITY || source->pixelFormat!=DISPLAYCONFIG_PIXELFORMAT_32BPP){
        reason=L"virtualNormalization.requiresCompleteIdentityLayout";return false;
    }
    const UINT32 width=target->activeSize.cx,height=target->activeSize.cy;
    if(!width || !height || width>16384 || height>16384 || static_cast<uint64_t>(width)*height>64ull*1024*1024 ||
        static_cast<int64_t>(source->position.x)+width>INT32_MAX || static_cast<int64_t>(source->position.y)+height>INT32_MAX){
        reason=L"virtualNormalization.invalidDimensions";return false;
    }
    if(source->width==width && source->height==height){reason=L"virtualNormalization.notNeeded";return false;}
    const UINT32 sourceIndex=path.sourceInfo.modeInfoIdx>>16,desktopIndex=path.targetInfo.modeInfoIdx&0xffffu;
    for(size_t i=0;i<refreshRequest.paths.size();++i){
        if(i==index)continue;
        const auto& other=refreshRequest.paths[i];const auto otherSource=Source(refreshRequest,other);
        if(!otherSource || otherSource==source || DesktopImage(refreshRequest,other)==desktop){reason=L"virtualNormalization.sharedMode";return false;}
        const int64_t right=static_cast<int64_t>(source->position.x)+width,bottom=static_cast<int64_t>(source->position.y)+height;
        const int64_t otherRight=static_cast<int64_t>(otherSource->position.x)+otherSource->width,otherBottom=static_cast<int64_t>(otherSource->position.y)+otherSource->height;
        if(source->position.x<otherRight && right>otherSource->position.x && source->position.y<otherBottom && bottom>otherSource->position.y){
            reason=L"virtualNormalization.overlapsAnotherSource";return false;
        }
    }
    if(!modeProbe(output,width,height,path.targetInfo.refreshRate,deadline)){reason=L"virtualNormalization.modeNotAdvertised";return false;}
    normalized=refreshRequest;
    normalized.modes[sourceIndex].sourceMode.width=width;normalized.modes[sourceIndex].sourceMode.height=height;
    DISPLAYCONFIG_DESKTOP_IMAGE_INFO image={};image.PathSourceSize={static_cast<LONG>(width),static_cast<LONG>(height)};
    image.DesktopImageRegion=image.DesktopImageClip={0,0,static_cast<LONG>(width),static_cast<LONG>(height)};
    std::memcpy(&normalized.modes[desktopIndex].targetMode,&image,sizeof(image));
    return true;
}
struct RefreshRestoreAttempt {
    static constexpr UINT32 ValidateFlags=0x00028060u,ApplyFlags=0x000280a0u;
    bool attempted=false,validateCalled=false,applyCalled=false;
    LONG validationError=ERROR_NOT_READY,applyError=ERROR_NOT_READY;
    bool baselineValidationCalled=false,currentValidationCalled=false,normalizedValidationCalled=false,normalizedSelected=false;
    LONG baselineValidationError=ERROR_NOT_READY,currentValidationError=ERROR_NOT_READY,normalizedValidationError=ERROR_NOT_READY;
};
template<typename ApplyFunction,typename ClockFunction>
static LONG RestoreRefreshOnce(RefreshRestoreAttempt& state,const Topology& request,ULONGLONG deadline,
    ApplyFunction apply,ClockFunction clock,const Topology* currentDiagnostic=nullptr,const Topology* normalizedCandidate=nullptr){
    if(state.attempted)return ERROR_ALREADY_EXISTS;
    state.attempted=true;
    const auto withinBudget=[&](){const auto now=clock();return now<deadline && deadline-now>StartupSettle::StableMs;};
    if(!withinBudget())return ERROR_TIMEOUT;
    if(currentDiagnostic){
        Topology current=*currentDiagnostic;state.currentValidationCalled=true;
        state.currentValidationError=apply(static_cast<UINT32>(current.paths.size()),current.paths.data(),
            static_cast<UINT32>(current.modes.size()),current.modes.data(),RefreshRestoreAttempt::ValidateFlags);
        if(!withinBudget())return ERROR_TIMEOUT;
    }
    Topology packet=request;const Topology* selected=&request;
    state.validateCalled=true;
    state.validationError=apply(static_cast<UINT32>(packet.paths.size()),packet.paths.data(),
        static_cast<UINT32>(packet.modes.size()),packet.modes.data(),RefreshRestoreAttempt::ValidateFlags);
    if(state.validationError!=ERROR_SUCCESS){
        if(!normalizedCandidate)return state.validationError;
        if(!withinBudget())return ERROR_TIMEOUT;
        // Only the caller's ownership-checked, advertised virtual-mode candidate
        // may replace a rejected packet. Its unchanged physical fields and full
        // packet must also pass validation before the single apply transaction.
        packet=*normalizedCandidate;selected=normalizedCandidate;state.normalizedValidationCalled=true;
        state.normalizedValidationError=apply(static_cast<UINT32>(packet.paths.size()),packet.paths.data(),
            static_cast<UINT32>(packet.modes.size()),packet.modes.data(),RefreshRestoreAttempt::ValidateFlags);
        if(state.normalizedValidationError!=ERROR_SUCCESS)return state.normalizedValidationError;
        state.normalizedSelected=true;
    }
    if(std::memcmp(packet.paths.data(),selected->paths.data(),packet.paths.size()*sizeof(DISPLAYCONFIG_PATH_INFO)) ||
        std::memcmp(packet.modes.data(),selected->modes.data(),packet.modes.size()*sizeof(DISPLAYCONFIG_MODE_INFO)))return ERROR_INVALID_DATA;
    if(!withinBudget())return ERROR_TIMEOUT;
    state.applyCalled=true;
    state.applyError=apply(static_cast<UINT32>(packet.paths.size()),packet.paths.data(),
        static_cast<UINT32>(packet.modes.size()),packet.modes.data(),RefreshRestoreAttempt::ApplyFlags);
    return state.applyError;
}
static std::string Utf8(const std::wstring& value) {
    int size=WideCharToMultiByte(CP_UTF8,0,value.c_str(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    std::string output(size,'\0');
    if(size)WideCharToMultiByte(CP_UTF8,0,value.c_str(),static_cast<int>(value.size()),&output[0],size,nullptr,nullptr);
    return output;
}
static std::string Json(const std::wstring& value) {
    std::string output="\"";
    for(unsigned char c:Utf8(value)){
        if(c=='"' || c=='\\'){output+='\\';output+=c;}
        else if(c<32){char escaped[7];snprintf(escaped,sizeof(escaped),"\\u%04x",c);output+=escaped;}
        else output+=c;
    }
    return output+'"';
}
static std::string RefreshRestoreJson(const RefreshRestoreAttempt& state,const Topology& baseline){
    std::string result="{\"attempted\":"+std::string(state.attempted?"true":"false")+
        ",\"validateCalled\":"+(state.validateCalled?"true":"false")+",\"validationError\":"+std::to_string(state.validationError)+
        ",\"applyCalled\":"+(state.applyCalled?"true":"false")+",\"applyError\":"+std::to_string(state.applyError)+
        ",\"baselineValidationCalled\":"+(state.baselineValidationCalled?"true":"false")+",\"baselineValidationError\":"+std::to_string(state.baselineValidationError)+
        ",\"currentValidationCalled\":"+(state.currentValidationCalled?"true":"false")+",\"currentValidationError\":"+std::to_string(state.currentValidationError)+
        ",\"normalizedValidationCalled\":"+(state.normalizedValidationCalled?"true":"false")+",\"normalizedValidationError\":"+std::to_string(state.normalizedValidationError)+
        ",\"normalizedSelected\":"+(state.normalizedSelected?"true":"false")+
        ",\"validationFlags\":"+std::to_string(RefreshRestoreAttempt::ValidateFlags)+
        ",\"applyFlags\":"+std::to_string(RefreshRestoreAttempt::ApplyFlags)+",\"requestedBaselineRefresh\":[";
    for(size_t i=0;i<baseline.paths.size();++i){
        const auto& target=baseline.paths[i].targetInfo;if(i)result+=',';
        result+="{\"adapterLow\":"+std::to_string(target.adapterId.LowPart)+",\"adapterHigh\":"+std::to_string(target.adapterId.HighPart)+
            ",\"targetId\":"+std::to_string(target.id)+",\"numerator\":"+std::to_string(target.refreshRate.Numerator)+
            ",\"denominator\":"+std::to_string(target.refreshRate.Denominator)+"}";
    }
    return result+"]}";
}
static bool Write(const std::wstring& path,const std::string& value) {
    std::wstring temporary=path+L".tmp";
    FILE* file=_wfopen(temporary.c_str(),L"wb");
    if(!file) return false;
    bool okay=fwrite(value.data(),1,value.size(),file)==value.size();
    if(fclose(file)!=0)okay=false;
    if(okay)okay=MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;
    if(!okay)DeleteFileW(temporary.c_str());
    return okay;
}
static bool Seconds(const wchar_t* value,unsigned& seconds) {
    if(!value || !*value)return false;
    unsigned result=0;
    for(const wchar_t* p=value;*p;++p){if(*p<L'0'||*p>L'9')return false;result=result*10+(*p-L'0');if(result>120)return false;}
    if(result<10)return false;seconds=result;return true;
}
static std::string RectJson(const RECT& r) {
    return "{\"left\":"+std::to_string(r.left)+",\"top\":"+std::to_string(r.top)+
        ",\"right\":"+std::to_string(r.right)+",\"bottom\":"+std::to_string(r.bottom)+"}";
}
static std::string DesktopImageJson(const DISPLAYCONFIG_MODE_INFO* mode){
    if(!mode)return "null";
    DISPLAYCONFIG_DESKTOP_IMAGE_INFO image={};std::memcpy(&image,&mode->targetMode,sizeof(image));
    const auto rectangle=[](const RECTL& r){return RectJson({r.left,r.top,r.right,r.bottom});};
    return "{\"modeId\":"+std::to_string(mode->id)+",\"adapterLow\":"+std::to_string(mode->adapterId.LowPart)+
        ",\"adapterHigh\":"+std::to_string(mode->adapterId.HighPart)+",\"pathSourceSize\":{\"x\":"+
        std::to_string(image.PathSourceSize.x)+",\"y\":"+std::to_string(image.PathSourceSize.y)+"},\"region\":"+
        rectangle(image.DesktopImageRegion)+",\"clip\":"+rectangle(image.DesktopImageClip)+"}";
}
static std::string TopologyJson(const Topology& topology) {
    std::string result="{\"queryFlags\":"+std::to_string(topology.queryFlags)+
        ",\"regionHookLoaded\":"+(topology.regionHookLoaded?"true":"false")+
        ",\"observationCount\":"+std::to_string(topology.observations.size())+",\"advancedColorObserved\":"+
        std::string(ColorObserved(topology)?"true":"false")+",\"outputs\":[";
    for(size_t i=0;i<topology.paths.size();++i){
        const auto& path=topology.paths[i];
        const Topology::Observation emptyObservation={};
        const auto& observed=i<topology.observations.size()?topology.observations[i]:emptyObservation;
        const DISPLAYCONFIG_SOURCE_MODE emptyMode={};const DISPLAYCONFIG_VIDEO_SIGNAL_INFO emptySignal={};
        const auto modePointer=Source(topology,path);const auto signalPointer=TargetSignal(topology,path);
        const auto mode=modePointer?modePointer:&emptyMode;const auto signal=signalPointer?signalPointer:&emptySignal;
        if(i)result+=',';
        result+="{\"device\":"+Json(observed.sourceName)+",\"pathFlags\":"+std::to_string(path.flags)+
            ",\"supportsVirtualMode\":"+((path.flags&0x08u)?"true":"false")+
            ",\"boostRefreshRate\":"+((path.flags&0x10u)?"true":"false")+
            ",\"sourceModeInfoRaw\":"+std::to_string(path.sourceInfo.modeInfoIdx)+
            ",\"targetModeInfoRaw\":"+std::to_string(path.targetInfo.modeInfoIdx)+
            ",\"desktopImage\":"+DesktopImageJson(DesktopImage(topology,path))+
            ",\"targetId\":"+std::to_string(path.targetInfo.id)+
            ",\"adapterLow\":"+std::to_string(path.targetInfo.adapterId.LowPart)+",\"adapterHigh\":"+
            std::to_string(path.targetInfo.adapterId.HighPart)+",\"sourceId\":"+std::to_string(path.sourceInfo.id)+
            ",\"sourceAdapterLow\":"+std::to_string(path.sourceInfo.adapterId.LowPart)+",\"sourceAdapterHigh\":"+
            std::to_string(path.sourceInfo.adapterId.HighPart)+",\"sourceModeValid\":"+(modePointer?"true":"false")+
            ",\"targetSignalValid\":"+(signalPointer?"true":"false")+",\"shellObserved\":"+(observed.shellObserved?"true":"false")+
            ",\"sourceMode\":{\"width\":"+std::to_string(mode->width)+
            ",\"height\":"+std::to_string(mode->height)+",\"x\":"+std::to_string(mode->position.x)+
            ",\"y\":"+std::to_string(mode->position.y)+",\"pixelFormat\":"+std::to_string(mode->pixelFormat)+
            "},\"targetSignal\":{\"width\":"+std::to_string(signal->activeSize.cx)+",\"height\":"+
            std::to_string(signal->activeSize.cy)+",\"totalWidth\":"+std::to_string(signal->totalSize.cx)+
            ",\"totalHeight\":"+std::to_string(signal->totalSize.cy)+",\"pixelRate\":"+std::to_string(signal->pixelRate)+
            ",\"vSyncNumerator\":"+std::to_string(signal->vSyncFreq.Numerator)+",\"vSyncDenominator\":"+
            std::to_string(signal->vSyncFreq.Denominator)+",\"hSyncNumerator\":"+std::to_string(signal->hSyncFreq.Numerator)+
            ",\"hSyncDenominator\":"+std::to_string(signal->hSyncFreq.Denominator)+",\"videoStandard\":"+
            std::to_string(signal->videoStandard&0x3fffffu)+",\"scanLineOrdering\":"+std::to_string(signal->scanLineOrdering)+
            ",\"videoStandardLow16\":"+std::to_string(signal->videoStandard&0xffffu)+
            ",\"vSyncFreqDivider\":"+std::to_string((signal->videoStandard>>16)&0x3fu)+
            "},\"refresh\":{\"numerator\":"+std::to_string(path.targetInfo.refreshRate.Numerator)+
            ",\"denominator\":"+std::to_string(path.targetInfo.refreshRate.Denominator)+"},\"rotation\":"+
            std::to_string(path.targetInfo.rotation)+",\"scaling\":"+std::to_string(path.targetInfo.scaling)+
            ",\"outputTechnology\":"+std::to_string(path.targetInfo.outputTechnology)+",\"scanLineOrdering\":"+
            std::to_string(path.targetInfo.scanLineOrdering)+",\"targetAvailable\":"+std::to_string(path.targetInfo.targetAvailable)+
            ",\"monitor\":"+RectJson(observed.monitor)+
            ",\"workArea\":"+RectJson(observed.workArea)+",\"primary\":"+
            (observed.monitorFlags&MONITORINFOF_PRIMARY?"true":"false")+",\"colorQueryVersion\":"+
            std::to_string(observed.colorVersion)+",\"colorFlags\":"+std::to_string(observed.colorFlags)+
            ",\"colorEncoding\":"+std::to_string(observed.colorEncoding)+",\"bitsPerColorChannel\":"+
            std::to_string(observed.colorBits)+",\"activeColorMode\":"+std::to_string(observed.activeColorMode)+"}";
    }
    return result+"]}";
}
static int Preflight(UINT32 queryFlags=QDC_ONLY_ACTIVE_PATHS) {
    // Only read display topology, monitor/work-area metadata and color state.
    // No software device, application inspection, capture or window enumeration.
    LONG error=ERROR_SUCCESS;
    if(!SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))error=GetLastError();
    Topology topology;if(error==ERROR_SUCCESS)error=Snapshot(topology,queryFlags);
    if(error!=ERROR_SUCCESS){
        printf("{\"success\":false,\"stage\":\"preflight\",\"windowsError\":%ld}\n",error);return 1;
    }
    const std::string result="{\"success\":true,\"stage\":\"preflight\",\"primaryAndWorkAreaObserved\":true,"+TopologyJson(topology).substr(1);
    puts(result.c_str());return 0;
}
static int ValidateCurrent(){
    LONG error=ERROR_SUCCESS;
    if(!SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))error=GetLastError();
    Topology topology;if(error==ERROR_SUCCESS)error=Snapshot(topology,0x52u);
    const bool called=error==ERROR_SUCCESS;LONG validated=ERROR_NOT_READY;
    if(called)validated=SetDisplayConfig(static_cast<UINT32>(topology.paths.size()),topology.paths.data(),
        static_cast<UINT32>(topology.modes.size()),topology.modes.data(),RefreshRestoreAttempt::ValidateFlags);
    const bool success=called && validated==ERROR_SUCCESS;
    const std::string report="{\"success\":"+std::string(success?"true":"false")+",\"stage\":\"validate-current\",\"snapshotError\":"+
        std::to_string(error)+",\"validationCalled\":"+(called?"true":"false")+",\"validationError\":"+std::to_string(validated)+
        ",\"validationFlags\":"+std::to_string(RefreshRestoreAttempt::ValidateFlags)+",\"applyCalled\":false,\"topology\":"+TopologyJson(topology)+"}";
    puts(report.c_str());return success?0:1;
}
int wmain(int argc,wchar_t** argv) {
    if(argc==2 && wcscmp(argv[1],L"--preflight")==0)return Preflight();
    // Explicit read-only views; the lease selects one supported query at startup.
    if(argc==2 && wcscmp(argv[1],L"--preflight-refresh-aware")==0)return Preflight(0x52u);
    if(argc==2 && wcscmp(argv[1],L"--validate-current")==0)return ValidateCurrent();
    if(argc==2 && wcscmp(argv[1],L"--self-test")==0){
        unsigned seconds=0;int passed=0;
        for(const wchar_t* input:{L"10",L"30",L"120"}){if(!Seconds(input,seconds))return 1;++passed;}
        for(const wchar_t* input:{L"",L"9",L"121",L"-1",L"1x",L"99999999999999999"}){if(Seconds(input,seconds))return 1;++passed;}
        Topology before;before.paths.resize(1);before.modes.resize(2);before.observations.resize(1);
        before.paths[0].sourceInfo.modeInfoIdx=0;before.paths[0].targetInfo.id=1;
        before.paths[0].targetInfo.modeInfoIdx=1;
        before.modes[0].infoType=DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE;
        before.modes[0].sourceMode.width=7680;before.modes[0].sourceMode.height=2160;
        before.modes[1].infoType=DISPLAYCONFIG_MODE_INFO_TYPE_TARGET;before.modes[1].id=1;
        before.modes[1].targetMode.targetVideoSignalInfo.activeSize={7680,2160};
        before.modes[1].targetMode.targetVideoSignalInfo.pixelRate=1000000;
        before.observations[0].sourceName=L"physical";before.observations[0].shellObserved=true;
        before.observations[0].monitor={-7680,0,0,2160};before.observations[0].workArea={-7680,0,0,2120};
        before.observations[0].monitorFlags=MONITORINFOF_PRIMARY;before.observations[0].colorVersion=2;
        before.observations[0].colorFlags=0x33;before.observations[0].colorBits=10;before.observations[0].activeColorMode=2;
        Topology after=before;if(!Preserved(before,after))return 1;++passed;
        after.modes[0].sourceMode.width=2560;if(Preserved(before,after))return 1;++passed;
        after=before;after.paths[0].targetInfo.refreshRate.Numerator=60;if(Preserved(before,after))return 1;++passed;
        after=before;after.paths.clear();if(Preserved(before,after))return 1;++passed;
        after=before;after.paths[0].sourceInfo.modeInfoIdx=9;if(Preserved(before,after))return 1;++passed;
        after=before;after.modes[0].id=4;if(Preserved(before,after))return 1;++passed;
        after=before;after.modes[0].adapterId.LowPart=4;if(Preserved(before,after))return 1;++passed;
        after=before;after.modes[1].targetMode.targetVideoSignalInfo.activeSize.cx=2560;if(Preserved(before,after))return 1;++passed;
        after=before;after.modes[1].targetMode.targetVideoSignalInfo.pixelRate++;if(Preserved(before,after))return 1;++passed;
        after=before;after.paths[0].targetInfo.modeInfoIdx=9;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].monitorFlags=0;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].workArea.right=-2560;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].monitor.left=0;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].colorFlags^=0x20;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].colorBits=8;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].colorEncoding=2;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].activeColorMode=0;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].colorVersion=0;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations[0].shellObserved=false;if(Preserved(before,after))return 1;++passed;
        after=before;after.observations.clear();if(Preserved(before,after))return 1;++passed;
        after=before;after.paths.push_back(after.paths[0]);after.observations.push_back(after.observations[0]);
        if(Preserved(before,after))return 1;++passed;
        if(!ColorObserved(before))return 1;++passed;
        after=before;after.observations[0].colorVersion=0;if(ColorObserved(after))return 1;++passed;
        // Named reasons include both observed values, and the same predicate
        // still rejects each change rather than accepting it for diagnostics.
        after=before;after.modes[0].sourceMode.width=2560;
        if(PreserveFailure(before,after)!=L"physical[0] physical: sourceMode.width (before=7680, observed=2560)")return 1;++passed;
        after=before;after.observations[0].workArea.bottom=2100;
        if(PreserveFailure(before,after)!=L"physical[0] physical: workArea.bottom (before=2120, observed=2100)")return 1;++passed;
        after=before;after.observations[0].activeColorMode=0;
        if(PreserveFailure(before,after)!=L"physical[0] physical: activeColorMode (before=2, observed=0)")return 1;++passed;
        after=before;after.modes[1].targetMode.targetVideoSignalInfo.pixelRate++;
        if(PreserveFailure(before,after)!=L"physical[0] physical: targetSignal.pixelRate (before=1000000, observed=1000001)")return 1;++passed;
        after=before;after.paths.clear();after.observations.clear();
        if(PreserveFailure(before,after)!=L"physical[0] physical: target.identity.missing")return 1;++passed;
        after=before;after.observations.clear();
        if(PreserveFailure(before,after)!=L"observed.observations.incomplete")return 1;++passed;
        if(TopologyJson(after).find("\"shellObserved\":false")==std::string::npos)return 1;++passed;
        after=before;after.paths[0].sourceInfo.modeInfoIdx=9;
        if(TopologyJson(after).find("\"sourceModeValid\":false")==std::string::npos)return 1;++passed;
        after=before;after.paths[0].targetInfo.modeInfoIdx=9;
        if(TopologyJson(after).find("\"targetSignalValid\":false")==std::string::npos)return 1;++passed;
        if(TopologyJson(before).find("\"hSyncNumerator\":0")==std::string::npos)return 1;++passed;
        if(!PreserveFailure(before,before).empty())return 1;++passed;
        after=before;after.queryFlags=0x52u;
        if(PreserveFailure(before,after)!=L"queryFlags.mismatch")return 1;++passed;
        // Packed modern indexes are valid only when both query and path agree.
        Topology modern=before;modern.queryFlags=0x52u;modern.paths[0].flags=0x08u;
        modern.paths[0].sourceInfo.modeInfoIdx=0x00000007u;
        modern.paths[0].targetInfo.modeInfoIdx=0x00010005u;
        if(Source(modern,modern.paths[0])!=&modern.modes[0].sourceMode)return 1;++passed;
        if(TargetSignal(modern,modern.paths[0])!=&modern.modes[1].targetMode.targetVideoSignalInfo)return 1;++passed;
        modern.paths[0].sourceInfo.modeInfoIdx=0xffff0007u;
        if(Source(modern,modern.paths[0]))return 1;++passed;
        modern.paths[0].sourceInfo.modeInfoIdx=0x00020007u;
        if(Source(modern,modern.paths[0]))return 1;++passed;
        modern.paths[0].sourceInfo.modeInfoIdx=0x00000007u;
        modern.modes[0].id=1;if(Source(modern,modern.paths[0]))return 1;++passed;
        modern.modes[0].id=0;modern.modes[0].infoType=DISPLAYCONFIG_MODE_INFO_TYPE_TARGET;
        if(Source(modern,modern.paths[0]))return 1;++passed;
        modern.modes[0]=before.modes[0];modern.modes[0].adapterId.LowPart=99;
        if(Source(modern,modern.paths[0]))return 1;++passed;
        modern.modes[0]=before.modes[0];modern.paths[0].targetInfo.modeInfoIdx=0xffff0005u;
        if(TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.paths[0].targetInfo.modeInfoIdx=0x00020005u;
        if(TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.paths[0].targetInfo.modeInfoIdx=0x00010005u;modern.modes[1].adapterId.LowPart=99;
        if(TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.modes[1]=before.modes[1];modern.modes[1].id=2;
        if(TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.modes[1]=before.modes[1];modern.modes[1].infoType=DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE;
        if(TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.modes[1]=before.modes[1];modern.paths[0].flags=0;
        if(Source(modern,modern.paths[0]) || TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern.paths[0].flags=0x08u;modern.queryFlags=QDC_ONLY_ACTIVE_PATHS;
        if(Source(modern,modern.paths[0]) || TargetSignal(modern,modern.paths[0]))return 1;++passed;
        modern=before;modern.queryFlags=0x52u;
        if(!Source(modern,modern.paths[0]) || !TargetSignal(modern,modern.paths[0]))return 1;++passed;
        auto supportedSnapshot=[](Topology& value,UINT32 flags,bool* unsupported)->LONG{
            value.queryFlags=flags;if(unsupported)*unsupported=false;return ERROR_SUCCESS;
        };
        auto unsupportedSnapshot=[](Topology& value,UINT32 flags,bool* unsupported)->LONG{
            value.queryFlags=flags;if(unsupported)*unsupported=flags==0x52u;
            return flags==0x52u?ERROR_NOT_SUPPORTED:ERROR_SUCCESS;
        };
        auto failedObservation=[](Topology& value,UINT32 flags,bool* unsupported)->LONG{
            value.queryFlags=flags;if(unsupported)*unsupported=false;return ERROR_INVALID_PARAMETER;
        };
        Topology baseline;bool fallback=false;LONG modernError=ERROR_SUCCESS;
        if(BaselineSnapshot(baseline,fallback,modernError,supportedSnapshot)!=ERROR_SUCCESS || fallback || baseline.queryFlags!=0x52u)return 1;++passed;
        if(BaselineSnapshot(baseline,fallback,modernError,unsupportedSnapshot)!=ERROR_SUCCESS || !fallback || baseline.queryFlags!=2 || modernError!=ERROR_NOT_SUPPORTED)return 1;++passed;
        if(BaselineSnapshot(baseline,fallback,modernError,failedObservation)!=ERROR_INVALID_PARAMETER || fallback || baseline.queryFlags!=0x52u)return 1;++passed;
        StartupSettle settle(1000,121000);
        if(settle.deadline!=11000 || settle.Observe(1000,true,true)!=SettleResult::Waiting)return 1;++passed;
        if(settle.Observe(1400,true,true)!=SettleResult::Waiting || settle.Observe(1749,true,true)!=SettleResult::Waiting)return 1;++passed;
        if(settle.Observe(1750,true,true)!=SettleResult::Ready)return 1;++passed;
        settle=StartupSettle(1000,121000);
        settle.Observe(1000,true,true);settle.Observe(1400,false,true);
        if(settle.Observe(1750,true,true)!=SettleResult::Waiting || settle.stableSince!=1750)return 1;++passed;
        settle.Observe(2100,true,true);
        if(settle.Observe(2500,true,true)!=SettleResult::Ready)return 1;++passed;
        settle=StartupSettle(1000,121000);settle.Observe(1000,true,true);settle.Observe(1400,true,false);
        if(settle.Observe(1750,true,true)!=SettleResult::Waiting || settle.stableSince!=1750)return 1;++passed;
        settle=StartupSettle(1000,121000);settle.Observe(1000,true,true);settle.Observe(1400,true,true,false);
        if(settle.Observe(1750,true,true)!=SettleResult::Waiting || settle.stableSince!=1400)return 1;++passed;
        settle=StartupSettle(1000,121000);settle.Observe(1000,true,true);
        if(settle.Observe(1800,true,true)!=SettleResult::Waiting || settle.stableSince!=1800)return 1;++passed;
        settle=StartupSettle(1000,1700);settle.Observe(1000,true,true);settle.Observe(1400,true,true);
        if(settle.deadline!=1700 || settle.Observe(1700,true,true)!=SettleResult::Expired)return 1;++passed;
        settle=StartupSettle(1000,121000);
        if(settle.Observe(10999,false,true)!=SettleResult::Waiting || settle.Observe(11000,true,true)!=SettleResult::Expired)return 1;++passed;
        settle=StartupSettle(1000,999);
        if(settle.Observe(1000,true,true)!=SettleResult::Expired)return 1;++passed;
        settle=StartupSettle(1000,121000);
        if(settle.Observe(999,true,true)!=SettleResult::Expired)return 1;++passed;
        if(!ExactInstance(L"SWD\\DisplayWeaveProjection\\Fixture-1",L"swd\\displayweaveprojection\\fixture-1"))return 1;++passed;
        if(ExactInstance(L"SWD\\DisplayWeaveProjection\\Fixture-10",L"SWD\\DisplayWeaveProjection\\Fixture-1"))return 1;++passed;
        if(ExactInstance(L"",L""))return 1;++passed;
        auto fakeOwner=[](LUID adapter,const std::wstring& instanceId)->Ownership{
            Ownership result;
            result.verified=adapter.LowPart==10 && ExactInstance(instanceId,L"SWD\\Fixture\\Owned");
            if(!result.verified)result.configurationError=CR_NO_SUCH_DEVNODE;
            result.adapterInstance=L"SWD\\Fixture\\Owned";return result;
        };
        std::wstring output;Ownership ownership;
        if(NewOwnedOutput(before,before,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Pending)return 1;++passed;
        after=before;auto path=before.paths[0];path.targetInfo.id=2;
        path.sourceInfo.adapterId.LowPart=path.targetInfo.adapterId.LowPart=10;
        path.targetInfo.outputTechnology=DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED;
        after.paths.push_back(path);after.observations.push_back(before.observations[0]);after.observations[1].sourceName=L"virtual";
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Owned || output!=L"virtual")return 1;++passed;
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Wrong",output,ownership,fakeOwner)!=OutputSearch::Rejected || ownership.verified)return 1;++passed;
        after.paths[1].sourceInfo.adapterId.LowPart=11;
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Rejected)return 1;++passed;
        after.paths[1].sourceInfo.adapterId.LowPart=10;after.paths[1].targetInfo.adapterId.LowPart=11;
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Rejected)return 1;++passed;
        after.paths[1]=path;after.paths[1].targetInfo.outputTechnology=DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Owned ||
            !ownership.verified || output!=L"virtual")return 1;
        ++passed;
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Wrong",output,ownership,fakeOwner)!=OutputSearch::Rejected ||
            ownership.verified || !output.empty())return 1;
        ++passed;
        auto unavailableOwner=[](LUID,const std::wstring&)->Ownership{
            Ownership result;result.displayError=ERROR_NOT_SUPPORTED;return result;
        };
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,unavailableOwner)!=OutputSearch::Rejected ||
            ownership.verified || ownership.displayError!=ERROR_NOT_SUPPORTED)return 1;
        ++passed;
        // Two new targets remain ambiguous even if both share the owned adapter.
        after.paths.push_back(after.paths[1]);after.paths[2].targetInfo.id=3;after.observations.push_back(after.observations[1]);
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Rejected ||
            ownership.verified || !output.empty())return 1;
        ++passed;
        after.paths.pop_back();after.observations.pop_back();
        after.paths[1]=path;after.paths.push_back(path);after.paths[2].targetInfo.id=3;after.observations.push_back(after.observations[1]);
        if(NewOwnedOutput(before,after,L"SWD\\Fixture\\Owned",output,ownership,fakeOwner)!=OutputSearch::Rejected)return 1;++passed;
        Topology refreshBefore=before;refreshBefore.queryFlags=0x52u;refreshBefore.paths[0].flags=1;
        refreshBefore.paths[0].targetInfo.refreshRate={12000,100};
        Topology refreshNow=refreshBefore;refreshNow.paths[0].targetInfo.refreshRate={240,1};
        path=refreshBefore.paths[0];path.sourceInfo.adapterId.LowPart=path.targetInfo.adapterId.LowPart=10;
        path.targetInfo.id=2;path.sourceInfo.modeInfoIdx=2;path.targetInfo.modeInfoIdx=3;
        path.targetInfo.refreshRate={60,1};refreshNow.paths.push_back(path);
        auto sourceMode=refreshBefore.modes[0];sourceMode.adapterId.LowPart=10;sourceMode.sourceMode.width=800;sourceMode.sourceMode.height=600;
        auto targetMode=refreshBefore.modes[1];targetMode.adapterId.LowPart=10;targetMode.id=2;
        targetMode.targetMode.targetVideoSignalInfo.activeSize={1280,720};
        refreshNow.modes.push_back(sourceMode);refreshNow.modes.push_back(targetMode);
        refreshNow.observations.push_back(refreshBefore.observations[0]);refreshNow.observations[1].sourceName=L"virtual";
        Topology refreshRequest;std::wstring candidateReason;
        if(!RefreshRestoreCandidate(refreshBefore,refreshNow,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        if(refreshRequest.paths[0].targetInfo.refreshRate.Numerator!=12000 || refreshRequest.paths[0].targetInfo.refreshRate.Denominator!=100 ||
            refreshRequest.paths[1].targetInfo.refreshRate.Numerator!=60 ||
            std::memcmp(refreshRequest.modes.data(),refreshNow.modes.data(),refreshNow.modes.size()*sizeof(DISPLAYCONFIG_MODE_INFO)))return 1;
        ++passed;
        auto unchanged=refreshRequest;unchanged.paths[0].targetInfo.refreshRate=refreshNow.paths[0].targetInfo.refreshRate;
        if(std::memcmp(unchanged.paths.data(),refreshNow.paths.data(),refreshNow.paths.size()*sizeof(DISPLAYCONFIG_PATH_INFO)))return 1;++passed;
        using Mutation=void (*)(Topology&);
        const Mutation nonRefreshChanges[]={
            [](Topology& t){t.modes[0].sourceMode.width--;},
            [](Topology& t){t.modes[0].sourceMode.height--;},
            [](Topology& t){t.modes[0].sourceMode.position.x++;},
            [](Topology& t){t.modes[0].sourceMode.pixelFormat=DISPLAYCONFIG_PIXELFORMAT_8BPP;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.pixelRate++;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.vSyncFreq.Numerator++;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.hSyncFreq.Denominator++;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.totalSize.cx++;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.activeSize.cy++;},
            [](Topology& t){t.modes[1].targetMode.targetVideoSignalInfo.videoStandard++;},
            [](Topology& t){t.observations[0].workArea.bottom++;},
            [](Topology& t){t.observations[0].monitor.right++;},
            [](Topology& t){t.observations[0].monitorFlags=0;},
            [](Topology& t){t.observations[0].activeColorMode=0;},
            [](Topology& t){t.observations[0].colorFlags^=1;},
            [](Topology& t){t.observations[0].colorBits=8;},
            [](Topology& t){t.observations[0].sourceName=L"renamed";},
            [](Topology& t){t.paths[0].targetInfo.rotation=DISPLAYCONFIG_ROTATION_ROTATE90;},
            [](Topology& t){t.paths[0].targetInfo.scaling=DISPLAYCONFIG_SCALING_CENTERED;},
            [](Topology& t){t.paths[0].sourceInfo.statusFlags^=1;},
            [](Topology& t){t.paths[0].targetInfo.statusFlags^=1;},
            [](Topology& t){t.paths[0].flags^=0x10u;},
            [](Topology& t){t.paths[0].targetInfo.id++;},
            [](Topology& t){t.paths[0].sourceInfo.adapterId.LowPart++;},
            [](Topology& t){t.paths[0].targetInfo.refreshRate.Denominator=0;},
            [](Topology& t){t.queryFlags=2;},
            [](Topology& t){t.paths[1].sourceInfo.adapterId.LowPart=11;},
            [](Topology& t){t.paths[1].targetInfo.modeInfoIdx=99;}
        };
        for(const auto change:nonRefreshChanges){
            auto invalid=refreshNow;change(invalid);
            if(RefreshRestoreCandidate(refreshBefore,invalid,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner) ||
                !refreshRequest.paths.empty() || candidateReason.empty())return 1;
            ++passed;
        }
        if(RefreshRestoreCandidate(refreshBefore,refreshNow,L"SWD\\Fixture\\Wrong",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        auto extra=refreshNow;extra.paths.push_back(extra.paths[1]);extra.paths.back().targetInfo.id++;
        extra.observations.push_back(extra.observations[1]);
        if(RefreshRestoreCandidate(refreshBefore,extra,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        auto noChange=refreshNow;noChange.paths[0].targetInfo.refreshRate=refreshBefore.paths[0].targetInfo.refreshRate;
        if(RefreshRestoreCandidate(refreshBefore,noChange,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        auto desktopBefore=refreshBefore,desktopNow=refreshNow;DISPLAYCONFIG_MODE_INFO desktop={};
        desktop.infoType=DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE;
        desktopBefore.modes.push_back(desktop);desktopNow.modes.push_back(desktop);
        desktopBefore.paths[0].flags=desktopNow.paths[0].flags=9;
        desktopBefore.paths[0].sourceInfo.modeInfoIdx=desktopNow.paths[0].sourceInfo.modeInfoIdx=0xffffu;
        desktopBefore.paths[0].targetInfo.modeInfoIdx=0x00010002u;desktopNow.paths[0].targetInfo.modeInfoIdx=0x00010004u;
        if(!RefreshRestoreCandidate(desktopBefore,desktopNow,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        desktopNow.modes[4].targetMode.targetVideoSignalInfo.pixelRate=1;
        if(RefreshRestoreCandidate(desktopBefore,desktopNow,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        if(!RefreshRestoreCandidate(refreshBefore,refreshNow,L"SWD\\Fixture\\Owned",refreshRequest,candidateReason,fakeOwner))return 1;++passed;
        RefreshRestoreAttempt attempt;unsigned calls=0;
        auto checkApply=[&](UINT32 count,DISPLAYCONFIG_PATH_INFO* paths,UINT32 modes,DISPLAYCONFIG_MODE_INFO* modeData,UINT32 flags)->LONG{
            ++calls;
            if(flags!=(calls==1?0x28060u:0x280a0u) || count!=refreshRequest.paths.size() || modes!=refreshRequest.modes.size() ||
                std::memcmp(paths,refreshRequest.paths.data(),count*sizeof(DISPLAYCONFIG_PATH_INFO)) ||
                std::memcmp(modeData,refreshRequest.modes.data(),modes*sizeof(DISPLAYCONFIG_MODE_INFO)))return ERROR_INVALID_DATA;
            return ERROR_SUCCESS;
        };
        auto clock=[]()->ULONGLONG{return 1000;};
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,checkApply,clock)!=ERROR_SUCCESS || calls!=2 || !attempt.applyCalled)return 1;++passed;
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,checkApply,clock)!=ERROR_ALREADY_EXISTS || calls!=2)return 1;++passed;
        attempt={};calls=0;
        if(RestoreRefreshOnce(attempt,refreshRequest,1700,checkApply,clock)!=ERROR_TIMEOUT || calls || attempt.validateCalled)return 1;++passed;
        attempt={};calls=0;
        auto rejectValidate=[&](UINT32,DISPLAYCONFIG_PATH_INFO*,UINT32,DISPLAYCONFIG_MODE_INFO*,UINT32)->LONG{++calls;return ERROR_BAD_CONFIGURATION;};
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,rejectValidate,clock)!=ERROR_BAD_CONFIGURATION || calls!=1 || attempt.applyCalled)return 1;++passed;
        attempt={};calls=0;
        auto rejectApply=[&](UINT32,DISPLAYCONFIG_PATH_INFO*,UINT32,DISPLAYCONFIG_MODE_INFO*,UINT32)->LONG{return ++calls==1?ERROR_SUCCESS:ERROR_ACCESS_DENIED;};
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,rejectApply,clock)!=ERROR_ACCESS_DENIED || calls!=2 || attempt.applyError!=ERROR_ACCESS_DENIED)return 1;++passed;
        attempt={};calls=0;unsigned clockCalls=0;
        auto expireAfterValidation=[&]()->ULONGLONG{return ++clockCalls==1?1000:5000;};
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,checkApply,expireAfterValidation)!=ERROR_TIMEOUT || calls!=1 || attempt.applyCalled)return 1;++passed;
        attempt={};calls=0;
        auto mutatePacket=[&](UINT32,DISPLAYCONFIG_PATH_INFO* paths,UINT32,DISPLAYCONFIG_MODE_INFO*,UINT32)->LONG{++calls;paths[0].flags=0;return ERROR_SUCCESS;};
        if(RestoreRefreshOnce(attempt,refreshRequest,5000,mutatePacket,clock)!=ERROR_INVALID_DATA || calls!=1 || attempt.applyCalled)return 1;++passed;
        // Diagnostic normalization edits only the exactly owned virtual source
        // and its desktop-image payload. The physical packet and target signal
        // remain byte-identical. Only the selected validated packet is applied.
        auto normalizable=refreshRequest;
        normalizable.paths[1].flags=9;
        normalizable.paths[1].sourceInfo.modeInfoIdx=(2u<<16)|0xffffu;
        normalizable.paths[1].targetInfo.modeInfoIdx=(3u<<16)|4u;
        normalizable.paths[1].targetInfo.rotation=DISPLAYCONFIG_ROTATION_IDENTITY;
        normalizable.paths[1].targetInfo.scaling=DISPLAYCONFIG_SCALING_IDENTITY;
        normalizable.modes[2].sourceMode.position.x=7680;
        normalizable.modes[2].sourceMode.pixelFormat=DISPLAYCONFIG_PIXELFORMAT_32BPP;
        DISPLAYCONFIG_MODE_INFO virtualDesktop={};virtualDesktop.infoType=DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE;
        virtualDesktop.adapterId.LowPart=10;
        DISPLAYCONFIG_DESKTOP_IMAGE_INFO oldImage={};oldImage.PathSourceSize={800,600};
        oldImage.DesktopImageRegion=oldImage.DesktopImageClip={0,0,800,600};
        std::memcpy(&virtualDesktop.targetMode,&oldImage,sizeof(oldImage));normalizable.modes.push_back(virtualDesktop);
        auto supported=[](const std::wstring& name,UINT32 w,UINT32 h,DISPLAYCONFIG_RATIONAL r,ULONGLONG)->bool{
            return name==L"virtual" && w==1280 && h==720 && r.Numerator==60 && r.Denominator==1;
        };
        auto unsupported=[](const std::wstring&,UINT32,UINT32,DISPLAYCONFIG_RATIONAL,ULONGLONG)->bool{return false;};
        Topology normalized;std::wstring normalizationReason;
        if(!NormalizeOwnedVirtualCandidate(refreshBefore,normalizable,L"SWD\\Fixture\\Owned",5000,normalized,normalizationReason,fakeOwner,supported))return 1;++passed;
        if(std::memcmp(normalized.paths.data(),normalizable.paths.data(),normalizable.paths.size()*sizeof(DISPLAYCONFIG_PATH_INFO)) ||
            std::memcmp(normalized.modes.data(),normalizable.modes.data(),2*sizeof(DISPLAYCONFIG_MODE_INFO)) ||
            std::memcmp(&normalized.modes[3],&normalizable.modes[3],sizeof(DISPLAYCONFIG_MODE_INFO)) ||
            normalized.modes[2].sourceMode.width!=1280 || normalized.modes[2].sourceMode.height!=720 ||
            normalized.modes[2].sourceMode.position.x!=7680 || !Preserved(refreshBefore,normalized))return 1;
        ++passed;
        DISPLAYCONFIG_DESKTOP_IMAGE_INFO newImage={};std::memcpy(&newImage,&normalized.modes[4].targetMode,sizeof(newImage));
        if(newImage.PathSourceSize.x!=1280 || newImage.PathSourceSize.y!=720 || newImage.DesktopImageRegion.right!=1280 ||
            newImage.DesktopImageRegion.bottom!=720 || newImage.DesktopImageClip.right!=1280 || newImage.DesktopImageClip.bottom!=720)return 1;
        ++passed;
        const auto normalizedGood=normalized;
        if(NormalizeOwnedVirtualCandidate(refreshBefore,normalizable,L"SWD\\Fixture\\Wrong",5000,normalized,normalizationReason,fakeOwner,supported))return 1;++passed;
        if(NormalizeOwnedVirtualCandidate(refreshBefore,normalizable,L"SWD\\Fixture\\Owned",5000,normalized,normalizationReason,fakeOwner,unsupported))return 1;++passed;
        using NormalizeMutation=void(*)(Topology&);
        const NormalizeMutation normalizeRejects[]={
            [](Topology& t){t.modes[2].sourceMode.position.x=7679;},
            [](Topology& t){t.paths[1].targetInfo.rotation=DISPLAYCONFIG_ROTATION_ROTATE90;},
            [](Topology& t){t.paths[1].targetInfo.scaling=DISPLAYCONFIG_SCALING_CENTERED;},
            [](Topology& t){t.modes[3].targetMode.targetVideoSignalInfo.activeSize.cx=0;},
            [](Topology& t){t.modes[3].targetMode.targetVideoSignalInfo.activeSize.cy=16385;},
            [](Topology& t){t.modes[2].sourceMode.position.x=INT32_MAX;},
            [](Topology& t){t.paths[1].targetInfo.modeInfoIdx=(3u<<16)|0xffffu;},
            [](Topology& t){t.modes[2].sourceMode.width=1280;t.modes[2].sourceMode.height=720;},
            [](Topology& t){t.modes[0].sourceMode.width--;}
        };
        for(const auto change:normalizeRejects){
            auto invalid=normalizable;change(invalid);
            if(NormalizeOwnedVirtualCandidate(refreshBefore,invalid,L"SWD\\Fixture\\Owned",5000,normalized,normalizationReason,fakeOwner,supported) ||
                !normalized.paths.empty() || normalizationReason.empty())return 1;
            ++passed;
        }
        attempt={};calls=0;
        auto diagnosticApply=[&](UINT32 count,DISPLAYCONFIG_PATH_INFO* paths,UINT32 modes,DISPLAYCONFIG_MODE_INFO* modeData,UINT32 flags)->LONG{
            ++calls;if(flags!=(calls==4?RefreshRestoreAttempt::ApplyFlags:RefreshRestoreAttempt::ValidateFlags))return ERROR_INVALID_DATA;
            if(calls>=3 && (count!=normalizedGood.paths.size() || modes!=normalizedGood.modes.size() ||
                std::memcmp(paths,normalizedGood.paths.data(),count*sizeof(DISPLAYCONFIG_PATH_INFO)) ||
                std::memcmp(modeData,normalizedGood.modes.data(),modes*sizeof(DISPLAYCONFIG_MODE_INFO))))return ERROR_INVALID_DATA;
            return calls<=2?ERROR_GEN_FAILURE:ERROR_SUCCESS;
        };
        if(RestoreRefreshOnce(attempt,normalizable,5000,diagnosticApply,clock,&refreshNow,&normalizedGood)!=ERROR_SUCCESS ||
            calls!=4 || !attempt.currentValidationCalled || attempt.currentValidationError!=ERROR_GEN_FAILURE ||
            !attempt.normalizedValidationCalled || attempt.normalizedValidationError!=ERROR_SUCCESS || !attempt.applyCalled || !attempt.normalizedSelected)return 1;
        ++passed;
        if(RestoreRefreshOnce(attempt,normalizable,5000,diagnosticApply,clock,&refreshNow,&normalizedGood)!=ERROR_ALREADY_EXISTS || calls!=4)return 1;++passed;
        attempt={};calls=0;clockCalls=0;
        if(RestoreRefreshOnce(attempt,normalizable,5000,diagnosticApply,expireAfterValidation,&refreshNow,&normalizedGood)!=ERROR_TIMEOUT ||
            calls!=1 || attempt.validateCalled || attempt.normalizedValidationCalled || attempt.applyCalled)return 1;
        ++passed;
        attempt={};calls=0;
        auto rejectNormalized=[&](UINT32,DISPLAYCONFIG_PATH_INFO*,UINT32,DISPLAYCONFIG_MODE_INFO*,UINT32 flags)->LONG{
            ++calls;return flags==RefreshRestoreAttempt::ValidateFlags?ERROR_GEN_FAILURE:ERROR_INVALID_DATA;
        };
        if(RestoreRefreshOnce(attempt,normalizable,5000,rejectNormalized,clock,&refreshNow,&normalizedGood)!=ERROR_GEN_FAILURE ||
            calls!=3 || attempt.normalizedSelected || attempt.applyCalled)return 1;
        ++passed;
        attempt={};calls=0;
        auto mutateNormalized=[&](UINT32,DISPLAYCONFIG_PATH_INFO* paths,UINT32,DISPLAYCONFIG_MODE_INFO*,UINT32)->LONG{
            if(++calls<3)return ERROR_GEN_FAILURE;paths[0].flags^=1;return ERROR_SUCCESS;
        };
        if(RestoreRefreshOnce(attempt,normalizable,5000,mutateNormalized,clock,&refreshNow,&normalizedGood)!=ERROR_INVALID_DATA ||
            calls!=3 || attempt.applyCalled)return 1;
        ++passed;
        attempt={};calls=0;clockCalls=0;
        auto expireBeforeNormalizedApply=[&]()->ULONGLONG{return ++clockCalls<4?1000:5000;};
        if(RestoreRefreshOnce(attempt,normalizable,5000,diagnosticApply,expireBeforeNormalizedApply,&refreshNow,&normalizedGood)!=ERROR_TIMEOUT ||
            calls!=3 || attempt.applyCalled)return 1;
        ++passed;
        ReleasePhase phase;
        if(phase.Observe(100,10000,false,false)!=ReleaseDecision::Idle || phase.begun)return 1;++passed;
        if(phase.Observe(100,10000,false,true)!=ReleaseDecision::Rejected || phase.begun)return 1;++passed;
        if(phase.Observe(100,10000,true,true)!=ReleaseDecision::Rejected || phase.begun)return 1;++passed;
        if(phase.Observe(100,10000,true,false)!=ReleaseDecision::Begun || !phase.begun || phase.deadline!=5100)return 1;++passed;
        if(phase.Observe(101,10000,true,false)!=ReleaseDecision::Rejected || phase.deadline!=5100)return 1;++passed;
        if(phase.Observe(99,10000,false,true)!=ReleaseDecision::Expired)return 1;++passed;
        if(phase.Observe(5099,10000,false,false)!=ReleaseDecision::Waiting)return 1;++passed;
        if(phase.Observe(5099,10000,false,true)!=ReleaseDecision::Complete || phase.completed)return 1;++passed;
        if(phase.Observe(5100,10000,false,true)!=ReleaseDecision::Expired)return 1;++passed;
        phase={};if(phase.Observe(100,350,true,false)!=ReleaseDecision::Begun || phase.deadline!=350)return 1;++passed;
        phase={};if(phase.Observe(350,350,true,false)!=ReleaseDecision::Expired || phase.begun)return 1;++passed;
        phase.completed=true;if(phase.Observe(350,500,true,false)!=ReleaseDecision::Rejected)return 1;++passed;
        auto releaseBefore=refreshBefore,releaseNow=refreshNow;bool transition=false;std::wstring releaseReason;
        releaseBefore.modes[1].targetMode.targetVideoSignalInfo.vSyncFreq={240,1};
        releaseNow.modes[1].targetMode.targetVideoSignalInfo.vSyncFreq={240,1};
        if(!ReleaseTopologyAllowed(releaseBefore,releaseNow,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner) || !transition)return 1;++passed;
        auto equivalentSignal=releaseNow;equivalentSignal.paths[0].targetInfo.refreshRate={24000,100};
        if(!ReleaseTopologyAllowed(releaseBefore,equivalentSignal,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner) || !transition)return 1;++passed;
        auto releaseUnchanged=releaseNow;releaseUnchanged.paths[0].targetInfo.refreshRate=releaseBefore.paths[0].targetInfo.refreshRate;
        if(!ReleaseTopologyAllowed(releaseBefore,releaseUnchanged,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner) || transition)return 1;++passed;
        for(const auto change:nonRefreshChanges){
            auto invalid=releaseNow;change(invalid);
            if(ReleaseTopologyAllowed(releaseBefore,invalid,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner) || releaseReason.empty())return 1;
            ++passed;
        }
        auto wrongRefresh=releaseNow;wrongRefresh.paths[0].targetInfo.refreshRate={60,1};
        if(ReleaseTopologyAllowed(releaseBefore,wrongRefresh,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner))return 1;++passed;
        if(ReleaseTopologyAllowed(releaseBefore,releaseNow,L"SWD\\Fixture\\Wrong",transition,releaseReason,fakeOwner))return 1;++passed;
        if(!ReleaseTopologyAllowed(releaseBefore,releaseNow,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner,&releaseNow))return 1;++passed;
        auto changedVirtual=releaseNow;changedVirtual.paths[1].targetInfo.id++;changedVirtual.modes[3].id++;
        if(ReleaseTopologyAllowed(releaseBefore,changedVirtual,L"SWD\\Fixture\\Owned",transition,releaseReason,fakeOwner,&releaseNow))return 1;++passed;
        // Exact restoration covers physical desktop-image mapping even when
        // source/target dimensions and the legacy query view are unchanged.
        auto exactBefore=desktopBefore;
        DISPLAYCONFIG_DESKTOP_IMAGE_INFO exactImage={};exactImage.PathSourceSize={7680,2160};
        exactImage.DesktopImageRegion=exactImage.DesktopImageClip={0,0,7680,2160};
        std::memcpy(&exactBefore.modes[2].targetMode,&exactImage,sizeof(exactImage));
        if(!Preserved(exactBefore,exactBefore))return 1;++passed;
        for(unsigned field=0;field<10;++field){
            auto changed=exactBefore;auto image=exactImage;
            LONG* values[]={&image.PathSourceSize.x,&image.PathSourceSize.y,&image.DesktopImageRegion.left,&image.DesktopImageRegion.top,
                &image.DesktopImageRegion.right,&image.DesktopImageRegion.bottom,&image.DesktopImageClip.left,&image.DesktopImageClip.top,
                &image.DesktopImageClip.right,&image.DesktopImageClip.bottom};
            ++*values[field];std::memcpy(&changed.modes[2].targetMode,&image,sizeof(image));
            if(Preserved(exactBefore,changed) || PreserveFailure(exactBefore,changed).find(L"desktopImage.")==std::wstring::npos)return 1;
            ++passed;
        }
        using MetadataMutation=void(*)(Topology&);
        const MetadataMutation metadataChanges[]={
            [](Topology& t){t.paths[0].flags^=0x10u;},
            [](Topology& t){t.paths[0].sourceInfo.statusFlags^=1;},
            [](Topology& t){t.paths[0].targetInfo.statusFlags^=1;},
            [](Topology& t){t.paths[0].sourceInfo.modeInfoIdx^=1;},
            [](Topology& t){t.modes[2].id++;},
            [](Topology& t){t.modes[2].adapterId.LowPart++;},
            [](Topology& t){t.modes[2].adapterId.HighPart++;},
            [](Topology& t){t.modes[2].infoType=DISPLAYCONFIG_MODE_INFO_TYPE_TARGET;},
            [](Topology& t){t.paths[0].targetInfo.modeInfoIdx=0x0001ffffu;}
        };
        for(const auto change:metadataChanges){auto invalid=exactBefore;change(invalid);if(Preserved(exactBefore,invalid))return 1;++passed;}
        auto renumbered=exactBefore;
        renumbered.modes={exactBefore.modes[2],exactBefore.modes[1],exactBefore.modes[0]};
        renumbered.paths[0].sourceInfo.modeInfoIdx=(2u<<16)|0xffffu;
        renumbered.paths[0].targetInfo.modeInfoIdx=1u<<16;
        if(!Preserved(exactBefore,renumbered))return 1;++passed;
        printf("PASS temporary device lease: %d policy checks; no device created\n",passed);return 0;
    }
    unsigned seconds=0;std::wstring report;
    if(argc!=6 || wcscmp(argv[1],L"--vdd-fixture") || wcscmp(argv[2],L"--seconds") ||
        !Seconds(argv[3],seconds) || wcscmp(argv[4],L"--report") || !*argv[5]){
        fprintf(stderr,"Usage: RegionDisplayLease --vdd-fixture --seconds 10..120 --report absolute-path\n");return 2;
    }
    wchar_t full[MAX_PATH];DWORD length=GetFullPathNameW(argv[5],MAX_PATH,full,nullptr);
    if(!length || length>=MAX_PATH){fprintf(stderr,"Invalid report path\n");return 2;}report=full;
    // Keep MONITORINFO work-area rectangles in physical pixels on mixed-DPI desktops.
    if(!SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)){
        fprintf(stderr,"Cannot select physical-pixel DPI context: %lu\n",GetLastError());return 3;
    }
    Topology before;bool legacyQueryFallback=false;LONG modernQueryError=ERROR_SUCCESS;
    LONG initial=BaselineSnapshot(before,legacyQueryFallback,modernQueryError);
    if(initial!=ERROR_SUCCESS){fprintf(stderr,"Cannot snapshot display topology: %ld\n",initial);return 3;}
    const UINT32 leaseQueryFlags=before.queryFlags;
    RefreshRestoreAttempt refreshRestore;
    if(leaseQueryFlags==0x52u){
        Topology validation=before;refreshRestore.baselineValidationCalled=true;
        refreshRestore.baselineValidationError=SetDisplayConfig(static_cast<UINT32>(validation.paths.size()),validation.paths.data(),
            static_cast<UINT32>(validation.modes.size()),validation.modes.data(),RefreshRestoreAttempt::ValidateFlags);
    }
    if(!Write(report+L".baseline-validation.json","{\"refreshRestore\":"+RefreshRestoreJson(refreshRestore,before)+",\"baseline\":"+TopologyJson(before)+"}")){
        fprintf(stderr,"Cannot write baseline validation diagnostic\n");return 3;
    }
    ReleaseEvents releaseEvents;const DWORD releaseEventError=releaseEvents.Create();
    if(releaseEventError!=ERROR_SUCCESS){fprintf(stderr,"Cannot create private fixture release events: %lu\n",releaseEventError);return 3;}
    HMODULE library=LoadLibraryExW(L"cfgmgr32.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
    auto create=library?reinterpret_cast<RsCreateDevice>(GetProcAddress(library,"SwDeviceCreate")):nullptr;
    auto close=library?reinterpret_cast<RsCloseDevice>(GetProcAddress(library,"SwDeviceClose")):nullptr;
    if(!create || !close){fprintf(stderr,"Software device API is unavailable\n");if(library)FreeLibrary(library);return 3;}
    Creation creation;creation.event=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!creation.event){FreeLibrary(library);return 3;}
    wchar_t instance[96];swprintf(instance,96,L"Fixture-%lu-%llu",GetCurrentProcessId(),GetTickCount64());
    RsSoftwareDeviceInfo info={};info.cbSize=sizeof(info);info.instanceId=instance;
    info.hardwareIds=L"MttVDD\0";info.compatibleIds=L"MttVDD\0";
    info.capabilities=1|2|8; // Removable, SilentInstall, DriverRequired. Lifetime remains handle-bound.
    info.description=L"DisplayWeave temporary projection fixture";
    HANDLE device=nullptr;
    const ULONGLONG lifetimeDeadline=GetTickCount64()+seconds*1000ull;
    HRESULT status=create(L"DisplayWeaveProjection",L"HTREE\\ROOT\\0",&info,0,nullptr,Created,&creation,&device);
    bool ready=false,preserved=true,startupSettled=false;std::wstring output;std::string stage="create";Ownership ownership;
    ReleasePhase release;Topology releaseLastSnapshot,releaseFirstTransition,establishedTopology;
    std::wstring releaseFailure;bool releaseJournalWritten=false;
    unsigned startupMismatchCount=0,startupSnapshotFailureCount=0;ULONGLONG startupDurationMs=0;
    std::wstring startupFirstFailure;Topology startupLastSnapshot;LONG startupLastSnapshotError=ERROR_SUCCESS;
    bool startupJournalWritten=false;
    std::wstring refreshCandidateFailure;
    std::wstring preserveFailure;bool physicalChangeJournalWritten=false;LONG preservationSnapshotError=ERROR_SUCCESS;
    auto physicalFailure=[&](const Topology& observed,LONG snapshotError)->std::wstring{
        return snapshotError==ERROR_SUCCESS?PreserveFailure(before,observed):
            L"physical.snapshot.unavailable (Windows error="+std::to_wstring(snapshotError)+L")";
    };
    auto recordPhysicalChange=[&](const Topology& observed,LONG snapshotError){
        if(physicalChangeJournalWritten && stage!="active")return true; // Preserve the first observation.
        const std::string journal="{\"stage\":\""+stage+"\",\"reason\":"+Json(preserveFailure)+
            ",\"snapshotWindowsError\":"+std::to_string(snapshotError)+",\"instanceId\":"+Json(creation.instance)+
            ",\"before\":"+TopologyJson(before)+",\"observed\":"+TopologyJson(observed)+"}";
        const bool written=Write(report+(physicalChangeJournalWritten?L".active-physical-change.json":L".physical-change.json"),journal);
        if(written)physicalChangeJournalWritten=true;
        else fprintf(stderr,"Cannot write physical change diagnostic journal\n");
        return written;
    };
    auto releaseJournal=[&](){
        const ULONGLONG now=GetTickCount64();
        const std::string data="{\"pid\":"+std::to_string(GetCurrentProcessId())+",\"instanceId\":"+Json(creation.instance)+
            ",\"outputDevice\":"+Json(output)+",\"begun\":"+(release.begun?"true":"false")+
            ",\"completed\":"+(release.completed?"true":"false")+",\"maxDurationMs\":5000,\"elapsedMs\":"+
            std::to_string(release.begun && now>=release.start?now-release.start:0)+
            ",\"transitionObserved\":"+(release.transitionObserved?"true":"false")+",\"transitionSamples\":"+
            std::to_string(release.transitionSamples)+",\"failure\":"+Json(releaseFailure)+
            ",\"before\":"+TopologyJson(before)+",\"firstTransition\":"+TopologyJson(releaseFirstTransition)+
            ",\"lastObserved\":"+TopologyJson(releaseLastSnapshot)+"}";
        releaseJournalWritten=Write(report+L".release.json",data);return releaseJournalWritten;
    };
    if(SUCCEEDED(status)){
        const ULONGLONG now=GetTickCount64();ULONGLONG remaining=now<lifetimeDeadline?lifetimeDeadline-now:0;
        DWORD wait=WaitForSingleObject(creation.event,static_cast<DWORD>(remaining<15000?remaining:15000));
        status=wait==WAIT_OBJECT_0?creation.result:HRESULT_FROM_WIN32(wait==WAIT_FAILED?GetLastError():ERROR_TIMEOUT);
    }
    if(SUCCEEDED(status)){
        if(!Write(report+L".created.json","{\"instanceId\":"+Json(creation.instance)+"}")) status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
    }
    if(SUCCEEDED(status)){
        stage="startup-settling";
        StartupSettle settle(GetTickCount64(),lifetimeDeadline);std::wstring previousOutput;
        StartupSettle refreshSettle(settle.start,settle.deadline);
        while(GetTickCount64()<settle.deadline){
            Topology now;LONG result=Snapshot(now,leaseQueryFlags);
            startupLastSnapshot=now;startupLastSnapshotError=result;
            preserveFailure=physicalFailure(now,result);preservationSnapshotError=result;
            const bool matches=preserveFailure.empty();OutputSearch search=OutputSearch::Pending;
            if(!matches){
                if(result==ERROR_SUCCESS)++startupMismatchCount;else ++startupSnapshotFailureCount;
                if(startupFirstFailure.empty())startupFirstFailure=preserveFailure;
                if(!recordPhysicalChange(now,result)){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);break;}
            }
            if(result==ERROR_SUCCESS){
                search=NewOwnedOutput(before,now,creation.instance,output,ownership);
                if(search==OutputSearch::Rejected){stage="verify-output-ownership";status=E_ACCESSDENIED;break;}
            }
            const bool sameOutput=previousOutput.empty() || _wcsicmp(previousOutput.c_str(),output.c_str())==0;
            const auto decision=settle.Observe(GetTickCount64(),matches,search==OutputSearch::Owned,sameOutput);
            previousOutput=output;
            if(decision==SettleResult::Ready){ready=true;startupSettled=true;establishedTopology=now;break;}
            if(decision==SettleResult::Expired)break;
            if(!refreshRestore.attempted){
                Topology refreshRequest;
                const bool eligible=result==ERROR_SUCCESS && !matches &&
                    RefreshRestoreCandidate(before,now,creation.instance,refreshRequest,refreshCandidateFailure);
                if(refreshSettle.Observe(GetTickCount64(),eligible,search==OutputSearch::Owned,sameOutput)==SettleResult::Ready){
                    // SetDisplayConfig uses all CURRENT paths/modes and changes
                    // original physical virtual-refresh rationals, optionally
                    // normalizing only the exactly owned advertised virtual
                    // source/desktop-image size if its initial packet is invalid.
                    // Validate then apply identical data; no ALLOW_CHANGES/SAVE.
                    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setdisplayconfig
                    stage="restore-physical-refresh";
                    Topology normalizedDiagnostic;std::wstring normalizationReason;
                    const bool haveNormalized=NormalizeOwnedVirtualCandidate(before,refreshRequest,creation.instance,settle.deadline,normalizedDiagnostic,normalizationReason);
                    const auto restoreJournal=[&](){return "{\"refreshRestore\":"+RefreshRestoreJson(refreshRestore,before)+
                        ",\"observedBefore\":"+TopologyJson(now)+",\"requested\":"+TopologyJson(refreshRequest)+
                        ",\"normalizationReason\":"+Json(normalizationReason)+",\"normalizedDiagnostic\":"+
                        (haveNormalized?TopologyJson(normalizedDiagnostic):"null")+",\"normalizedApplyAllowed\":true}";};
                    if(!Write(report+L".refresh-restore.json",restoreJournal())){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);break;}
                    const LONG restored=RestoreRefreshOnce(refreshRestore,refreshRequest,settle.deadline,SetDisplayConfig,GetTickCount64,
                        &now,haveNormalized?&normalizedDiagnostic:nullptr);
                    if(!Write(report+L".refresh-restore.json",restoreJournal())){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);break;}
                    if(restored!=ERROR_SUCCESS){status=HRESULT_FROM_WIN32(restored);break;}
                    // No ready result from the pre-transaction sample. The next
                    // samples must match the complete original baseline for 750ms.
                    settle.stable=false;settle.sampled=false;stage="startup-settling";
                }
            }
            const ULONGLONG sampledAt=GetTickCount64();
            if(sampledAt<settle.deadline)Sleep(static_cast<DWORD>((settle.deadline-sampledAt)<100?settle.deadline-sampledAt:100));
        }
        startupDurationMs=GetTickCount64()-settle.start;
        const std::string journal="{\"settled\":"+std::string(startupSettled?"true":"false")+
            ",\"instanceId\":"+Json(creation.instance)+
            ",\"queryFlags\":"+std::to_string(leaseQueryFlags)+",\"durationMs\":"+std::to_string(startupDurationMs)+
            ",\"maximumMs\":10000,\"requiredStableMs\":750,\"mismatchCount\":"+std::to_string(startupMismatchCount)+
            ",\"snapshotFailureCount\":"+std::to_string(startupSnapshotFailureCount)+",\"firstFailure\":"+Json(startupFirstFailure)+
            ",\"lastFailure\":"+Json(preserveFailure)+",\"lastSnapshotWindowsError\":"+std::to_string(startupLastSnapshotError)+
            ",\"refreshRestore\":"+RefreshRestoreJson(refreshRestore,before)+",\"refreshCandidateFailure\":"+Json(refreshCandidateFailure)+
            ",\"before\":"+TopologyJson(before)+",\"lastObserved\":"+TopologyJson(startupLastSnapshot)+"}";
        startupJournalWritten=Write(report+L".startup-settling.json",journal);
        if(!startupJournalWritten){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);ready=false;}
        if(!ready && SUCCEEDED(status))status=HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        if(!startupSettled)preserved=false;
    }
    if(ready){
        stage="active";
        std::string data="{\"pid\":"+std::to_string(GetCurrentProcessId())+",\"instanceId\":"+Json(creation.instance)+
            ",\"outputDevice\":"+Json(output)+",\"physicalPreserved\":true,\"physicalPreservedSinceReady\":true,\"startupSettled\":true"+
            ",\"physicalPreservedThroughoutStartup\":"+((startupMismatchCount==0 && startupSnapshotFailureCount==0)?"true":"false")+
            ",\"startupMismatchCount\":"+std::to_string(startupMismatchCount)+",\"startupSnapshotFailureCount\":"+
            std::to_string(startupSnapshotFailureCount)+",\"startupDurationMs\":"+std::to_string(startupDurationMs)+
            ",\"queryFlags\":"+std::to_string(leaseQueryFlags)+",\"primaryAndWorkAreaObserved\":true,\"advancedColorObserved\":"+
            (ColorObserved(before)?"true":"false")+",\"ownershipVerified\":true,\"adapterInstanceId\":"+Json(ownership.adapterInstance)+
            ",\"refreshRestore\":"+RefreshRestoreJson(refreshRestore,before)+
            ",\"releaseProtocol\":{\"beginEvent\":"+Json(releaseEvents.beginName)+",\"completeEvent\":"+
            Json(releaseEvents.completeName)+",\"ackEvent\":"+Json(releaseEvents.ackName)+",\"maxDurationMs\":5000}"+
            ",\"seconds\":"+std::to_string(seconds)+"}";
        if(!Write(report+L".ready.json",data)){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);ready=false;}
        ULONGLONG until=lifetimeDeadline;
        while(ready && GetTickCount64()<until){
            const DWORD beginSignal=WaitForSingleObject(releaseEvents.begin,0);
            const DWORD beginError=beginSignal==WAIT_FAILED?GetLastError():ERROR_SUCCESS;
            const DWORD completeSignal=WaitForSingleObject(releaseEvents.complete,0);
            const DWORD completeError=completeSignal==WAIT_FAILED?GetLastError():ERROR_SUCCESS;
            if(beginSignal==WAIT_FAILED || completeSignal==WAIT_FAILED){
                stage="fixture-release";status=HRESULT_FROM_WIN32(beginError?beginError:completeError);releaseFailure=L"release.eventWaitFailed";releaseJournal();break;
            }
            const auto releaseDecision=release.Observe(GetTickCount64(),lifetimeDeadline,beginSignal==WAIT_OBJECT_0,completeSignal==WAIT_OBJECT_0);
            if(releaseDecision==ReleaseDecision::Rejected || releaseDecision==ReleaseDecision::Expired){
                stage="fixture-release";status=HRESULT_FROM_WIN32(releaseDecision==ReleaseDecision::Expired?ERROR_TIMEOUT:ERROR_INVALID_STATE);
                releaseFailure=releaseDecision==ReleaseDecision::Expired?L"release.deadline":L"release.invalidSignalOrder";releaseJournal();break;
            }
            Topology now;LONG result=Snapshot(now,leaseQueryFlags);
            if(release.begun){
                stage="fixture-release";releaseLastSnapshot=now;bool transition=false;
                if(result!=ERROR_SUCCESS || !ReleaseTopologyAllowed(before,now,creation.instance,transition,releaseFailure,AdapterOwnership,&establishedTopology)){
                    preserved=false;preservationSnapshotError=result;preserveFailure=physicalFailure(now,result);
                    if(result!=ERROR_SUCCESS)releaseFailure=L"release.snapshotUnavailable";
                    status=E_UNEXPECTED;releaseJournal();break;
                }
                if(GetTickCount64()>=release.deadline){status=HRESULT_FROM_WIN32(ERROR_TIMEOUT);releaseFailure=L"release.deadline";releaseJournal();break;}
                if(transition){
                    if(!release.transitionObserved)releaseFirstTransition=now;
                    release.transitionObserved=true;++release.transitionSamples;preserved=false;
                    preserveFailure=physicalFailure(now,result);
                }
                // COMPLETE is committed only after the same exact ownership and
                // physical-state gate passes for this final release observation.
                if(releaseDecision==ReleaseDecision::Complete)release.completed=true;
                if(!releaseJournal()){status=HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);break;}
                // Focus/cursor cleanup may start only after this durable BEGIN
                // acknowledgement; failures above never signal readiness.
                if(releaseDecision==ReleaseDecision::Begun && !SetEvent(releaseEvents.ack)){
                    status=HRESULT_FROM_WIN32(GetLastError());releaseFailure=L"release.ackSignalFailed";releaseJournal();break;
                }
                if(release.completed)break;
                Sleep(100);continue;
            }
            if(result!=ERROR_SUCCESS || !Preserved(before,now)){
                preserved=false;preservationSnapshotError=result;preserveFailure=physicalFailure(now,result);
                recordPhysicalChange(now,result);status=E_UNEXPECTED;break;
            }
            std::wstring currentOutput;Ownership currentOwnership;
            if(NewOwnedOutput(before,now,creation.instance,currentOutput,currentOwnership)!=OutputSearch::Owned ||
                _wcsicmp(currentOutput.c_str(),output.c_str())){
                ownership=currentOwnership;ownership.verified=false;stage="verify-output-ownership";status=E_ACCESSDENIED;break;
            }
            Sleep(100);
        }
        if(release.begun && !release.completed && SUCCEEDED(status)){
            stage="fixture-release";status=HRESULT_FROM_WIN32(ERROR_TIMEOUT);releaseFailure=L"release.lifetimeDeadline";releaseJournal();
        }
    }
    if(device)close(device); // Also joins an in-progress callback, including timed-out creation.
    CloseHandle(creation.event);FreeLibrary(library);
    bool removed=false;
    ULONGLONG removalDeadline=GetTickCount64()+10000;
    while(GetTickCount64()<removalDeadline){
        Topology now;
        if(Snapshot(now,leaseQueryFlags)==ERROR_SUCCESS && now.paths.size()==before.paths.size() && Preserved(before,now)){removed=true;break;}
        Sleep(100);
    }
    char code[16];snprintf(code,sizeof(code),"0x%08lX",static_cast<unsigned long>(status));
    bool success=SUCCEEDED(status) && ready && removed && (preserved || release.completed) && (!release.begun || release.completed);
    std::string result="{\"success\":"+std::string(success?"true":"false")+",\"stage\":\""+stage+
        "\",\"hresult\":\""+code+"\",\"instanceId\":"+Json(creation.instance)+",\"outputDevice\":"+Json(output)+
        ",\"physicalPreserved\":"+(preserved?"true":"false")+",\"primaryAndWorkAreaObserved\":true,\"advancedColorObserved\":"+
        (ColorObserved(before)?"true":"false")+",\"physicalPreservedSinceReady\":"+((ready && preserved)?"true":"false")+
        ",\"startupSettled\":"+(startupSettled?"true":"false")+",\"physicalPreservedThroughoutStartup\":"+
        ((startupSettled && startupMismatchCount==0 && startupSnapshotFailureCount==0)?"true":"false")+
        ",\"startupMismatchCount\":"+std::to_string(startupMismatchCount)+",\"startupSnapshotFailureCount\":"+
        std::to_string(startupSnapshotFailureCount)+",\"startupDurationMs\":"+std::to_string(startupDurationMs)+
        ",\"startupFirstFailure\":"+Json(startupFirstFailure)+",\"startupJournalWritten\":"+(startupJournalWritten?"true":"false")+
        ",\"refreshRestore\":"+RefreshRestoreJson(refreshRestore,before)+",\"refreshCandidateFailure\":"+Json(refreshCandidateFailure)+
        ",\"releaseCompleted\":"+(release.completed?"true":"false")+",\"releaseTransitionObserved\":"+(release.transitionObserved?"true":"false")+
        ",\"releaseTransitionSamples\":"+std::to_string(release.transitionSamples)+",\"releaseJournalWritten\":"+(releaseJournalWritten?"true":"false")+
        ",\"releaseFailure\":"+Json(releaseFailure)+",\"physicalRestoredAfterRelease\":"+((release.completed && removed)?"true":"false")+
        ",\"queryFlags\":"+std::to_string(leaseQueryFlags)+",\"legacyQueryFallback\":"+(legacyQueryFallback?"true":"false")+
        ",\"modernQueryError\":"+std::to_string(modernQueryError)+",\"ownershipVerified\":"+(ownership.verified?"true":"false")+
        ",\"preserveFailure\":"+Json(preserveFailure)+",\"preservationSnapshotError\":"+std::to_string(preservationSnapshotError)+
        ",\"physicalChangeJournalWritten\":"+(physicalChangeJournalWritten?"true":"false")+
        ",\"regionHookLoaded\":"+(GetModuleHandleW(L"RegionHook.dll")?"true":"false")+
        ",\"ownershipDisplayError\":"+std::to_string(ownership.displayError)+",\"ownershipConfigurationError\":"+
        std::to_string(ownership.configurationError)+",\"adapterInstanceId\":"+Json(ownership.adapterInstance)+
        ",\"originalTopologyObservedAfterClose\":"+(removed?"true":"false")+"}";
    const bool written=Write(report,result);puts(result.c_str());
    if(!written)fprintf(stderr,"Cannot write final fixture report\n");return success && written?0:1;
}
