#include "LoaderFailureProtocol.h"
#include "../RegionProtocol.h"
#include <stdio.h>
#include <stdint.h>
#include <wchar.h>
#include <string>

static HWND window;
static bool quitting;
static std::wstring report;
static RECT original;
static LONG_PTR originalStyle, originalExStyle;
static LoaderFailureStatus* fixture;
static HANDLE fixtureMapping, controllerMapping;
static const RegionProtocol* controller;
static DWORD localLoaderError;
static bool localLoadRejected;
static LONG localRejectedCount;

static void HoldControllerMapping() {
    if (controller) return;
    wchar_t name[96]; swprintf(name, 96, L"Local\\DisplayWeave_%lu", GetCurrentProcessId());
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!mapping) return;
    const RegionProtocol* view = static_cast<const RegionProtocol*>(MapViewOfFile(mapping,
        FILE_MAP_READ, 0, 0, sizeof(RegionProtocol)));
    if (!view) { CloseHandle(mapping); return; }
    controllerMapping = mapping; controller = view;
}
static bool SaveReport() {
    HoldControllerMapping();
    RECT bounds = {};
    bool geometry = GetWindowRect(window, &bounds) && EqualRect(&bounds, &original);
    bool styles = GetWindowLongPtrW(window, GWL_STYLE) == originalStyle &&
        GetWindowLongPtrW(window, GWL_EXSTYLE) == originalExStyle;
    bool moduleAbsent = GetModuleHandleW(L"RegionHook.dll") == NULL;
    bool inactive = false, unacknowledged = false;
    LONG active = -1, ready = -2, initialized = InterlockedCompareExchange(&fixture->initializeCalls, 0, 0);
    LONG callbacks = InterlockedCompareExchange(&fixture->callbackCalls, 0, 0);
    LONG rejected = InterlockedCompareExchange(&fixture->rejectedLoads, 0, 0);
    if (controller && controller->magic == RS_PROTOCOL_MAGIC && controller->version == RS_PROTOCOL_VERSION &&
        controller->size == sizeof(RegionProtocol) && controller->targetPid == GetCurrentProcessId()) {
        // This is a read-only view; interlocked writes would violate its access.
        LONG sequence = controller->sequence;
        active = controller->active; ready = controller->hookReady;
        DWORD acknowledged = controller->acknowledgedPid;
        MemoryBarrier();
        inactive = !(sequence & 1) && sequence == controller->sequence && active == 0;
        unacknowledged = ready == 0 && acknowledged == 0;
    }
    bool success = localLoadRejected && localLoaderError == ERROR_DLL_INIT_FAILED && localRejectedCount == 1 &&
        rejected == 2 && initialized == 0 && callbacks == 0 && inactive && unacknowledged && moduleAbsent && geometry && styles && IsWindow(window);
    FILE* file = _wfopen((report + L".tmp").c_str(), L"wb");
    if (!file) return false;
    fprintf(file, "{\"success\":%s,\"pid\":%lu,\"localLoadRejected\":%s,\"localLoaderError\":%lu,"
        "\"localRejectedCount\":%ld,\"rejectedLoads\":%ld,\"initializeCalls\":%ld,\"callbackCalls\":%ld,"
        "\"controllerMappingObserved\":%s,\"controllerActive\":%ld,\"controllerReady\":%ld,\"controllerInactive\":%s,"
        "\"moduleAbsent\":%s,\"boundsUnchanged\":%s,\"stylesUnchanged\":%s,\"windowAlive\":%s,\"windowHidden\":%s}\n",
        success ? "true" : "false", GetCurrentProcessId(), localLoadRejected ? "true" : "false", localLoaderError,
        localRejectedCount, rejected, initialized, callbacks, controller ? "true" : "false", active, ready, inactive ? "true" : "false",
        moduleAbsent ? "true" : "false", geometry ? "true" : "false", styles ? "true" : "false", IsWindow(window) ? "true" : "false",
        !IsWindowVisible(window) ? "true" : "false");
    fclose(file);
    return MoveFileExW((report + L".tmp").c_str(), report.c_str(), MOVEFILE_REPLACE_EXISTING) != FALSE;
}
static LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_APP + 100) {
        if (wp == 1) return SaveReport() ? 1 : 0;
        if (wp == 2) { quitting = true; return 1; }
    }
    if (message == WM_CLOSE || message == WM_DESTROY) { quitting = true; return 0; }
    return DefWindowProcW(hwnd, message, wp, lp);
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 5 || wcscmp(argv[1], L"--dll") != 0 || wcscmp(argv[3], L"--report") != 0) return 64;
    report = argv[4];
    SetProcessDPIAware();
    wchar_t name[96]; LoaderFailureName(name, GetCurrentProcessId());
    fixtureMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(LoaderFailureStatus), name);
    if (!fixtureMapping || GetLastError() == ERROR_ALREADY_EXISTS) return 65;
    fixture = static_cast<LoaderFailureStatus*>(MapViewOfFile(fixtureMapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(LoaderFailureStatus)));
    if (!fixture) return 66;
    ZeroMemory(fixture, sizeof(*fixture));
    fixture->magic = RS_LOADER_FIXTURE_MAGIC; fixture->size = sizeof(*fixture); fixture->pid = GetCurrentProcessId();
    SetLastError(ERROR_SUCCESS);
    HMODULE module = LoadLibraryW(argv[2]);
    localLoaderError = module ? ERROR_SUCCESS : GetLastError(); // immediately, in this same thread
    localLoadRejected = module == NULL;
    if (module) FreeLibrary(module);
    localRejectedCount = InterlockedCompareExchange(&fixture->rejectedLoads, 0, 0);
    WNDCLASSW cls = {};
    cls.hInstance = GetModuleHandleW(NULL); cls.lpfnWndProc = WindowProcedure;
    cls.lpszClassName = L"DisplayWeaveLoaderFailureProbe";
    if (!RegisterClassW(&cls)) return 67;
    POINT point = {};
    MONITORINFO monitor = {sizeof(MONITORINFO)};
    if (!GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTOPRIMARY), &monitor)) return 68;
    window = CreateWindowExW(0, cls.lpszClassName, L"DisplayWeave owned loader failure fixture", WS_OVERLAPPEDWINDOW,
        monitor.rcMonitor.left + 40, monitor.rcMonitor.top + 60, 360, 240, NULL, NULL, cls.hInstance, NULL);
    if (!window || !GetWindowRect(window, &original)) return 69;
    originalStyle = GetWindowLongPtrW(window, GWL_STYLE); originalExStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
    FILE* ready = _wfopen((report + L".ready.json").c_str(), L"wb");
    if (!ready) return 70;
    fprintf(ready, "{\"pid\":%lu,\"hwnd\":\"%llx\",\"x\":%ld,\"y\":%ld,\"width\":%ld,\"height\":%ld,\"localLoaderError\":%lu}\n",
        GetCurrentProcessId(), static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(window)),
        monitor.rcMonitor.left, monitor.rcMonitor.top, monitor.rcMonitor.right - monitor.rcMonitor.left,
        monitor.rcMonitor.bottom - monitor.rcMonitor.top, localLoaderError);
    fclose(ready);
    ULONGLONG began = GetTickCount64();
    while (!quitting && GetTickCount64() - began < 45000) {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        HoldControllerMapping();
        MsgWaitForMultipleObjects(0, NULL, FALSE, 5, QS_ALLINPUT);
    }
    if (IsWindow(window)) DestroyWindow(window);
    if (controller) UnmapViewOfFile(controller);
    if (controllerMapping) CloseHandle(controllerMapping);
    UnmapViewOfFile(fixture); CloseHandle(fixtureMapping);
    return 0;
}
