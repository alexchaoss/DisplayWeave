#pragma once
#include "RegionProtocol.h"
#include <limits.h>

// Optional sidecar to the stable 96-byte control protocol. Older resident
// workers continue to use their selected rectangle and ignore this mapping.
#define RS_LAYOUT_MAGIC 0x52534c31u
#define RS_LAYOUT_VERSION 1u
#define RS_LAYOUT_MAX_REGIONS 9
#define RS_CAP_VIRTUAL_MONITORS 4u

struct RegionBox { LONG x, y, width, height; };
struct RegionLayoutProtocol {
    DWORD magic, version, size;
    volatile LONG sequence;
    DWORD targetPid, controllerPid;
    LONG count, reserved;
    RegionBox regions[RS_LAYOUT_MAX_REGIONS];
};
static_assert(sizeof(RegionLayoutProtocol) == 176, "Layout sidecar ABI changed");
struct RegionLayoutSnapshot {
    DWORD targetPid, controllerPid;
    LONG count;
    RegionBox regions[RS_LAYOUT_MAX_REGIONS];
};
inline bool RS_ValidBox(const RegionBox& r) {
    return r.width > 0 && r.height > 0 &&
        static_cast<int64_t>(r.x) + r.width <= INT_MAX &&
        static_cast<int64_t>(r.y) + r.height <= INT_MAX;
}
inline bool RS_ValidLayout(const RegionLayoutSnapshot& s) {
    if (s.count < 1 || s.count > RS_LAYOUT_MAX_REGIONS) return false;
    for (int i = 0; i < s.count; ++i) {
        const RegionBox& a = s.regions[i];
        if (!RS_ValidBox(a)) return false;
        for (int j = 0; j < i; ++j) {
            const RegionBox& b = s.regions[j];
            if (static_cast<int64_t>(a.x) < static_cast<int64_t>(b.x) + b.width &&
                static_cast<int64_t>(b.x) < static_cast<int64_t>(a.x) + a.width &&
                static_cast<int64_t>(a.y) < static_cast<int64_t>(b.y) + b.height &&
                static_cast<int64_t>(b.y) < static_cast<int64_t>(a.y) + a.height) return false;
        }
    }
    return true;
}
inline bool RS_ReadLayout(RegionLayoutProtocol* shared, const RegionSnapshot& control,
    RegionLayoutSnapshot& result) {
    if (!shared || shared->magic != RS_LAYOUT_MAGIC || shared->version != RS_LAYOUT_VERSION ||
        shared->size != sizeof(RegionLayoutProtocol)) return false;
    for (int attempt = 0; attempt < 4; ++attempt) {
        LONG before = InterlockedCompareExchange(&shared->sequence, 0, 0);
        if (before & 1) continue;
        result.targetPid = shared->targetPid; result.controllerPid = shared->controllerPid;
        result.count = shared->count;
        for (int i = 0; i < RS_LAYOUT_MAX_REGIONS; ++i) result.regions[i] = shared->regions[i];
        MemoryBarrier();
        if (before == InterlockedCompareExchange(&shared->sequence, 0, 0))
            return result.targetPid == control.targetPid && result.controllerPid == control.controllerPid &&
                RS_ValidLayout(result);
    }
    return false;
}
inline void RS_SelectedLayout(const RegionSnapshot& s, RegionLayoutSnapshot& layout) {
    layout = {};
    layout.targetPid = s.targetPid; layout.controllerPid = s.controllerPid; layout.count = 1;
    layout.regions[0] = {s.x, s.y, s.width, s.height};
}
