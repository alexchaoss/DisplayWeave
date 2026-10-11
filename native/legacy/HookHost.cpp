#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <aclapi.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <limits.h>
#include <errno.h>
#include <string.h>
#include <vector>
#include <string>
#include "RegionProtocol.h"
#include "RegionLayoutProtocol.h"
#include "VirtualMonitorModel.h"
#include "ShellPolicy.h"
#include "CompatibilityPolicy.h"

static RegionProtocol* shared;
static RegionLayoutProtocol* layoutShared;
static volatile LONG quitRequested;
static volatile LONG requestedActive = 1;
static volatile LONG activationAllowed;
static SRWLOCK configurationLock = SRWLOCK_INIT;
static DWORD threadWakeError, windowWakeError, sentWakeError;

static bool ReadyCapabilities(LONG capabilities) {
    return (capabilities & RS_CAP_USER32) != 0 &&
        (capabilities & (RS_CAP_DXGI | RS_CAP_VIRTUAL_MONITORS)) != 0;
}

static bool ParseLayout(const wchar_t* text, RegionLayoutSnapshot& result) {
    result = {};
    if (!text || !*text) return false;
    const wchar_t* cursor = text;
    while (*cursor && result.count < RS_LAYOUT_MAX_REGIONS) {
        LONG values[4];
        for (int field = 0; field < 4; ++field) {
            // Strict decimal grammar avoids locale, silent overflow and empty fields.
            const wchar_t* digits = cursor;
            if (*digits == L'-' || *digits == L'+') ++digits;
            if (*digits < L'0' || *digits > L'9') return false;
            errno = 0;
            wchar_t* end = NULL;
            long long value = wcstoll(cursor, &end, 10);
            if (errno == ERANGE || value < INT_MIN || value > INT_MAX) return false;
            values[field] = static_cast<LONG>(value);
            cursor = end;
            if (field != 3) { if (*cursor++ != L',') return false; }
        }
        result.regions[result.count++] = {values[0], values[1], values[2], values[3]};
        if (!*cursor) break;
        if (*cursor++ != L';' || !*cursor) return false;
    }
    return !*cursor && RS_ValidLayout(result);
}

static bool OwnsLayout() {
    return layoutShared && layoutShared->magic == RS_LAYOUT_MAGIC &&
        layoutShared->version == RS_LAYOUT_VERSION && layoutShared->size == sizeof(RegionLayoutProtocol) &&
        layoutShared->controllerPid == GetCurrentProcessId() && shared && layoutShared->targetPid == shared->targetPid;
}

static void PublishLayout(const RegionLayoutSnapshot& layout) {
    LONG sequence = InterlockedCompareExchange(&layoutShared->sequence, 0, 0);
    InterlockedExchange(&layoutShared->sequence, sequence | 1);
    layoutShared->magic = RS_LAYOUT_MAGIC; layoutShared->version = RS_LAYOUT_VERSION;
    layoutShared->size = sizeof(RegionLayoutProtocol);
    layoutShared->targetPid = layout.targetPid; layoutShared->controllerPid = layout.controllerPid;
    layoutShared->count = layout.count; layoutShared->reserved = 0;
    for (int i = 0; i < RS_LAYOUT_MAX_REGIONS; ++i) layoutShared->regions[i] = layout.regions[i];
    MemoryBarrier();
    InterlockedIncrement(&layoutShared->sequence);
}

static const char* InitializationStage(LONG capabilities, bool reuse = false) {
    if (reuse && !(capabilities & RS_INIT_MASK)) return "existing worker stage unavailable";
    if (capabilities & RS_INIT_SUBCLASS) return "waiting for fresh worker acknowledgment";
    if (capabilities & (RS_INIT_HOOKS_ENABLED | RS_CAP_USER32 | RS_CAP_DXGI))
        return "waiting for the selected window callback";
    if (capabilities & RS_INIT_HOOK_SETUP) return "installing native compatibility hooks";
    if (capabilities & RS_INIT_DEVICE_READY) return "preparing native compatibility hooks";
    if (capabilities & RS_INIT_WARP) return "software graphics initialization in progress";
    if (capabilities & RS_INIT_HARDWARE) return "hardware graphics initialization in progress";
    if (capabilities & RS_INIT_DISCOVERY) return "creating the graphics discovery window";
    if (capabilities & RS_INIT_WORKER) return "starting the compatibility worker";
    if (capabilities & RS_INIT_CALLBACK) return "waiting for the initialization worker";
    return "no bootstrap callback acknowledged";
}

static void ClearSuccessfulAcknowledgment(volatile LONG* ready) {
    // A callback can fail before or during a reset. Never erase its terminal
    // negative status; a separate read followed by an exchange would race it.
    InterlockedCompareExchange(ready, 0, 1);
}

struct ConfigurationScope {
    ConfigurationScope() { AcquireSRWLockExclusive(&configurationLock); }
    ~ConfigurationScope() { ReleaseSRWLockExclusive(&configurationLock); }
};

struct IpcSecurity {
    SECURITY_ATTRIBUTES attributes;
    SECURITY_DESCRIPTOR descriptor;
    std::vector<BYTE> defaultDacl;
    DWORD labelSid[SECURITY_MAX_SID_SIZE / sizeof(DWORD)];
    DWORD labelAcl[(sizeof(ACL) + sizeof(SYSTEM_MANDATORY_LABEL_ACE) + SECURITY_MAX_SID_SIZE) / sizeof(DWORD) + 1];

    IpcSecurity() : attributes(), descriptor(), labelSid(), labelAcl() {}

    bool Initialize(DWORD& error) {
        // Used only for a confirmed elevated controller and medium target. Copy
        // the creator's DACL without adding principals; a medium/no-write-up
        // label permits ordinary app acknowledgments while excluding low IL.
        // High targets keep default object security instead.
        HANDLE token = NULL;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
            error = GetLastError(); return false;
        }
        DWORD needed = 0;
        GetTokenInformation(token, TokenDefaultDacl, NULL, 0, &needed);
        if (needed < sizeof(TOKEN_DEFAULT_DACL) || needed > 65536) {
            error = GetLastError(); CloseHandle(token);
            if (!error) error = ERROR_INVALID_ACL;
            return false;
        }
        defaultDacl.resize(needed);
        BOOL queried = GetTokenInformation(token, TokenDefaultDacl, defaultDacl.data(), needed, &needed);
        error = queried ? ERROR_SUCCESS : GetLastError();
        CloseHandle(token);
        if (!queried) return false;
        PACL dacl = reinterpret_cast<TOKEN_DEFAULT_DACL*>(defaultDacl.data())->DefaultDacl;
        // A null DACL would allow everybody; do not substitute one if retrieval
        // or an unusual creator token cannot supply a valid default DACL.
        if (!dacl || !IsValidAcl(dacl)) { error = ERROR_INVALID_ACL; return false; }
        SID_IDENTIFIER_AUTHORITY authority = SECURITY_MANDATORY_LABEL_AUTHORITY;
        if (!InitializeSid(labelSid, &authority, 1)) { error = GetLastError(); return false; }
        *GetSidSubAuthority(labelSid, 0) = SECURITY_MANDATORY_MEDIUM_RID;
        PACL sacl = reinterpret_cast<PACL>(labelAcl);
        if (!InitializeAcl(sacl, sizeof(labelAcl), ACL_REVISION) ||
            !AddMandatoryAce(sacl, ACL_REVISION, 0, SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, labelSid) ||
            !InitializeSecurityDescriptor(&descriptor, SECURITY_DESCRIPTOR_REVISION) ||
            !SetSecurityDescriptorDacl(&descriptor, TRUE, dacl, FALSE) ||
            !SetSecurityDescriptorSacl(&descriptor, TRUE, sacl, FALSE)) {
            error = GetLastError(); return false;
        }
        attributes.nLength = sizeof(attributes);
        attributes.lpSecurityDescriptor = &descriptor;
        attributes.bInheritHandle = FALSE;
        return true;
    }
};

struct ControllerMutex {
    HANDLE handle;
    bool owned;
    ControllerMutex() : handle(NULL), owned(false) {}
    ~ControllerMutex() {
        if (owned) ReleaseMutex(handle);
        if (handle) CloseHandle(handle);
    }
    bool Acquire(DWORD pid, SECURITY_ATTRIBUTES* security, DWORD& error) {
        wchar_t name[96];
        swprintf(name, 96, L"Local\\DisplayWeave_Controller_%lu", pid);
        handle = CreateMutexW(security, FALSE, name);
        if (!handle) { error = GetLastError(); return false; }
        DWORD result = WaitForSingleObject(handle, 0);
        owned = result == WAIT_OBJECT_0 || result == WAIT_ABANDONED;
        if (!owned) error = result == WAIT_TIMEOUT ? ERROR_ALREADY_EXISTS : GetLastError();
        return owned;
    }
};

static bool OwnsConfiguration() {
    return shared && shared->controllerPid == GetCurrentProcessId();
}

static bool ValidRegion(int x, int y, int width, int height) {
    return width > 0 && height > 0 &&
        static_cast<int64_t>(x) + width <= INT_MAX && static_cast<int64_t>(y) + height <= INT_MAX;
}

static void Configure(int x, int y, int width, int height, int active) {
    InterlockedIncrement(&shared->sequence);
    shared->x = x; shared->y = y; shared->width = width; shared->height = height;
    RECT rect = { x, y, x + width, y + height };
    shared->targetMonitor = reinterpret_cast<uintptr_t>(MonitorFromRect(&rect, MONITOR_DEFAULTTONEAREST));
    InterlockedExchange(&shared->active, active);
    MemoryBarrier();
    InterlockedIncrement(&shared->sequence);

}

