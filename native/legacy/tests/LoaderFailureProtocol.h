#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#define RS_LOADER_FIXTURE_MAGIC 0x52534631u
typedef struct LoaderFailureStatus {
    DWORD magic, size, pid;
    volatile LONG rejectedLoads;
    volatile LONG initializeCalls;
    volatile LONG callbackCalls;
} LoaderFailureStatus;

// Small bounded formatting helper shared with a deliberately CRT-free DLL.
static void LoaderFailureName(WCHAR name[96], DWORD pid) {
    const WCHAR* prefix = L"Local\\DisplayWeave_LoaderFailure_";
    WCHAR digits[12]; DWORD i = 0, count = 0;
    while (*prefix) name[i++] = *prefix++;
    do { digits[count++] = (WCHAR)(L'0' + pid % 10); pid /= 10; } while (pid);
    while (count) name[i++] = digits[--count];
    name[i] = 0;
}
