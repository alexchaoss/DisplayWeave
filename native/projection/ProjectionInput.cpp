#include "ProjectionInput.h"
#include <sddl.h>
#include <tlhelp32.h>
#include <objbase.h>
#include <algorithm>
#include <climits>
#include <cstdint>
#include <cwchar>
#include <memory>

namespace projection_input {
namespace policy {
bool ValidRect(const RECT& r) { return r.right > r.left && r.bottom > r.top; }
bool SameRect(const RECT& a,const RECT& b) { return a.left==b.left && a.top==b.top && a.right==b.right && a.bottom==b.bottom; }
bool Contains(const RECT& r,POINT p) { return ValidRect(r) && p.x>=r.left && p.x<r.right && p.y>=r.top && p.y<r.bottom; }
bool Contains(const RECT& a,const RECT& b) { return ValidRect(a) && ValidRect(b) && b.left>=a.left && b.top>=a.top && b.right<=a.right && b.bottom<=a.bottom; }
bool MapPoint(const RECT& from,const RECT& to,POINT input,POINT& output) {
    if(!Contains(from,input) || !ValidRect(to)) return false;
    // Ratios use 64 bits and half-open pixel bounds, including negative origins.
    const uint64_t fw=static_cast<int64_t>(from.right)-from.left,fh=static_cast<int64_t>(from.bottom)-from.top;
    const uint64_t tw=static_cast<int64_t>(to.right)-to.left,th=static_cast<int64_t>(to.bottom)-to.top;
    output.x=static_cast<LONG>(static_cast<int64_t>(to.left)+(static_cast<uint64_t>(static_cast<int64_t>(input.x)-from.left)*tw)/fw);
    output.y=static_cast<LONG>(static_cast<int64_t>(to.top)+(static_cast<uint64_t>(static_cast<int64_t>(input.y)-from.top)*th)/fh);
    return Contains(to,output);
}
bool MayRestoreClip(bool owned,const RECT& current,const RECT& expected) { return owned && SameRect(current,expected); }
bool MayRestoreCursor(bool owned,POINT current,const RECT& source,POINT destination,const RECT& clip) {
    return owned && Contains(source,current) && Contains(clip,destination);
}
bool ClaimCleanup(volatile LONG& state,bool repairLateMutation) {
    // Shared states: idle0, armed1, cleaning2, cleaned3, cancelled4.
    if(InterlockedCompareExchange(&state,2,1)==1)return true;
    return repairLateMutation && InterlockedCompareExchange(&state,2,3)==3;
}
// The lease rejects a repeated BEGIN, so presenter and guardian share one claim.
bool ClaimLeaseRelease(volatile LONG& claim) { return InterlockedCompareExchange(&claim,1,0)==0; }
FocusReturnDecision DecideFocusReturn(bool sourceForeground,bool sourceIdentity,bool returnIdentity,bool outputUnchanged,bool inputDesktop) {
    if(!sourceForeground)return FocusReturnDecision::FocusChanged;
    if(!sourceIdentity)return FocusReturnDecision::SourceUnavailable;
    if(!inputDesktop)return FocusReturnDecision::DesktopChanged;
    if(!returnIdentity)return FocusReturnDecision::ReturnUnavailable;
    if(!outputUnchanged)return FocusReturnDecision::OutputChanged;
    return FocusReturnDecision::Attempt;
}
FocusAcknowledgment DecideFocusAcknowledgment(bool accepted,UINT_PTR current,UINT_PTR source,UINT_PTR destination,bool expired) {
    if(current==destination)return FocusAcknowledgment::Reached;
    if(current && current!=source)return FocusAcknowledgment::ForegroundChanged;
    if(!accepted)return FocusAcknowledgment::Denied;
    return expired?FocusAcknowledgment::TimedOut:FocusAcknowledgment::Wait;
}
static bool CleanupAttemptFinished(LONG state,bool mutationPossible) {
    // A completed attempt publishes its flags before state3. A cancelled,
    // never-mutated entry needs no restore. State2 is never evidence of success.
    return state==3 || (state==4 && !mutationPossible);
}
} // namespace policy
bool ValidLeaseReleaseEventName(const std::wstring& name,const wchar_t* suffix) {
    static const std::wstring prefix=L"Local\\DisplayWeaveProjectionRelease-";
    const std::wstring ending=std::wstring(L"-")+suffix;
    if(name.size()>120 || name.size()<=prefix.size()+ending.size() || name.compare(0,prefix.size(),prefix) ||
        name.compare(name.size()-ending.size(),ending.size(),ending))return false;
    for(size_t i=prefix.size();i<name.size()-ending.size();++i){
        const wchar_t c=name[i];
        if(!((c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f')||(c>=L'A'&&c<=L'F')||c==L'-'))return false;
    }
    return true;
}
namespace policy {
std::vector<std::string> Tests(unsigned& count) {
    std::vector<std::string> failures;
    auto check=[&](bool result,const char* name){++count;if(!result)failures.emplace_back(name);};
    RECT image{900,100,2180,820},source{-1280,0,0,720},same=source;
    POINT mapped{};
    check(ValidRect(source),"negative-origin valid source");
    check(!ValidRect({1,2,1,3}),"zero-width rectangle rejected");
    check(!ValidRect({0,3,2,2}),"inverted rectangle rejected");
    check(MapPoint(image,source,{900,100},mapped)&&mapped.x==-1280&&mapped.y==0,"map top-left");
    check(MapPoint(image,source,{2179,819},mapped)&&mapped.x==-1&&mapped.y==719,"map last pixel");
    check(MapPoint(image,source,{1540,460},mapped)&&mapped.x==-640&&mapped.y==360,"map center");
    check(!MapPoint(image,source,{2180,460},mapped),"right boundary is exclusive");
    check(!MapPoint(image,source,{1540,820},mapped),"bottom boundary is exclusive");
    check(!MapPoint(image,source,{899,100},mapped),"left outside rejected");
    check(!MapPoint(image,{0,0,0,1},{900,100},mapped),"invalid target rejected");
    check(MapPoint({0,0,3,3},{-10,-20,-3,-13},{2,2},mapped)&&mapped.x==-6&&mapped.y==-16,"odd-sized mapping");
    check(MapPoint({LONG_MIN,LONG_MIN,LONG_MAX,LONG_MAX},{LONG_MIN,LONG_MIN,LONG_MAX,LONG_MAX},{LONG_MAX-1,LONG_MAX-1},mapped)&&mapped.x==LONG_MAX-1,"full LONG range does not overflow");
    check(Contains(source,source),"same rectangle contained");
    check(!Contains(source,{-1280,0,1,720}),"outside rectangle rejected");
    check(MayRestoreClip(true,same,source),"owned unchanged clip restored");
    check(!MayRestoreClip(false,same,source),"preexisting application clip is not ours");
    check(!MayRestoreClip(true,{-1200,20,-20,700},source),"narrower application clip preserved");
    check(!MayRestoreClip(true,{-2000,0,100,800},source),"wider replacement clip preserved");
    RECT desktop{-1280,0,7680,2160};POINT returnPoint{1540,460};
    check(MayRestoreCursor(true,{-640,360},source,returnPoint,desktop),"owned cursor returns to physical image");
    check(!MayRestoreCursor(false,{-640,360},source,returnPoint,desktop),"unchanged cursor not moved");
    check(!MayRestoreCursor(true,{10,20},source,returnPoint,desktop),"user cursor moved elsewhere preserved");
    check(!MayRestoreCursor(true,{-640,360},source,returnPoint,source),"foreign clip blocking return respected");
    volatile LONG state=1;
    check(ClaimCleanup(state,false)&&state==2,"one cleanup actor claims armed state");
    check(!ClaimCleanup(state,false)&&state==2,"second cleanup actor cannot race first");
    state=3;check(!ClaimCleanup(state,false)&&state==3,"ordinary repeated cleanup is idempotent");
    check(ClaimCleanup(state,true)&&state==2,"late mutation after guardian cleanup gets a fresh claim");
    state=2;check(!ClaimCleanup(state,true)&&state==2,"late repair waits for active cleanup");
    state=0;check(!ClaimCleanup(state,true)&&state==0,"unarmed session is never restored");
    state=4;check(!ClaimCleanup(state,true)&&state==4,"cancelled entry is never restored");
    check(!CleanupAttemptFinished(2,true),"guardian still cleaning is incomplete after bounded wait");
    check(!CleanupAttemptFinished(1,true),"armed state is not cleanup evidence");
    check(!CleanupAttemptFinished(0,false),"idle entry requires explicit cancellation");
    check(CleanupAttemptFinished(3,true),"completed restore publishes a finished attempt");
    check(CleanupAttemptFinished(4,false),"cancelled unmodified entry needs no restore");
    check(!CleanupAttemptFinished(4,true),"cancelled state cannot hide a possible mutation");
    check(DecideFocusReturn(true,true,true,true,true)==FocusReturnDecision::Attempt,"exact source focus and valid physical return allow one normal request");
    check(DecideFocusReturn(false,true,true,true,true)==FocusReturnDecision::FocusChanged,"another foreground window is never replaced");
    check(DecideFocusReturn(false,false,false,false,false)==FocusReturnDecision::FocusChanged,"changed user focus takes precedence over stale identities");
    check(DecideFocusReturn(true,false,true,true,true)==FocusReturnDecision::SourceUnavailable,"reused source PID or HWND cannot authorize focus return");
    check(DecideFocusReturn(true,true,false,true,true)==FocusReturnDecision::ReturnUnavailable,"dead or changed return window rejected");
    check(DecideFocusReturn(true,true,true,false,true)==FocusReturnDecision::OutputChanged,"changed physical output cannot receive automatic focus");
    check(DecideFocusReturn(true,true,true,true,false)==FocusReturnDecision::DesktopChanged,"different input desktop prevents focus change");
    check(DecideFocusAcknowledgment(true,1,1,2,false)==FocusAcknowledgment::Wait,"accepted asynchronous source-to-target change is observed");
    check(DecideFocusAcknowledgment(true,0,1,2,false)==FocusAcknowledgment::Wait,"transitional null foreground is observed within bound");
    check(DecideFocusAcknowledgment(true,2,1,2,true)==FocusAcknowledgment::Reached,"observed destination acknowledges activation");
    check(DecideFocusAcknowledgment(false,3,1,2,false)==FocusAcknowledgment::ForegroundChanged,"unrelated user focus stops activation observation");
    check(DecideFocusAcknowledgment(false,1,1,2,false)==FocusAcknowledgment::Denied,"actual false activation result is distinguished");
    check(DecideFocusAcknowledgment(true,1,1,2,true)==FocusAcknowledgment::TimedOut,"accepted request with source unchanged times out");
    check(DecideFocusAcknowledgment(true,0,1,2,true)==FocusAcknowledgment::TimedOut,"transitional foreground observation remains bounded");
    volatile LONG release=0;
    check(ClaimLeaseRelease(release)&&release==1,"first cleanup actor claims the single lease BEGIN");
    check(!ClaimLeaseRelease(release)&&release==1,"second actor never repeats BEGIN");
    const std::wstring begin=L"Local\\DisplayWeaveProjectionRelease-1234-00112233445566778899aabbccddeeff-Begin";
    check(ValidLeaseReleaseEventName(begin,L"Begin"),"lease BEGIN event name accepted");
    check(!ValidLeaseReleaseEventName(begin,L"Ack"),"suffix must match the event role");
    check(!ValidLeaseReleaseEventName(L"Global\\DisplayWeaveProjectionRelease-1-ab-Begin",L"Begin"),"global namespace rejected");
    check(!ValidLeaseReleaseEventName(L"Local\\DisplayWeaveProjectionRelease-1-a\\b-Begin",L"Begin"),"path separators rejected");
    check(!ValidLeaseReleaseEventName(L"Local\\DisplayWeaveProjectionRelease--Begin",L"Begin"),"empty lease identity rejected");
    check(!ValidLeaseReleaseEventName(begin+std::wstring(80,L'a')+L"-Begin",L"Begin"),"overlong name rejected");
    return failures;
}
} // namespace policy

namespace {
constexpr LONG kMagic=0x52534931;
constexpr LONG kVersion=3;
constexpr int kReleaseHotkey=0x52a1;
constexpr DWORD kHeartbeatTimeout=2500;
constexpr DWORD kMaximumDuration=600000;
enum { StateIdle=0,StateArmed=1,StateCleaning=2,StateCleaned=3,StateCancelled=4 };
enum { RestoredClip=1,RestoredCursor=2,Incomplete=4,ForeignClip=8 };
enum { FocusNotRequested=0,FocusReturned=1,FocusChanged=2,FocusSourceUnavailable=3,FocusReturnUnavailable=4,FocusOutputChanged=5,FocusDesktopChanged=6,FocusDenied=7,FocusAcceptedTimeout=8 };
struct Shared {
    LONG magic,version,size;
    DWORD parentPid,sourcePid,sourceTid,returnPid,returnTid;
    FILETIME parentCreated,sourceCreated,returnCreated;
    ULONGLONG started,deadline;
    UINT64 sourceWindow,ownerWindow,returnWindow;
    RECT source,ownedClip,originalClip;
    RECT returnOutputBounds,returnOutputWork;
    DWORD returnOutputFlags;
    wchar_t returnOutputName[CCHDEVICENAME];
    POINT returnCursor;
    volatile LONG state,ready,heartbeat,clipOwned,cursorOwned,cleanupFlags,cleanupError,guardianRecovered;
    volatile LONG guardianFocusAttempted,guardianFocusRestored,guardianFocusReason,guardianFocusError,guardianFocusAccepted;
    wchar_t releaseBeginName[128],releaseAckName[128];
    volatile LONG releaseClaim,releaseAcked,releaseError;
};
LONG Read(volatile LONG* value) { return InterlockedCompareExchange(value,0,0); }
bool SameTime(FILETIME a,FILETIME b) { return a.dwLowDateTime==b.dwLowDateTime && a.dwHighDateTime==b.dwHighDateTime; }
bool ProcessCreation(HANDLE process,FILETIME& created) { FILETIME exit{},kernel{},user{};return GetProcessTimes(process,&created,&exit,&kernel,&user)!=FALSE; }
POINT Center(const RECT& r) { return {static_cast<LONG>(static_cast<int64_t>(r.left)+(static_cast<int64_t>(r.right)-r.left)/2),static_cast<LONG>(static_cast<int64_t>(r.top)+(static_cast<int64_t>(r.bottom)-r.top)/2)}; }
DWORD ErrorOr(DWORD fallback) { DWORD error=GetLastError();return error?error:fallback; }
constexpr DWORD kLeaseAckWaitMs=750;
// Runs inside a claimed cleanup, before focus or cursor restoration. A missing
// ACK is reported but never blocks restoring the user's own input state.
void BeginLeaseRelease(Shared* shared,HANDLE begin,HANDLE ack) {
    if(!shared || !begin || !ack)return;
    if(policy::ClaimLeaseRelease(shared->releaseClaim) && !SetEvent(begin)){
        InterlockedCompareExchange(&shared->releaseError,static_cast<LONG>(ErrorOr(ERROR_GEN_FAILURE)),0);return;
    }
    const DWORD wait=WaitForSingleObject(ack,kLeaseAckWaitMs);
    if(wait==WAIT_OBJECT_0)InterlockedExchange(&shared->releaseAcked,1);
    else InterlockedCompareExchange(&shared->releaseError,static_cast<LONG>(wait==WAIT_TIMEOUT?ERROR_TIMEOUT:ErrorOr(ERROR_GEN_FAILURE)),0);
}

bool WindowIdentity(HWND hwnd,DWORD pid,DWORD tid) {
    DWORD actual=0;DWORD thread=GetWindowThreadProcessId(hwnd,&actual);
    return thread && actual==pid && (!tid || thread==tid) && IsWindow(hwnd);
}
bool SourceClient(HWND hwnd,RECT& result) {
    if(!GetClientRect(hwnd,&result))return false;
    POINT point{};if(!ClientToScreen(hwnd,&point))return false;
    return OffsetRect(&result,point.x,point.y)!=FALSE && policy::ValidRect(result);
}
bool IsShell(HWND hwnd,DWORD pid) {
    for(HWND shell:{GetShellWindow(),GetDesktopWindow()}){DWORD owner=0;GetWindowThreadProcessId(shell,&owner);if(hwnd==shell || (owner && owner==pid))return true;}
    wchar_t name[128]{};if(!GetClassNameW(hwnd,name,128))return true;
    for(const wchar_t* reserved:{L"Progman",L"WorkerW",L"Shell_TrayWnd",L"Shell_SecondaryTrayWnd",L"SHELLDLL_DefView"})if(!_wcsicmp(name,reserved))return true;
    return false;
}
struct Monitor { std::wstring name;RECT bounds,work;DWORD flags; };
BOOL CALLBACK CollectMonitor(HMONITOR monitor,HDC,LPRECT,LPARAM parameter) {
    auto& values=*reinterpret_cast<std::vector<Monitor>*>(parameter);
    MONITORINFOEXW info{};info.cbSize=sizeof(info);
    if(values.size()>=128 || !GetMonitorInfoW(monitor,&info))return FALSE;
    values.push_back({info.szDevice,info.rcMonitor,info.rcWork,info.dwFlags});return TRUE;
}
bool Topology(std::vector<Monitor>& values) {
    values.clear();if(!EnumDisplayMonitors(nullptr,nullptr,CollectMonitor,reinterpret_cast<LPARAM>(&values)) || values.empty())return false;
    std::sort(values.begin(),values.end(),[](const Monitor& a,const Monitor& b){return a.name<b.name;});return true;
}
bool SameTopology(const std::vector<Monitor>& a,const std::vector<Monitor>& b) {
    if(a.size()!=b.size())return false;
    for(size_t i=0;i<a.size();++i)if(a[i].name!=b[i].name || a[i].flags!=b[i].flags || !policy::SameRect(a[i].bounds,b[i].bounds) || !policy::SameRect(a[i].work,b[i].work))return false;
    return true;
}
bool DistinctOutputs(const std::vector<Monitor>& values,const RECT& source,const RECT& image) {
    int sourceIndex=-1,imageIndex=-1;
    for(size_t i=0;i<values.size();++i){
        if(policy::Contains(values[i].bounds,source)){if(sourceIndex!=-1)return false;sourceIndex=static_cast<int>(i);}
        if(policy::Contains(values[i].bounds,image)){if(imageIndex!=-1)return false;imageIndex=static_cast<int>(i);}
    }
    return sourceIndex>=0 && imageIndex>=0 && sourceIndex!=imageIndex;
}
bool OwnInputDesktop() {
    HDESK input=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);
    if(!input)return false;
    wchar_t active[256]{},own[256]{};DWORD needed=0;
    bool okay=GetUserObjectInformationW(input,UOI_NAME,active,sizeof(active),&needed) &&
        GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,own,sizeof(own),&needed) && !wcscmp(active,own);
    CloseDesktop(input);return okay;
}

bool LiveIdentity(HANDLE process,DWORD pid,FILETIME created,HWND window,DWORD thread) {
    FILETIME actual{};
    return process && WaitForSingleObject(process,0)==WAIT_TIMEOUT && GetProcessId(process)==pid &&
        ProcessCreation(process,actual) && SameTime(actual,created) && WindowIdentity(window,pid,thread);
}
bool WindowOnOutput(HWND window,const Monitor& output) {
    RECT bounds{},intersection{};
    if(!IsWindowVisible(window) || IsIconic(window) || GetAncestor(window,GA_ROOT)!=window || !GetWindowRect(window,&bounds) ||
        !IntersectRect(&intersection,&bounds,&output.bounds))return false;
    MONITORINFOEXW actual{};actual.cbSize=sizeof(actual);
    return GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONULL),&actual) && output.name==actual.szDevice &&
        policy::SameRect(output.bounds,actual.rcMonitor) && policy::SameRect(output.work,actual.rcWork) && output.flags==actual.dwFlags;
}
Monitor ReturnOutput(const Shared* shared) {
    return {shared->returnOutputName,shared->returnOutputBounds,shared->returnOutputWork,shared->returnOutputFlags};
}
bool OutputUnchanged(const Shared* shared,HWND window) {
    std::vector<Monitor> values;if(!Topology(values))return false;
    const Monitor saved=ReturnOutput(shared);unsigned matches=0;
    for(const auto& current:values)if(current.name==saved.name){
        if(!policy::SameRect(current.bounds,saved.bounds) || !policy::SameRect(current.work,saved.work) || current.flags!=saved.flags)return false;
        ++matches;
    }
    return matches==1 && WindowOnOutput(window,saved);
}
struct FocusResult { bool attempted=false,accepted=false,restored=false,incomplete=false;DWORD error=ERROR_SUCCESS;LONG reason=FocusNotRequested; };
const char* FocusReason(LONG reason) {
    switch(reason){
    case FocusReturned:return "returned";
    case FocusChanged:return "foreground_changed";
    case FocusSourceUnavailable:return "source_identity_unavailable";
    case FocusReturnUnavailable:return "return_window_unavailable";
    case FocusOutputChanged:return "physical_return_output_changed";
    case FocusDesktopChanged:return "input_desktop_changed";
    case FocusDenied:return "windows_focus_request_denied";
    case FocusAcceptedTimeout:return "accepted_activation_timeout";
    default:return "not_requested";
    }
}
FocusResult RestoreFocusWindow(Shared* shared,HANDLE sourceProcess,HANDLE returnProcess) {
    FocusResult result;
    HWND source=reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->sourceWindow));
    HWND destination=reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->returnWindow));
    const bool foreground=GetForegroundWindow()==source;
    const bool sourceLive=foreground && LiveIdentity(sourceProcess,shared->sourcePid,shared->sourceCreated,source,shared->sourceTid);
    const bool desktop=sourceLive && OwnInputDesktop();
    const bool returnLive=desktop && destination!=source && LiveIdentity(returnProcess,shared->returnPid,shared->returnCreated,destination,shared->returnTid) &&
        IsWindowVisible(destination) && !IsIconic(destination);
    const bool output=returnLive && OutputUnchanged(shared,destination);
    switch(policy::DecideFocusReturn(foreground,sourceLive,returnLive,output,desktop)){
    case policy::FocusReturnDecision::FocusChanged:result.reason=FocusChanged;return result;
    case policy::FocusReturnDecision::SourceUnavailable:result.reason=FocusSourceUnavailable;return result;
    case policy::FocusReturnDecision::ReturnUnavailable:result.reason=FocusReturnUnavailable;result.incomplete=true;result.error=ERROR_INVALID_WINDOW_HANDLE;return result;
    case policy::FocusReturnDecision::OutputChanged:result.reason=FocusOutputChanged;result.incomplete=true;result.error=ERROR_INVALID_DATA;return result;
    case policy::FocusReturnDecision::DesktopChanged:result.reason=FocusDesktopChanged;result.incomplete=true;result.error=ERROR_ACCESS_DENIED;return result;
    case policy::FocusReturnDecision::Attempt:break;
    }
    // Recheck after the read-only validation. There is no atomic Windows API
    // combining a foreground comparison with activation; never retry or bypass
    // the ordinary foreground policy if the user switches windows here.
    if(GetForegroundWindow()!=source){result.reason=FocusChanged;return result;}
    if(!LiveIdentity(sourceProcess,shared->sourcePid,shared->sourceCreated,source,shared->sourceTid) ||
        !LiveIdentity(returnProcess,shared->returnPid,shared->returnCreated,destination,shared->returnTid)){
        result.reason=FocusReturnUnavailable;result.incomplete=true;result.error=ERROR_INVALID_WINDOW_HANDLE;return result;
    }
    if(GetForegroundWindow()!=source){result.reason=FocusChanged;return result;}
    result.attempted=true;result.accepted=SetForegroundWindow(destination)!=FALSE;
    // Cross-input-queue activation can complete after a successful API return.
    // Observe at most250ms without issuing another activation or input request.
    const ULONGLONG acknowledgmentDeadline=GetTickCount64()+250;
    for(;;){
        const ULONGLONG now=GetTickCount64();
        const auto decision=policy::DecideFocusAcknowledgment(result.accepted,reinterpret_cast<UINT_PTR>(GetForegroundWindow()),
            reinterpret_cast<UINT_PTR>(source),reinterpret_cast<UINT_PTR>(destination),now>=acknowledgmentDeadline);
        if(decision==policy::FocusAcknowledgment::Reached){result.restored=true;result.reason=FocusReturned;break;}
        if(decision==policy::FocusAcknowledgment::ForegroundChanged){result.reason=FocusChanged;return result;}
        if(decision==policy::FocusAcknowledgment::Denied){result.reason=FocusDenied;result.incomplete=true;result.error=ERROR_ACCESS_DENIED;return result;}
        if(decision==policy::FocusAcknowledgment::TimedOut){result.reason=FocusAcceptedTimeout;result.incomplete=true;result.error=ERROR_TIMEOUT;return result;}
        Sleep(static_cast<DWORD>(std::min<ULONGLONG>(10,acknowledgmentDeadline-now)));
    }
    // Give the source a bounded opportunity to release its own clip in response
    // to deactivation. Observations only: never overwrite a foreign clip.
    const ULONGLONG until=GetTickCount64()+200;
    while(GetTickCount64()<until && GetForegroundWindow()==destination){
        RECT clip{};
        if(!GetClipCursor(&clip) || policy::Contains(clip,shared->returnCursor))break;
        Sleep(10);
    }
    return result;
}
void PublishGuardianFocus(Shared* shared,const FocusResult& result) {
    InterlockedExchange(&shared->guardianFocusAttempted,result.attempted?1:0);
    InterlockedExchange(&shared->guardianFocusRestored,result.restored?1:0);
    InterlockedExchange(&shared->guardianFocusAccepted,result.accepted?1:0);
    InterlockedExchange(&shared->guardianFocusError,static_cast<LONG>(result.error));
    InterlockedExchange(&shared->guardianFocusReason,result.reason);
}

