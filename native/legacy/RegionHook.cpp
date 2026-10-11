#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "RegionProtocol.h"
#include "RegionLayoutProtocol.h"
#include "VirtualMonitorModel.h"
#include "MinHook.h"

// Application-local USER32 monitor and window compatibility. This does not
// intercept a graphics API, own a display output, or create a display driver.
typedef BOOL (WINAPI* GetMonitorInfoWFn)(HMONITOR, LPMONITORINFO);
typedef BOOL (WINAPI* GetMonitorInfoAFn)(HMONITOR, LPMONITORINFO);
typedef BOOL (WINAPI* EnumDisplayMonitorsFn)(HDC, LPCRECT, MONITORENUMPROC, LPARAM);
typedef HMONITOR (WINAPI* MonitorFromPointFn)(POINT, DWORD);
typedef HMONITOR (WINAPI* MonitorFromRectFn)(LPCRECT, DWORD);
typedef HMONITOR (WINAPI* MonitorFromWindowFn)(HWND, DWORD);
typedef int (WINAPI* GetSystemMetricsFn)(int);
typedef BOOL (WINAPI* SetWindowPosFn)(HWND, HWND, int, int, int, int, UINT);
typedef BOOL (WINAPI* MoveWindowFn)(HWND, int, int, int, int, BOOL);
typedef BOOL (WINAPI* SetWindowPlacementFn)(HWND, const WINDOWPLACEMENT*);
static GetMonitorInfoWFn realGetMonitorInfoW;
static GetMonitorInfoAFn realGetMonitorInfoA;
static EnumDisplayMonitorsFn realEnumDisplayMonitors;
static MonitorFromPointFn realMonitorFromPoint;
static MonitorFromRectFn realMonitorFromRect;
static MonitorFromWindowFn realMonitorFromWindow;
static GetSystemMetricsFn realGetSystemMetrics;
static SetWindowPosFn realSetWindowPos;
static MoveWindowFn realMoveWindow;
static SetWindowPlacementFn realSetWindowPlacement;
static RegionProtocol* shared;
static HANDLE mapping;
static RegionLayoutProtocol* volatile layoutShared;
static HANDLE layoutMapping;
static volatile LONG initializationStarted;
static volatile LONG hooksInstalled;
static volatile LONG restoreNeeded;
static volatile LONG restorePending;
static volatile LONG automaticBorderless;
static volatile LONG automaticMaximized;
static volatile LONG nativeRestore;
static volatile LONG identitySequence;
static thread_local bool internalCall;
static SRWLOCK savedLock = SRWLOCK_INIT;
static SRWLOCK subclassLock = SRWLOCK_INIT;
static const wchar_t* const originalProcedureProperty = L"DisplayWeave.OriginalProcedure.v2";
static const wchar_t* const subclassProperty = L"DisplayWeave.Subclass.v2";
static const wchar_t* const identityProperty = L"DisplayWeave.WindowIdentity.v3";
static LRESULT CALLBACK RegionWindowProcedure(HWND, UINT, WPARAM, LPARAM);

struct InternalScope {
    bool previous;
    InternalScope() : previous(internalCall) { internalCall = true; }
    ~InternalScope() { internalCall = previous; }
};
struct ExternalScope {
    bool previous;
    ExternalScope() : previous(internalCall) { internalCall = false; }
    ~ExternalScope() { internalCall = previous; }
};
struct NativeRestoreScope {
    NativeRestoreScope() { InterlockedIncrement(&nativeRestore); }
    ~NativeRestoreScope() { InterlockedDecrement(&nativeRestore); }
};

static void InitializationProgress(LONG progress) {
    if (shared) InterlockedOr(&shared->capabilities, progress);
}
static bool IsTarget(const RegionSnapshot& s, HWND hwnd) {
    DWORD pid = 0;
    return hwnd == s.hwnd && IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, &pid) != 0 &&
        pid == s.targetPid && pid == GetCurrentProcessId();
}
static bool Active(RegionSnapshot& s) {
    return !internalCall && RS_ReadSnapshot(shared, s) && RS_IsLive(s) && IsTarget(s, s.hwnd);
}
static bool RefreshActive(const RegionSnapshot& previous, RegionSnapshot& current) {
    return RS_ReadSnapshot(shared, current) && RS_IsLive(current) &&
        current.controllerPid == previous.controllerPid && current.targetPid == previous.targetPid &&
        current.hwnd == previous.hwnd && IsTarget(current, current.hwnd);
}
static RECT SelectedRectangle(const RegionSnapshot& s) {
    RECT r = {s.x, s.y, s.x, s.y};
    int64_t right = static_cast<int64_t>(s.x) + s.width;
    int64_t bottom = static_cast<int64_t>(s.y) + s.height;
    if (right <= LONG_MAX && bottom <= LONG_MAX) {
        r.right = static_cast<LONG>(right); r.bottom = static_cast<LONG>(bottom);
    }
    return r;
}

// Synthetic handles exist only inside this process. Never send them to USER32.
static const UINT_PTR syntheticBase = 0x52530000u;
static int SyntheticIndex(HMONITOR monitor) {
    UINT_PTR value = reinterpret_cast<UINT_PTR>(monitor);
    return value >= syntheticBase && value < syntheticBase + RS_LAYOUT_MAX_REGIONS ?
        static_cast<int>(value - syntheticBase) : -1;
}
static HMONITOR SyntheticHandle(int slot) { return reinterpret_cast<HMONITOR>(syntheticBase + slot); }
static HMONITOR PhysicalHandle(HMONITOR monitor) {
    if (SyntheticIndex(monitor) < 0) return monitor;
    RegionSnapshot s;
    return RS_ReadSnapshot(shared, s) && s.targetPid == GetCurrentProcessId() &&
        SyntheticIndex(s.monitor) < 0 ? s.monitor : NULL;
}

