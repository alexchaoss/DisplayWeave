#include "LoaderFailureProtocol.h"

// The fixture intentionally has no CRT or PE TLS callbacks. This data symbol
// satisfies the MinGW linker's TLS-index reference without a TLS directory.
DWORD _tls_index;

// With -nostdlib Zig's COFF linker does not supply MinGW's usual stdcall
// export aliases. The x86 module definition names its internal symbols.
#if defined(__i386__)
#define FIXTURE_EXPORT
#else
#define FIXTURE_EXPORT __declspec(dllexport)
#endif

static BOOL SameName(const WCHAR* a, const WCHAR* b) {
    while (*a && *b) {
        WCHAR x = *a++, y = *b++;
        if (x >= L'A' && x <= L'Z') x += L'a' - L'A';
        if (y >= L'A' && y <= L'Z') y += L'a' - L'A';
        if (x != y) return FALSE;
    }
    return *a == *b;
}
static void Record(DWORD kind) {
    WCHAR name[96]; HANDLE mapping; LoaderFailureStatus* status;
    DWORD pid = GetCurrentProcessId();
    LoaderFailureName(name, pid);
    mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!mapping) return;
    status = (LoaderFailureStatus*)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE,
        0, 0, sizeof(LoaderFailureStatus));
    if (status && status->magic == RS_LOADER_FIXTURE_MAGIC && status->size == sizeof(*status) && status->pid == pid) {
        if (kind == 0) InterlockedIncrement(&status->rejectedLoads);
        else if (kind == 1) InterlockedIncrement(&status->initializeCalls);
        else InterlockedIncrement(&status->callbackCalls);
    }
    if (status) UnmapViewOfFile(status);
    CloseHandle(mapping);
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    WCHAR path[1024]; DWORD length; const WCHAR* base; const WCHAR* cursor;
    (void)instance; (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    length = GetModuleFileNameW(NULL, path, 1024);
    if (!length || length >= 1024) return FALSE;
    base = path;
    for (cursor = path; *cursor; ++cursor) if (*cursor == L'\\' || *cursor == L'/') base = cursor + 1;
    // Host preloading must succeed so the test reaches the TARGET loader.
    if (SameName(base, L"RegionHookHost.exe")) return TRUE;
    if (SameName(base, L"LoaderFailureProbe.exe")) Record(0);
    return FALSE;
}
FIXTURE_EXPORT DWORD WINAPI RegionInitialize(void* unused) {
    (void)unused; Record(1); return ERROR_INVALID_FUNCTION;
}
FIXTURE_EXPORT LRESULT CALLBACK RegionGetMessageHook(int code, WPARAM wp, LPARAM lp) {
    Record(2); return CallNextHookEx(NULL, code, wp, lp);
}
FIXTURE_EXPORT LRESULT CALLBACK RegionCallWindowHook(int code, WPARAM wp, LPARAM lp) {
    Record(2); return CallNextHookEx(NULL, code, wp, lp);
}