static DWORD WINAPI ReadCommands(void*) {
    char line[2048];
    while (fgets(line, sizeof(line), stdin)) {
        ConfigurationScope guard;
        if (strncmp(line, "QUIT", 4) == 0) {
            InterlockedExchange(&requestedActive, 0);
            if (OwnsConfiguration()) InterlockedExchange(&shared->active, 0);
            InterlockedExchange(&quitRequested, 1);
            break;
        }
        if (!OwnsConfiguration()) { puts("ERROR The application controller changed"); fflush(stdout); break; }
        if (strncmp(line, "OFF", 3) == 0) {
            InterlockedExchange(&requestedActive, 0);
            InterlockedExchange(&shared->active, 0);
            puts("OK OFF"); fflush(stdout);
        } else if (strncmp(line, "ON", 2) == 0) {
            InterlockedExchange(&requestedActive, 1);
            if (InterlockedCompareExchange(&activationAllowed, 0, 0)) InterlockedExchange(&shared->active, 1);
            puts("OK ON"); fflush(stdout);
        } else if (strncmp(line, "MODE ", 5) == 0) {
            char mode[16], extra;
            if (sscanf(line + 5, "%15s %c", mode, &extra) == 1 &&
                (strcmp(mode, "AUTO") == 0 || strcmp(mode, "FORCE") == 0)) {
                InterlockedIncrement(&shared->sequence);
                InterlockedExchange(&shared->mode, strcmp(mode, "AUTO") == 0 ? RS_MODE_AUTO : RS_MODE_FORCE);
                MemoryBarrier();
                InterlockedIncrement(&shared->sequence);
                printf("OK MODE %s\n", mode); fflush(stdout);
            } else { puts("ERROR Expected MODE AUTO or MODE FORCE"); fflush(stdout); }
        } else if (strncmp(line, "LAYOUT ", 7) == 0) {
            std::wstring encoded;
            bool ascii = true;
            for (const unsigned char* p = reinterpret_cast<const unsigned char*>(line + 7); *p && *p != '\r' && *p != '\n'; ++p) {
                if (*p > 127) { ascii = false; break; }
                encoded += static_cast<wchar_t>(*p);
            }
            RegionLayoutSnapshot layout = {};
            if (ascii && OwnsLayout() && ParseLayout(encoded.c_str(), layout)) {
                layout.targetPid = shared->targetPid; layout.controllerPid = GetCurrentProcessId();
                PublishLayout(layout);
                puts("OK LAYOUT"); fflush(stdout);
            } else { puts("ERROR Invalid LAYOUT or layout controller changed"); fflush(stdout); }
        } else if (strncmp(line, "UPDATE ", 7) == 0) {
            int x, y, width, height;
            char extra;
            if (sscanf(line + 7, "%d %d %d %d %c", &x, &y, &width, &height, &extra) == 4 && ValidRegion(x, y, width, height)) {
                Configure(x, y, width, height, shared->active);
                puts("OK UPDATE"); fflush(stdout);
            } else { puts("ERROR Invalid UPDATE rectangle"); fflush(stdout); }
        } else { puts("ERROR Unknown command"); fflush(stdout); }
    }
    ConfigurationScope guard;
    InterlockedExchange(&requestedActive, 0);
    if (OwnsConfiguration()) InterlockedExchange(&shared->active, 0);
    InterlockedExchange(&quitRequested, 1);
    return 0;
}

static bool MatchingArchitecture(HANDLE target) {
    BOOL ours = FALSE, theirs = FALSE;
    if (!IsWow64Process(GetCurrentProcess(), &ours) || !IsWow64Process(target, &theirs)) return false;
    return ours == theirs;
}

struct ProcessAccessLevel {
    bool known, elevated;
    DWORD integrity;
};

static bool IsAdministratorMismatch(const ProcessAccessLevel& ours, const ProcessAccessLevel& target) {
    // Diagnose only a confirmed ordinary medium-to-high elevation mismatch.
    // Unknown tokens and protected/system processes retain their original errors.
    return ours.known && target.known && !ours.elevated && target.elevated &&
        ours.integrity >= SECURITY_MANDATORY_MEDIUM_RID && ours.integrity < SECURITY_MANDATORY_HIGH_RID &&
        target.integrity >= SECURITY_MANDATORY_HIGH_RID && target.integrity < SECURITY_MANDATORY_SYSTEM_RID;
}

static bool NeedsMediumIpc(const ProcessAccessLevel& ours, const ProcessAccessLevel& target) {
    return ours.known && target.known && ours.elevated &&
        ours.integrity >= SECURITY_MANDATORY_HIGH_RID && ours.integrity < SECURITY_MANDATORY_SYSTEM_RID &&
        !target.elevated && target.integrity >= SECURITY_MANDATORY_MEDIUM_RID &&
        target.integrity < SECURITY_MANDATORY_HIGH_RID;
}

static ProcessAccessLevel ReadProcessAccessLevel(HANDLE process) {
    ProcessAccessLevel result = {};
    HANDLE token = NULL;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return result;
    TOKEN_ELEVATION elevation = {};
    DWORD needed = 0;
    if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &needed)) {
        needed = 0;
        GetTokenInformation(token, TokenIntegrityLevel, NULL, 0, &needed);
        if (needed >= sizeof(TOKEN_MANDATORY_LABEL) && needed <= 65536) {
            std::vector<BYTE> information(needed);
            if (GetTokenInformation(token, TokenIntegrityLevel, information.data(), needed, &needed)) {
                TOKEN_MANDATORY_LABEL* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(information.data());
                if (IsValidSid(label->Label.Sid)) {
                    BYTE count = *GetSidSubAuthorityCount(label->Label.Sid);
                    if (count != 0) {
                        result.integrity = *GetSidSubAuthority(label->Label.Sid, count - 1);
                        result.elevated = elevation.TokenIsElevated != 0;
                        result.known = true;
                    }
                }
            }
        }
    }
    CloseHandle(token);
    return result;
}

static int Error(const char* message, DWORD code) {
    // Windows reserves bit 29 for application-defined statuses. Do not label
    // controller observations as errors returned by a remote Windows API.
    if (code & 0x20000000u) printf("ERROR %s (DisplayWeave status 0x%08lX)\n", message, code);
    else printf("ERROR %s (Windows error %lu)\n", message, code);
    fflush(stdout);
    return 1;
}

static bool VerifyIpcSecurity(HANDLE object, IpcSecurity& expected, GENERIC_MAPPING rights) {
    PSECURITY_DESCRIPTOR descriptor = NULL;
    PACL dacl = NULL, sacl = NULL;
    DWORD result = GetSecurityInfo(object, SE_KERNEL_OBJECT,
        DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, NULL, NULL, &dacl, &sacl, &descriptor);
    PACL source = reinterpret_cast<TOKEN_DEFAULT_DACL*>(expected.defaultDacl.data())->DefaultDacl;
    bool valid = result == ERROR_SUCCESS && dacl && sacl && sacl->AceCount == 1 &&
        dacl->AceCount == source->AceCount;
    if (!valid) printf("IPC test descriptor: error=%lu dacl=%d sacl=%d sourceAces=%u actualAces=%u labelAces=%u\n",
        result, dacl != NULL, sacl != NULL, source->AceCount, dacl ? dacl->AceCount : 0, sacl ? sacl->AceCount : 0);
    for (DWORD i = 0; valid && i < source->AceCount; ++i) {
        void *actualAce = NULL, *sourceAce = NULL;
        valid = GetAce(dacl, i, &actualAce) && GetAce(source, i, &sourceAce) &&
            reinterpret_cast<ACE_HEADER*>(actualAce)->AceSize == reinterpret_cast<ACE_HEADER*>(sourceAce)->AceSize;
        if (valid) {
            // Windows expands generic rights when creating each kernel object.
            // Compare the effective ACE, including its unchanged SID and flags.
            BYTE* bytes = static_cast<BYTE*>(sourceAce);
            std::vector<BYTE> normalized(bytes, bytes + reinterpret_cast<ACE_HEADER*>(sourceAce)->AceSize);
            ACE_HEADER* header = reinterpret_cast<ACE_HEADER*>(normalized.data());
            if (header->AceType == ACCESS_ALLOWED_ACE_TYPE || header->AceType == ACCESS_DENIED_ACE_TYPE)
                MapGenericMask(&reinterpret_cast<ACCESS_ALLOWED_ACE*>(normalized.data())->Mask, &rights);
            valid = memcmp(actualAce, normalized.data(), normalized.size()) == 0;
        }
    }
    void* label = NULL;
    if (valid) {
        valid = GetAce(sacl, 0, &label) != FALSE;
        if (valid) {
            SYSTEM_MANDATORY_LABEL_ACE* ace = reinterpret_cast<SYSTEM_MANDATORY_LABEL_ACE*>(label);
            valid = ace->Header.AceType == SYSTEM_MANDATORY_LABEL_ACE_TYPE &&
                ace->Mask == SYSTEM_MANDATORY_LABEL_NO_WRITE_UP && EqualSid(&ace->SidStart, expected.labelSid);
        }
    }
    if (descriptor) LocalFree(descriptor);
    return valid;
}

static int IpcSecuritySelfTest() {
    // Exercise real kernel objects without accessing any application or asking
    // for elevation. Unique names avoid the production IPC namespace entirely.
    IpcSecurity security;
    DWORD error = 0;
    if (!security.Initialize(error)) return Error("IPC security descriptor test failed", error);
    wchar_t name[128];
    swprintf(name, 128, L"Local\\DisplayWeave_IpcTest_%lu_%llu", GetCurrentProcessId(), GetTickCount64());
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &security.attributes, PAGE_READWRITE, 0, 4096, name);
    if (!mapping) return Error("IPC mapping creation test failed", GetLastError());
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(mapping); return Error("IPC test name collision", ERROR_ALREADY_EXISTS); }
    GENERIC_MAPPING sectionRights = { STANDARD_RIGHTS_READ | SECTION_QUERY | SECTION_MAP_READ,
        STANDARD_RIGHTS_WRITE | SECTION_MAP_WRITE, STANDARD_RIGHTS_EXECUTE | SECTION_MAP_EXECUTE, SECTION_ALL_ACCESS };
    bool valid = VerifyIpcSecurity(mapping, security, sectionRights);
    HANDLE reopened = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    LONG* view = reopened ? static_cast<LONG*>(MapViewOfFile(reopened, FILE_MAP_ALL_ACCESS, 0, 0, 4096)) : NULL;
    if (view) { *view = 123; valid = valid && *view == 123; UnmapViewOfFile(view); }
    else valid = false;
    if (reopened) CloseHandle(reopened);
    CloseHandle(mapping);
    HANDLE mutex = CreateMutexW(&security.attributes, FALSE, NULL);
    if (!mutex) return Error("IPC mutex creation test failed", GetLastError());
    GENERIC_MAPPING mutexRights = { STANDARD_RIGHTS_READ | MUTEX_MODIFY_STATE,
        STANDARD_RIGHTS_WRITE | MUTEX_MODIFY_STATE, STANDARD_RIGHTS_EXECUTE | SYNCHRONIZE, MUTEX_ALL_ACCESS };
    valid = valid && VerifyIpcSecurity(mutex, security, mutexRights);
    DWORD waited = WaitForSingleObject(mutex, 0);
    valid = valid && waited == WAIT_OBJECT_0;
    if (waited == WAIT_OBJECT_0) ReleaseMutex(mutex);
    CloseHandle(mutex);
    if (!valid) return Error("IPC security or access test failed", ERROR_INVALID_DATA);
    puts("PASS IPC security: creator DACL preserved, medium/no-write-up labels, mapping read/write, mutex ownership; no application accessed");
    return 0;
}

static bool ValidPathComponent(const std::wstring& part) {
    if (part.empty() || part.back() == L'.' || part.back() == L' ') return false;
    for (size_t i = 0; i < part.size(); ++i)
        if (part[i] < 0x20 || wcschr(L":*?\"<>|", part[i])) return false;
    return true;
}