static const int maximumPhysicalMonitors = 64;
static const int maximumMonitors = maximumPhysicalMonitors + RS_LAYOUT_MAX_REGIONS;
struct MonitorEntry {
    HMONITOR handle;
    HMONITOR physical;
    MONITORINFOEXW info;
    int slot;
};
struct MonitorTopology {
    MonitorEntry entries[maximumMonitors];
    RECT rectangles[maximumMonitors];
    int count;
    int primary;
    int virtualCount;
    bool overflow;
    RECT desktop;
};
static BOOL CALLBACK CollectPhysical(HMONITOR monitor, HDC, LPRECT, LPARAM data) {
    MonitorTopology& t = *reinterpret_cast<MonitorTopology*>(data);
    if (t.count >= maximumPhysicalMonitors || SyntheticIndex(monitor) >= 0) { t.overflow = true; return FALSE; }
    MonitorEntry entry = {};
    entry.handle = entry.physical = monitor;
    entry.slot = -1;
    entry.info.cbSize = sizeof(entry.info);
    if (!realGetMonitorInfoW(monitor, reinterpret_cast<LPMONITORINFO>(&entry.info))) return TRUE;
    t.entries[t.count++] = entry;
    return TRUE;
}
static bool BuildTopology(const RegionSnapshot& s, MonitorTopology& t) {
    InternalScope scope;
    ZeroMemory(&t, sizeof(t));
    t.primary = -1;
    if (!realEnumDisplayMonitors || !realEnumDisplayMonitors(NULL, NULL, CollectPhysical,
        reinterpret_cast<LPARAM>(&t)) || t.overflow) return false;
    int selected = -1;
    for (int i = 0; i < t.count; ++i) if (t.entries[i].physical == s.monitor) selected = i;
    if (selected < 0) return false;
    MonitorEntry physical = t.entries[selected];
    RegionLayoutSnapshot layout;
    RegionLayoutProtocol* sidecar = static_cast<RegionLayoutProtocol*>(
        InterlockedCompareExchangePointer(reinterpret_cast<PVOID volatile*>(&layoutShared), NULL, NULL));
    if (!RS_ReadLayout(sidecar, s, layout)) RS_SelectedLayout(s, layout);
    if (!RS_ValidLayout(layout)) return false;
    bool valid = true;
    for (int i = 0; i < layout.count; ++i) {
        const RegionBox& b = layout.regions[i];
        RECT r = {b.x, b.y, b.x + b.width, b.y + b.height};
        if (!RegionVirtualMonitor::Contains(physical.info.rcMonitor, r)) valid = false;
    }
    if (!valid) RS_SelectedLayout(s, layout);
    if (!RS_ValidLayout(layout)) return false;
    RECT selectedRect = SelectedRectangle(s);
    if (!RegionVirtualMonitor::Contains(physical.info.rcMonitor, selectedRect)) return false;
    // Remove just this physical monitor; keep all other physical handles and
    // bounds, including monitors left/above the primary desktop origin.
    for (int i = selected; i + 1 < t.count; ++i) t.entries[i] = t.entries[i + 1];
    --t.count;
    int firstVirtual = t.count;
    RECT virtualRects[RS_LAYOUT_MAX_REGIONS];
    for (int i = 0; i < layout.count; ++i) {
        const RegionBox& b = layout.regions[i];
        RECT r = {b.x, b.y, b.x + b.width, b.y + b.height};
        if (!RegionVirtualMonitor::Contains(physical.info.rcMonitor, r)) return false;
        virtualRects[i] = r;
        MonitorEntry entry = physical;
        entry.handle = SyntheticHandle(i); entry.slot = i;
        entry.info.rcMonitor = r;
        if (!RegionVirtualMonitor::Intersection(r, physical.info.rcWork, entry.info.rcWork))
            entry.info.rcWork = RECT{r.left, r.top, r.left, r.top};
        swprintf(entry.info.szDevice, CCHDEVICENAME, L"\\\\.\\DISPLAYWEAVE%d", i + 1);
        t.entries[t.count++] = entry;
    }
    int defaultSlot = RegionVirtualMonitor::Select(virtualRects, layout.count, 0,
        selectedRect, MONITOR_DEFAULTTONEAREST);
    t.virtualCount = layout.count;
    t.primary = firstVirtual + defaultSlot;
    t.desktop = t.entries[0].info.rcMonitor;
    for (int i = 0; i < t.count; ++i) {
        MonitorEntry& entry = t.entries[i];
        entry.info.dwFlags = i == t.primary ? MONITORINFOF_PRIMARY : 0;
        const RECT& r = entry.info.rcMonitor;
        t.rectangles[i] = r;
        if (r.left < t.desktop.left) t.desktop.left = r.left;
        if (r.top < t.desktop.top) t.desktop.top = r.top;
        if (r.right > t.desktop.right) t.desktop.right = r.right;
        if (r.bottom > t.desktop.bottom) t.desktop.bottom = r.bottom;
    }
    return true;
}
static const MonitorEntry* FindMonitor(const MonitorTopology& t, HMONITOR monitor, HMONITOR selectedPhysical) {
    if (monitor == selectedPhysical) return &t.entries[t.primary];
    for (int i = 0; i < t.count; ++i) if (t.entries[i].handle == monitor) return &t.entries[i];
    // An application may retain a synthetic handle after a layout shrinks.
    return SyntheticIndex(monitor) >= 0 ? &t.entries[t.primary] : NULL;
}
static BOOL WINAPI HookGetMonitorInfoW(HMONITOR monitor, LPMONITORINFO info) {
    RegionSnapshot s;
    MonitorTopology t;
    if (info && info->cbSize >= sizeof(MONITORINFO) && Active(s) && BuildTopology(s, t)) {
        const MonitorEntry* entry = FindMonitor(t, monitor, s.monitor);
        if (entry) {
            info->rcMonitor = entry->info.rcMonitor; info->rcWork = entry->info.rcWork;
            info->dwFlags = entry->info.dwFlags;
            if (info->cbSize >= sizeof(MONITORINFOEXW))
                CopyMemory(reinterpret_cast<MONITORINFOEXW*>(info)->szDevice,
                    entry->info.szDevice, sizeof(entry->info.szDevice));
            return TRUE;
        }
    }
    HMONITOR physical = PhysicalHandle(monitor);
    if (!physical && SyntheticIndex(monitor) >= 0) { SetLastError(ERROR_INVALID_HANDLE); return FALSE; }
    return realGetMonitorInfoW(physical, info);
}
static BOOL WINAPI HookGetMonitorInfoA(HMONITOR monitor, LPMONITORINFO info) {
    RegionSnapshot s;
    MonitorTopology t;
    if (info && info->cbSize >= sizeof(MONITORINFO) && Active(s) && BuildTopology(s, t)) {
        const MonitorEntry* entry = FindMonitor(t, monitor, s.monitor);
        if (entry) {
            info->rcMonitor = entry->info.rcMonitor; info->rcWork = entry->info.rcWork;
            info->dwFlags = entry->info.dwFlags;
            if (info->cbSize >= sizeof(MONITORINFOEXA)) {
                char* name = reinterpret_cast<MONITORINFOEXA*>(info)->szDevice;
                if (!WideCharToMultiByte(CP_ACP, 0, entry->info.szDevice, -1, name, CCHDEVICENAME, NULL, NULL)) name[0] = 0;
            }
            return TRUE;
        }
    }
    HMONITOR physical = PhysicalHandle(monitor);
    if (!physical && SyntheticIndex(monitor) >= 0) { SetLastError(ERROR_INVALID_HANDLE); return FALSE; }
    return realGetMonitorInfoA(physical, info);
}

