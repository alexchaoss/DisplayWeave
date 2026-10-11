#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>
#include <vector>

namespace projection_input {
// Physical screen pixels. Only the caller's explicit interaction action may
// invoke Enter. The renderer must already support the source cursor's shape.
struct Config {
    HWND sourceWindow = nullptr;
    HWND ownerWindow = nullptr;
    HWND returnFocusWindow = nullptr;
    DWORD sourcePid = 0;
    RECT sourceClient{};
    RECT projectedImage{};
    DWORD maximumDurationMs = 60000;
    bool allowEntryOutsideImage = false;
    // Optional lease release gate, opened and verified by the caller before
    // entry. The names let the guardian open its own handles. Whoever claims
    // cleanup signals BEGIN once and waits briefly for the lease ACK before
    // focus/cursor restoration lets the source leave exclusive mode.
    HANDLE leaseReleaseBegin = nullptr, leaseReleaseAck = nullptr;
    std::wstring leaseReleaseBeginName, leaseReleaseAckName;
};
struct Status {
    bool active = false, entered = false, guardianReady = false;
    bool clipRestored = false, cursorRestored = false, focusRestored = false;
    bool foreignClipObserved = false, releaseIncomplete = false, guardianRecovered = false;
    bool guardianFocusAttempted = false, guardianFocusRestored = false, guardianFocusSkipped = false;
    bool guardianFocusRequestAccepted = false, focusRequestAttempted = false, focusRequestAccepted = false;
    bool leaseReleaseConfigured = false, leaseReleaseStarted = false, leaseReleaseAcknowledged = false;
    DWORD leaseReleaseError = ERROR_SUCCESS;
    DWORD guardianFocusError = ERROR_SUCCESS;
    DWORD focusRequestError = ERROR_SUCCESS;
    std::string guardianFocusReason = "not_requested";
    std::string focusRequestReason = "not_requested";
    DWORD error = ERROR_SUCCESS;
    unsigned long long elapsedMs = 0;
    std::string reason = "not_entered";
};

// The same UI thread must call all methods and dispatch HandleMessage before
// TranslateMessage/DispatchMessage. No keyboard/mouse hooks or SendInput are used.
// Ctrl+Alt+F10 is registered only for the active session. Escape remains the
// source application's ordinary key. Session destruction releases our state.
class Session {
public:
    Session();
    ~Session();
    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    bool Enter(const Config& config, std::string& error);
    void Tick();
    bool HandleMessage(const MSG& message);
    void Release(const char* reason = "released");
    bool Active() const;
    const Status& GetStatus() const;
private:
    struct Impl;
    Impl* impl;
};

// Must be checked before the presenter's ordinary CLI parser. This branch creates
// no window/capture session and never receives input. It only watches its exact
// creating process and conditionally restores that process's recorded state.
bool TryRunGuardian(int argc, wchar_t** argv, int& exitCode);

// Read-only checks used to defer a real application's entry until its client
// exactly matches the captured rectangle.
bool SourceClientMatches(HWND window, const RECT& expected);
bool ValidLeaseReleaseEventName(const std::wstring& name, const wchar_t* suffix);

namespace policy {
bool ValidRect(const RECT& rectangle);
bool SameRect(const RECT& a, const RECT& b);
bool Contains(const RECT& rectangle, POINT point);
bool Contains(const RECT& outer, const RECT& inner);
bool MapPoint(const RECT& from, const RECT& to, POINT input, POINT& output);
bool MayRestoreClip(bool clipOwned, const RECT& current, const RECT& owned);
bool MayRestoreCursor(bool cursorOwned, POINT current, const RECT& source, POINT destination, const RECT& clip);
bool ClaimCleanup(volatile LONG& state, bool repairLateMutation);
bool ClaimLeaseRelease(volatile LONG& claim);
enum class FocusReturnDecision { Attempt, FocusChanged, SourceUnavailable, ReturnUnavailable, OutputChanged, DesktopChanged };
FocusReturnDecision DecideFocusReturn(bool sourceForeground, bool sourceIdentity, bool returnIdentity, bool outputUnchanged, bool inputDesktop);
enum class FocusAcknowledgment { Reached, Wait, ForegroundChanged, Denied, TimedOut };
FocusAcknowledgment DecideFocusAcknowledgment(bool accepted, UINT_PTR current, UINT_PTR source, UINT_PTR destination, bool expired);
std::vector<std::string> Tests(unsigned& count);
}
} // namespace projection_input
