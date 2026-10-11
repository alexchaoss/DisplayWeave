#pragma once
#include <windows.h>
#include <stdint.h>

// Same layout for x86 and x64. The controller is the only configuration writer.
// The DLL writes status fields 64..84 and 92; configuration remains host-owned.
#define RS_PROTOCOL_MAGIC 0x52534731u
#define RS_PROTOCOL_VERSION 2u
#define RS_CAP_USER32 1u
#define RS_CAP_DXGI 2u
// Optional progress bits; protocol-v2 workers from earlier releases publish
// only the two capability bits. Missing progress therefore means unknown.
#define RS_INIT_CALLBACK 0x00000100u
#define RS_INIT_WORKER 0x00000200u
#define RS_INIT_DISCOVERY 0x00000400u
#define RS_INIT_HARDWARE 0x00000800u
#define RS_INIT_WARP 0x00001000u
#define RS_INIT_DEVICE_READY 0x00002000u
#define RS_INIT_HOOK_SETUP 0x00004000u
#define RS_INIT_HOOKS_ENABLED 0x00008000u
#define RS_INIT_SUBCLASS 0x00010000u
#define RS_INIT_MASK 0x0001ff00u
#define RS_HEARTBEAT_TIMEOUT_MS 4000u
#define RS_MODE_AUTO 0
#define RS_MODE_FORCE 1
#define RS_STATE_NORMAL 0
#define RS_STATE_MAXIMIZED 1
#define RS_STATE_FULLSCREEN 2
#define RS_STATE_FORCED 3

#pragma pack(push, 8)
struct RegionProtocol {
    DWORD magic;                    // 0
    DWORD version;                  // 4
    DWORD size;                     // 8
    volatile LONG sequence;         // 12, odd while controller writes
    DWORD targetPid;                // 16
    DWORD controllerPid;            // 20
    uint64_t targetHwnd;            // 24
    uint64_t targetMonitor;         // 32
    LONG x;                         // 40
    LONG y;                         // 44
    LONG width;                     // 48
    LONG height;                    // 52
    volatile LONG active;           // 56
    volatile LONG heartbeatTick;    // 60, unsigned GetTickCount value
    volatile LONG hookReady;        // 64, 1 only after all required hooks work
    volatile LONG lastError;        // 68, Win32 error or HRESULT
    volatile LONG acknowledgedPid;  // 72
    volatile LONG capabilities;     // 76
    volatile LONG dxgiTransitions;  // 80, successfully emulated TRUE requests
    volatile LONG resizeIntercepts; // 84
    volatile LONG mode;             // 88, RS_MODE_* (controller-owned)
    volatile LONG windowState;      // 92, RS_STATE_* (DLL-owned)
};
#pragma pack(pop)
static_assert(sizeof(RegionProtocol) == 96, "Shared protocol ABI changed");

struct RegionSnapshot {
    DWORD targetPid;
    DWORD controllerPid;
    HWND hwnd;
    HMONITOR monitor;
    int x, y, width, height;
    bool active;
    DWORD heartbeat;
    LONG mode;
};

inline bool RS_ReadSnapshot(RegionProtocol* shared, RegionSnapshot& result) {
    if (!shared || shared->magic != RS_PROTOCOL_MAGIC ||
        shared->version != RS_PROTOCOL_VERSION || shared->size != sizeof(RegionProtocol)) return false;
    for (int attempt = 0; attempt != 4; ++attempt) {
        LONG before = InterlockedCompareExchange(&shared->sequence, 0, 0);
        if (before & 1) continue;
        result.targetPid = shared->targetPid;
        result.controllerPid = shared->controllerPid;
        result.hwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(shared->targetHwnd));
        result.monitor = reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(shared->targetMonitor));
        result.x = shared->x; result.y = shared->y;
        result.width = shared->width; result.height = shared->height;
        result.active = shared->active != 0;
        result.heartbeat = static_cast<DWORD>(shared->heartbeatTick);
        result.mode = shared->mode;
        MemoryBarrier();
        if (before == InterlockedCompareExchange(&shared->sequence, 0, 0))
            return result.width > 0 && result.height > 0 && result.hwnd != NULL &&
                (result.mode == RS_MODE_AUTO || result.mode == RS_MODE_FORCE);
    }
    return false;
}

inline bool RS_IsLive(const RegionSnapshot& s) {
    return s.active && s.targetPid == GetCurrentProcessId() &&
        static_cast<DWORD>(GetTickCount() - s.heartbeat) < RS_HEARTBEAT_TIMEOUT_MS;
}
