#pragma once
#include <wchar.h>

// These launcher processes have reported crashes when prepared by our hooks.
// Match only complete image basenames: games launched by Battle.net remain
// eligible, including games installed beneath similarly named directories.
inline bool RS_IsIncompatibleLauncher(const wchar_t* image) {
    if (!image || !*image) return false;
    const wchar_t* name = image;
    for (const wchar_t* p = image; *p; ++p)
        if (*p == L'\\' || *p == L'/') name = p + 1;
    return _wcsicmp(name, L"Battle.net.exe") == 0 ||
        _wcsicmp(name, L"Battle.net Launcher.exe") == 0 ||
        _wcsicmp(name, L"Battle.net Helper.exe") == 0;
}