struct EnumerationContext {
    const MonitorTopology* topology;
    HMONITOR selected;
    HDC sourceDC;
    MONITORENUMPROC callback;
    LPARAM data;
};
static BOOL CALLBACK EnumerateVirtual(HMONITOR monitor, HDC dc, LPRECT clipped, LPARAM data) {
    EnumerationContext& c = *reinterpret_cast<EnumerationContext*>(data);
    if (monitor != c.selected) {
        ExternalScope callbackScope;
        return c.callback(monitor, dc, clipped, c.data);
    }
    for (int i = 0; i < c.topology->count; ++i) {
        const MonitorEntry& entry = c.topology->entries[i];
        if (entry.slot < 0) continue;
        RECT bounds = entry.info.rcMonitor;
        if (c.sourceDC) {
            // USER32 supplies callback/clip rectangles in the caller DC's
            // coordinates. Resolve GDI helpers only for this optional path.
            typedef BOOL (WINAPI* GetDCOrgExFn)(HDC, LPPOINT);
            typedef BOOL (WINAPI* DPtoLPFn)(HDC, LPPOINT, int);
            HMODULE gdi = GetModuleHandleW(L"gdi32.dll");
            GetDCOrgExFn originFn = gdi ? reinterpret_cast<GetDCOrgExFn>(GetProcAddress(gdi, "GetDCOrgEx")) : NULL;
            DPtoLPFn convertFn = gdi ? reinterpret_cast<DPtoLPFn>(GetProcAddress(gdi, "DPtoLP")) : NULL;
            POINT origin = {};
            if (!originFn || !convertFn || !originFn(c.sourceDC, &origin)) { SetLastError(ERROR_INVALID_HANDLE); return FALSE; }
            POINT points[2] = {{bounds.left - origin.x, bounds.top - origin.y},
                {bounds.right - origin.x, bounds.bottom - origin.y}};
            if (!convertFn(c.sourceDC, points, 2)) return FALSE;
            bounds.left = points[0].x < points[1].x ? points[0].x : points[1].x;
            bounds.right = points[0].x > points[1].x ? points[0].x : points[1].x;
            bounds.top = points[0].y < points[1].y ? points[0].y : points[1].y;
            bounds.bottom = points[0].y > points[1].y ? points[0].y : points[1].y;
        }
        RECT intersection;
        if (!RegionVirtualMonitor::Intersection(bounds, *clipped, intersection)) continue;
        ExternalScope callbackScope;
        if (!c.callback(entry.handle, dc, &intersection, c.data)) return FALSE;
    }
    return TRUE;
}
static BOOL WINAPI HookEnumDisplayMonitors(HDC dc, LPCRECT clip, MONITORENUMPROC callback, LPARAM data) {
    RegionSnapshot s;
    MonitorTopology t;
    if (callback && Active(s) && BuildTopology(s, t)) {
        EnumerationContext context = {&t, s.monitor, dc, callback, data};
        InternalScope scope;
        return realEnumDisplayMonitors(dc, clip, EnumerateVirtual, reinterpret_cast<LPARAM>(&context));
    }
    return realEnumDisplayMonitors(dc, clip, callback, data);
}
static HMONITOR SelectMonitor(const MonitorTopology& t, const RECT& r, DWORD flags) {
    int index = RegionVirtualMonitor::Select(t.rectangles, t.count, t.primary, r, flags);
    return index < 0 ? NULL : t.entries[index].handle;
}
static HMONITOR WINAPI HookMonitorFromPoint(POINT p, DWORD flags) {
    RegionSnapshot s; MonitorTopology t;
    if (Active(s) && BuildTopology(s, t)) {
        RECT point = {p.x, p.y, p.x, p.y};
        return SelectMonitor(t, point, flags);
    }
    return realMonitorFromPoint(p, flags);
}
static HMONITOR WINAPI HookMonitorFromRect(LPCRECT rect, DWORD flags) {
    RegionSnapshot s; MonitorTopology t;
    if (rect && Active(s) && BuildTopology(s, t)) return SelectMonitor(t, *rect, flags);
    return realMonitorFromRect(rect, flags);
}
// WINDOWPLACEMENT uses workspace coordinates except for tool windows.
static RECT PlacementToScreen(HWND hwnd, const RECT& normal) {
    InternalScope scope;
    RECT result = normal;
    if (!(GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) {
        HMONITOR monitor = realMonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        MONITORINFO info = {sizeof(MONITORINFO)};
        if (realGetMonitorInfoW(monitor, &info))
            OffsetRect(&result, info.rcWork.left - info.rcMonitor.left, info.rcWork.top - info.rcMonitor.top);
    }
    return result;
}
static HMONITOR WINAPI HookMonitorFromWindow(HWND hwnd, DWORD flags) {
    RegionSnapshot s; MonitorTopology t;
    if (Active(s) && BuildTopology(s, t)) {
        RECT rect;
        bool haveRectangle = false;
        if (IsIconic(hwnd)) {
            WINDOWPLACEMENT placement = {sizeof(WINDOWPLACEMENT)};
            if (GetWindowPlacement(hwnd, &placement)) { rect = PlacementToScreen(hwnd, placement.rcNormalPosition); haveRectangle = true; }
        } else haveRectangle = GetWindowRect(hwnd, &rect) != FALSE;
        if (haveRectangle) return SelectMonitor(t, rect, flags);
        if (flags & MONITOR_DEFAULTTOPRIMARY) return t.entries[t.primary].handle;
    }
    return realMonitorFromWindow(hwnd, flags);
}
static int BoundedDimension(LONG start, LONG end) {
    int64_t size = static_cast<int64_t>(end) - start;
    return size > INT_MAX ? INT_MAX : static_cast<int>(size);
}
static int WINAPI HookGetSystemMetrics(int index) {
    switch (index) {
    case SM_CXSCREEN: case SM_CYSCREEN: case SM_CXFULLSCREEN: case SM_CYFULLSCREEN:
    case SM_XVIRTUALSCREEN: case SM_YVIRTUALSCREEN: case SM_CXVIRTUALSCREEN: case SM_CYVIRTUALSCREEN: case SM_CMONITORS:
        break;
    default: return realGetSystemMetrics(index);
    }
    RegionSnapshot s; MonitorTopology t;
    if (Active(s) && BuildTopology(s, t)) {
        const RECT& primary = t.entries[t.primary].info.rcMonitor;
        switch (index) {
        case SM_CXSCREEN: case SM_CXFULLSCREEN: return BoundedDimension(primary.left, primary.right);
        case SM_CYSCREEN: case SM_CYFULLSCREEN: return BoundedDimension(primary.top, primary.bottom);
        case SM_XVIRTUALSCREEN: return t.desktop.left;
        case SM_YVIRTUALSCREEN: return t.desktop.top;
        case SM_CXVIRTUALSCREEN: return BoundedDimension(t.desktop.left, t.desktop.right);
        case SM_CYVIRTUALSCREEN: return BoundedDimension(t.desktop.top, t.desktop.bottom);
        case SM_CMONITORS: return t.count;
        default: break;
        }
    }
    return realGetSystemMetrics(index);
}

struct SavedWindow {
    HWND hwnd;
    DWORD controller, process, thread;
    HANDLE identity;
    WINDOWPLACEMENT placement;
    LONG_PTR style;
    bool valid, frameChanged;
};
static SavedWindow saved;
static const LONG_PTR restoredFrameMask = static_cast<LONG_PTR>(WS_OVERLAPPEDWINDOW | WS_POPUP);
static SavedWindow CopySaved() {
    AcquireSRWLockShared(&savedLock); SavedWindow copy = saved; ReleaseSRWLockShared(&savedLock); return copy;
}
static bool SavedIdentity(const SavedWindow& state) {
    DWORD pid = 0;
    return state.valid && IsWindow(state.hwnd) && GetWindowThreadProcessId(state.hwnd, &pid) == state.thread &&
        pid == state.process && GetPropW(state.hwnd, identityProperty) == state.identity;
}
static void SaveWindow(const RegionSnapshot& s) {
    InternalScope scope;
    SavedWindow next = {};
    next.hwnd = s.hwnd; next.controller = s.controllerPid;
    next.thread = GetWindowThreadProcessId(s.hwnd, &next.process);
    next.placement.length = sizeof(WINDOWPLACEMENT);
    next.style = GetWindowLongPtrW(s.hwnd, GWL_STYLE);
    next.identity = GetPropW(s.hwnd, identityProperty);
    if (!next.identity) {
        LONG sequence = InterlockedIncrement(&identitySequence);
        if (!sequence) sequence = InterlockedIncrement(&identitySequence);
        next.identity = reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(static_cast<DWORD>(sequence)));
        if (!SetPropW(s.hwnd, identityProperty, next.identity)) return;
    }
    next.valid = IsTarget(s, s.hwnd) && GetWindowPlacement(s.hwnd, &next.placement) != FALSE;
    AcquireSRWLockExclusive(&savedLock); saved = next; ReleaseSRWLockExclusive(&savedLock);
}
static bool Constrained(const RegionSnapshot& s) {
    return !InterlockedCompareExchange(&nativeRestore, 0, 0) && (s.mode == RS_MODE_FORCE ||
        IsZoomed(s.hwnd) || InterlockedCompareExchange(&automaticBorderless, 0, 0));
}
static void SaveAutomaticNormal(const RegionSnapshot& s) {
    if (s.mode != RS_MODE_AUTO || IsZoomed(s.hwnd) || IsIconic(s.hwnd) ||
        InterlockedCompareExchange(&automaticBorderless, 0, 0) || InterlockedCompareExchange(&nativeRestore, 0, 0) ||
        InterlockedCompareExchange(&restoreNeeded, 0, 0) || InterlockedCompareExchange(&restorePending, 0, 0)) return;
    SavedWindow old = CopySaved();
    LONG_PTR style = GetWindowLongPtrW(s.hwnd, GWL_STYLE);
    if (old.valid && (old.style & WS_CAPTION) && !(style & WS_CAPTION)) return;
    SaveWindow(s);
}
static void RestoreFrame() {
    InternalScope scope;
    AcquireSRWLockExclusive(&savedLock);
    SavedWindow old = saved; saved.frameChanged = false;
    ReleaseSRWLockExclusive(&savedLock);
    if (!SavedIdentity(old)) return;
    LONG_PTR current = GetWindowLongPtrW(old.hwnd, GWL_STYLE);
    // A browser/application can remove its own frame before its fullscreen
    // resize reaches us. Releasing our confinement restores that saved frame
    // too; frameChanged alone only describes changes made by the Force path.
    LONG_PTR restored = (current & ~restoredFrameMask) | (old.style & restoredFrameMask);
    if (current == restored && !old.frameChanged) return;
    SetWindowLongPtrW(old.hwnd, GWL_STYLE, restored);
    SetWindowPos(old.hwnd, NULL, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
        SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_ASYNCWINDOWPOS);
}
static void RestoreWindow() {
    InternalScope scope;
    InterlockedExchange(&restoreNeeded, 0);
    InterlockedExchange(&automaticMaximized, 0);
    RestoreFrame();
    SavedWindow old = CopySaved();
    if (!SavedIdentity(old)) return;
    InterlockedExchange(&restorePending, 1);
    WINDOWPLACEMENT placement = old.placement;
    placement.flags |= WPF_ASYNCWINDOWPLACEMENT;
    if (IsIconic(old.hwnd)) placement.showCmd = SW_SHOWMINNOACTIVE;
    else if (placement.showCmd == SW_SHOWNORMAL || placement.showCmd == SW_RESTORE) placement.showCmd = SW_SHOWNOACTIVATE;
    SetWindowPlacement(old.hwnd, &placement);
}
static void ConfirmRestoration() {
    if (!InterlockedCompareExchange(&restorePending, 0, 0)) return;
    InternalScope scope;
    SavedWindow old = CopySaved();
    if (!SavedIdentity(old)) { InterlockedExchange(&restorePending, 0); return; }
    if ((GetWindowLongPtrW(old.hwnd, GWL_STYLE) & restoredFrameMask) != (old.style & restoredFrameMask)) return;
    WINDOWPLACEMENT placement = {sizeof(WINDOWPLACEMENT)};
    if (!GetWindowPlacement(old.hwnd, &placement)) return;
    RECT bounds;
    RECT normal = PlacementToScreen(old.hwnd, old.placement.rcNormalPosition);
    bool restored = !IsZoomed(old.hwnd) && !IsIconic(old.hwnd) && GetWindowRect(old.hwnd, &bounds) &&
        RegionVirtualMonitor::Equal(bounds, normal);
    if (old.placement.showCmd == SW_SHOWMAXIMIZED && IsZoomed(old.hwnd))
        restored = RegionVirtualMonitor::Equal(placement.rcNormalPosition, old.placement.rcNormalPosition);
    if (restored) InterlockedExchange(&restorePending, 0);
}
static void FitWindow(const RegionSnapshot& expected, bool stripFrame) {
    InternalScope scope;
    RegionSnapshot s;
    if (!RefreshActive(expected, s) || IsIconic(s.hwnd) || !IsWindowVisible(s.hwnd) || !Constrained(s)) return;
    SavedWindow old = CopySaved();
    if (!SavedIdentity(old) || old.hwnd != s.hwnd || old.controller != s.controllerPid) return;
    UINT flags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;
    if (stripFrame) {
        LONG_PTR current = GetWindowLongPtrW(s.hwnd, GWL_STYLE);
        LONG_PTR next = current & ~static_cast<LONG_PTR>(WS_CAPTION | WS_THICKFRAME);
        if (current != next && RefreshActive(expected, s)) {
            InterlockedExchange(&restoreNeeded, 1);
            SetLastError(0);
            LONG_PTR previous = SetWindowLongPtrW(s.hwnd, GWL_STYLE, next);
            DWORD error = GetLastError();
            InterlockedExchange(&restoreNeeded, 1);
            if (previous || !error) {
                AcquireSRWLockExclusive(&savedLock);
                if (saved.hwnd == s.hwnd && saved.controller == s.controllerPid) saved.frameChanged = true;
                ReleaseSRWLockExclusive(&savedLock);
                flags |= SWP_FRAMECHANGED;
            } else InterlockedExchange(&shared->lastError, error);
        }
    }
    RECT current;
    RECT desired = SelectedRectangle(s);
    if (!RegionVirtualMonitor::Positive(desired)) return;
    if (GetWindowRect(s.hwnd, &current) && RegionVirtualMonitor::Equal(current, desired) && !(flags & SWP_FRAMECHANGED)) return;
    if (!RefreshActive(expected, s)) return;
    InterlockedExchange(&restoreNeeded, 1);
    SetWindowPos(s.hwnd, NULL, s.x, s.y, s.width, s.height, flags);
    InterlockedExchange(&restoreNeeded, 1);
}
static bool FullRectangleRequest(const RegionSnapshot& s, int x, int y, int width, int height, UINT flags) {
    if ((flags & (SWP_NOMOVE | SWP_NOSIZE)) || width <= 0 || height <= 0 ||
        static_cast<int64_t>(x) + width > LONG_MAX || static_cast<int64_t>(y) + height > LONG_MAX) return false;
    RECT request = {x, y, x + width, y + height};
    RECT selected = SelectedRectangle(s);
    if (RegionVirtualMonitor::Equal(request, selected)) return true;
    MONITORINFO info = {sizeof(MONITORINFO)};
    if (realGetMonitorInfoW(s.monitor, &info) && RegionVirtualMonitor::Equal(request, info.rcMonitor)) return true;
    MonitorTopology t;
    if (BuildTopology(s, t)) for (int i = 0; i < t.count; ++i)
        if (t.entries[i].slot >= 0 && RegionVirtualMonitor::Equal(request, t.entries[i].info.rcMonitor)) return true;
    return false;
}
static bool RedirectRequest(const RegionSnapshot& s, int x, int y, int width, int height, UINT flags) {
    if (InterlockedCompareExchange(&nativeRestore, 0, 0)) return false;
    if (s.mode == RS_MODE_FORCE || IsZoomed(s.hwnd)) return true;
    bool framed = (GetWindowLongPtrW(s.hwnd, GWL_STYLE) & (WS_CAPTION | WS_THICKFRAME)) != 0;
    bool fullscreen = FullRectangleRequest(s, x, y, width, height, flags);
    if (InterlockedCompareExchange(&automaticBorderless, 0, 0)) {
        if (framed || (!(flags & SWP_NOSIZE) && !fullscreen)) {
            InterlockedExchange(&automaticBorderless, 0);
            InterlockedExchange(&restoreNeeded, 0); // app's own normal placement follows
            return false;
        }
        return true;
    }
    if (!framed && fullscreen) { InterlockedExchange(&automaticBorderless, 1); return true; }
    return false;
}
static BOOL WINAPI HookSetWindowPos(HWND hwnd, HWND after, int x, int y, int width, int height, UINT flags) {
    RegionSnapshot s, current;
    bool redirected = false;
    if (Active(s) && IsTarget(s, hwnd) && !IsIconic(hwnd) && RedirectRequest(s, x, y, width, height, flags) && RefreshActive(s, current)) {
        x = current.x; y = current.y; width = current.width; height = current.height;
        flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
        InterlockedIncrement(&shared->resizeIntercepts);
        InterlockedExchange(&restoreNeeded, 1); redirected = true;
    }
    BOOL result = realSetWindowPos(hwnd, after, x, y, width, height, flags);
    if (redirected) InterlockedExchange(&restoreNeeded, 1);
    return result;
}
static BOOL WINAPI HookMoveWindow(HWND hwnd, int x, int y, int width, int height, BOOL repaint) {
    RegionSnapshot s, current;
    bool redirected = false;
    if (Active(s) && IsTarget(s, hwnd) && !IsIconic(hwnd) && RedirectRequest(s, x, y, width, height, 0) && RefreshActive(s, current)) {
        x = current.x; y = current.y; width = current.width; height = current.height;
        InterlockedIncrement(&shared->resizeIntercepts);
        InterlockedExchange(&restoreNeeded, 1); redirected = true;
    }
    BOOL result = realMoveWindow(hwnd, x, y, width, height, repaint);
    if (redirected) InterlockedExchange(&restoreNeeded, 1);
    return result;
}
static BOOL WINAPI HookSetWindowPlacement(HWND hwnd, const WINDOWPLACEMENT* placement) {
    RegionSnapshot s;
    if (!placement || placement->length != sizeof(WINDOWPLACEMENT) || !Active(s) || !IsTarget(s, hwnd))
        return realSetWindowPlacement(hwnd, placement);
    // Preserve the normal placement supplied with a maximize request: replacing
    // it with the region would break SW_RESTORE. WM_GETMINMAXINFO sets max bounds.
    SaveAutomaticNormal(s);
    bool restoring = placement->showCmd != SW_SHOWMAXIMIZED && placement->showCmd != SW_SHOWMINIMIZED &&
        placement->showCmd != SW_MINIMIZE && placement->showCmd != SW_SHOWMINNOACTIVE && s.mode == RS_MODE_AUTO;
    if (restoring) InterlockedIncrement(&nativeRestore);
    BOOL result = realSetWindowPlacement(hwnd, placement);
    if (restoring) InterlockedDecrement(&nativeRestore);
    RegionSnapshot current;
    if (result && RefreshActive(s, current) && !IsIconic(hwnd)) {
        if (placement->showCmd == SW_SHOWMAXIMIZED) {
            InterlockedExchange(&automaticMaximized, 1); InterlockedExchange(&restoreNeeded, 1);
        }
        if (Constrained(current)) FitWindow(current, current.mode == RS_MODE_FORCE);
    }
    return result;
}
static LRESULT CallOriginalProcedure(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    WNDPROC previous = reinterpret_cast<WNDPROC>(GetPropW(hwnd, originalProcedureProperty));
    return previous ? CallWindowProcW(previous, hwnd, message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
}
static LRESULT CALLBACK RegionWindowProcedure(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    RegionSnapshot s;
    bool active = Active(s) && IsTarget(s, hwnd);
    bool automatic = active && s.mode == RS_MODE_AUTO;
    if (automatic && (message == WM_GETMINMAXINFO || message == WM_EXITSIZEMOVE ||
        (message == WM_STYLECHANGING && static_cast<int>(wParam) == GWL_STYLE) ||
        (message == WM_SYSCOMMAND && (wParam & 0xfff0) == SC_MAXIMIZE))) SaveAutomaticNormal(s);
    bool restoreCommand = automatic && message == WM_SYSCOMMAND && (wParam & 0xfff0) == SC_RESTORE;
    if (restoreCommand) InterlockedIncrement(&nativeRestore);
    LRESULT result = CallOriginalProcedure(hwnd, message, wParam, lParam);
    if (restoreCommand) InterlockedDecrement(&nativeRestore);
    RegionSnapshot current;
    if (active && RefreshActive(s, current)) {
        if (message == WM_GETMINMAXINFO && lParam) {
            MONITORINFO monitor = {sizeof(MONITORINFO)};
            HMONITOR actual = realMonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (realGetMonitorInfoW(actual, &monitor)) {
                MINMAXINFO* limits = reinterpret_cast<MINMAXINFO*>(lParam);
                limits->ptMaxPosition.x = current.x - monitor.rcMonitor.left;
                limits->ptMaxPosition.y = current.y - monitor.rcMonitor.top;
                limits->ptMaxSize.x = current.width; limits->ptMaxSize.y = current.height;
            }
        } else if (message == WM_WINDOWPOSCHANGING && lParam && !IsIconic(hwnd)) {
            WINDOWPOS* pos = reinterpret_cast<WINDOWPOS*>(lParam);
            if (RedirectRequest(current, pos->x, pos->y, pos->cx, pos->cy, pos->flags)) {
                pos->x = current.x; pos->y = current.y; pos->cx = current.width; pos->cy = current.height;
                pos->flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
                InterlockedExchange(&restoreNeeded, 1); InterlockedIncrement(&shared->resizeIntercepts);
            }
        } else if (automatic && message == WM_SIZE && wParam == SIZE_MAXIMIZED) {
            InterlockedExchange(&automaticMaximized, 1); InterlockedExchange(&restoreNeeded, 1);
        } else if (automatic && message == WM_SIZE && wParam == SIZE_RESTORED && InterlockedExchange(&automaticMaximized, 0)) {
            RestoreWindow();
        }
    }
    if (message == WM_NCDESTROY) {
        RemovePropW(hwnd, subclassProperty); RemovePropW(hwnd, originalProcedureProperty); RemovePropW(hwnd, identityProperty);
    }
    return result;
}
static bool EnsureWindowSubclass(const RegionSnapshot& s) {
    if (!IsTarget(s, s.hwnd)) return false;
    // This DLL and worker are in the window's process. SetWindowLongPtr supports
    // in-process subclassing; it does not require a queued-message bootstrap.
    AcquireSRWLockExclusive(&subclassLock);
    HANDLE marker = GetPropW(s.hwnd, subclassProperty);
    if (marker) {
        bool ready = marker == reinterpret_cast<HANDLE>(static_cast<INT_PTR>(1));
        ReleaseSRWLockExclusive(&subclassLock); return ready;
    }
    WNDPROC previous = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(s.hwnd, GWLP_WNDPROC));
    bool ok = previous && SetPropW(s.hwnd, originalProcedureProperty, reinterpret_cast<HANDLE>(previous)) &&
        SetPropW(s.hwnd, subclassProperty, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(2)));
    if (ok) {
        SetLastError(0);
        LONG_PTR replaced = SetWindowLongPtrW(s.hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&RegionWindowProcedure));
        ok = replaced != 0 || GetLastError() == 0;
        if (ok && replaced && replaced != reinterpret_cast<LONG_PTR>(previous))
            SetPropW(s.hwnd, originalProcedureProperty, reinterpret_cast<HANDLE>(replaced));
        if (ok) ok = SetPropW(s.hwnd, subclassProperty, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(1))) != FALSE;
        if (!ok && GetWindowLongPtrW(s.hwnd, GWLP_WNDPROC) == reinterpret_cast<LONG_PTR>(&RegionWindowProcedure))
            SetWindowLongPtrW(s.hwnd, GWLP_WNDPROC, replaced ? replaced : reinterpret_cast<LONG_PTR>(previous));
    }
    if (!ok) { RemovePropW(s.hwnd, originalProcedureProperty); RemovePropW(s.hwnd, subclassProperty); }
    ReleaseSRWLockExclusive(&subclassLock);
    if (ok) InitializationProgress(RS_INIT_SUBCLASS);
    return ok;
}