// Focus recovery, if authorized by the exact still-foreground source, runs
// before this routine. Cursor restoration only changes our unchanged settings.
void RestoreCursorState(Shared* shared,LONG flags=0,DWORD error=ERROR_SUCCESS) {
    RECT clip{};POINT cursor{};
    if(!OwnInputDesktop() || !GetClipCursor(&clip)){flags|=Incomplete;error=ErrorOr(ERROR_ACCESS_DENIED);}
    else {
        if(policy::MayRestoreClip(Read(&shared->clipOwned)!=0,clip,shared->ownedClip)){
            if(ClipCursor(&shared->originalClip)){flags|=RestoredClip;}
            else {flags|=Incomplete;error=ErrorOr(ERROR_GEN_FAILURE);}
        }else if(policy::SameRect(clip,shared->originalClip))flags|=RestoredClip;
        else flags|=ForeignClip;
        if(!GetClipCursor(&clip) || !GetCursorPos(&cursor)){flags|=Incomplete;if(!error)error=ErrorOr(ERROR_GEN_FAILURE);}
        else {
            if(policy::MayRestoreCursor(Read(&shared->cursorOwned)!=0,cursor,shared->source,shared->returnCursor,clip)){
                if(SetCursorPos(shared->returnCursor.x,shared->returnCursor.y))flags|=RestoredCursor;
                else {flags|=Incomplete;if(!error)error=ErrorOr(ERROR_GEN_FAILURE);}
            }
            if(!policy::Contains(clip,shared->returnCursor))flags|=Incomplete;
            if((flags&RestoredCursor) && (!GetCursorPos(&cursor) || cursor.x!=shared->returnCursor.x || cursor.y!=shared->returnCursor.y))flags|=Incomplete;
        }
    }
    InterlockedExchange(&shared->cleanupError,static_cast<LONG>(error));
    InterlockedExchange(&shared->cleanupFlags,flags);
    InterlockedExchange(&shared->state,StateCleaned);
}
bool PrivateSecurity(PSECURITY_DESCRIPTOR& descriptor) {
    HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return false;
    DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);
    std::vector<BYTE> data(bytes);
    bool okay=bytes && GetTokenInformation(token,TokenUser,data.data(),bytes,&bytes);
    CloseHandle(token);if(!okay)return false;
    wchar_t* sid=nullptr;
    if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid,&sid))return false;
    std::wstring sddl=L"D:P(A;;GA;;;SY)(A;;GA;;;";sddl+=sid;sddl+=L")";LocalFree(sid);
    return ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr)!=FALSE;
}
bool ImagePath(HANDLE process,std::wstring& path) {
    std::vector<wchar_t> value(32768);DWORD size=static_cast<DWORD>(value.size());
    if(!QueryFullProcessImageNameW(process,0,value.data(),&size))return false;
    path.assign(value.data(),size);return true;
}
DWORD ActualParent() {
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snapshot==INVALID_HANDLE_VALUE)return 0;
    PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);DWORD result=0;
    if(Process32FirstW(snapshot,&entry))do{if(entry.th32ProcessID==GetCurrentProcessId()){result=entry.th32ParentProcessID;break;}}while(Process32NextW(snapshot,&entry));
    CloseHandle(snapshot);return result;
}
int Guardian(const wchar_t* name) {
    if(!name || wcslen(name)>160 || wcsncmp(name,L"Local\\DisplayWeave_Input_",25))return 2;
    using SetAwareness=HANDLE (WINAPI*)(HANDLE);
    auto setAwareness=reinterpret_cast<SetAwareness>(GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetThreadDpiAwarenessContext"));
    if(!setAwareness || !setAwareness(reinterpret_cast<HANDLE>(static_cast<INT_PTR>(-4))))return 3;
    HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);if(!mapping)return 3;
    auto* shared=static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
    if(!shared){CloseHandle(mapping);return 3;}
    HANDLE parent=nullptr,sourceProcess=nullptr,returnProcess=nullptr,releaseBegin=nullptr,releaseAck=nullptr;int result=3;
    const ULONGLONG now=GetTickCount64();
    // A configured lease gate must be opened before readiness; otherwise the
    // presenter's entry fails rather than running without coordinated release.
    const bool layout=shared->magic==kMagic && shared->version==kVersion && shared->size==sizeof(Shared);
    const bool releaseGate=layout && shared->releaseBeginName[0]!=0;
    if(releaseGate && !shared->releaseBeginName[127] && !shared->releaseAckName[127] &&
        ValidLeaseReleaseEventName(shared->releaseBeginName,L"Begin") && ValidLeaseReleaseEventName(shared->releaseAckName,L"Ack")){
        releaseBegin=OpenEventW(EVENT_MODIFY_STATE|SYNCHRONIZE,FALSE,shared->releaseBeginName);
        releaseAck=OpenEventW(SYNCHRONIZE,FALSE,shared->releaseAckName);
    }
    if(layout && shared->parentPid==ActualParent() &&
        shared->parentPid!=GetCurrentProcessId() && shared->started<=now && shared->deadline>now &&
        shared->deadline-shared->started<=kMaximumDuration && policy::ValidRect(shared->source) && policy::ValidRect(shared->originalClip) &&
        policy::ValidRect(shared->returnOutputBounds) && policy::ValidRect(shared->returnOutputWork) &&
        shared->returnOutputName[0] && shared->returnOutputName[CCHDEVICENAME-1]==0 && (!releaseGate || (releaseBegin && releaseAck))){
        parent=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,shared->parentPid);
        sourceProcess=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,shared->sourcePid);
        returnProcess=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,shared->returnPid);
        FILETIME created{};std::wstring parentPath,ownPath;
        if(parent && ProcessCreation(parent,created) && SameTime(created,shared->parentCreated) &&
            ImagePath(parent,parentPath) && ImagePath(GetCurrentProcess(),ownPath) && !_wcsicmp(parentPath.c_str(),ownPath.c_str()) &&
            WindowIdentity(reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->ownerWindow)),shared->parentPid,0) &&
            LiveIdentity(sourceProcess,shared->sourcePid,shared->sourceCreated,reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->sourceWindow)),shared->sourceTid) &&
            LiveIdentity(returnProcess,shared->returnPid,shared->returnCreated,reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->returnWindow)),shared->returnTid) &&
            OutputUnchanged(shared,reinterpret_cast<HWND>(static_cast<UINT_PTR>(shared->returnWindow)))){
            InterlockedExchange(&shared->ready,1);result=0;
            for(;;){
                LONG state=Read(&shared->state);
                if(state==StateCleaned || state==StateCancelled)break;
                bool parentDead=WaitForSingleObject(parent,0)!=WAIT_TIMEOUT;
                bool expired=GetTickCount64()>=shared->deadline || static_cast<DWORD>(GetTickCount()-Read(&shared->heartbeat))>kHeartbeatTimeout;
                if(parentDead || expired){
                    if(state==StateIdle){InterlockedCompareExchange(&shared->state,StateCancelled,StateIdle);break;}
                    // A dead parent cannot resume a partially completed cleanup.
                    // A live parent already cleaning is not preempted: racing
                    // two global-cursor restores could overwrite newer state.
                    // The guardian keeps watching until completion or parent
                    // exit. A hung Windows cursor API therefore has no promised
                    // recovery deadline; the parent's bounded wait must report
                    // an unfinished attempt rather than call it successful.
                    if(state==StateCleaning && parentDead)InterlockedCompareExchange(&shared->state,StateArmed,StateCleaning);
                    if(policy::ClaimCleanup(shared->state,false)){
                        InterlockedExchange(&shared->guardianRecovered,1);
                        BeginLeaseRelease(shared,releaseBegin,releaseAck);
                        const FocusResult focus=RestoreFocusWindow(shared,sourceProcess,returnProcess);
                        PublishGuardianFocus(shared,focus);
                        RestoreCursorState(shared,focus.incomplete?Incomplete:0,focus.error);break;
                    }
                    if(Read(&shared->state)==StateCleaned)break;
                }
                Sleep(50);
            }
        }
    }
    for(HANDLE handle:{parent,sourceProcess,returnProcess,releaseBegin,releaseAck})if(handle)CloseHandle(handle);
    UnmapViewOfFile(shared);CloseHandle(mapping);return result;
}
} // namespace

