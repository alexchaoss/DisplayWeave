#pragma once
#include "ProjectionPlan.h"
#include <cstddef>
#include <type_traits>

// Proposed little-endian device records only. No IOCTL dispatch, WDF callbacks,
// authorization, lease implementation, or driver is supplied by this header.
// A future broker must bind Apply to the matching prepared request/content and
// owning session, and implement replay/idempotency; framing alone does not do so.
namespace RegionProjection::Protocol {
inline constexpr std::uint32_t Magic = 0x5253504a; // RSPJ
inline constexpr std::uint32_t Version = 1;
enum class Command : std::uint32_t { PrepareMonitors = 1, ApplyMonitors = 2 };
struct MonitorRecordV1 {
    std::uint64_t outputIdHigh, outputIdLow;
    std::uint32_t width, height, refreshNumerator, refreshDenominator;
    std::uint32_t color, reserved;
};
struct MonitorSetRequestV1 {
    std::uint32_t magic, structSize, version, command;
    std::uint64_t requestIdHigh, requestIdLow, expectedRevision;
    std::uint32_t monitorCount, reserved;
    MonitorRecordV1 monitors[MaxRegions];
};
static_assert(sizeof(MonitorRecordV1) == 40, "Monitor record ABI changed");
static_assert(sizeof(MonitorSetRequestV1) == 408, "Monitor request ABI changed");
static_assert(offsetof(MonitorSetRequestV1, monitors) == 48, "Monitor request alignment changed");
static_assert(std::is_trivially_copyable<MonitorSetRequestV1>::value &&
    std::is_standard_layout<MonitorSetRequestV1>::value, "Wire request must be fixed-width POD");

enum class RequestError { None, WrongSize, WrongVersion, InvalidHeader, InvalidRecord, DuplicateOutput,
    CapabilityRejected, NonzeroUnusedRecord };
struct RequestResult {
    RequestError error = RequestError::None;
    std::uint32_t index = NoIndex;
    Error modelError = Error::None;
    explicit operator bool() const { return error == RequestError::None; }
};
inline bool IsZero(const MonitorRecordV1& r) {
    return !(r.outputIdHigh || r.outputIdLow || r.width || r.height || r.refreshNumerator ||
        r.refreshDenominator || r.color || r.reserved);
}
// Call only after checking an incoming byte buffer has sizeof(request) bytes and
// copying it into aligned local storage. receivedBytes still verifies framing.
inline RequestResult ValidateRequest(const MonitorSetRequestV1& r, std::size_t receivedBytes,
    const Capabilities& caps) {
    if (receivedBytes != sizeof(r) || r.structSize != sizeof(r)) return {RequestError::WrongSize};
    if (r.version != Version) return {RequestError::WrongVersion};
    if (r.magic != Magic || r.reserved != 0 || !(r.requestIdHigh || r.requestIdLow) ||
        (r.command != std::uint32_t(Command::PrepareMonitors) && r.command != std::uint32_t(Command::ApplyMonitors)) ||
        r.monitorCount > MaxRegions) return {RequestError::InvalidHeader};
    if (auto result = ValidateCapabilities(caps); !result)
        return {RequestError::CapabilityRejected, NoIndex, result.error};
    if (r.monitorCount > caps.maxActiveOutputs)
        return {RequestError::CapabilityRejected, NoIndex, Error::TooManyActiveOutputs};
    std::uint64_t pixels = 0;
    for (std::uint32_t i = 0; i < MaxRegions; ++i) {
        const auto& m = r.monitors[i];
        if (i >= r.monitorCount) {
            if (!IsZero(m)) return {RequestError::NonzeroUnusedRecord, i};
            continue;
        }
        if (!(m.outputIdHigh || m.outputIdLow) || m.reserved) return {RequestError::InvalidRecord, i};
        for (std::uint32_t j = 0; j < i; ++j)
            if (m.outputIdHigh == r.monitors[j].outputIdHigh && m.outputIdLow == r.monitors[j].outputIdLow)
                return {RequestError::DuplicateOutput, i};
        const SourceMode mode{m.width, m.height, {m.refreshNumerator, m.refreshDenominator}, ColorSpace(m.color)};
        if (auto result = ValidateSourceMode(mode, caps); !result)
            return {RequestError::CapabilityRejected, i, result.error};
        const auto size = std::uint64_t(m.width) * m.height;
        if (size > caps.maxTotalSourcePixels - pixels)
            return {RequestError::CapabilityRejected, i, Error::TotalPixelLimit};
        pixels += size;
    }
    return {};
}

inline Result BuildMonitorRequest(const LayoutDraft& draft, const Capabilities& caps,
    StableId requestId, Command command, MonitorSetRequestV1& output, const ValidationPolicy& policy = {}) {
    output = {};
    if (!IsValid(requestId)) return Failure(Error::InvalidIdentity);
    if (command != Command::PrepareMonitors && command != Command::ApplyMonitors) return Failure(Error::InvalidPreparedPlan);
    if (auto result = ValidateDraft(draft, caps, policy); !result) return result;
    output.magic = Magic; output.structSize = sizeof(output); output.version = Version;
    output.command = std::uint32_t(command); output.requestIdHigh = requestId.high; output.requestIdLow = requestId.low;
    output.expectedRevision = draft.expectedAppliedRevision;
    for (std::uint32_t i = 0; i < draft.regionCount; ++i) {
        const auto& region = draft.regions[i];
        if (!region.enabled) continue;
        const auto& mode = region.source.mode;
        output.monitors[output.monitorCount++] = {region.source.output.value.high, region.source.output.value.low,
            mode.width, mode.height, mode.refresh.numerator, mode.refresh.denominator, std::uint32_t(mode.color), 0};
    }
    return {};
}
} // namespace RegionProjection::Protocol