static bool InstallHooks() {
    InternalScope scope;
    InitializationProgress(RS_INIT_DISCOVERY);
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (!user32) { InterlockedExchange(&shared->lastError, ERROR_MOD_NOT_FOUND); return false; }
    InitializationProgress(RS_INIT_HOOK_SETUP);
    bool ok = MH_Initialize() == MH_OK;
#define RS_USER_HOOK(name) if (ok) ok = MH_CreateHook(reinterpret_cast<void*>(GetProcAddress(user32, #name)), \
    reinterpret_cast<void*>(&Hook##name), reinterpret_cast<void**>(&real##name)) == MH_OK
    RS_USER_HOOK(GetMonitorInfoW);
    RS_USER_HOOK(GetMonitorInfoA);
    RS_USER_HOOK(EnumDisplayMonitors);
    RS_USER_HOOK(MonitorFromPoint);
    RS_USER_HOOK(MonitorFromRect);
    RS_USER_HOOK(MonitorFromWindow);
    RS_USER_HOOK(GetSystemMetrics);
    RS_USER_HOOK(SetWindowPos);
    RS_USER_HOOK(MoveWindow);
    RS_USER_HOOK(SetWindowPlacement);
#undef RS_USER_HOOK
    if (ok) ok = MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
    if (ok) InitializationProgress(RS_INIT_HOOKS_ENABLED);
    else {
        MH_DisableHook(MH_ALL_HOOKS); MH_Uninitialize();
        InterlockedExchange(&shared->lastError, ERROR_NOT_SUPPORTED);
    }
    return ok;
}
static void OpenLayout() {
    if (layoutShared) return;
    wchar_t name[96]; swprintf(name, 96, L"Local\\DisplayWeave_Layout_%lu", GetCurrentProcessId());
    // RS_ReadLayout uses an interlocked sequence read, so its view must be
    // writable even though only the controller changes layout contents.
    HANDLE handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!handle) return;
    RegionLayoutProtocol* view = static_cast<RegionLayoutProtocol*>(MapViewOfFile(handle,
        FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(RegionLayoutProtocol)));
    if (!view) { CloseHandle(handle); return; }
    layoutMapping = handle;
    InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&layoutShared), view);
}
static DWORD WINAPI Worker(void*) {
    InitializationProgress(RS_INIT_WORKER);
    RegionSnapshot s;
    if (!RS_ReadSnapshot(shared, s) || !IsTarget(s, s.hwnd)) {
        InterlockedExchange(&shared->lastError, ERROR_INVALID_DATA); InterlockedExchange(&shared->hookReady, -1); return 0;
    }
    SaveWindow(s);
    OpenLayout();
    if (!InstallHooks()) {
        InterlockedExchange(&shared->acknowledgedPid, GetCurrentProcessId()); InterlockedExchange(&shared->hookReady, -1); return 0;
    }
    InterlockedExchange(&hooksInstalled, 1);
    DWORD controller = s.controllerPid;
    HWND assignedWindow = s.hwnd;
    LONG previousMode = s.mode;
    DWORD layoutAttempt = GetTickCount();
    for (;;) {
        if (!layoutShared && static_cast<DWORD>(GetTickCount() - layoutAttempt) >= 1000) { OpenLayout(); layoutAttempt = GetTickCount(); }
        if (RS_ReadSnapshot(shared, s)) {
            bool active = RS_IsLive(s) && IsTarget(s, s.hwnd);
            if (controller != s.controllerPid || assignedWindow != s.hwnd) {
                if (InterlockedCompareExchange(&restoreNeeded, 0, 0)) RestoreWindow();
                InterlockedExchange(&automaticBorderless, 0); InterlockedExchange(&automaticMaximized, 0);
                SavedWindow old = CopySaved();
                if (old.hwnd == s.hwnd && SavedIdentity(old)) {
                    // Async restoration may still be queued on a busy UI
                    // thread. Keep the original restore point on handoff.
                    AcquireSRWLockExclusive(&savedLock); saved.controller = s.controllerPid; ReleaseSRWLockExclusive(&savedLock);
                } else {
                    InterlockedExchange(&restorePending, 0); SaveWindow(s);
                }
                controller = s.controllerPid; assignedWindow = s.hwnd; previousMode = s.mode;
            }
            if (previousMode != s.mode) {
                if (InterlockedCompareExchange(&restoreNeeded, 0, 0)) RestoreWindow();
                InterlockedExchange(&automaticBorderless, 0); InterlockedExchange(&automaticMaximized, 0); previousMode = s.mode;
            }
            if (!active && InterlockedCompareExchange(&restoreNeeded, 0, 0)) RestoreWindow();
            ConfirmRestoration();
            bool subclassReady = EnsureWindowSubclass(s);
            if (active && subclassReady) {
                if (s.mode == RS_MODE_AUTO && !IsZoomed(s.hwnd) && !IsIconic(s.hwnd) &&
                    !InterlockedCompareExchange(&restorePending, 0, 0)) {
                    LONG_PTR style = GetWindowLongPtrW(s.hwnd, GWL_STYLE);
                    if (!(style & (WS_CAPTION | WS_THICKFRAME))) {
                        RECT bounds;
                        if (GetWindowRect(s.hwnd, &bounds) && FullRectangleRequest(s, bounds.left, bounds.top,
                            bounds.right - bounds.left, bounds.bottom - bounds.top, 0)) InterlockedExchange(&automaticBorderless, 1);
                    } else if (InterlockedExchange(&automaticBorderless, 0) && InterlockedCompareExchange(&restoreNeeded, 0, 0)) RestoreWindow();
                }
                if (Constrained(s)) FitWindow(s, s.mode == RS_MODE_FORCE);
                else SaveAutomaticNormal(s);
            }
            LONG state = !active ? RS_STATE_NORMAL : s.mode == RS_MODE_FORCE ? RS_STATE_FORCED :
                InterlockedCompareExchange(&automaticBorderless, 0, 0) ? RS_STATE_FULLSCREEN :
                IsZoomed(s.hwnd) ? RS_STATE_MAXIMIZED : RS_STATE_NORMAL;
            InterlockedExchange(&shared->windowState, state);
            InterlockedAnd(&shared->capabilities, ~RS_CAP_DXGI);
            InterlockedOr(&shared->capabilities, RS_CAP_USER32 | RS_CAP_VIRTUAL_MONITORS);
            InterlockedExchange(&shared->acknowledgedPid, GetCurrentProcessId());
            MemoryBarrier(); InterlockedExchange(&shared->hookReady, subclassReady ? 1 : 0);
        }
        Sleep(100);
    }
}
static DWORD Bootstrap() {
    if (InterlockedCompareExchange(&initializationStarted, 1, 0) != 0) return ERROR_SUCCESS;
    wchar_t name[80]; swprintf(name, 80, L"Local\\DisplayWeave_%lu", GetCurrentProcessId());
    mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    shared = mapping ? static_cast<RegionProtocol*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(RegionProtocol))) : NULL;
    if (!shared) {
        DWORD error = GetLastError();
        if (mapping) CloseHandle(mapping);
        mapping = NULL; InterlockedExchange(&initializationStarted, 0); return error ? error : ERROR_INVALID_DATA;
    }
    RegionSnapshot s;
    if (!RS_ReadSnapshot(shared, s) || !IsTarget(s, s.hwnd)) {
        UnmapViewOfFile(shared); CloseHandle(mapping); shared = NULL; mapping = NULL;
        InterlockedExchange(&initializationStarted, 0); return ERROR_INVALID_DATA;
    }
    InitializationProgress(RS_INIT_CALLBACK);
    HMODULE pinned = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&Bootstrap), &pinned)) {
        DWORD error = GetLastError(); InterlockedExchange(&shared->lastError, error);
        InterlockedExchange(&shared->hookReady, -1); return error;
    }
    HANDLE thread = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    if (!thread) {
        DWORD error = GetLastError(); InterlockedExchange(&shared->lastError, error);
        InterlockedExchange(&shared->hookReady, -1); return error;
    }
    CloseHandle(thread); return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) DWORD WINAPI RegionInitialize(void*) { return Bootstrap(); }
extern "C" __declspec(dllexport) LRESULT CALLBACK RegionGetMessageHook(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0) Bootstrap();
    return CallNextHookEx(NULL, code, wParam, lParam);
}
extern "C" __declspec(dllexport) LRESULT CALLBACK RegionCallWindowHook(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0) Bootstrap();
    return CallNextHookEx(NULL, code, wParam, lParam);
}
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