// Lexical, deterministic normalization: no current-directory lookup, file open,
// or symlink resolution. Ambiguous/device/relative paths fail closed. Distinct
// installation directories remain distinct even if they contain identical DLLs.
static bool NormalizeAbsolutePath(const wchar_t* input, std::wstring& result) {
    result.clear();
    if (!input || !*input) return false;
    std::wstring path(input);
    for (size_t i = 0; i < path.size(); ++i) if (path[i] == L'/') path[i] = L'\\';
    if (path.size() >= 8 && _wcsnicmp(path.c_str(), L"\\\\?\\UNC\\", 8) == 0)
        path = L"\\\\" + path.substr(8);
    else if (path.compare(0, 4, L"\\\\?\\") == 0) path.erase(0, 4);
    if (path.compare(0, 4, L"\\\\.\\") == 0) return false;
    std::wstring root;
    size_t start;
    if (path.size() >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') ||
        (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' && path[2] == L'\\') {
        root = path.substr(0, 3); start = 3;
    } else if (path.compare(0, 2, L"\\\\") == 0) {
        size_t serverEnd = path.find(L'\\', 2);
        if (serverEnd == std::wstring::npos) return false;
        size_t shareEnd = path.find(L'\\', serverEnd + 1);
        if (shareEnd == std::wstring::npos || !ValidPathComponent(path.substr(2, serverEnd - 2)) ||
            !ValidPathComponent(path.substr(serverEnd + 1, shareEnd - serverEnd - 1))) return false;
        root = path.substr(0, shareEnd + 1); start = shareEnd + 1;
    } else return false;
    std::vector<std::wstring> parts;
    while (start < path.size()) {
        size_t end = path.find(L'\\', start);
        if (end == std::wstring::npos) end = path.size();
        std::wstring part = path.substr(start, end - start);
        if (part == L"..") {
            if (parts.empty()) return false;
            parts.pop_back();
        } else if (!part.empty() && part != L".") {
            if (!ValidPathComponent(part)) return false;
            parts.push_back(part);
        }
        start = end + 1;
    }
    if (parts.empty() || path.back() == L'\\') return false;
    result = root;
    for (size_t i = 0; i < parts.size(); ++i) {
        if (i) result += L'\\';
        result += parts[i];
    }
    return true;
}

static bool SameAbsolutePath(const wchar_t* first, const wchar_t* second) {
    std::wstring a, b;
    return NormalizeAbsolutePath(first, a) && NormalizeAbsolutePath(second, b) &&
        CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

#include "BootstrapLoader.h"

enum HandoffPolicy { HandoffFresh, HandoffReuse, HandoffInvalidMapping, HandoffFailedWorker };

// A resident worker keeps this build's control mapping open for the target's lifetime, so
// the mapping, not a module named RegionHook.dll, identifies it. A RegionHook.dll from an
// earlier product name or protocol stays pass-through beside a freshly loaded hook.
static HandoffPolicy DecideHandoff(bool residentMapping, bool validMapping, bool liveWorker) {
    if (!residentMapping) return HandoffFresh;
    if (!validMapping) return HandoffInvalidMapping;
    return liveWorker ? HandoffReuse : HandoffFailedWorker;
}

static bool DeadControllerResult(bool opened, DWORD openError, DWORD waitResult) {
    return opened ? waitResult == WAIT_OBJECT_0 : openError == ERROR_INVALID_PARAMETER;
}

static int PolicySelfTest() {
    struct TextCase { const wchar_t* value; bool blocked; };
    const TextCase processCases[] = {
        {L"explorer.exe", true}, {L"C:\\Windows\\EXPLORER.EXE", true},
        {L"C:/Windows/sihost.exe", true}, {L"dwm.exe", true},
        {L"ShellExperienceHost.exe", true}, {L"StartMenuExperienceHost.exe", true},
        {L"ShellHost.exe", true}, {L"CredentialUIBroker.exe", true},
        {L"SearchHost.exe", true}, {L"SearchApp.exe", true}, {L"SearchUI.exe", true},
        {L"LockApp.exe", true}, {L"LogonUI.exe", true}, {L"Winlogon.exe", true},
        {L"TextInputHost.exe", true}, {L"InputApp.exe", true},
        {L"RegionDxgiProbe.exe", false}, {L"ChatGPT.exe", false},
        {L"C:\\Apps\\explorer.exe.backup", false}, {L"myexplorer.exe", false},
        {L"C:\\explorer.exe\\editor.exe", false}, {NULL, false}, {L"", false}
    };
    const TextCase classCases[] = {
        {L"Progman", true}, {L"workerw", true}, {L"Shell_TrayWnd", true},
        {L"Shell_SecondaryTrayWnd", true}, {L"SHELLDLL_DefView", true},
        {L"CabinetWClass", true}, {L"ExploreWClass", true},
        {L"Editor_MainWindow", false}, {L"RegionDxgiProbe", false},
        {L"SysListView32", false}, {L"Windows.UI.Core.CoreWindow", false},
        {L"Shell_TrayWndExtra", false}, {NULL, false}, {L"", false}
    };
    int count = 0;
    const TextCase launcherCases[] = {
        {L"Battle.net.exe", true}, {L"battle.NET LAUNCHER.exe", true}, {L"BATTLE.NET Helper.exe", true},
        {L"C:\\Apps\\Battle.net.exe", true}, {L"C:/Apps/Battle.net Launcher.exe", true},
        {L"D:\\Tools\\Battle.net Helper.exe", true},
        {L"Game.exe", false}, {L"Game64.exe", false}, {L"Client.exe", false},
        {L"Shooter.exe", false}, {L"Agent.exe", false}, {L"MyBattle.net.exe", false},
        {L"Battle.net.exe.backup", false}, {L"Battle.netGame.exe", false},
        {L"C:\\Battle.net.exe\\Game.exe", false}, {L"C:/Games/Battle.net/Game.exe", false},
        {L"Battle.net", false}, {L"", false}, {NULL, false}
    };
    for (size_t i = 0; i < sizeof(launcherCases) / sizeof(launcherCases[0]); ++i) {
        ++count;
        if (RS_IsIncompatibleLauncher(launcherCases[i].value) != launcherCases[i].blocked)
            return Error("Launcher compatibility policy self-test failed", ERROR_INVALID_DATA);
    }
    for (size_t i = 0; i < sizeof(processCases) / sizeof(processCases[0]); ++i) {
        ++count;
        if (RS_IsShellProcessName(processCases[i].value) != processCases[i].blocked)
            return Error("Shell process policy self-test failed", ERROR_INVALID_DATA);
    }
    for (size_t i = 0; i < sizeof(classCases) / sizeof(classCases[0]); ++i) {
        ++count;
        if (RS_IsShellWindowClass(classCases[i].value) != classCases[i].blocked)
            return Error("Shell class policy self-test failed", ERROR_INVALID_DATA);
    }
    struct OwnerCase { uint32_t target, shell, desktop; bool taskbar, blocked; };
    const OwnerCase owners[] = {
        {10, 10, 20, false, true}, {20, 10, 20, false, true},
        {30, 10, 20, true, true}, {30, 10, 20, false, false},
        {30, 0, 0, false, false}, {0, 0, 0, true, false}
    };
    for (size_t i = 0; i < sizeof(owners) / sizeof(owners[0]); ++i) {
        ++count;
        if (RS_IsShellOwner(owners[i].target, owners[i].shell, owners[i].desktop,
            owners[i].taskbar) != owners[i].blocked)
            return Error("Shell owner policy self-test failed", ERROR_INVALID_DATA);
    }
    if (!RS_IsTaskbarClass(L"shell_secondarytraywnd") || !RS_IsTaskbarClass(L"Shell_TrayWnd") ||
        RS_IsTaskbarClass(L"CabinetWClass") || RS_IsTaskbarClass(NULL))
        return Error("Taskbar class policy self-test failed", ERROR_INVALID_DATA);
    struct PathCase { const wchar_t* first; const wchar_t* second; bool same; };
    const PathCase paths[] = {
        {L"C:\\Apps\\v1\\RegionHook.dll", L"c:\\apps\\V1\\REGIONHOOK.DLL", true},
        {L"C:/Apps/v1/RegionHook.dll", L"C:\\Apps\\v1\\RegionHook.dll", true},
        {L"C:\\Apps\\v1\\.\\RegionHook.dll", L"C:\\Apps\\v1\\RegionHook.dll", true},
        {L"C:\\Apps\\v1\\temp\\..\\RegionHook.dll", L"C:\\Apps\\v1\\RegionHook.dll", true},
        {L"\\\\?\\C:\\Apps\\v1\\RegionHook.dll", L"C:\\Apps\\v1\\RegionHook.dll", true},
        {L"\\\\?\\UNC\\server\\share\\v1\\RegionHook.dll", L"\\\\SERVER\\share\\v1\\RegionHook.dll", true},
        {L"C:\\Apps\\v1\\RegionHook.dll", L"C:\\Apps\\v2\\RegionHook.dll", false},
        {L"C:\\Apps\\v1\\RegionHook.dll", L"D:\\Apps\\v1\\RegionHook.dll", false},
        {L"\\\\server\\share\\v1\\RegionHook.dll", L"\\\\server\\share\\v2\\RegionHook.dll", false},
        {L"RegionHook.dll", L"RegionHook.dll", false},
        {L"C:RegionHook.dll", L"C:RegionHook.dll", false},
        {L"C:\\..\\RegionHook.dll", L"C:\\RegionHook.dll", false},
        {L"C:\\Apps\\RegionHook.dll.", L"C:\\Apps\\RegionHook.dll", false},
        {L"C:\\Apps\\RegionHook.dll:stream", L"C:\\Apps\\RegionHook.dll:stream", false},
        {NULL, L"C:\\Apps\\RegionHook.dll", false}
    };
    for (size_t i = 0; i < sizeof(paths) / sizeof(paths[0]); ++i) {
        ++count;
        if (SameAbsolutePath(paths[i].first, paths[i].second) != paths[i].same)
            return Error("Resident hook path policy self-test failed", ERROR_INVALID_DATA);
    }
    struct HandoffCase { bool resident, mapping, worker; HandoffPolicy expected; };
    const HandoffCase handoffs[] = {
        {false, false, false, HandoffFresh}, {false, true, true, HandoffFresh},
        {true, true, true, HandoffReuse}, {true, false, true, HandoffInvalidMapping},
        {true, true, false, HandoffFailedWorker}, {true, false, false, HandoffInvalidMapping}
    };
    for (size_t i = 0; i < sizeof(handoffs) / sizeof(handoffs[0]); ++i) {
        ++count;
        if (DecideHandoff(handoffs[i].resident, handoffs[i].mapping, handoffs[i].worker) != handoffs[i].expected)
            return Error("Resident handoff policy self-test failed", ERROR_INVALID_DATA);
    }
    struct ControllerCase { bool opened; DWORD error, wait; bool dead; };
    const ControllerCase controllers[] = {
        {true, ERROR_SUCCESS, WAIT_OBJECT_0, true}, {true, ERROR_SUCCESS, WAIT_TIMEOUT, false},
        {true, ERROR_SUCCESS, WAIT_FAILED, false}, {false, ERROR_INVALID_PARAMETER, WAIT_FAILED, true},
        {false, ERROR_ACCESS_DENIED, WAIT_FAILED, false}
    };
    for (size_t i = 0; i < sizeof(controllers) / sizeof(controllers[0]); ++i) {
        ++count;
        if (DeadControllerResult(controllers[i].opened, controllers[i].error, controllers[i].wait) != controllers[i].dead)
            return Error("Previous controller policy self-test failed", ERROR_INVALID_DATA);
    }
    struct ElevationCase { ProcessAccessLevel ours, target; bool mismatch; };
    const ElevationCase elevations[] = {
        {{true, false, 0x2000}, {true, true, 0x3000}, true},
        {{true, false, 0x2100}, {true, true, 0x3000}, true},
        {{true, true, 0x3000}, {true, true, 0x3000}, false},
        {{true, false, 0x2000}, {true, false, 0x2000}, false},
        {{true, false, 0x2000}, {true, false, 0x3000}, false},
        {{true, true, 0x2000}, {true, true, 0x3000}, false},
        {{false, false, 0}, {true, true, 0x3000}, false},
        {{true, false, 0x2000}, {false, true, 0x3000}, false},
        {{true, false, 0x2000}, {true, true, 0x4000}, false},
        {{true, false, 0x1000}, {true, true, 0x3000}, false}
    };
    for (size_t i = 0; i < sizeof(elevations) / sizeof(elevations[0]); ++i) {
        ++count;
        if (IsAdministratorMismatch(elevations[i].ours, elevations[i].target) != elevations[i].mismatch)
            return Error("Administrator mismatch policy self-test failed", ERROR_INVALID_DATA);
    }
    const ElevationCase ipcCases[] = {
        {{true, true, 0x3000}, {true, false, 0x2000}, true},
        {{true, true, 0x3000}, {true, false, 0x2100}, true},
        {{true, true, 0x3000}, {true, true, 0x3000}, false},
        {{true, false, 0x2000}, {true, false, 0x2000}, false},
        {{true, true, 0x3000}, {true, false, 0x1000}, false},
        {{false, true, 0x3000}, {true, false, 0x2000}, false},
        {{true, true, 0x3000}, {false, false, 0x2000}, false},
        {{true, true, 0x4000}, {true, false, 0x2000}, false},
        {{true, false, 0x3000}, {true, false, 0x2000}, false},
        {{true, true, 0x3000}, {true, true, 0x2000}, false}
    };
    for (size_t i = 0; i < sizeof(ipcCases) / sizeof(ipcCases[0]); ++i) {
        ++count;
        if (NeedsMediumIpc(ipcCases[i].ours, ipcCases[i].target) != ipcCases[i].mismatch)
            return Error("Cross-integrity IPC policy self-test failed", ERROR_INVALID_DATA);
    }
    struct StageCase { LONG bits; const char* expected; bool reuse; };
    const StageCase stages[] = {
        {0, "no bootstrap callback acknowledged"},
        {RS_INIT_CALLBACK, "waiting for the initialization worker"},
        {RS_INIT_CALLBACK | RS_INIT_WORKER, "starting the compatibility worker"},
        {RS_INIT_DISCOVERY, "creating the graphics discovery window"},
        {RS_INIT_HARDWARE, "hardware graphics initialization in progress"},
        {RS_INIT_HARDWARE | RS_INIT_WARP, "software graphics initialization in progress"},
        {RS_INIT_DEVICE_READY, "preparing native compatibility hooks"},
        {RS_INIT_HOOK_SETUP, "installing native compatibility hooks"},
        {RS_INIT_HOOKS_ENABLED, "waiting for the selected window callback"},
        {RS_CAP_USER32 | RS_CAP_DXGI, "waiting for the selected window callback"},
        {RS_INIT_MASK | RS_CAP_USER32 | RS_CAP_DXGI, "waiting for fresh worker acknowledgment"},
        {0, "existing worker stage unavailable", true},
        {RS_CAP_USER32 | RS_CAP_DXGI, "existing worker stage unavailable", true}
    };
    for (size_t i = 0; i < sizeof(stages) / sizeof(stages[0]); ++i) {
        ++count;
        if (strcmp(InitializationStage(stages[i].bits, stages[i].reuse), stages[i].expected) != 0)
            return Error("Initialization diagnostics policy self-test failed", ERROR_INVALID_DATA);
    }
    struct ReadyCase { LONG before, after; };
    const ReadyCase readyCases[] = {{1, 0}, {0, 0}, {-1, -1}, {-5, -5}, {2, 2}};
    for (size_t i = 0; i < sizeof(readyCases) / sizeof(readyCases[0]); ++i) {
        ++count;
        volatile LONG ready = readyCases[i].before;
        ClearSuccessfulAcknowledgment(&ready);
        if (ready != readyCases[i].after)
            return Error("Acknowledgment reset atomic self-test failed", ERROR_INVALID_DATA);
    }
    struct CapabilityCase { LONG bits; bool ready; };
    const CapabilityCase capabilities[] = {
        {0, false}, {1, false}, {2, false}, {3, true}, {4, false}, {5, true}, {6, false}, {7, true},
        {RS_INIT_MASK, false}, {RS_INIT_MASK | 3, true}, {RS_INIT_MASK | 5, true}
    };
    for (size_t i = 0; i < sizeof(capabilities) / sizeof(capabilities[0]); ++i) {
        ++count;
        if (ReadyCapabilities(capabilities[i].bits) != capabilities[i].ready)
            return Error("Native readiness capability self-test failed", ERROR_INVALID_DATA);
    }
    const TextCase layoutCases[] = {
        {L"0,0,1920,1080", true}, {L"-1920,0,1920,1080;0,0,1920,1080", true},
        {L"0,0,100,100;100,0,100,100;0,100,100,100", true},
        {L"-2147483648,-2147483648,1,1", true}, {L"2147483646,0,1,1", true},
        {L"0,0,10,10;9,0,10,10", false}, {L"0,0,10,10;0,0,10,10", false},
        {L"0,0,0,10", false}, {L"0,0,-1,10", false}, {L"0,0,10,0", false},
        {L"2147483647,0,1,1", false}, {L"0,2147483647,1,1", false},
        {L"2147483648,0,1,1", false}, {L"-2147483649,0,1,1", false},
        {L"99999999999999999999999999,0,1,1", false}, {L"0,0,1,1;", false},
        {L"0,0,,1", false}, {L"0,0,1", false}, {L"0,0,1,1garbage", false},
        {L"0,0,1,1;;1,0,1,1", false}, {L"0, 0,1,1", false}, {L"", false}, {NULL, false},
        {L"0,0,1,1;1,0,1,1;2,0,1,1;3,0,1,1;4,0,1,1;5,0,1,1;6,0,1,1;7,0,1,1;8,0,1,1", true},
        {L"0,0,1,1;1,0,1,1;2,0,1,1;3,0,1,1;4,0,1,1;5,0,1,1;6,0,1,1;7,0,1,1;8,0,1,1;9,0,1,1", false}
    };
    for (size_t i = 0; i < sizeof(layoutCases) / sizeof(layoutCases[0]); ++i) {
        ++count;
        RegionLayoutSnapshot layout = {};
        if (ParseLayout(layoutCases[i].value, layout) != layoutCases[i].blocked)
            return Error("Region layout parsing self-test failed", ERROR_INVALID_DATA);
    }
    struct AddressCase { uintptr_t local; DWORD size; uintptr_t entry, remote; DWORD remoteSize; bool valid; uintptr_t expected; };
    const AddressCase addresses[] = {
        {0x10000, 0x1000, 0x10200, 0x30000, 0x1000, true, 0x30200},
        {0x30000, 0x1000, 0x30200, 0x10000, 0x1000, true, 0x10200},
        {0x10000, 0x1000, 0x10000, 0x30000, 0x1000, true, 0x30000},
        {0x10000, 0x1000, 0x10fff, 0x30000, 0x1000, true, 0x30fff},
        {0x10000, 0x1000, 0x11000, 0x30000, 0x1000, false, 0},
        {0x10000, 0x1000, 0xffff, 0x30000, 0x1000, false, 0},
        {0x10000, 0x1000, 0x10200, 0x30000, 0x2000, false, 0},
        {0x10000, 0x1000, 0x10200, UINTPTR_MAX - 0x800, 0x1000, false, 0},
        {UINTPTR_MAX - 0x800, 0x1000, UINTPTR_MAX - 0x200, 0x30000, 0x1000, false, 0},
        {0, 0x1000, 0x200, 0x30000, 0x1000, false, 0},
        {0x10000, 0, 0x10000, 0x30000, 0, false, 0},
        {0x10000, 0x1000, 0x10200, 0, 0x1000, false, 0}
    };
    for (size_t i = 0; i < sizeof(addresses) / sizeof(addresses[0]); ++i) {
        ++count; uintptr_t result = 1;
        const AddressCase& c = addresses[i];
        if (BootstrapAddress(c.local, c.size, c.entry, c.remote, c.remoteSize, result) != c.valid || result != c.expected)
            return Error("Bootstrap address validation self-test failed", ERROR_INVALID_DATA);
    }
    RegionLayoutProtocol sidecar = {};
    RegionSnapshot control = {}; control.targetPid = 10; control.controllerPid = 20;
    sidecar.magic = RS_LAYOUT_MAGIC; sidecar.version = RS_LAYOUT_VERSION; sidecar.size = sizeof(sidecar);
    sidecar.targetPid = 10; sidecar.controllerPid = 20; sidecar.count = 1;
    sidecar.regions[0] = {0, 0, 100, 100};
    RegionLayoutSnapshot snapshot = {};
    ++count; if (!RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout valid sidecar test failed", ERROR_INVALID_DATA);
    sidecar.sequence = 1;
    ++count; if (RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout partial update test failed", ERROR_INVALID_DATA);
    sidecar.sequence = 2; sidecar.controllerPid = 21;
    ++count; if (RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout owner identity test failed", ERROR_INVALID_DATA);
    sidecar.controllerPid = 20; sidecar.targetPid = 11;
    ++count; if (RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout target identity test failed", ERROR_INVALID_DATA);
    sidecar.targetPid = 10; sidecar.version = 2;
    ++count; if (RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout incompatible header test failed", ERROR_INVALID_DATA);
    sidecar.version = RS_LAYOUT_VERSION; sidecar.count = 10;
    ++count; if (RS_ReadLayout(&sidecar, control, snapshot)) return Error("Layout capacity test failed", ERROR_INVALID_DATA);
    const RECT monitors[] = {{-1000, -500, 0, 500}, {0, -500, 1000, 500}, {1000, -500, 2000, 500}};
    struct MonitorCase { RECT query; DWORD flags; int expected; };
    const MonitorCase selections[] = {
        {{-999, -499, -999, -499}, MONITOR_DEFAULTTONULL, 0},
        {{-1, 0, -1, 0}, MONITOR_DEFAULTTONULL, 0},
        {{0, 0, 0, 0}, MONITOR_DEFAULTTONULL, 1},
        {{1000, 0, 1000, 0}, MONITOR_DEFAULTTONULL, 2},
        {{2000, 0, 2000, 0}, MONITOR_DEFAULTTONULL, -1},
        {{0, 500, 0, 500}, MONITOR_DEFAULTTONULL, -1},
        {{1000, 0, 1000, 100}, MONITOR_DEFAULTTONULL, 2},
        {{1000, 0, 1100, 0}, MONITOR_DEFAULTTONULL, 2},
        {{-100, 0, 200, 100}, MONITOR_DEFAULTTONULL, 1},
        {{-200, 0, 200, 100}, MONITOR_DEFAULTTONULL, 1},
        {{900, 0, 1300, 100}, MONITOR_DEFAULTTOPRIMARY, 2},
        {{-3000, 0, -2000, 100}, MONITOR_DEFAULTTONULL, -1},
        {{-3000, 0, -2000, 100}, MONITOR_DEFAULTTOPRIMARY, 1},
        {{-3000, 0, -2000, 100}, MONITOR_DEFAULTTONEAREST, 0},
        {{INT_MAX, INT_MAX, INT_MAX, INT_MAX}, MONITOR_DEFAULTTONEAREST, 2},
        {{INT_MIN, INT_MIN, INT_MIN, INT_MIN}, MONITOR_DEFAULTTONEAREST, 0},
        {{0, 700, 0, 700}, MONITOR_DEFAULTTONEAREST, 1}
    };
    for (size_t i = 0; i < sizeof(selections) / sizeof(selections[0]); ++i) {
        ++count;
        if (RegionVirtualMonitor::Select(monitors, 3, 1, selections[i].query, selections[i].flags) != selections[i].expected)
            return Error("Virtual monitor selection geometry self-test failed", ERROR_INVALID_DATA);
    }
    const RECT gapMonitors[] = {{0, 0, 100, 100}, {200, 0, 300, 100}};
    RECT gap = {150, 50, 150, 50};
    ++count; if (RegionVirtualMonitor::Select(gapMonitors, 2, 1, gap, MONITOR_DEFAULTTONEAREST) != 1)
        return Error("Virtual monitor nearest primary tie test failed", ERROR_INVALID_DATA);
    ++count; if (RegionVirtualMonitor::Select(gapMonitors, 2, 0, gap, MONITOR_DEFAULTTONEAREST) != 0)
        return Error("Virtual monitor alternate primary tie test failed", ERROR_INVALID_DATA);
    ++count; if (RegionVirtualMonitor::Select(NULL, 3, 1, gap, MONITOR_DEFAULTTOPRIMARY) != -1 ||
        RegionVirtualMonitor::Select(monitors, 0, 0, gap, MONITOR_DEFAULTTOPRIMARY) != -1 ||
        RegionVirtualMonitor::Select(monitors, 3, 3, gap, MONITOR_DEFAULTTOPRIMARY) != -1)
        return Error("Virtual monitor invalid model test failed", ERROR_INVALID_DATA);
    struct LoaderCase { DWORD raw, wait, processExit; bool inspected; size_t copies; bool exact; BootstrapOutcome expected; };
    const LoaderCase loaderCases[] = {
        {0, WAIT_TIMEOUT, 0, true, 1, true, BootstrapVerified},
        {0x12340000, WAIT_TIMEOUT, 0, true, 1, true, BootstrapVerified},
        {STILL_ACTIVE, WAIT_TIMEOUT, 0, true, 1, true, BootstrapVerified},
        {0xc0000005u, WAIT_TIMEOUT, 0, true, 1, true, BootstrapVerified},
        {0, WAIT_TIMEOUT, 0, true, 0, false, BootstrapModuleMissing},
        {0x12340000, WAIT_TIMEOUT, 0, true, 0, false, BootstrapModuleMissing},
        {STILL_ACTIVE, WAIT_TIMEOUT, 0, true, 0, false, BootstrapModuleMissing},
        {0xc0000005u, WAIT_TIMEOUT, 0, true, 0, false, BootstrapModuleMissing},
        {0, WAIT_TIMEOUT, 0, true, 1, false, BootstrapIdentityMismatch},
        {0x12340000, WAIT_TIMEOUT, 0, true, 2, true, BootstrapIdentityMismatch},
        {0, WAIT_OBJECT_0, 0, false, 0, false, BootstrapTargetExited},
        {STILL_ACTIVE, WAIT_OBJECT_0, STILL_ACTIVE, false, 0, false, BootstrapTargetExited},
        {0xc0000005u, WAIT_OBJECT_0, 0xc0000005u, false, 0, false, BootstrapTargetExited},
        {0x12340000, WAIT_OBJECT_0, 0, true, 1, true, BootstrapTargetExited},
        {0, WAIT_FAILED, 0, false, 0, false, BootstrapObservationIncomplete},
        {0, WAIT_ABANDONED, 0, true, 1, true, BootstrapObservationIncomplete},
        {0, WAIT_TIMEOUT, 0, false, 0, false, BootstrapObservationIncomplete},
        {0x12340000, WAIT_TIMEOUT, 0, false, 1, true, BootstrapObservationIncomplete}
    };
    for (size_t i = 0; i < sizeof(loaderCases) / sizeof(loaderCases[0]); ++i) {
        ++count;
        const LoaderCase& test = loaderCases[i];
        BootstrapObservation observation = {123, test.raw, test.wait, test.processExit, test.inspected, test.copies, test.exact};
        if (BootstrapClassify(observation) != test.expected)
            return Error("Loader observation classification self-test failed", ERROR_INVALID_DATA);
        std::string diagnostic = BootstrapDiagnostic(observation);
        const char* category = test.expected == BootstrapModuleMissing ? "LOADER_MODULE_NOT_OBSERVED:" :
            test.expected == BootstrapIdentityMismatch ? "LOADER_MODULE_IDENTITY_MISMATCH:" :
            test.expected == BootstrapTargetExited ? "LOADER_TARGET_EXITED:" : "LOADER_OBSERVATION_INCOMPLETE:";
        char raw[64]; snprintf(raw, sizeof(raw), "thread result DWORD 0x%08lX", test.raw);
        if (test.expected == BootstrapVerified ? !diagnostic.empty() :
            diagnostic.find(category) != 0 || diagnostic.find(raw) == std::string::npos ||
            diagnostic.find("Initialization was not invoked") == std::string::npos ||
            diagnostic.find("Windows error") != std::string::npos)
            return Error("Loader observation diagnostic self-test failed", ERROR_INVALID_DATA);
        if (test.expected == BootstrapModuleMissing && diagnostic.find("remote loader error unavailable") == std::string::npos)
            return Error("Loader unknown cause diagnostic self-test failed", ERROR_INVALID_DATA);
        if (test.expected == BootstrapTargetExited) {
            char exit[64]; snprintf(exit, sizeof(exit), "process exit DWORD 0x%08lX", test.processExit);
            if (diagnostic.find(exit) == std::string::npos || diagnostic.find("target state running") != std::string::npos)
                return Error("Loader exited process diagnostic self-test failed", ERROR_INVALID_DATA);
        }
    }
    ++count;
    if (!(RS_ERROR_LOADER_MODULE_NOT_OBSERVED & 0x20000000u) || !(RS_ERROR_LOADER_MODULE_IDENTITY & 0x20000000u) ||
        RS_ERROR_LOADER_MODULE_NOT_OBSERVED == RS_ERROR_LOADER_MODULE_IDENTITY)
        return Error("Loader application status namespace self-test failed", ERROR_INVALID_DATA);
    printf("PASS shell and resident-hook policy: %d deterministic cases; no windows or processes accessed\n", count + 4);
    return 0;
}

struct TaskbarOwnerCheck { DWORD targetPid; bool found; };

static BOOL CALLBACK CheckTaskbarOwner(HWND hwnd, LPARAM parameter) {
    TaskbarOwnerCheck* check = reinterpret_cast<TaskbarOwnerCheck*>(parameter);
    wchar_t className[256];
    if (GetClassNameW(hwnd, className, 256) && RS_IsTaskbarClass(className)) {
        DWORD owner = 0;
        GetWindowThreadProcessId(hwnd, &owner);
        if (owner != 0 && owner == check->targetPid) { check->found = true; return FALSE; }
    }
    return TRUE;
}

// Read-only preflight. Run before opening/writing the control mapping or loading
// the hook DLL; selecting an Explorer folder must never hook its desktop process.
static bool ValidateNonShellTarget(DWORD pid, HWND hwnd, HANDLE process,
    const char*& reason, DWORD& error) {
    wchar_t className[256];
    if (!GetClassNameW(hwnd, className, 256)) {
        reason = "Cannot verify the selected window class"; error = GetLastError(); return false;
    }
    if (RS_IsShellWindowClass(className)) {
        reason = "Windows desktop, taskbar, and Explorer windows cannot be controlled";
        error = ERROR_ACCESS_DENIED; return false;
    }
    wchar_t imagePath[32768];
    DWORD imageLength = static_cast<DWORD>(sizeof(imagePath) / sizeof(imagePath[0]));
    if (!QueryFullProcessImageNameW(process, 0, imagePath, &imageLength)) {
        reason = "Cannot verify that the selected process is not the Windows shell";
        error = GetLastError(); return false;
    }
    if (RS_IsShellProcessName(imagePath)) {
        reason = "Windows shell processes, including every Explorer window, cannot be controlled";
        error = ERROR_ACCESS_DENIED; return false;
    }
    if (RS_IsIncompatibleLauncher(imagePath)) {
        reason = "Battle.net launcher is excluded because fullscreen hooks can cause crashes; games remain eligible";
        error = ERROR_NOT_SUPPORTED; return false;
    }
    DWORD shellOwner = 0, desktopOwner = 0;
    HWND shell = GetShellWindow(), desktop = GetDesktopWindow();
    if (shell) GetWindowThreadProcessId(shell, &shellOwner);
    if (desktop) GetWindowThreadProcessId(desktop, &desktopOwner);
    TaskbarOwnerCheck check = {pid, false};
    SetLastError(ERROR_SUCCESS);
    BOOL enumerated = EnumWindows(CheckTaskbarOwner, reinterpret_cast<LPARAM>(&check));
    if (!enumerated && !check.found) {
        reason = "Cannot verify Windows taskbar process ownership";
        error = GetLastError(); if (error == ERROR_SUCCESS) error = ERROR_GEN_FAILURE;
        return false;
    }
    if (RS_IsShellOwner(pid, shellOwner, desktopOwner, check.found)) {
        reason = "The process owning the Windows desktop or taskbar cannot be controlled";
        error = ERROR_ACCESS_DENIED; return false;
    }
    return true;
}

static bool GetSiblingHookPath(std::wstring& result, DWORD& error) {
    std::vector<wchar_t> buffer(32768, L'\0');
    DWORD length = GetModuleFileNameW(NULL, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (!length || length >= buffer.size()) {
        error = length ? ERROR_FILENAME_EXCED_RANGE : GetLastError();
        if (!error) error = ERROR_GEN_FAILURE;
        return false;
    }
    std::wstring host(buffer.data(), length);
    size_t slash = host.find_last_of(L"\\/");
    if (slash == std::wstring::npos ||
        !NormalizeAbsolutePath((host.substr(0, slash + 1) + L"RegionHook.dll").c_str(), result)) {
        error = ERROR_INVALID_NAME;
        return false;
    }
    return true;
}

static bool ExistingSubclass(HWND hwnd) {
    return GetPropW(hwnd, L"DisplayWeave.Subclass.v2") == reinterpret_cast<HANDLE>(static_cast<INT_PTR>(1)) &&
        GetPropW(hwnd, L"DisplayWeave.OriginalProcedure.v2") != NULL;
}

static bool PreviousControllerExited(DWORD pid, DWORD& error) {
    if (!pid) { error = ERROR_INVALID_DATA; return false; }
    HANDLE controller = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!controller) {
        error = GetLastError();
        return DeadControllerResult(false, error, WAIT_FAILED);
    }
    DWORD wait = WaitForSingleObject(controller, 0);
    error = wait == WAIT_TIMEOUT ? ERROR_ALREADY_EXISTS : wait == WAIT_OBJECT_0 ? ERROR_SUCCESS : GetLastError();
    CloseHandle(controller);
    if (!error && wait != WAIT_OBJECT_0) error = ERROR_GEN_FAILURE;
    return DeadControllerResult(true, ERROR_SUCCESS, wait);
}

static bool SameWindowIdentity(HWND hwnd, DWORD pid, DWORD thread) {
    DWORD actual = 0;
    return IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, &actual) == thread && actual == pid;
}

static HWND bootstrapWindow;
static DWORD bootstrapPid, bootstrapThread;
static bool BootstrapKeepAlive() {
    if (InterlockedCompareExchange(&quitRequested, 0, 0) || !OwnsConfiguration() ||
        !SameWindowIdentity(bootstrapWindow, bootstrapPid, bootstrapThread)) return false;
    InterlockedExchange(&shared->heartbeatTick, static_cast<LONG>(GetTickCount()));
    return true;
}

static void ResetAcknowledgment() {
    InterlockedExchange(&shared->acknowledgedPid, 0);
    // Optional initialization diagnostics must survive a fast first callback
    // and each subsequent acknowledgment reset. Old workers publish plain 3.
    InterlockedAnd(&shared->capabilities, ~static_cast<LONG>(RS_CAP_USER32 | RS_CAP_DXGI | RS_CAP_VIRTUAL_MONITORS));
    MemoryBarrier();
    // Clear last, matching the worker's publish-last ordering. Two distinct
    // completions exclude the one loop that could have read old configuration.
    ClearSuccessfulAcknowledgment(&shared->hookReady);
}

static DWORD WaitForAcknowledgments(HANDLE targetProcess, DWORD pid, HWND hwnd,
    DWORD threadId, DWORD start, bool wakeMessageHook, bool wakeSentHook, int cycles,
    HMODULE bootstrapDll, const std::wstring& dllPath, bool loaderImmediately, std::string& loaderFailure) {
    ResetAcknowledgment();
    DWORD lastSentWake = start - 250;
    bool loaderAttempted = false;
    while (!InterlockedCompareExchange(&quitRequested, 0, 0) &&
        static_cast<DWORD>(GetTickCount() - start) < 12000) {
        if (!OwnsConfiguration()) return ERROR_INVALID_OWNER;
        if (!SameWindowIdentity(hwnd, pid, threadId)) return ERROR_INVALID_WINDOW_HANDLE;
        InterlockedExchange(&shared->heartbeatTick, static_cast<LONG>(GetTickCount()));
        if (shared->hookReady < 0) return shared->lastError ? static_cast<DWORD>(shared->lastError) : ERROR_GEN_FAILURE;
        if (bootstrapDll && !loaderAttempted &&
            (loaderImmediately || (static_cast<DWORD>(GetTickCount() - start) >= 750 &&
                !(InterlockedCompareExchange(&shared->capabilities, 0, 0) & RS_INIT_MASK)))) {
            loaderAttempted = true;
            const char* reason = NULL; DWORD error = ERROR_SUCCESS;
            // Recheck the actual selected process/window policy just before the
            // only path requesting process-write and thread-creation access.
            if (!ValidateNonShellTarget(pid, hwnd, targetProcess, reason, error)) {
                loaderFailure = reason; return error;
            }
            if (!MatchingArchitecture(targetProcess)) {
                loaderFailure = "The selected application architecture changed before loading";
                return ERROR_BAD_EXE_FORMAT;
            }
            bootstrapWindow = hwnd; bootstrapPid = pid; bootstrapThread = threadId;
            BootstrapLoader loader;
            puts("INIT Standard Windows loader"); fflush(stdout);
            error = loader.Run(pid, targetProcess, bootstrapDll, dllPath, start, BootstrapKeepAlive, loaderFailure);
            if (error) return error;
        }
        if (wakeMessageHook) {
            SetLastError(ERROR_SUCCESS);
            threadWakeError = PostThreadMessageW(threadId, WM_NULL, 0, 0) ? ERROR_SUCCESS : GetLastError();
            SetLastError(ERROR_SUCCESS);
            windowWakeError = PostMessageW(hwnd, WM_NULL, 0, 0) ? ERROR_SUCCESS : GetLastError();
        }
        if (wakeSentHook && static_cast<DWORD>(GetTickCount() - lastSentWake) >= 250) {
            lastSentWake = GetTickCount();
            DWORD_PTR ignored = 0;
            SetLastError(ERROR_SUCCESS);
            LRESULT sent = SendMessageTimeoutW(hwnd, WM_NULL, 0, 0,
                SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT, 100, &ignored);
            sentWakeError = sent ? ERROR_SUCCESS : GetLastError();
            if (!sent && !sentWakeError) sentWakeError = ERROR_GEN_FAILURE;
        }
        if (shared->hookReady == 1 && static_cast<DWORD>(shared->acknowledgedPid) == pid &&
            ReadyCapabilities(shared->capabilities)) {
            if (--cycles == 0) return ERROR_SUCCESS;
            ResetAcknowledgment();
        } else if (shared->hookReady < 0) {
            DWORD error = static_cast<DWORD>(shared->lastError);
            return error ? error : ERROR_GEN_FAILURE;
        }
        DWORD wait = WaitForSingleObject(targetProcess, 50);
        if (wait != WAIT_TIMEOUT) return wait == WAIT_OBJECT_0 ? ERROR_PROCESS_ABORTED : GetLastError();
    }
    return quitRequested ? ERROR_SUCCESS : ERROR_TIMEOUT;
}

static void WriteJsonString(const wchar_t* value) {
    putchar('"');
    for (const wchar_t* p = value; p && *p; ++p) {
        unsigned int ch = static_cast<unsigned short>(*p);
        if (ch == '"' || ch == '\\') { putchar('\\'); putchar(static_cast<int>(ch)); }
        else if (ch >= 0x20 && ch <= 0x7e) putchar(static_cast<int>(ch));
        else printf("\\u%04X", ch);
    }
    putchar('"');
}

static void WriteJsonRectangle(const RECT& rect) {
    printf("{\"left\":%ld,\"top\":%ld,\"right\":%ld,\"bottom\":%ld}",
        rect.left, rect.top, rect.right, rect.bottom);
}

struct MonitorStatus { MONITORINFOEXW info; DWORD error; };
struct ShellWindowStatus {
    HWND hwnd;
    const char* role;
    wchar_t className[256];
    DWORD pid, error;
    RECT bounds;
    bool visible;
};

static BOOL CALLBACK CollectMonitorStatus(HMONITOR monitor, HDC, LPRECT, LPARAM parameter) {
    std::vector<MonitorStatus>* results = reinterpret_cast<std::vector<MonitorStatus>*>(parameter);
    MonitorStatus value = {};
    value.info.cbSize = sizeof(value.info);
    if (!GetMonitorInfoW(monitor, reinterpret_cast<MONITORINFO*>(&value.info))) {
        value.error = GetLastError();
        if (!value.error) value.error = ERROR_GEN_FAILURE;
    }
    results->push_back(value);
    return TRUE;
}

static ShellWindowStatus ReadShellWindowStatus(HWND hwnd, const char* role) {
    ShellWindowStatus value = {};
    value.hwnd = hwnd; value.role = role;
    if (!hwnd || !GetClassNameW(hwnd, value.className, 256) ||
        !GetWindowThreadProcessId(hwnd, &value.pid) || !GetWindowRect(hwnd, &value.bounds)) {
        value.error = GetLastError();
        if (!value.error) value.error = ERROR_INVALID_WINDOW_HANDLE;
    }
    value.visible = hwnd && IsWindowVisible(hwnd) != FALSE;
    return value;
}

static BOOL CALLBACK CollectShellWindowStatus(HWND hwnd, LPARAM parameter) {
    wchar_t className[256];
    if (!GetClassNameW(hwnd, className, 256)) return TRUE;
    const char* role = _wcsicmp(className, L"Progman") == 0 ? "progman" :
        _wcsicmp(className, L"WorkerW") == 0 ? "worker" :
        _wcsicmp(className, L"Shell_TrayWnd") == 0 ? "taskbar" :
        _wcsicmp(className, L"Shell_SecondaryTrayWnd") == 0 ? "secondaryTaskbar" : NULL;
    if (role) reinterpret_cast<std::vector<ShellWindowStatus>*>(parameter)->push_back(ReadShellWindowStatus(hwnd, role));
    return TRUE;
}

static int ShellStatus() {
    // Only this short-lived diagnostic thread's coordinate interpretation is
    // changed. No system display, application window, mapping or hook is changed.
    typedef HANDLE (WINAPI* SetThreadDpiContextFn)(HANDLE);
    SetThreadDpiContextFn setContext = reinterpret_cast<SetThreadDpiContextFn>(
        GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetThreadDpiAwarenessContext"));
    HANDLE oldContext = setContext ? setContext(reinterpret_cast<HANDLE>(static_cast<INT_PTR>(-4))) : NULL;
    if (!oldContext && setContext)
        oldContext = setContext(reinterpret_cast<HANDLE>(static_cast<INT_PTR>(-3)));
    bool physicalPixels = oldContext != NULL;
    std::vector<MonitorStatus> monitors;
    std::vector<ShellWindowStatus> windows;
    SetLastError(ERROR_SUCCESS);
    BOOL monitorsOk = EnumDisplayMonitors(NULL, NULL, CollectMonitorStatus, reinterpret_cast<LPARAM>(&monitors));
    DWORD monitorError = monitorsOk ? ERROR_SUCCESS : GetLastError();
    if (!monitorsOk && !monitorError) monitorError = ERROR_GEN_FAILURE;
    windows.push_back(ReadShellWindowStatus(GetDesktopWindow(), "desktop"));
    DWORD shellOwner = 0;
    HWND shell = GetShellWindow();
    if (shell) GetWindowThreadProcessId(shell, &shellOwner);
    SetLastError(ERROR_SUCCESS);
    BOOL windowsOk = EnumWindows(CollectShellWindowStatus, reinterpret_cast<LPARAM>(&windows));
    DWORD windowError = windowsOk ? ERROR_SUCCESS : GetLastError();
    if (!windowsOk && !windowError) windowError = ERROR_GEN_FAILURE;
    if (oldContext) setContext(oldContext);
    bool ok = physicalPixels && monitorsOk && windowsOk && !monitors.empty();
    for (size_t i = 0; i < monitors.size(); ++i) if (monitors[i].error) ok = false;
    for (size_t i = 0; i < windows.size(); ++i) if (windows[i].error) ok = false;
    printf("{\"ok\":%s,\"physicalPixels\":%s,\"shellOwnerPid\":%lu,\"monitorEnumerationError\":%lu,\"windowEnumerationError\":%lu,\"monitors\":[",
        ok ? "true" : "false", physicalPixels ? "true" : "false", shellOwner, monitorError, windowError);
    for (size_t i = 0; i < monitors.size(); ++i) {
        const MonitorStatus& value = monitors[i];
        if (i) putchar(',');
        printf("{\"device\":"); WriteJsonString(value.info.szDevice);
        printf(",\"primary\":%s,\"bounds\":", value.info.dwFlags & MONITORINFOF_PRIMARY ? "true" : "false");
        WriteJsonRectangle(value.info.rcMonitor);
        printf(",\"workArea\":"); WriteJsonRectangle(value.info.rcWork);
        printf(",\"error\":%lu}", value.error);
    }
    printf("],\"windows\":[");
    for (size_t i = 0; i < windows.size(); ++i) {
        const ShellWindowStatus& value = windows[i];
        if (i) putchar(',');
        printf("{\"role\":\"%s\",\"hwnd\":\"0x%llX\",\"pid\":%lu,\"class\":", value.role,
            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(value.hwnd)), value.pid);
        WriteJsonString(value.className);
        printf(",\"visible\":%s,\"bounds\":", value.visible ? "true" : "false");
        WriteJsonRectangle(value.bounds);
        printf(",\"error\":%lu}", value.error);
    }
    puts("]}");
    return ok ? 0 : 1;
}

int wmain(int argc, wchar_t** argv) {
    // This branch calls only pure classifiers and stdout. It neither enumerates
    // windows nor opens processes, mappings, DLLs, hooks, or helper threads.
    if (argc == 2 && wcscmp(argv[1], L"--self-test-policy") == 0) return PolicySelfTest();
    if (argc == 2 && wcscmp(argv[1], L"--self-test-ipc") == 0) return IpcSecuritySelfTest();
    if (argc == 2 && wcscmp(argv[1], L"--shell-status") == 0) return ShellStatus();
    if (argc < 7)
        return Error("Usage: RegionHookHost.exe pid hwnd x y width height [--auto] [--regions x,y,w,h;...] [--bootstrap-loader]", ERROR_INVALID_PARAMETER);
    bool automatic = false, loaderImmediately = false, suppliedLayout = false;
    RegionLayoutSnapshot initialLayout = {};
    for (int i = 7; i < argc; ++i) {
        if (wcscmp(argv[i], L"--auto") == 0 && !automatic) automatic = true;
        else if (wcscmp(argv[i], L"--bootstrap-loader") == 0 && !loaderImmediately) loaderImmediately = true;
        else if (wcscmp(argv[i], L"--regions") == 0 && !suppliedLayout && i + 1 < argc) {
            suppliedLayout = true;
            if (!ParseLayout(argv[++i], initialLayout)) return Error("Invalid region layout", ERROR_INVALID_PARAMETER);
        } else return Error("Invalid or repeated fullscreen helper option", ERROR_INVALID_PARAMETER);
    }
    wchar_t* end;
    errno = 0;
    unsigned long pidLong = wcstoul(argv[1], &end, 10);
    if (end == argv[1] || *end || errno == ERANGE || argv[1][0] == L'-' || pidLong == 0)
        return Error("Invalid process ID", ERROR_INVALID_PARAMETER);
    DWORD pid = static_cast<DWORD>(pidLong);
    errno = 0;
    unsigned long long hwndValue = wcstoull(argv[2], &end,
        (argv[2][0] == L'0' && (argv[2][1] == L'x' || argv[2][1] == L'X')) ? 16 : 10);
    if (end == argv[2] || *end || errno == ERANGE || argv[2][0] == L'-' || hwndValue == 0 || hwndValue > UINTPTR_MAX)
        return Error("Invalid window handle", ERROR_INVALID_PARAMETER);
    HWND hwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(hwndValue));
    int values[4];
    for (int i = 0; i != 4; ++i) {
        errno = 0;
        long long value = wcstoll(argv[i + 3], &end, 10);
        if (end == argv[i + 3] || *end || errno == ERANGE || value < INT_MIN || value > INT_MAX)
            return Error("Invalid rectangle", ERROR_INVALID_PARAMETER);
        values[i] = static_cast<int>(value);
    }
    if (!ValidRegion(values[0], values[1], values[2], values[3])) return Error("Invalid rectangle", ERROR_INVALID_PARAMETER);
    if (!suppliedLayout) {
        initialLayout.count = 1;
        initialLayout.regions[0] = {values[0], values[1], values[2], values[3]};
    }
    initialLayout.targetPid = pid; initialLayout.controllerPid = GetCurrentProcessId();
    DWORD actualPid = 0;
    DWORD threadId = GetWindowThreadProcessId(hwnd, &actualPid);
    if (!IsWindow(hwnd) || actualPid != pid || !threadId || pid == GetCurrentProcessId())
        return Error("Window does not belong to the selected process", ERROR_INVALID_WINDOW_HANDLE);
    HANDLE targetProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!targetProcess) return Error("Cannot access selected process; no elevation attempted", GetLastError());
    const char* preflightReason = NULL;
    DWORD preflightError = ERROR_SUCCESS;
    if (!ValidateNonShellTarget(pid, hwnd, targetProcess, preflightReason, preflightError)) {
        CloseHandle(targetProcess);
        return Error(preflightReason, preflightError);
    }
    if (!MatchingArchitecture(targetProcess)) {
        CloseHandle(targetProcess);
        return Error("Use the helper matching the target application's 32-bit or 64-bit architecture", ERROR_BAD_EXE_FORMAT);
    }
    ProcessAccessLevel ourAccess = ReadProcessAccessLevel(GetCurrentProcess());
    ProcessAccessLevel targetAccess = ReadProcessAccessLevel(targetProcess);
    if (IsAdministratorMismatch(ourAccess, targetAccess)) {
        CloseHandle(targetProcess);
        return Error("The selected application is running as administrator. Restart DisplayWeave as administrator to control it",
            ERROR_ELEVATION_REQUIRED);
    }
    IpcSecurity ipcSecurity;
    bool mediumIpc = NeedsMediumIpc(ourAccess, targetAccess);
    if (mediumIpc && !ipcSecurity.Initialize(preflightError)) {
        CloseHandle(targetProcess);
        return Error("Cannot prepare private application controls with the current user's permissions", preflightError);
    }
    SECURITY_ATTRIBUTES* ipcAttributes = mediumIpc ? &ipcSecurity.attributes : NULL;
    ControllerMutex controllerMutex;
    if (!controllerMutex.Acquire(pid, ipcAttributes, preflightError)) {
        CloseHandle(targetProcess);
        return Error("Another DisplayWeave controller already owns this application", preflightError);
    }
    std::wstring dllPath;
    if (!GetSiblingHookPath(dllPath, preflightError)) {
        CloseHandle(targetProcess);
        return Error("Cannot resolve the fullscreen helper's absolute DLL path", preflightError);
    }
    wchar_t mappingName[80];
    swprintf(mappingName, 80, L"Local\\DisplayWeave_%lu", pid);
    SetLastError(ERROR_SUCCESS);
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, ipcAttributes, PAGE_READWRITE, 0, sizeof(RegionProtocol), mappingName);
    if (!mapping) {
        DWORD error = GetLastError(); CloseHandle(targetProcess);
        return Error("Cannot create application control mapping", error);
    }
    bool reuse = GetLastError() == ERROR_ALREADY_EXISTS;
    shared = static_cast<RegionProtocol*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(RegionProtocol)));
    if (!shared) {
        DWORD error = GetLastError();
        bool oldVersion = false;
        if (reuse) {
            const DWORD* header = static_cast<const DWORD*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 12));
            if (header) {
                oldVersion = header[0] == RS_PROTOCOL_MAGIC &&
                    (header[1] != RS_PROTOCOL_VERSION || header[2] != sizeof(RegionProtocol));
                UnmapViewOfFile(header);
            }
        }
        CloseHandle(mapping); CloseHandle(targetProcess);
        return Error(oldVersion ? "Restart the target application to use this DisplayWeave version" :
            "Cannot map application controls", oldVersion ? ERROR_REVISION_MISMATCH : error);
    }
    if (reuse && (shared->magic != RS_PROTOCOL_MAGIC ||
        shared->version != RS_PROTOCOL_VERSION || shared->size != sizeof(RegionProtocol) || shared->targetPid != pid)) {
        UnmapViewOfFile(shared); CloseHandle(mapping); CloseHandle(targetProcess);
        return Error("Restart the target application to use this DisplayWeave version", ERROR_REVISION_MISMATCH);
    }
    if (reuse && !PreviousControllerExited(shared->controllerPid, preflightError)) {
        UnmapViewOfFile(shared); CloseHandle(mapping); CloseHandle(targetProcess);
        return Error(preflightError == ERROR_ALREADY_EXISTS ? "This process already has a live DisplayWeave controller" :
            "Cannot verify that the previous DisplayWeave controller has exited; attachment was not attempted", preflightError);
    }
    RegionSnapshot previous = {};
    // The worker acknowledges every 100 ms even after its previous window closed, and
    // subclasses whichever window it is given; only one whose hooks failed (-1) stopped.
    HandoffPolicy handoff = DecideHandoff(reuse, reuse && RS_ReadSnapshot(shared, previous),
        reuse && shared->hookReady >= 0 && static_cast<DWORD>(shared->acknowledgedPid) == pid &&
        ReadyCapabilities(shared->capabilities));
    if (reuse && handoff != HandoffReuse) {
        const char* reason = handoff == HandoffFailedWorker ?
            "Restart the target application; its resident DisplayWeave worker could not install its hooks" :
            "Restart the target application; its resident DisplayWeave control mapping is incomplete or incompatible";
        UnmapViewOfFile(shared); CloseHandle(mapping); CloseHandle(targetProcess);
        return Error(reason, ERROR_REVISION_MISMATCH);
    }
    if (!SameWindowIdentity(hwnd, pid, threadId)) {
        UnmapViewOfFile(shared); CloseHandle(mapping); CloseHandle(targetProcess);
        return Error("The selected window changed before attachment; select it again", ERROR_INVALID_WINDOW_HANDLE);
    }
    // A sidecar has no independent lifetime/heartbeat authority: its owner must
    // match the previous stable control mapping before we can take it over.
    swprintf(mappingName, 80, L"Local\\DisplayWeave_Layout_%lu", pid);
    SetLastError(ERROR_SUCCESS);
    HANDLE layoutMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, ipcAttributes, PAGE_READWRITE,
        0, sizeof(RegionLayoutProtocol), mappingName);
    DWORD layoutError = layoutMapping ? ERROR_SUCCESS : GetLastError();
    bool layoutExists = layoutMapping && GetLastError() == ERROR_ALREADY_EXISTS;
    if (layoutMapping) layoutShared = static_cast<RegionLayoutProtocol*>(
        MapViewOfFile(layoutMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(RegionLayoutProtocol)));
    if (layoutMapping && !layoutShared) layoutError = GetLastError();
    RegionLayoutSnapshot priorLayout = {};
    RegionSnapshot priorControl = {};
    if (layoutShared && layoutExists && (!reuse || !RS_ReadSnapshot(shared, priorControl) ||
        !RS_ReadLayout(layoutShared, priorControl, priorLayout) || priorControl.targetPid != pid ||
        priorControl.controllerPid != shared->controllerPid)) layoutError = ERROR_REVISION_MISMATCH;
    if (layoutError || !layoutShared) {
        if (layoutShared) UnmapViewOfFile(layoutShared);
        if (layoutMapping) CloseHandle(layoutMapping);
        UnmapViewOfFile(shared); CloseHandle(mapping); CloseHandle(targetProcess);
        return Error("Cannot safely initialize the selected application's region layout mapping", layoutError ? layoutError : ERROR_INVALID_DATA);
    }
    PublishLayout(initialLayout);
    // Existing mappings may remain alive in a resident DLL. Publish configuration
    // through the sequence counter; never zero memory underneath active readers.
    LONG seq = InterlockedCompareExchange(&shared->sequence, 0, 0);
    InterlockedExchange(&shared->sequence, (seq | 1));
    shared->magic = RS_PROTOCOL_MAGIC; shared->version = RS_PROTOCOL_VERSION; shared->size = sizeof(RegionProtocol);
    shared->targetPid = pid; shared->controllerPid = GetCurrentProcessId();
    shared->targetHwnd = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(hwnd));
    shared->x = values[0]; shared->y = values[1]; shared->width = values[2]; shared->height = values[3];
    RECT region = { values[0], values[1], values[0] + values[2], values[1] + values[3] };
    shared->targetMonitor = reinterpret_cast<uintptr_t>(MonitorFromRect(&region, MONITOR_DEFAULTTONEAREST));
    // Keep hooks pass-through until the current resident worker has freshly
    // acknowledged this controller twice. ON requests remain queued until then.
    shared->active = 0;
    shared->heartbeatTick = static_cast<LONG>(GetTickCount());
    shared->lastError = 0; shared->acknowledgedPid = 0;
    shared->capabilities = reuse ? (shared->capabilities & RS_INIT_MASK) : 0;
    shared->hookReady = 0;
    shared->dxgiTransitions = 0; shared->resizeIntercepts = 0;
    shared->mode = automatic ? RS_MODE_AUTO : RS_MODE_FORCE;
    shared->windowState = RS_STATE_NORMAL;
    MemoryBarrier();
    InterlockedIncrement(&shared->sequence);

    HANDLE reader = CreateThread(NULL, 0, ReadCommands, NULL, 0, NULL);
    if (!reader) {
        InterlockedExchange(&shared->active, 0);
        return Error("Cannot start command listener", GetLastError());
    }

    // Reuse communicates only with the pinned worker already in the target.
    // Never load its old on-disk image or introduce the new DLL into that target.
    DWORD start = GetTickCount();
    BootstrapFile lockedHookFile;
    DWORD lockError = ERROR_SUCCESS;
    bool fileLocked = reuse || lockedHookFile.Open(dllPath, lockError);
    HMODULE dll = reuse || !fileLocked ? NULL : LoadLibraryW(dllPath.c_str());
    HHOOK hook = NULL;
    HHOOK sentHook = NULL;
    DWORD failure = ERROR_SUCCESS;
    const char* failureText = "Native compatibility hook did not initialize";
    char startupFailure[640] = {};
    std::string loaderFailure;
    if (!fileLocked) { failure = lockError; failureText = "Cannot lock and verify RegionHook.dll beside the helper"; }
    else if (!reuse && !dll) { failure = GetLastError(); failureText = "Cannot load RegionHook.dll beside the helper"; }
    HOOKPROC procedure = dll ? reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "RegionGetMessageHook")) : NULL;
    if (dll && !procedure) procedure = reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "_RegionGetMessageHook@12"));
    if (dll && !procedure) procedure = reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "RegionGetMessageHook@12"));
    if (dll && !procedure && !loaderImmediately) failure = ERROR_PROC_NOT_FOUND;
    if (procedure && !loaderImmediately) {
        hook = SetWindowsHookExW(WH_GETMESSAGE, procedure, dll, threadId);
        if (!hook) { failure = GetLastError(); failureText = "The selected app rejected the thread-specific hook"; }
    }
    HOOKPROC sentProcedure = dll ? reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "RegionCallWindowHook")) : NULL;
    if (dll && !sentProcedure) sentProcedure = reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "_RegionCallWindowHook@12"));
    if (dll && !sentProcedure) sentProcedure = reinterpret_cast<HOOKPROC>(GetProcAddress(dll, "RegionCallWindowHook@12"));
    if (sentProcedure && !loaderImmediately) {
        sentHook = SetWindowsHookExW(WH_CALLWNDPROC, sentProcedure, dll, threadId);
        if (sentHook) failure = ERROR_SUCCESS;
        else if (!hook) { failure = GetLastError(); failureText = "The selected app rejected the thread-specific hook"; }
    }
    if (failure == ERROR_SUCCESS && (reuse || hook || sentHook || (loaderImmediately && dll))) {
        failure = WaitForAcknowledgments(targetProcess, pid, hwnd, threadId, start, hook != NULL, sentHook != NULL, 2,
            reuse ? NULL : dll, dllPath, loaderImmediately, loaderFailure);
        if (failure == ERROR_SUCCESS && !quitRequested) {
            {
                ConfigurationScope guard;
                if (!OwnsConfiguration()) failure = ERROR_INVALID_OWNER;
                else if (!SameWindowIdentity(hwnd, pid, threadId) || (reuse && !ExistingSubclass(hwnd)))
                    failure = ERROR_INVALID_WINDOW_HANDLE;
                else if (!quitRequested) {
                    InterlockedExchange(&activationAllowed, 1);
                    InterlockedExchange(&shared->active, InterlockedCompareExchange(&requestedActive, 0, 0));
                }
            }
            if (failure == ERROR_SUCCESS && !quitRequested)
                failure = WaitForAcknowledgments(targetProcess, pid, hwnd, threadId, start, hook != NULL, sentHook != NULL, 2,
                    NULL, dllPath, false, loaderFailure);
        }
        if (failure != ERROR_SUCCESS && !loaderFailure.empty()) failureText = loaderFailure.c_str();
        else if (failure == ERROR_TIMEOUT) {
            snprintf(startupFailure, sizeof(startupFailure),
                "Native fullscreen startup timed out: %s; wake errors (thread/window/sent) %lu/%lu/%lu",
                InitializationStage(InterlockedCompareExchange(&shared->capabilities, 0, 0), reuse),
                threadWakeError, windowWakeError, sentWakeError);
            failureText = startupFailure;
        } else if (failure != ERROR_SUCCESS && !reuse && shared->hookReady < 0) {
            snprintf(startupFailure, sizeof(startupFailure), "Native fullscreen startup failed: %s",
                InitializationStage(InterlockedCompareExchange(&shared->capabilities, 0, 0)));
            failureText = startupFailure;
        } else if (failure != ERROR_SUCCESS && reuse)
            failureText = "The existing DisplayWeave worker did not acknowledge reconnection; the application was not restarted";
    }
    if (failure == ERROR_SUCCESS && (reuse || hook || sentHook || (loaderImmediately && dll)) && shared->hookReady == 1 && !quitRequested) {
        printf(reuse ? "READY %lu REUSED CAPS %lu\n" : "READY %lu CAPS %lu\n", pid,
            static_cast<DWORD>(shared->capabilities) & (RS_CAP_USER32 | RS_CAP_DXGI | RS_CAP_VIRTUAL_MONITORS)); fflush(stdout);
        LONG lastState = -1;
        while (!InterlockedCompareExchange(&quitRequested, 0, 0) && WaitForSingleObject(targetProcess, 100) == WAIT_TIMEOUT) {
            if (!OwnsConfiguration()) { failure = ERROR_INVALID_OWNER; failureText = "The application controller changed"; break; }
            InterlockedExchange(&shared->heartbeatTick, static_cast<LONG>(GetTickCount()));
            DWORD windowPid = 0;
            if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, &windowPid) != threadId || windowPid != pid) break;
            LONG state = InterlockedCompareExchange(&shared->windowState, 0, 0);
            if (state != lastState) {
                const char* label = state == RS_STATE_MAXIMIZED ? "MAXIMIZED" :
                    state == RS_STATE_FULLSCREEN ? "FULLSCREEN" : state == RS_STATE_FORCED ? "FORCED" : "NORMAL";
                printf("STATE %s\n", label); fflush(stdout);
                lastState = state;
            }
        }
    }
    {
        ConfigurationScope guard;
        InterlockedExchange(&activationAllowed, 0);
        if (OwnsConfiguration()) {
            InterlockedExchange(&shared->active, 0);
            InterlockedExchange(&shared->heartbeatTick, static_cast<LONG>(GetTickCount()));
        }
    }
    // Give the resident worker a chance to restore before its hook is detached.
    Sleep(200);
    if (sentHook) UnhookWindowsHookEx(sentHook);
    if (hook) UnhookWindowsHookEx(hook);
    if (dll) FreeLibrary(dll);
    // On target exit the reader may still be in a synchronous pipe read. Keep
    // its mapping valid until process teardown rather than unmapping under it.
    if (WaitForSingleObject(reader, 0) == WAIT_OBJECT_0) {
        UnmapViewOfFile(shared); UnmapViewOfFile(layoutShared);
    }
    CloseHandle(reader);
    CloseHandle(layoutMapping);
    CloseHandle(mapping);
    CloseHandle(targetProcess);
    if (failure != ERROR_SUCCESS) return Error(failureText, failure);
    puts("STOPPED"); fflush(stdout);
    return 0;
}
