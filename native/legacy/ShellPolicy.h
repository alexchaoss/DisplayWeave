#pragma once
#include <wchar.h>
#include <stddef.h>
#include <stdint.h>

// Pure classification, shared with the host's no-side-effects policy tests.
// Protect every window belonging to these processes, including Explorer's
// ordinary CabinetWClass file-manager windows: monitor hooks are process-wide.
inline bool RS_IsShellProcessName(const wchar_t* image) {
    if (!image || !*image) return false;
    const wchar_t* name = image;
    for (const wchar_t* p = image; *p; ++p)
        if (*p == L'\\' || *p == L'/') name = p + 1;
    const wchar_t* const protectedNames[] = {
        L"explorer.exe", L"sihost.exe", L"dwm.exe",
        L"ShellExperienceHost.exe", L"StartMenuExperienceHost.exe", L"ShellHost.exe",
        L"SearchHost.exe", L"SearchApp.exe", L"SearchUI.exe",
        L"LockApp.exe", L"LogonUI.exe", L"Winlogon.exe",
        L"TextInputHost.exe", L"InputApp.exe", L"CredentialUIBroker.exe"
    };
    for (size_t i = 0; i < sizeof(protectedNames) / sizeof(protectedNames[0]); ++i)
        if (_wcsicmp(name, protectedNames[i]) == 0) return true;
    return false;
}

inline bool RS_IsShellWindowClass(const wchar_t* name) {
    if (!name || !*name) return false;
    const wchar_t* const protectedClasses[] = {
        L"Progman", L"WorkerW", L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd",
        L"SHELLDLL_DefView", L"CabinetWClass", L"ExploreWClass"
    };
    for (size_t i = 0; i < sizeof(protectedClasses) / sizeof(protectedClasses[0]); ++i)
        if (_wcsicmp(name, protectedClasses[i]) == 0) return true;
    return false;
}

inline bool RS_IsTaskbarClass(const wchar_t* name) {
    return name && (_wcsicmp(name, L"Shell_TrayWnd") == 0 ||
        _wcsicmp(name, L"Shell_SecondaryTrayWnd") == 0);
}

inline bool RS_IsShellOwner(uint32_t target, uint32_t shellOwner, uint32_t desktopOwner,
    bool ownsTaskbar) {
    return target != 0 && (ownsTaskbar || (shellOwner != 0 && target == shellOwner) ||
        (desktopOwner != 0 && target == desktopOwner));
}