struct Session::Impl {
    Config config;Status status;
    DWORD thread=0,sourceThread=0,returnPid=0,returnThread=0;
    HWND returnFocus=nullptr;
    HANDLE sourceProcess=nullptr,returnProcess=nullptr,mapping=nullptr,guardian=nullptr;
    Shared* shared=nullptr;
    bool hotkey=false;
    ULONGLONG started=0,lastTopology=0;
    std::vector<Monitor> monitors;
    Monitor returnOutput{};
    ~Impl(){CloseResources();}
    void CloseResources(){
        if(shared){UnmapViewOfFile(shared);shared=nullptr;}
        for(HANDLE* handle:{&sourceProcess,&returnProcess,&mapping,&guardian})if(*handle){CloseHandle(*handle);*handle=nullptr;}
    }
    void ReadCleanup(){
        if(!shared)return;
        LONG flags=Read(&shared->cleanupFlags);
        status.clipRestored=(flags&RestoredClip)!=0;status.cursorRestored=(flags&RestoredCursor)!=0;
        status.foreignClipObserved=status.foreignClipObserved || (flags&ForeignClip)!=0;
        status.releaseIncomplete=status.releaseIncomplete || (flags&Incomplete)!=0;status.guardianRecovered=Read(&shared->guardianRecovered)!=0;
        const LONG focusReason=Read(&shared->guardianFocusReason);
        status.guardianFocusAttempted=Read(&shared->guardianFocusAttempted)!=0;
        status.guardianFocusRestored=Read(&shared->guardianFocusRestored)!=0;
        status.guardianFocusRequestAccepted=Read(&shared->guardianFocusAccepted)!=0;
        status.guardianFocusSkipped=focusReason!=FocusNotRequested && !status.guardianFocusAttempted;
        status.guardianFocusError=static_cast<DWORD>(Read(&shared->guardianFocusError));
        status.guardianFocusReason=FocusReason(focusReason);
        status.focusRestored=status.focusRestored || status.guardianFocusRestored;
        status.leaseReleaseStarted=Read(&shared->releaseClaim)!=0;
        status.leaseReleaseAcknowledged=Read(&shared->releaseAcked)!=0;
        status.leaseReleaseError=static_cast<DWORD>(Read(&shared->releaseError));
        DWORD error=static_cast<DWORD>(Read(&shared->cleanupError));if(error)status.error=error;
    }
    void BeginRelease(){BeginLeaseRelease(shared,config.leaseReleaseBegin,config.leaseReleaseAck);}
    void RestoreOwnFocus(){
        const FocusResult focus=RestoreFocusWindow(shared,sourceProcess,returnProcess);
        status.focusRequestAttempted=status.focusRequestAttempted || focus.attempted;
        status.focusRequestAccepted=status.focusRequestAccepted || focus.accepted;
        status.focusRequestError=focus.error;status.focusRequestReason=FocusReason(focus.reason);
        status.focusRestored=status.focusRestored || focus.restored;
        status.releaseIncomplete=status.releaseIncomplete || focus.incomplete;
        if(focus.error)status.error=focus.error;
    }
    bool ConfirmMutationStillArmed(){
        if(Read(&shared->state)==StateArmed)return true;
        // A renderer can resume after the guardian timed out and already
        // restored the cursor. Never leave that late API mutation behind.
        // Wait for any current restore, then conditionally restore once more.
        const ULONGLONG until=GetTickCount64()+1000;
        while(Read(&shared->state)==StateCleaning && GetTickCount64()<until)Sleep(10);
        if(policy::ClaimCleanup(shared->state,true)){BeginRelease();RestoreOwnFocus();RestoreCursorState(shared);}
        else if(Read(&shared->state)!=StateCleaned)status.releaseIncomplete=true;
        ReadCleanup();return false;
    }
    void Stop(const char* reason,bool restoreFocus){
        if(thread && thread!=GetCurrentThreadId()){status.error=ERROR_INVALID_THREAD_ID;status.releaseIncomplete=true;return;}
        if(!shared && !hotkey && !sourceProcess && !returnProcess)return;
        status.active=false;status.reason=reason?reason:"released";
        if(started)status.elapsedMs=GetTickCount64()-started;
        if(shared){
            // Every ending session releases the lease first: a focus change,
            // including one the user already caused, can end exclusive mode.
            if(policy::ClaimCleanup(shared->state,false)){
                BeginRelease();
                if(restoreFocus)RestoreOwnFocus();
                RestoreCursorState(shared);
            }else if(InterlockedCompareExchange(&shared->state,StateCancelled,StateIdle)==StateIdle){
                BeginRelease();
                if(restoreFocus)RestoreOwnFocus();
            }
            DWORD waitResult=guardian?WaitForSingleObject(guardian,500):WAIT_OBJECT_0;
            DWORD waitError=waitResult==WAIT_FAILED?ErrorOr(ERROR_GEN_FAILURE):ERROR_SUCCESS;
            ReadCleanup();
            const bool mutationPossible=Read(&shared->clipOwned)!=0 || Read(&shared->cursorOwned)!=0;
            if(!policy::CleanupAttemptFinished(Read(&shared->state),mutationPossible)){
                status.releaseIncomplete=true;status.error=waitError?waitError:ERROR_TIMEOUT;
            }else if(waitError){status.releaseIncomplete=true;status.error=waitError;}
            // CloseResources releases only this process's mapping/handle refs.
            // An in-flight guardian retains its own view and can still finish.
        }
        if(hotkey){if(!UnregisterHotKey(nullptr,kReleaseHotkey)){status.error=ErrorOr(ERROR_GEN_FAILURE);status.releaseIncomplete=true;}hotkey=false;}
        CloseResources();
    }
    bool StartGuardian(POINT cursor,const RECT& clip,std::string& error){
        GUID id{};if(FAILED(CoCreateGuid(&id))){error="Cannot create an input-session nonce.";return false;}
        wchar_t guid[48]{};StringFromGUID2(id,guid,48);
        std::wstring name=L"Local\\DisplayWeave_Input_"+std::to_wstring(GetCurrentProcessId())+L"_"+guid;
        PSECURITY_DESCRIPTOR descriptor=nullptr;
        if(!PrivateSecurity(descriptor)){error="Cannot restrict the input guardian to this user and SYSTEM.";return false;}
        SECURITY_ATTRIBUTES security{sizeof(security),descriptor,FALSE};
        SetLastError(ERROR_SUCCESS);mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,&security,PAGE_READWRITE,0,sizeof(Shared),name.c_str());
        DWORD createError=GetLastError();LocalFree(descriptor);
        if(!mapping || createError==ERROR_ALREADY_EXISTS){error="Cannot create a private input guardian mapping.";return false;}
        shared=static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
        if(!shared){error="Cannot initialize the input guardian mapping.";return false;}
        ZeroMemory(shared,sizeof(*shared));
        shared->magic=kMagic;shared->version=kVersion;shared->size=sizeof(*shared);
        shared->parentPid=GetCurrentProcessId();shared->sourcePid=config.sourcePid;shared->sourceTid=sourceThread;
        shared->returnPid=returnPid;shared->returnTid=returnThread;
        if(!ProcessCreation(GetCurrentProcess(),shared->parentCreated)){error="Cannot record the input owner identity.";return false;}
        if(!ProcessCreation(sourceProcess,shared->sourceCreated) || !ProcessCreation(returnProcess,shared->returnCreated)){
            error="Cannot record the source and physical return process identities.";return false;
        }
        shared->started=started;shared->deadline=started+config.maximumDurationMs;
        shared->sourceWindow=reinterpret_cast<UINT_PTR>(config.sourceWindow);shared->ownerWindow=reinterpret_cast<UINT_PTR>(config.ownerWindow);
        shared->returnWindow=reinterpret_cast<UINT_PTR>(returnFocus);
        shared->returnOutputBounds=returnOutput.bounds;shared->returnOutputWork=returnOutput.work;shared->returnOutputFlags=returnOutput.flags;
        if(returnOutput.name.empty() || returnOutput.name.size()>=CCHDEVICENAME){error="Cannot record the physical return output.";return false;}
        std::copy(returnOutput.name.begin(),returnOutput.name.end(),shared->returnOutputName);
        if(status.leaseReleaseConfigured){
            std::copy(config.leaseReleaseBeginName.begin(),config.leaseReleaseBeginName.end(),shared->releaseBeginName);
            std::copy(config.leaseReleaseAckName.begin(),config.leaseReleaseAckName.end(),shared->releaseAckName);
        }
        shared->source=config.sourceClient;shared->ownedClip=config.sourceClient;shared->originalClip=clip;shared->returnCursor=cursor;
        InterlockedExchange(&shared->heartbeat,GetTickCount());
        std::wstring executable;if(!ImagePath(GetCurrentProcess(),executable)){error="Cannot locate the input guardian executable.";return false;}
        std::wstring command=L"\""+executable+L"\" --input-guardian \""+name+L"\"";
        STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
        if(!CreateProcessW(executable.c_str(),&command[0],nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process)){
            error="Cannot start the input guardian.";return false;
        }
        guardian=process.hProcess;CloseHandle(process.hThread);
        const ULONGLONG until=GetTickCount64()+1500;
        while(GetTickCount64()<until && WaitForSingleObject(guardian,0)==WAIT_TIMEOUT){
            if(Read(&shared->ready)==1){status.guardianReady=true;return true;}Sleep(10);
        }
        error="The input guardian did not acknowledge this session.";return false;
    }
};

