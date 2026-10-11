#include "ProjectionPlan.h"
#include "ProjectionProtocol.h"
#include <cstdio>
#include <cstring>
#include <utility>

using namespace RegionProjection;
namespace Wire = RegionProjection::Protocol;
namespace {
unsigned checks = 0, failures = 0;
void Check(bool passed, const char* label) {
    ++checks;
    if (!passed) { ++failures; std::printf("FAIL: %s\n", label); }
}
void Expect(Result result, Error expected, const char* label) {
    ++checks;
    if (result.error != expected) {
        ++failures;
        std::printf("FAIL: %s: expected %s, got %s (index %u)\n", label,
            ErrorName(expected), ErrorName(result.error), result.index);
    }
}
void ExpectWire(Wire::RequestResult result, Wire::RequestError expected, const char* label) {
    ++checks;
    if (result.error != expected) {
        ++failures;
        std::printf("FAIL: %s: wire expected %u, got %u\n", label,
            unsigned(expected), unsigned(result.error));
    }
}
LayoutDraft Layout() {
    LayoutDraft d;
    d.layoutId = {1, 1}; d.draftRevision = 7; d.expectedAppliedRevision = 3;
    d.physicalTarget.value = {2, 10}; d.physicalBounds = {-3840, -1080, 3840, 1080}; d.regionCount = 3;
    for (std::uint32_t i = 0; i < d.regionCount; ++i) {
        auto& r = d.regions[i];
        r.id = {3, 200 + i}; r.source.output.value = {4, 100 + i};
        r.destination.pixels = {-3840 + std::int32_t(i) * 1280, -1080, 1280, 1080};
        r.source.mode = {1280, 1080, {60000, 1001}, ColorSpace::Sdr};
    }
    return d;
}
PhysicalTopology Physical() {
    auto draft = Layout();
    PhysicalTopology p;
    p.count = 2;
    p.displays[0] = {draft.physicalTarget, draft.physicalBounds, {-3840, -1080, 3840, 1040},
        {3840, 1080, {144, 1}, ColorSpace::Hdr10}, 0, true};
    p.displays[1] = {{{2, 11}}, {0, 0, 1920, 1080}, {0, 0, 1920, 1040},
        {1920, 1080, {60, 1}, ColorSpace::Sdr}, 0, false};
    return p;
}
void GeometryTests() {
    auto d = Layout(); Capabilities caps;
    Expect(ValidateDraft(d, caps), Error::None, "negative-origin full partition accepted");
    Check(!Overlaps(d.regions[0].destination.pixels, d.regions[1].destination.pixels), "shared edge is not overlap");
    Check(!Overlaps({0, 0, 64, 64}, {64, 64, 64, 64}), "shared corner is not overlap");
    Check(Overlaps({0, 0, 64, 64}, {63, 63, 64, 64}), "one-pixel overlap detected");
    Check(IsValid({INT32_MIN, INT32_MIN, INT32_MAX, INT32_MAX}), "negative extreme endpoints accepted");
    Check(!IsValid({INT32_MAX, 0, 1, 64}), "positive X endpoint overflow rejected");
    Check(!IsValid({0, INT32_MAX - 32, 64, 64}), "positive Y endpoint overflow rejected");
    Check(!IsValid({INT32_MIN, 0, -1, 64}), "negative width rejected before arithmetic");
    Check(Area({INT32_MIN, INT32_MIN, INT32_MAX, INT32_MAX}) ==
        std::uint64_t(INT32_MAX) * INT32_MAX, "large area uses 64-bit product");
    d.regions[1].destination.pixels.x--;
    auto overlap = ValidateDraft(d, caps);
    Expect(overlap, Error::RegionsOverlap, "one-pixel layout overlap rejected");
    Check(overlap.index == 1 && overlap.relatedIndex == 0, "overlap identifies both cells");
    d = Layout(); d.regions[1].destination.pixels.width--;
    Expect(ValidateDraft(d, caps), Error::IncompletePartition, "one-pixel gap rejected");
    ValidationPolicy partial; partial.requireFullPartition = false;
    Expect(ValidateDraft(d, caps, partial), Error::None, "explicit partial policy allows gap");
    d = Layout(); d.regions[0].destination.pixels.x--;
    Expect(ValidateDraft(d, caps), Error::RegionOutsidePhysical, "negative-side out of bounds rejected");
    d = Layout(); d.regions[2].destination.pixels.width++;
    Expect(ValidateDraft(d, caps), Error::RegionOutsidePhysical, "right edge outside physical monitor rejected");
    d = Layout(); d.regions[0].destination.pixels.width = 63;
    Expect(ValidateDraft(d, caps), Error::RegionTooSmall, "minimum destination width enforced");
    d = Layout(); d.regions[0].destination.pixels.height = 0;
    Expect(ValidateDraft(d, caps), Error::InvalidRegionBounds, "zero region rejected");
    d = Layout(); d.regions[0].destination.pixels = {INT32_MAX, 0, 1280, 1080};
    Expect(ValidateDraft(d, caps), Error::InvalidRegionBounds, "region overflow rejected before containment");
    d = Layout(); d.physicalBounds.width = 0;
    Expect(ValidateDraft(d, caps), Error::InvalidPhysicalBounds, "empty physical bounds rejected");
    d = Layout(); d.regionCount = 10;
    Expect(ValidateDraft(d, caps), Error::InvalidRegionCount, "over-capacity count checked before indexing");
    d.regionCount = 0;
    Expect(ValidateDraft(d, caps), Error::InvalidRegionCount, "empty partition rejected");
    d = Layout(); d.regions[1].id = d.regions[0].id;
    Expect(ValidateDraft(d, caps), Error::DuplicateRegionId, "duplicate stable region ID rejected");
    d = Layout(); d.regions[1].source.output = d.regions[0].source.output;
    Expect(ValidateDraft(d, caps), Error::DuplicateOutputId, "one output cannot own two region records");
    d = Layout(); d.regions[1].source.output.value = d.physicalTarget.value;
    Expect(ValidateDraft(d, caps), Error::PhysicalOutputAsSource, "target physical display cannot become source");
    d = Layout(); d.regions[1].id = {};
    Expect(ValidateDraft(d, caps), Error::InvalidIdentity, "missing region ID rejected");
    d = Layout(); d.draftRevision = 0;
    Expect(ValidateDraft(d, caps), Error::InvalidDraftRevision, "unversioned draft rejected");
    d = Layout(); std::swap(d.regions[0], d.regions[2]);
    Expect(ValidateDraft(d, caps), Error::None, "record order does not define region identity");
    d.regions[0].source.mode = {1920, 1080, {60, 1}, ColorSpace::Sdr};
    Expect(ValidateDraft(d, caps), Error::None, "source size can differ from physical destination");
    ValidationPolicy badPolicy; badPolicy.minDestinationWidth = 0;
    Expect(ValidateDraft(d, caps, badPolicy), Error::InvalidPolicy, "invalid minimum policy rejected");

    // Uneven dimensions model ruler endpoints not divisible by the grid interval.
    d = Layout(); d.physicalBounds = {-2000, -1500, 2003, 1501}; d.regionCount = 9;
    for (std::uint32_t y = 0; y < 3; ++y) for (std::uint32_t x = 0; x < 3; ++x) {
        const auto i = y * 3 + x;
        const auto left = std::int32_t(x * 2003 / 3), right = std::int32_t((x + 1) * 2003 / 3);
        const auto top = std::int32_t(y * 1501 / 3), bottom = std::int32_t((y + 1) * 1501 / 3);
        d.regions[i] = {{3, 200 + i}, {{-2000 + left, -1500 + top, right - left, bottom - top}},
            {{{4, 100 + i}}, {640, 480, {60, 1}, ColorSpace::Sdr}}, true};
    }
    Expect(ValidateDraft(d, caps), Error::None, "nine-cell uneven 2D partition has no rounding gaps");
    d.regions[8].destination.pixels.y--;
    Expect(ValidateDraft(d, caps), Error::RegionsOverlap, "vertical 2D overlap rejected");
}
void CapabilityTests() {
    auto d = Layout(); Capabilities c;
    c.maxActiveOutputs = 2;
    Expect(ValidateDraft(d, c), Error::TooManyActiveOutputs, "provider concurrency limit enforced");
    d.regions[1].enabled = false;
    Expect(ValidateDraft(d, c), Error::None, "disabled cell retains partition without output allocation");
    c = {}; d = Layout(); d.regions[0].source.mode.refresh = {240000, 1001};
    Expect(ValidateDraft(d, c), Error::None, "fractional high refresh below ceiling accepted");
    d.regions[0].source.mode.refresh = {240001, 1000};
    Expect(ValidateDraft(d, c), Error::SourceRefreshUnsupported, "rational refresh above ceiling rejected exactly");
    d.regions[0].source.mode.refresh = {60, 0};
    Expect(ValidateDraft(d, c), Error::InvalidSourceMode, "zero denominator rejected");
    d.regions[0].source.mode.refresh = {0, 1};
    Expect(ValidateDraft(d, c), Error::InvalidSourceMode, "zero numerator rejected");
    d.regions[0].source.mode.refresh = {1, 2};
    Expect(ValidateDraft(d, c), Error::SourceRefreshUnsupported, "refresh floor enforced");
    Check(Equal(RefreshRate{60000, 1000}, RefreshRate{60, 1}), "equivalent rational refresh accepted");
    Check(AtMost({UINT32_MAX, UINT32_MAX}, {UINT32_MAX, UINT32_MAX - 1}), "rational comparison uses 64-bit products");
    d = Layout(); d.regions[0].source.mode.color = ColorSpace::Hdr10;
    Expect(ValidateDraft(d, c), Error::UnsupportedColor, "HDR is explicitly unsupported in v1");
    d.regions[0].source.mode.color = ColorSpace(0xffffffffu);
    Expect(ValidateDraft(d, c), Error::UnsupportedColor, "unknown color rejected");
    d = Layout(); d.regions[0].source.mode.width = 20000;
    Expect(ValidateDraft(d, c), Error::SourceSizeUnsupported, "source width capability enforced");
    d.regions[0].source.mode.width = 0;
    Expect(ValidateDraft(d, c), Error::InvalidSourceMode, "zero source width rejected");
    d = Layout(); c.maxPixelsPerOutput = 1280 * 1080 - 1;
    Expect(ValidateDraft(d, c), Error::OutputPixelLimit, "per-output pixel budget enforced");
    c = {}; c.maxTotalSourcePixels = 1280 * 1080 * 2;
    Expect(ValidateDraft(d, c), Error::TotalPixelLimit, "aggregate pixel budget enforced");
    d.regions[2].enabled = false;
    Expect(ValidateDraft(d, c), Error::None, "aggregate pixel budget excludes disabled outputs");
    c = {}; c.minSourceWidth = c.maxSourceWidth + 1;
    Expect(ValidateDraft(d, c), Error::InvalidCapabilities, "contradictory size capabilities rejected");
    c = {}; c.maxActiveOutputs = MaxRegions + 1;
    Expect(ValidateCapabilities(c), Error::InvalidCapabilities, "provider cannot exceed fixed record capacity");
    c = {}; c.protocolVersion = 2;
    Expect(ValidateCapabilities(c), Error::InvalidCapabilities, "unsupported provider version rejected");
    c = {}; c.maxSourceWidth = UINT32_MAX;
    Expect(ValidateCapabilities(c), Error::InvalidCapabilities, "provider dimensions must fit desktop coordinate model");
    c = {}; c.maxRefresh = {0, 1};
    Expect(ValidateCapabilities(c), Error::InvalidCapabilities, "invalid refresh capability rejected");
}
void RevisionAndPreservationTests() {
    auto d = Layout(); auto before = Physical(); Capabilities c; PreparedPlan plan;
    Expect(PrepareApply(d, 3, c, before, plan), Error::None, "draft prepared against applied revision");
    Check(plan.ready && plan.nextAppliedRevision == 4 && plan.draft.draftRevision == 7,
        "draft revision and applied revision stay distinct");
    d.regions[0].destination.pixels.width--;
    Check(plan.draft.regions[0].destination.pixels.width == 1280, "later editor mutation cannot alter prepared copy");
    Expect(ValidateCommit(plan, 3, before), Error::None, "unchanged physical display state permits model commit");
    Expect(ValidateCommit(plan, 4, before), Error::StaleAppliedRevision, "competing apply invalidates prepared revision");
    auto after = before; std::swap(after.displays[0], after.displays[1]);
    after.displays[1].mode.refresh = {144000, 1000};
    Expect(ValidateCommit(plan, 3, after), Error::None, "display order and equivalent refresh representation are immaterial");
    after = before; after.displays[0].bounds.width = 1280;
    Expect(ValidateCommit(plan, 3, after), Error::InvalidPhysicalTopology, "physically shrinking desktop cannot pass commit");
    after = before; after.displays[0].mode.width = 1280;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "physical resolution change rejected");
    after = before; after.displays[1].mode.refresh = {59, 1};
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "unassigned monitor refresh must also remain unchanged");
    after = before; after.displays[0].primary = false;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "physical primary status change rejected");
    after = before; after.displays[0].workArea.width = 1280;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "taskbar work-area restriction rejected");
    after = before; after.displays[0].bounds.x -= 1; after.displays[0].workArea.x -= 1;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "physical desktop position change rejected");
    after = before; after.displays[0].mode.color = ColorSpace::Sdr;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "existing physical HDR must remain unchanged");
    after = before; after.displays[1].rotationDegrees = 90;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayChanged, "unassigned monitor rotation change rejected");
    after = before; after.count = 1;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalTopologyChanged, "physical removal aborts commit");
    after = before; after.displays[1].id.value.low++;
    Expect(ValidateCommit(plan, 3, after), Error::PhysicalDisplayMissing, "replacement monitor is not mistaken for original");
    after = before; after.displays[1].id = after.displays[0].id;
    Expect(ValidatePhysicalTopology(after), Error::InvalidPhysicalTopology, "duplicate topology IDs rejected");
    auto corrupted = plan; corrupted.nextAppliedRevision++;
    Expect(ValidateCommit(corrupted, 3, before), Error::InvalidPreparedPlan, "malformed prepared revision rejected");
    corrupted = plan; corrupted.draft.regions[1].source.output.value = before.displays[1].id.value;
    Expect(ValidateCommit(corrupted, 3, before), Error::PhysicalOutputAsSource, "commit revalidates every physical output identity");
    d = Layout();
    Expect(PrepareApply(d, 4, c, before, plan), Error::StaleAppliedRevision, "stale draft cannot prepare");
    Check(!plan.ready && plan.nextAppliedRevision == 0, "failed prepare clears previous successful plan");
    Expect(ValidateCommit(plan, 3, before), Error::InvalidPreparedPlan, "unprepared plan cannot commit");
    d.expectedAppliedRevision = UINT64_MAX;
    Expect(PrepareApply(d, UINT64_MAX, c, before, plan), Error::AppliedRevisionOverflow, "applied revision cannot wrap");
    d = Layout(); d.physicalBounds.x--;
    for (std::uint32_t i = 0; i < d.regionCount; ++i) d.regions[i].destination.pixels.x--;
    Expect(PrepareApply(d, 3, c, before, plan), Error::PhysicalDisplayChanged, "stale target geometry rejected before apply");
    d = Layout(); d.physicalTarget.value = {9, 9};
    Expect(PrepareApply(d, 3, c, before, plan), Error::PhysicalDisplayMissing, "unknown physical target rejected");
}
void ProtocolTests() {
    auto d = Layout(); Capabilities caps; Wire::MonitorSetRequestV1 request;
    Expect(Wire::BuildMonitorRequest(d, caps, {8, 9}, Wire::Command::PrepareMonitors, request), Error::None,
        "valid draft builds bounded device request");
    ExpectWire(Wire::ValidateRequest(request, sizeof(request), caps), Wire::RequestError::None,
        "device request independently validates");
    Check(request.monitorCount == 3 && request.monitors[0].width == 1280 && request.expectedRevision == 3,
        "device records contain source modes and expected revision");
    auto shifted = d; shifted.physicalBounds.x += 100;
    for (std::uint32_t i = 0; i < shifted.regionCount; ++i) shifted.regions[i].destination.pixels.x += 100;
    Wire::MonitorSetRequestV1 shiftedRequest;
    Expect(Wire::BuildMonitorRequest(shifted, caps, {8, 9}, Wire::Command::PrepareMonitors, shiftedRequest), Error::None,
        "physically moved layout still constructs virtual mode request");
    Check(std::memcmp(&request, &shiftedRequest, sizeof(request)) == 0,
        "physical coordinates never leak into device records");
    auto bad = request;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad) - 1, caps), Wire::RequestError::WrongSize, "truncated framing rejected");
    bad.structSize--;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::WrongSize, "wrong declared size rejected");
    bad = request; bad.version = 2;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::WrongVersion, "wire version rejected");
    bad = request; bad.monitorCount = UINT32_MAX;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::InvalidHeader, "hostile count rejected before indexing");
    bad = request; bad.command = 99;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::InvalidHeader, "unknown command rejected");
    bad = request; bad.reserved = 1;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::InvalidHeader, "reserved header must be zero");
    bad = request; bad.requestIdHigh = bad.requestIdLow = 0;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::InvalidHeader, "missing request identity rejected");
    bad = request; bad.monitors[0].reserved = 1;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::InvalidRecord, "reserved record must be zero");
    bad = request; bad.monitors[1] = bad.monitors[0];
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::DuplicateOutput, "wire duplicate output rejected");
    bad = request; bad.monitors[0].color = 1;
    auto rejected = Wire::ValidateRequest(bad, sizeof(bad), caps);
    ExpectWire(rejected, Wire::RequestError::CapabilityRejected, "wire HDR cannot bypass mode policy");
    Check(rejected.modelError == Error::UnsupportedColor, "wire diagnostic preserves capability reason");
    bad = request; bad.monitors[8].width = 64;
    ExpectWire(Wire::ValidateRequest(bad, sizeof(bad), caps), Wire::RequestError::NonzeroUnusedRecord,
        "trailing uncounted records cannot smuggle data");
    d.regions[1].enabled = false;
    Expect(Wire::BuildMonitorRequest(d, caps, {8, 9}, Wire::Command::ApplyMonitors, request), Error::None,
        "disabled cell omitted from device desired set");
    Check(request.monitorCount == 2 && request.monitors[1].outputIdLow == 102 && Wire::IsZero(request.monitors[2]),
        "remaining output retains identity and unused record is cleared");
    for (std::uint32_t i = 0; i < d.regionCount; ++i) d.regions[i].enabled = false;
    Expect(Wire::BuildMonitorRequest(d, caps, {8, 9}, Wire::Command::ApplyMonitors, request), Error::None,
        "all-disabled layout produces empty desired monitor set");
    ExpectWire(Wire::ValidateRequest(request, sizeof(request), caps), Wire::RequestError::None,
        "zero desired monitors is valid for removing owned outputs");
    Check(request.monitorCount == 0, "empty desired set preserves layout without allocation");
    d.regions[0].source.mode.width = 0;
    Expect(Wire::BuildMonitorRequest(d, caps, {8, 9}, Wire::Command::ApplyMonitors, request), Error::InvalidSourceMode,
        "invalid build refuses even a disabled malformed descriptor");
    Check(request.magic == 0 && request.monitorCount == 0, "failed build clears stale request");
}
} // namespace

int main() {
    GeometryTests(); CapabilityTests(); RevisionAndPreservationTests(); ProtocolTests();
    std::printf("Projection plan tests: %u checks, %u failures\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
