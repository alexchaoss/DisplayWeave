#pragma once

#include <array>
#include <cstdint>
#include <limits>

// Pure C++17 planning model. No Windows calls, driver implementation, device
// installation, topology changes, frame capture, or application manipulation.
namespace RegionProjection {

inline constexpr std::uint32_t MaxRegions = 9;
inline constexpr std::uint32_t MaxPhysicalDisplays = 16;
inline constexpr std::uint32_t NoIndex = std::numeric_limits<std::uint32_t>::max();

struct StableId { std::uint64_t high = 0, low = 0; };
inline bool operator==(StableId a, StableId b) { return a.high == b.high && a.low == b.low; }
inline bool operator!=(StableId a, StableId b) { return !(a == b); }
inline bool IsValid(StableId id) { return id.high != 0 || id.low != 0; }

// Distinct types prevent using a physical display handle as a virtual output ID.
struct PhysicalDisplayId { StableId value; };
struct VirtualOutputId { StableId value; };

// Half-open, signed physical pixels in the Windows extended desktop. An edge
// must remain representable as int32, including for displays left of the origin.
struct PixelRect {
    std::int32_t x = 0, y = 0, width = 0, height = 0;
};
inline std::int64_t Right(const PixelRect& r) { return std::int64_t(r.x) + r.width; }
inline std::int64_t Bottom(const PixelRect& r) { return std::int64_t(r.y) + r.height; }
inline bool IsValid(const PixelRect& r) {
    return r.width > 0 && r.height > 0 &&
        Right(r) <= std::numeric_limits<std::int32_t>::max() &&
        Bottom(r) <= std::numeric_limits<std::int32_t>::max();
}
inline bool Equal(const PixelRect& a, const PixelRect& b) {
    return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
}
inline bool Contains(const PixelRect& outer, const PixelRect& inner) {
    return IsValid(outer) && IsValid(inner) && inner.x >= outer.x && inner.y >= outer.y &&
        Right(inner) <= Right(outer) && Bottom(inner) <= Bottom(outer);
}
inline bool Overlaps(const PixelRect& a, const PixelRect& b) {
    return IsValid(a) && IsValid(b) && std::int64_t(a.x) < Right(b) &&
        std::int64_t(b.x) < Right(a) && std::int64_t(a.y) < Bottom(b) && std::int64_t(b.y) < Bottom(a);
}
inline std::uint64_t Area(const PixelRect& r) {
    return IsValid(r) ? std::uint64_t(r.width) * std::uint64_t(r.height) : 0;
}

struct RefreshRate { std::uint32_t numerator = 60, denominator = 1; };
inline bool IsValid(RefreshRate r) { return r.numerator != 0 && r.denominator != 0; }
inline bool Equal(RefreshRate a, RefreshRate b) {
    return IsValid(a) && IsValid(b) && std::uint64_t(a.numerator) * b.denominator ==
        std::uint64_t(b.numerator) * a.denominator;
}
inline bool AtMost(RefreshRate value, RefreshRate ceiling) {
    return IsValid(value) && IsValid(ceiling) && std::uint64_t(value.numerator) * ceiling.denominator <=
        std::uint64_t(ceiling.numerator) * value.denominator;
}

enum class ColorSpace : std::uint32_t { Sdr = 0, Hdr10 = 1, ScRgb = 2 };
struct SourceMode {
    std::uint32_t width = 0, height = 0;
    RefreshRate refresh;
    ColorSpace color = ColorSpace::Sdr;
};
inline bool Equal(const SourceMode& a, const SourceMode& b) {
    return a.width == b.width && a.height == b.height && Equal(a.refresh, b.refresh) && a.color == b.color;
}

struct PhysicalDestination { PixelRect pixels; };
struct VirtualSource {
    VirtualOutputId output;
    SourceMode mode;
    // The eventual source desktop coordinates come from observed OS topology,
    // never from destination.pixels. They are deliberately absent from a draft.
};
struct Region {
    StableId id;
    PhysicalDestination destination;
    VirtualSource source;
    bool enabled = true;
};
struct LayoutDraft {
    StableId layoutId;
    std::uint64_t draftRevision = 0;
    std::uint64_t expectedAppliedRevision = 0;
    PhysicalDisplayId physicalTarget;
    PixelRect physicalBounds;
    std::uint32_t regionCount = 0;
    std::array<Region, MaxRegions> regions{};
};
struct ValidationPolicy {
    std::uint32_t minDestinationWidth = 64, minDestinationHeight = 64;
    bool requireFullPartition = true;
};
struct Capabilities {
    std::uint32_t protocolVersion = 1;
    std::uint32_t maxActiveOutputs = MaxRegions;
    std::uint32_t minSourceWidth = 64, minSourceHeight = 64;
    std::uint32_t maxSourceWidth = 16384, maxSourceHeight = 16384;
    std::uint64_t maxPixelsPerOutput = 64ULL * 1024 * 1024;
    std::uint64_t maxTotalSourcePixels = 64ULL * 1024 * 1024;
    RefreshRate minRefresh{1, 1};
    RefreshRate maxRefresh{240, 1};
    // Version 1 deliberately supports SDR only. These values are validation
    // bounds supplied by a provider, not claims of hardware or driver support.
};

enum class Error {
    None, InvalidPolicy, InvalidCapabilities, InvalidIdentity, InvalidDraftRevision,
    InvalidPhysicalBounds, InvalidRegionCount, InvalidRegionBounds, RegionTooSmall,
    RegionOutsidePhysical, DuplicateRegionId, DuplicateOutputId, PhysicalOutputAsSource,
    RegionsOverlap, IncompletePartition, InvalidSourceMode, UnsupportedColor,
    SourceSizeUnsupported, SourceRefreshUnsupported, OutputPixelLimit, TotalPixelLimit,
    TooManyActiveOutputs, InvalidPhysicalTopology, PhysicalDisplayMissing, PhysicalDisplayChanged,
    PhysicalTopologyChanged, StaleAppliedRevision, AppliedRevisionOverflow, InvalidPreparedPlan
};
struct Result {
    Error error = Error::None;
    std::uint32_t index = NoIndex;
    std::uint32_t relatedIndex = NoIndex;
    explicit operator bool() const { return error == Error::None; }
};
inline Result Failure(Error error, std::uint32_t index = NoIndex, std::uint32_t other = NoIndex) {
    return {error, index, other};
}
inline const char* ErrorName(Error error) {
    switch (error) {
#define RS_PROJECTION_ERROR(name) case Error::name: return #name
    RS_PROJECTION_ERROR(None); RS_PROJECTION_ERROR(InvalidPolicy); RS_PROJECTION_ERROR(InvalidCapabilities);
    RS_PROJECTION_ERROR(InvalidIdentity); RS_PROJECTION_ERROR(InvalidDraftRevision);
    RS_PROJECTION_ERROR(InvalidPhysicalBounds); RS_PROJECTION_ERROR(InvalidRegionCount);
    RS_PROJECTION_ERROR(InvalidRegionBounds); RS_PROJECTION_ERROR(RegionTooSmall);
    RS_PROJECTION_ERROR(RegionOutsidePhysical); RS_PROJECTION_ERROR(DuplicateRegionId);
    RS_PROJECTION_ERROR(DuplicateOutputId); RS_PROJECTION_ERROR(PhysicalOutputAsSource);
    RS_PROJECTION_ERROR(RegionsOverlap); RS_PROJECTION_ERROR(IncompletePartition);
    RS_PROJECTION_ERROR(InvalidSourceMode); RS_PROJECTION_ERROR(UnsupportedColor);
    RS_PROJECTION_ERROR(SourceSizeUnsupported); RS_PROJECTION_ERROR(SourceRefreshUnsupported);
    RS_PROJECTION_ERROR(OutputPixelLimit); RS_PROJECTION_ERROR(TotalPixelLimit);
    RS_PROJECTION_ERROR(TooManyActiveOutputs); RS_PROJECTION_ERROR(InvalidPhysicalTopology);
    RS_PROJECTION_ERROR(PhysicalDisplayMissing); RS_PROJECTION_ERROR(PhysicalDisplayChanged);
    RS_PROJECTION_ERROR(PhysicalTopologyChanged); RS_PROJECTION_ERROR(StaleAppliedRevision);
    RS_PROJECTION_ERROR(AppliedRevisionOverflow); RS_PROJECTION_ERROR(InvalidPreparedPlan);
#undef RS_PROJECTION_ERROR
    }
    return "Unknown";
}

inline Result ValidateCapabilities(const Capabilities& c) {
    if (c.protocolVersion != 1 || c.maxActiveOutputs == 0 || c.maxActiveOutputs > MaxRegions ||
        c.minSourceWidth == 0 || c.minSourceHeight == 0 ||
        c.minSourceWidth > c.maxSourceWidth || c.minSourceHeight > c.maxSourceHeight ||
        c.maxSourceWidth > std::uint32_t(std::numeric_limits<std::int32_t>::max()) ||
        c.maxSourceHeight > std::uint32_t(std::numeric_limits<std::int32_t>::max()) ||
        c.maxPixelsPerOutput == 0 || c.maxTotalSourcePixels == 0 ||
        !AtMost(c.minRefresh, c.maxRefresh)) return Failure(Error::InvalidCapabilities);
    const auto smallest = std::uint64_t(c.minSourceWidth) * c.minSourceHeight;
    if (smallest > c.maxPixelsPerOutput || smallest > c.maxTotalSourcePixels)
        return Failure(Error::InvalidCapabilities);
    return {};
}

inline Result ValidateSourceMode(const SourceMode& m, const Capabilities& c) {
    if (auto r = ValidateCapabilities(c); !r) return r;
    if (m.width == 0 || m.height == 0 || !IsValid(m.refresh)) return Failure(Error::InvalidSourceMode);
    if (m.color != ColorSpace::Sdr) return Failure(Error::UnsupportedColor);
    if (m.width < c.minSourceWidth || m.height < c.minSourceHeight ||
        m.width > c.maxSourceWidth || m.height > c.maxSourceHeight) return Failure(Error::SourceSizeUnsupported);
    if (!AtMost(c.minRefresh, m.refresh) || !AtMost(m.refresh, c.maxRefresh))
        return Failure(Error::SourceRefreshUnsupported);
    if (std::uint64_t(m.width) * m.height > c.maxPixelsPerOutput) return Failure(Error::OutputPixelLimit);
    return {};
}

inline Result ValidateDraft(const LayoutDraft& d, const Capabilities& c,
    const ValidationPolicy& policy = {}) {
    if (policy.minDestinationWidth == 0 || policy.minDestinationHeight == 0 ||
        policy.minDestinationWidth > std::uint32_t(std::numeric_limits<std::int32_t>::max()) ||
        policy.minDestinationHeight > std::uint32_t(std::numeric_limits<std::int32_t>::max()))
        return Failure(Error::InvalidPolicy);
    if (auto r = ValidateCapabilities(c); !r) return r;
    if (!IsValid(d.layoutId) || !IsValid(d.physicalTarget.value)) return Failure(Error::InvalidIdentity);
    if (d.draftRevision == 0) return Failure(Error::InvalidDraftRevision);
    if (!IsValid(d.physicalBounds)) return Failure(Error::InvalidPhysicalBounds);
    if (d.regionCount == 0 || d.regionCount > MaxRegions) return Failure(Error::InvalidRegionCount);
    std::uint64_t totalArea = 0, sourcePixels = 0;
    std::uint32_t activeOutputs = 0;
    for (std::uint32_t i = 0; i < d.regionCount; ++i) {
        const auto& region = d.regions[i];
        const auto& rect = region.destination.pixels;
        if (!IsValid(region.id) || !IsValid(region.source.output.value)) return Failure(Error::InvalidIdentity, i);
        if (region.source.output.value == d.physicalTarget.value) return Failure(Error::PhysicalOutputAsSource, i);
        if (!IsValid(rect)) return Failure(Error::InvalidRegionBounds, i);
        if (std::uint32_t(rect.width) < policy.minDestinationWidth || std::uint32_t(rect.height) < policy.minDestinationHeight)
            return Failure(Error::RegionTooSmall, i);
        if (!Contains(d.physicalBounds, rect)) return Failure(Error::RegionOutsidePhysical, i);
        for (std::uint32_t j = 0; j < i; ++j) {
            if (region.id == d.regions[j].id) return Failure(Error::DuplicateRegionId, i, j);
            if (region.source.output.value == d.regions[j].source.output.value) return Failure(Error::DuplicateOutputId, i, j);
            if (Overlaps(rect, d.regions[j].destination.pixels)) return Failure(Error::RegionsOverlap, i, j);
        }
        // Non-overlap and containment mean the sum never exceeds the monitor's
        // area (at most INT32_MAX squared), so addition cannot overflow uint64.
        totalArea += Area(rect);
        // Disabled cells retain valid source descriptors for later re-enable;
        // only enabled cells consume concurrent output and pixel budgets.
        if (auto r = ValidateSourceMode(region.source.mode, c); !r) { r.index = i; return r; }
        if (region.enabled) {
            if (++activeOutputs > c.maxActiveOutputs) return Failure(Error::TooManyActiveOutputs, i);
            auto pixels = std::uint64_t(region.source.mode.width) * region.source.mode.height;
            if (pixels > c.maxTotalSourcePixels - sourcePixels) return Failure(Error::TotalPixelLimit, i);
            sourcePixels += pixels;
        }
    }
    if (policy.requireFullPartition && totalArea != Area(d.physicalBounds)) return Failure(Error::IncompletePartition);
    return {};
}

// Snapshot all active physical outputs, including unassigned monitors. SourceMode
// is used here for comparison only; a physical monitor may already be HDR.
// Windows display numbers and HMONITOR values must not be used as StableIds.
struct PhysicalDisplayState {
    PhysicalDisplayId id;
    PixelRect bounds;
    PixelRect workArea;
    SourceMode mode;
    std::uint32_t rotationDegrees = 0;
    bool primary = false;
};
struct PhysicalTopology {
    std::uint32_t count = 0;
    std::array<PhysicalDisplayState, MaxPhysicalDisplays> displays{};
};
inline Result ValidatePhysicalTopology(const PhysicalTopology& p) {
    if (p.count == 0 || p.count > MaxPhysicalDisplays) return Failure(Error::InvalidPhysicalTopology);
    std::uint32_t primaryCount = 0;
    for (std::uint32_t i = 0; i < p.count; ++i) {
        const auto& v = p.displays[i];
        if (!IsValid(v.id.value) || !IsValid(v.bounds) || !Contains(v.bounds, v.workArea) ||
            v.mode.width == 0 || v.mode.height == 0 || !IsValid(v.mode.refresh) ||
            (v.rotationDegrees != 0 && v.rotationDegrees != 90 && v.rotationDegrees != 180 && v.rotationDegrees != 270) ||
            (v.mode.color != ColorSpace::Sdr && v.mode.color != ColorSpace::Hdr10 && v.mode.color != ColorSpace::ScRgb))
            return Failure(Error::InvalidPhysicalTopology, i);
        if (v.primary && ++primaryCount > 1) return Failure(Error::InvalidPhysicalTopology, i);
        for (std::uint32_t j = 0; j < i; ++j)
            if (v.id.value == p.displays[j].id.value) return Failure(Error::InvalidPhysicalTopology, i, j);
    }
    return {};
}
inline Result ValidatePhysicalPreservation(const PhysicalTopology& before, const PhysicalTopology& after) {
    if (auto r = ValidatePhysicalTopology(before); !r) return r;
    if (auto r = ValidatePhysicalTopology(after); !r) return r;
    if (before.count != after.count) return Failure(Error::PhysicalTopologyChanged);
    for (std::uint32_t i = 0; i < before.count; ++i) {
        const auto& a = before.displays[i];
        const PhysicalDisplayState* match = nullptr;
        for (std::uint32_t j = 0; j < after.count; ++j)
            if (after.displays[j].id.value == a.id.value) { match = &after.displays[j]; break; }
        if (!match) return Failure(Error::PhysicalDisplayMissing, i);
        if (!Equal(a.bounds, match->bounds) || !Equal(a.workArea, match->workArea) ||
            !Equal(a.mode, match->mode) || a.rotationDegrees != match->rotationDegrees || a.primary != match->primary)
            return Failure(Error::PhysicalDisplayChanged, i);
    }
    return {};
}

// An immutable-by-convention copy separates editor drafts from a pending apply.
// It is not an authorization token and must not be trusted across an IPC boundary.
struct PreparedPlan {
    LayoutDraft draft;
    Capabilities capabilities;
    ValidationPolicy policy;
    PhysicalTopology physicalBefore;
    std::uint64_t nextAppliedRevision = 0;
    bool ready = false;
};
inline Result PrepareApply(const LayoutDraft& draft, std::uint64_t appliedRevision,
    const Capabilities& capabilities, const PhysicalTopology& physicalBefore,
    PreparedPlan& output, const ValidationPolicy& policy = {}) {
    output = {};
    if (draft.expectedAppliedRevision != appliedRevision) return Failure(Error::StaleAppliedRevision);
    if (appliedRevision == std::numeric_limits<std::uint64_t>::max()) return Failure(Error::AppliedRevisionOverflow);
    if (auto r = ValidateDraft(draft, capabilities, policy); !r) return r;
    if (auto r = ValidatePhysicalTopology(physicalBefore); !r) return r;
    const PhysicalDisplayState* target = nullptr;
    for (std::uint32_t i = 0; i < physicalBefore.count; ++i) {
        const auto& physical = physicalBefore.displays[i];
        if (physical.id.value == draft.physicalTarget.value) target = &physical;
        for (std::uint32_t j = 0; j < draft.regionCount; ++j)
            if (draft.regions[j].source.output.value == physical.id.value) return Failure(Error::PhysicalOutputAsSource, j, i);
    }
    if (!target) return Failure(Error::PhysicalDisplayMissing);
    if (!Equal(target->bounds, draft.physicalBounds)) return Failure(Error::PhysicalDisplayChanged);
    output.draft = draft;
    output.capabilities = capabilities;
    output.policy = policy;
    output.physicalBefore = physicalBefore;
    output.nextAppliedRevision = appliedRevision + 1;
    output.ready = true;
    return {};
}

// This validates only model/revision/physical invariants. The caller must also
// verify observed virtual modes, a valid captured frame, and any input release
// gate before committing state. No applied revision is mutated by this function.
inline Result ValidateCommit(const PreparedPlan& p, std::uint64_t appliedRevision,
    const PhysicalTopology& physicalAfter) {
    if (!p.ready || p.draft.expectedAppliedRevision == std::numeric_limits<std::uint64_t>::max() ||
        p.nextAppliedRevision != p.draft.expectedAppliedRevision + 1) return Failure(Error::InvalidPreparedPlan);
    if (p.draft.expectedAppliedRevision != appliedRevision) return Failure(Error::StaleAppliedRevision);
    // Re-run prepare validation so accidental edits to a local plan fail closed.
    PreparedPlan checked;
    if (auto r = PrepareApply(p.draft, appliedRevision, p.capabilities, p.physicalBefore, checked, p.policy); !r) return r;
    return ValidatePhysicalPreservation(p.physicalBefore, physicalAfter);
}

} // namespace RegionProjection
