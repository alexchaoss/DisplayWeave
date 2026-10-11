#pragma once
#include <windows.h>
#include <stdint.h>

// Geometry only: no display, window, process, or shared-memory side effects.
// Half-open rectangles agree with USER32's point/monitor conventions.
namespace RegionVirtualMonitor {
inline bool Positive(const RECT& r) { return r.right > r.left && r.bottom > r.top; }
inline bool Equal(const RECT& a, const RECT& b) {
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}
inline bool Contains(const RECT& outer, const RECT& inner) {
    return Positive(inner) && inner.left >= outer.left && inner.top >= outer.top &&
        inner.right <= outer.right && inner.bottom <= outer.bottom;
}
inline bool Intersection(const RECT& a, const RECT& b, RECT& result) {
    result.left = a.left > b.left ? a.left : b.left;
    result.top = a.top > b.top ? a.top : b.top;
    result.right = a.right < b.right ? a.right : b.right;
    result.bottom = a.bottom < b.bottom ? a.bottom : b.bottom;
    return Positive(result);
}
inline long double Overlap(const RECT& a, const RECT& b) {
    RECT r;
    return Intersection(a, b, r) ? static_cast<long double>(static_cast<int64_t>(r.right) - r.left) *
        static_cast<long double>(static_cast<int64_t>(r.bottom) - r.top) : 0;
}
inline long double Distance(const RECT& a, const RECT& b) {
    int64_t dx = a.right < b.left ? static_cast<int64_t>(b.left) - a.right :
        b.right < a.left ? static_cast<int64_t>(a.left) - b.right : 0;
    int64_t dy = a.bottom < b.top ? static_cast<int64_t>(b.top) - a.bottom :
        b.bottom < a.top ? static_cast<int64_t>(a.top) - b.bottom : 0;
    return static_cast<long double>(dx) * dx + static_cast<long double>(dy) * dy;
}
inline bool PointInside(const RECT& r, const POINT& p) {
    return p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom;
}
// Primary wins exact ties. A rectangle outside every monitor obeys the caller's
// DEFAULTTONULL/DEFAULTTOPRIMARY/DEFAULTTONEAREST policy.
inline int Select(const RECT* rectangles, int count, int primary, const RECT& query, DWORD flags) {
    if (!rectangles || count <= 0 || primary < 0 || primary >= count) return -1;
    const bool point = !Positive(query);
    POINT p = {query.left, query.top};
    int best = -1;
    long double area = 0;
    for (int i = 0; i < count; ++i) {
        long double candidate = point ? (PointInside(rectangles[i], p) ? 1 : 0) : Overlap(rectangles[i], query);
        if (candidate > area || (candidate > 0 && candidate == area && i == primary)) {
            area = candidate; best = i;
        }
    }
    if (best >= 0) return best;
    if (flags & MONITOR_DEFAULTTOPRIMARY) return primary;
    if (!(flags & MONITOR_DEFAULTTONEAREST)) return -1;
    RECT normalized = point ? RECT{p.x, p.y, p.x, p.y} : query;
    best = primary;
    long double distance = Distance(rectangles[primary], normalized);
    for (int i = 0; i < count; ++i) {
        long double candidate = Distance(rectangles[i], normalized);
        if (candidate < distance) { distance = candidate; best = i; }
    }
    return best;
}
}