Session::Session():impl(new Impl){}
Session::~Session(){impl->Stop("session_destroyed",true);delete impl;}
bool Session::Active()const{return impl->status.active;}
const Status& Session::GetStatus()const{return impl->status;}
bool Session::Enter(const Config& config,std::string& error){
    auto& self=*impl;
    if(self.status.active){error="Interaction is already active.";return false;}
    self.CloseResources();self.status={};self.config=config;self.thread=GetCurrentThreadId();self.started=GetTickCount64();
    auto fail=[&](const char* text,DWORD code)->bool{error=text;self.status.error=code;self.status.reason="entry_failed";self.Stop("entry_failed",true);return false;};
    DWORD ownerPid=0;DWORD ownerThread=GetWindowThreadProcessId(config.ownerWindow,&ownerPid);
    if(ownerPid!=GetCurrentProcessId() || ownerThread!=self.thread || !IsWindow(config.ownerWindow))return fail("The interaction owner must be this UI thread's window.",ERROR_INVALID_WINDOW_HANDLE);
    if(!policy::ValidRect(config.sourceClient) || !policy::ValidRect(config.projectedImage) || config.maximumDurationMs<1000 || config.maximumDurationMs>kMaximumDuration)
        return fail("Invalid interaction geometry or bounded lifetime.",ERROR_INVALID_PARAMETER);
    // Names fit the 128-character mapping fields (validation caps them at 120).
    self.status.leaseReleaseConfigured=config.leaseReleaseBegin || config.leaseReleaseAck || !config.leaseReleaseBeginName.empty() || !config.leaseReleaseAckName.empty();
    if(self.status.leaseReleaseConfigured && (!config.leaseReleaseBegin || !config.leaseReleaseAck ||
        !ValidLeaseReleaseEventName(config.leaseReleaseBeginName,L"Begin") || !ValidLeaseReleaseEventName(config.leaseReleaseAckName,L"Ack") ||
        WaitForSingleObject(config.leaseReleaseAck,0)!=WAIT_TIMEOUT))
        return fail("The lease release gate is incomplete or already acknowledged.",ERROR_INVALID_PARAMETER);
    DWORD sourcePid=0;self.sourceThread=GetWindowThreadProcessId(config.sourceWindow,&sourcePid);
    if(!self.sourceThread || sourcePid!=config.sourcePid || IsShell(config.sourceWindow,sourcePid) || !IsWindowVisible(config.sourceWindow) || IsIconic(config.sourceWindow) ||
        GetAncestor(config.sourceWindow,GA_ROOT)!=config.sourceWindow)return fail("The selected source is not a live application window.",ERROR_INVALID_WINDOW_HANDLE);
    RECT actual{};
    if(!SourceClient(config.sourceWindow,actual) || !policy::SameRect(actual,config.sourceClient))return fail("The source client moved or resized before interaction.",ERROR_INVALID_DATA);
    if(!OwnInputDesktop())return fail("The input desktop is not available to this presenter.",ERROR_ACCESS_DENIED);
    if(!Topology(self.monitors) || !DistinctOutputs(self.monitors,config.sourceClient,config.projectedImage))return fail("Source and projected image must occupy distinct, fully identified outputs.",ERROR_INVALID_DATA);
    for(const auto& monitor:self.monitors)if(policy::Contains(monitor.bounds,config.projectedImage))self.returnOutput=monitor;
    self.sourceProcess=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,config.sourcePid);
    if(!self.sourceProcess || WaitForSingleObject(self.sourceProcess,0)!=WAIT_TIMEOUT)return fail("Cannot verify the live source process.",ErrorOr(ERROR_ACCESS_DENIED));
    POINT entry{},mapped{},returnCursor{};RECT originalClip{};
    if(!GetCursorPos(&entry) || !GetClipCursor(&originalClip))return fail("Cannot save the cursor state.",ErrorOr(ERROR_GEN_FAILURE));
    returnCursor=entry;
    if(!policy::MapPoint(config.projectedImage,config.sourceClient,entry,mapped)){
        if(!config.allowEntryOutsideImage)return fail("Move the pointer inside the projected image before entering interaction.",ERROR_INVALID_PARAMETER);
        mapped=Center(config.sourceClient);
        if(policy::Contains(config.sourceClient,entry))returnCursor=Center(config.projectedImage);
    }
    self.returnFocus=config.returnFocusWindow?config.returnFocusWindow:GetForegroundWindow();
    if(config.returnFocusWindow && (config.returnFocusWindow==config.sourceWindow || !IsWindowVisible(config.returnFocusWindow) || IsIconic(config.returnFocusWindow)))
        return fail("The supplied physical return-focus window is unavailable or is the source.",ERROR_INVALID_WINDOW_HANDLE);
    if(!self.returnFocus || self.returnFocus==config.sourceWindow)self.returnFocus=config.ownerWindow;
    self.returnThread=GetWindowThreadProcessId(self.returnFocus,&self.returnPid);
    self.returnProcess=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,self.returnPid);
    if(!self.returnThread || !self.returnProcess || WaitForSingleObject(self.returnProcess,0)!=WAIT_TIMEOUT)
        return fail("Cannot preserve the return-focus window's identity.",ErrorOr(ERROR_ACCESS_DENIED));
    if(IsShell(self.returnFocus,self.returnPid) || !WindowOnOutput(self.returnFocus,self.returnOutput))
        return fail("The return-focus window must be a visible application on the projected image's physical output.",ERROR_INVALID_WINDOW_HANDLE);
    if(!RegisterHotKey(nullptr,kReleaseHotkey,MOD_CONTROL|MOD_ALT|MOD_NOREPEAT,VK_F10))return fail("Ctrl+Alt+F10 is unavailable; interaction was not started.",ErrorOr(ERROR_HOTKEY_ALREADY_REGISTERED));
    self.hotkey=true;
    if(!self.StartGuardian(returnCursor,originalClip,error)){
        self.status.error=ErrorOr(ERROR_GEN_FAILURE);self.Stop("guardian_start_failed",false);return false;
    }
    // Focus can itself cause an exclusive source to clip the cursor. Arm the
    // guardian before acquiring focus, as well as before explicit cursor writes.
    InterlockedExchange(&self.shared->heartbeat,GetTickCount());
    if(InterlockedCompareExchange(&self.shared->state,StateArmed,StateIdle)!=StateIdle)return fail("Input guardian cancelled the entry deadline.",ERROR_TIMEOUT);
    if(GetForegroundWindow()!=config.sourceWindow)SetForegroundWindow(config.sourceWindow);
    if(!self.ConfirmMutationStillArmed())return fail("The guardian released interaction while source focus was being acquired.",ERROR_TIMEOUT);
    if(GetForegroundWindow()!=config.sourceWindow)return fail("Windows did not grant focus to the selected source; no focus bypass is attempted.",ERROR_ACCESS_DENIED);
    if(!WindowIdentity(config.sourceWindow,config.sourcePid,self.sourceThread) || !SourceClient(config.sourceWindow,actual) || !policy::SameRect(actual,config.sourceClient))
        return fail("Source identity or geometry changed while acquiring focus.",ERROR_INVALID_DATA);
    RECT beforeClip{};
    if(!GetClipCursor(&beforeClip))return fail("Cannot verify cursor confinement before entry.",ErrorOr(ERROR_GEN_FAILURE));
    if(!policy::SameRect(beforeClip,originalClip) && !policy::SameRect(beforeClip,config.sourceClient)){
        self.status.foreignClipObserved=true;return fail("The application changed cursor confinement while acquiring focus.",ERROR_BUSY);
    }
    // The guardian is already armed. Its conditional restore is harmless if a
    // failure occurs before a particular cursor API call takes effect.
    InterlockedExchange(&self.shared->heartbeat,GetTickCount());
    if(!self.ConfirmMutationStillArmed())return fail("Input guardian cancelled the entry deadline.",ERROR_TIMEOUT);
    if(!policy::SameRect(beforeClip,config.sourceClient)){
        InterlockedExchange(&self.shared->clipOwned,1);
        if(!ClipCursor(&config.sourceClient))return fail("Cannot confine the pointer to the selected source.",ErrorOr(ERROR_ACCESS_DENIED));
        if(!self.ConfirmMutationStillArmed())return fail("The guardian released interaction during pointer entry.",ERROR_TIMEOUT);
    }
    InterlockedExchange(&self.shared->cursorOwned,1);
    if(!SetCursorPos(mapped.x,mapped.y))return fail("Cannot place the pointer in the selected source.",ErrorOr(ERROR_ACCESS_DENIED));
    if(!self.ConfirmMutationStillArmed())return fail("The guardian released interaction during pointer entry.",ERROR_TIMEOUT);
    POINT observed{};
    if(Read(&self.shared->state)!=StateArmed || !GetCursorPos(&observed) || !policy::Contains(config.sourceClient,observed))return fail("Pointer entry was interrupted or constrained by the application.",ERROR_BUSY);
    self.status.active=true;self.status.entered=true;self.status.reason="modal_hardware_direct";self.lastTopology=self.started;
    return true;
}
void Session::Tick(){
    auto& self=*impl;if(!self.status.active)return;
    if(GetCurrentThreadId()!=self.thread){self.status.error=ERROR_INVALID_THREAD_ID;return;}
    self.status.elapsedMs=GetTickCount64()-self.started;
    if(!self.shared || Read(&self.shared->state)!=StateArmed){self.Stop("guardian_released",false);return;}
    if(!self.guardian || WaitForSingleObject(self.guardian,0)!=WAIT_TIMEOUT){self.Stop("guardian_unavailable",true);return;}
    if(GetTickCount64()>=self.shared->deadline){self.Stop("deadline",true);return;}
    if(WaitForSingleObject(self.sourceProcess,0)!=WAIT_TIMEOUT || !WindowIdentity(self.config.sourceWindow,self.config.sourcePid,self.sourceThread)){
        self.Stop("source_closed",false);return;
    }
    if(!OwnInputDesktop()){self.Stop("input_desktop_changed",false);return;}
    if(GetForegroundWindow()!=self.config.sourceWindow){self.Stop("focus_lost",false);return;}
    RECT actual{};if(!SourceClient(self.config.sourceWindow,actual) || !policy::SameRect(actual,self.config.sourceClient)){
        self.Stop("source_geometry_changed",true);return;
    }
    RECT clip{};if(!GetClipCursor(&clip)){self.Stop("cursor_clip_unavailable",true);return;}
    if(!policy::SameRect(clip,self.config.sourceClient)){self.status.foreignClipObserved=true;self.Stop("application_changed_clip",true);return;}
    if(GetTickCount64()-self.lastTopology>=250){
        std::vector<Monitor> current;
        if(!Topology(current) || !SameTopology(self.monitors,current)){self.Stop("display_topology_changed",true);return;}
        self.lastTopology=GetTickCount64();
    }
    InterlockedExchange(&self.shared->heartbeat,GetTickCount());
}
bool Session::HandleMessage(const MSG& message){
    if(impl->hotkey && message.message==WM_HOTKEY && message.wParam==kReleaseHotkey){impl->Stop("release_shortcut",true);return true;}return false;
}
void Session::Release(const char* reason){impl->Stop(reason,true);}
bool SourceClientMatches(HWND window,const RECT& expected){
    RECT actual{};return IsWindow(window) && IsWindowVisible(window) && !IsIconic(window) && SourceClient(window,actual) && policy::SameRect(actual,expected);
}
bool TryRunGuardian(int argc,wchar_t** argv,int& exitCode){
    if(argc<2 || wcscmp(argv[1],L"--input-guardian"))return false;
    exitCode=argc==3?Guardian(argv[2]):2;return true;
}
} // namespace projection_input
