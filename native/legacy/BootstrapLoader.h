#pragma once
#include <wincrypt.h>

// Bit 29 marks application-defined statuses. These describe observations made
// by this controller, not a GetLastError value from the remote loader thread.
static const DWORD RS_ERROR_LOADER_MODULE_NOT_OBSERVED = 0x20005201u;
static const DWORD RS_ERROR_LOADER_MODULE_IDENTITY = 0x20005202u;
enum BootstrapOutcome { BootstrapVerified, BootstrapTargetExited, BootstrapModuleMissing,
    BootstrapIdentityMismatch, BootstrapObservationIncomplete };
struct BootstrapObservation {
    DWORD pid;
    DWORD threadResult;
    DWORD processWait;
    DWORD processExit;
    bool modulesInspected;
    size_t hookCount;
    bool exactPath;
};
inline BootstrapOutcome BootstrapClassify(const BootstrapObservation& observed) {
    // A signaled process/thread can legitimately have returned 259. Only the
    // wait result establishes whether it is still running. A LoadLibrary thread
    // result is a raw DWORD (possibly a truncated x64 handle), never an error.
    if (observed.processWait == WAIT_OBJECT_0) return BootstrapTargetExited;
    if (observed.processWait != WAIT_TIMEOUT || !observed.modulesInspected) return BootstrapObservationIncomplete;
    if (!observed.hookCount) return BootstrapModuleMissing;
    if (observed.hookCount != 1 || !observed.exactPath) return BootstrapIdentityMismatch;
    return BootstrapVerified;
}
inline std::string BootstrapDiagnostic(const BootstrapObservation& observed) {
    char buffer[640];
    BootstrapOutcome outcome = BootstrapClassify(observed);
    if (outcome == BootstrapTargetExited) {
        snprintf(buffer, sizeof(buffer),
            "LOADER_TARGET_EXITED: The selected process exited before its fullscreen component could be verified; "
            "PID %lu, thread result DWORD 0x%08lX, process exit DWORD 0x%08lX. Initialization was not invoked",
            observed.pid, observed.threadResult, observed.processExit);
    } else if (outcome == BootstrapModuleMissing) {
        snprintf(buffer, sizeof(buffer),
            "LOADER_MODULE_NOT_OBSERVED: Fullscreen component was not observed after the loader thread ended; "
            "PID %lu, thread result DWORD 0x%08lX, target state running, RegionHook.dll copies 0; "
            "remote loader error unavailable. Initialization was not invoked", observed.pid, observed.threadResult);
    } else if (outcome == BootstrapIdentityMismatch) {
        snprintf(buffer, sizeof(buffer),
            "LOADER_MODULE_IDENTITY_MISMATCH: The observed fullscreen component did not have one verified path; "
            "PID %lu, thread result DWORD 0x%08lX, target state running, RegionHook.dll copies %lu, exact path %s. "
            "Initialization was not invoked", observed.pid, observed.threadResult,
            static_cast<unsigned long>(observed.hookCount), observed.exactPath ? "yes" : "no");
    } else if (outcome == BootstrapObservationIncomplete) {
        snprintf(buffer, sizeof(buffer),
            "LOADER_OBSERVATION_INCOMPLETE: Cannot verify the selected process after its loader thread ended; "
            "PID %lu, thread result DWORD 0x%08lX. Initialization was not invoked", observed.pid, observed.threadResult);
    } else return std::string();
    return std::string(buffer);
}

// Included after the host's strict absolute-path comparison helpers. This is
// ordinary same-privilege Windows loading, scoped to the selected process.
// There is no debug privilege, executable allocation, arbitrary code buffer,
// existing-memory write, thread hijack, or security-policy change.
struct BootstrapModule {
    std::wstring path;
    uintptr_t base;
    DWORD size;
};

inline bool BootstrapAddress(uintptr_t localBase, DWORD localSize, uintptr_t localEntry,
    uintptr_t remoteBase, DWORD remoteSize, uintptr_t& result) {
    result = 0;
    if (!localBase || !remoteBase || !localSize || localSize != remoteSize ||
        localEntry < localBase || localEntry - localBase >= localSize ||
        localBase > UINTPTR_MAX - localSize || remoteBase > UINTPTR_MAX - remoteSize) return false;
    result = remoteBase + (localEntry - localBase);
    return true;
}

inline bool BootstrapModules(DWORD pid, std::vector<BootstrapModule>& modules, DWORD& error) {
    modules.clear();
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 3; ++attempt) {
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot != INVALID_HANDLE_VALUE) break;
        error = GetLastError();
        if (error != ERROR_BAD_LENGTH) return false;
        Sleep(10);
    }
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    MODULEENTRY32W entry = {}; entry.dwSize = sizeof(entry);
    bool ok = Module32FirstW(snapshot, &entry) != FALSE;
    if (!ok) error = GetLastError();
    while (ok) {
        const size_t capacity = sizeof(entry.szExePath) / sizeof(entry.szExePath[0]);
        std::wstring path;
        if (!wmemchr(entry.szExePath, L'\0', capacity) || wcslen(entry.szExePath) >= capacity - 1 ||
            !NormalizeAbsolutePath(entry.szExePath, path) || !entry.modBaseAddr || !entry.modBaseSize) {
            error = ERROR_INVALID_DATA; ok = false; break;
        }
        modules.push_back({path, reinterpret_cast<uintptr_t>(entry.modBaseAddr), entry.modBaseSize});
        entry.dwSize = sizeof(entry);
        if (!Module32NextW(snapshot, &entry)) {
            error = GetLastError();
            ok = error == ERROR_NO_MORE_FILES;
            if (ok) error = ERROR_SUCCESS;
            break;
        }
    }
    CloseHandle(snapshot);
    return ok;
}

inline bool BootstrapModulePath(HMODULE module, std::wstring& result, DWORD& error) {
    std::vector<wchar_t> path(32768, L'\0');
    DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size() || !NormalizeAbsolutePath(path.data(), result)) {
        error = length >= path.size() ? ERROR_INSUFFICIENT_BUFFER : GetLastError();
        if (!error) error = ERROR_INVALID_NAME;
        return false;
    }
    return true;
}

inline bool BootstrapRead(HANDLE process, uintptr_t address, void* result, SIZE_T size, DWORD& error) {
    SIZE_T read = 0;
    if (address > UINTPTR_MAX - size || !ReadProcessMemory(process,
        reinterpret_cast<const void*>(address), result, size, &read) || read != size) {
        error = GetLastError(); if (!error) error = ERROR_PARTIAL_COPY;
        return false;
    }
    return true;
}

// Match the actual loaded PE identity, not merely a similarly named module.
// Addresses are derived only from the enumerated target base and validated RVA.
inline bool BootstrapResolve(HANDLE process, HMODULE local, FARPROC entry,
    const BootstrapModule& remote, uintptr_t& result, DWORD& error) {
    IMAGE_DOS_HEADER dos = {};
    IMAGE_NT_HEADERS headers = {};
    IMAGE_DOS_HEADER localDos = {};
    IMAGE_NT_HEADERS localHeaders = {};
    uintptr_t base = reinterpret_cast<uintptr_t>(local);
    if (!BootstrapRead(GetCurrentProcess(), base, &localDos, sizeof(localDos), error) ||
        localDos.e_magic != IMAGE_DOS_SIGNATURE || localDos.e_lfanew < sizeof(IMAGE_DOS_HEADER) ||
        localDos.e_lfanew > 1024 * 1024 ||
        !BootstrapRead(GetCurrentProcess(), base + localDos.e_lfanew, &localHeaders, sizeof(localHeaders), error) ||
        !BootstrapRead(process, remote.base, &dos, sizeof(dos), error) ||
        dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew != localDos.e_lfanew ||
        static_cast<uint64_t>(dos.e_lfanew) + sizeof(headers) > remote.size ||
        !BootstrapRead(process, remote.base + dos.e_lfanew, &headers, sizeof(headers), error)) {
        if (!error) error = ERROR_BAD_EXE_FORMAT;
        return false;
    }
    if (headers.Signature != IMAGE_NT_SIGNATURE || localHeaders.Signature != IMAGE_NT_SIGNATURE ||
        headers.FileHeader.Machine != localHeaders.FileHeader.Machine ||
        headers.FileHeader.TimeDateStamp != localHeaders.FileHeader.TimeDateStamp ||
        headers.FileHeader.NumberOfSections != localHeaders.FileHeader.NumberOfSections ||
        headers.OptionalHeader.Magic != localHeaders.OptionalHeader.Magic ||
        headers.OptionalHeader.SizeOfImage != localHeaders.OptionalHeader.SizeOfImage ||
        headers.OptionalHeader.SizeOfImage != remote.size ||
        headers.OptionalHeader.CheckSum != localHeaders.OptionalHeader.CheckSum ||
        !BootstrapAddress(base, localHeaders.OptionalHeader.SizeOfImage, reinterpret_cast<uintptr_t>(entry),
            remote.base, remote.size, result)) {
        error = ERROR_REVISION_MISMATCH; return false;
    }
    MEMORY_BASIC_INFORMATION memory = {};
    if (!VirtualQueryEx(process, reinterpret_cast<const void*>(result), &memory, sizeof(memory)) ||
        memory.State != MEM_COMMIT || memory.Type != MEM_IMAGE ||
        reinterpret_cast<uintptr_t>(memory.AllocationBase) != remote.base ||
        (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
        !(memory.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) {
        error = ERROR_INVALID_ADDRESS; return false;
    }
    return true;
}

struct BootstrapFile {
    HANDLE handle;
    BYTE hash[32];
    BootstrapFile() : handle(INVALID_HANDLE_VALUE), hash() {}
    ~BootstrapFile() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
    bool Open(const std::wstring& path, DWORD& error) {
        // Deny writes/deletion from verification through loading. Resolve the
        // actual file identity so directory links cannot redirect a second open.
        handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (handle == INVALID_HANDLE_VALUE) { error = GetLastError(); return false; }
        std::vector<wchar_t> finalPath(32768, L'\0');
        DWORD length = GetFinalPathNameByHandleW(handle, finalPath.data(), static_cast<DWORD>(finalPath.size()), FILE_NAME_NORMALIZED);
        if (!length || length >= finalPath.size() || !SameAbsolutePath(path.c_str(), finalPath.data())) {
            error = ERROR_INVALID_NAME; return false;
        }
        HCRYPTPROV provider = 0; HCRYPTHASH digest = 0;
        if (!CryptAcquireContextW(&provider, NULL, NULL, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)) {
            error = GetLastError(); return false;
        }
        bool ok = CryptCreateHash(provider, CALG_SHA_256, 0, 0, &digest) != FALSE;
        if (!ok) error = GetLastError();
        BYTE buffer[65536]; DWORD read = 0;
        while (ok) {
            if (!ReadFile(handle, buffer, sizeof(buffer), &read, NULL)) { error = GetLastError(); ok = false; break; }
            if (!read) break;
            if (!CryptHashData(digest, buffer, read, 0)) { error = GetLastError(); ok = false; break; }
        }
        DWORD size = sizeof(hash);
        if (ok && !CryptGetHashParam(digest, HP_HASHVAL, hash, &size, 0)) { error = GetLastError(); ok = false; }
        if (digest) CryptDestroyHash(digest);
        CryptReleaseContext(provider, 0);
        return ok && size == sizeof(hash);
    }
};

class BootstrapLoader {
    HANDLE process, thread;
    void* pathBuffer;
    bool (*keepAlive)();
    DWORD start;
    bool Complete() { return thread && WaitForSingleObject(thread, 0) == WAIT_OBJECT_0; }
    DWORD RunThread(uintptr_t address, void* parameter, const char* stage, std::string& reason) {
        if (!keepAlive()) { reason = "Application startup was cancelled before the loader thread started"; return ERROR_CANCELLED; }
        if (static_cast<DWORD>(GetTickCount() - start) >= 12000) {
            reason = std::string("Native fullscreen startup timed out before ") + stage + " started";
            return ERROR_TIMEOUT;
        }
        if (thread) { CloseHandle(thread); thread = NULL; }
        thread = CreateRemoteThread(process, NULL, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(address), parameter, 0, NULL);
        if (!thread) { reason = std::string("Windows refused the selected application's ") + stage; return GetLastError(); }
        for (;;) {
            DWORD wait = WaitForSingleObject(thread, 25);
            if (wait == WAIT_OBJECT_0) return ERROR_SUCCESS;
            if (wait != WAIT_TIMEOUT) { reason = "Cannot wait for the application loader thread"; return GetLastError(); }
            if (!keepAlive()) {
                reason = std::string("Application startup was cancelled during ") + stage + "; a late-loaded DLL will remain inactive";
                return ERROR_CANCELLED;
            }
            if (static_cast<DWORD>(GetTickCount() - start) >= 12000) {
                reason = std::string("Native fullscreen startup timed out: ") + stage + " thread has not completed; " +
                    (pathBuffer ? "its temporary path buffer is retained; " : "") + "late-loaded hooks remain inactive";
                return ERROR_TIMEOUT;
            }
        }
    }
public:
    BootstrapLoader() : process(NULL), thread(NULL), pathBuffer(NULL), keepAlive(NULL), start(0) {}
    ~BootstrapLoader() {
        // Never release an argument still readable by a running remote thread.
        // A bounded timeout deliberately leaves this small inactive allocation.
        if (pathBuffer && (!thread || Complete())) VirtualFreeEx(process, pathBuffer, 0, MEM_RELEASE);
        if (thread) CloseHandle(thread);
        if (process) CloseHandle(process);
    }
    DWORD Run(DWORD pid, HANDLE originalProcess, HMODULE dll, const std::wstring& dllPath,
        DWORD began, bool (*alive)(), std::string& reason) {
        start = began; keepAlive = alive;
        reason = "Cannot prepare the selected application's standard Windows loader";
        if (!keepAlive()) return ERROR_CANCELLED;
        process = OpenProcess(SYNCHRONIZE | PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE |
            PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, FALSE, pid);
        if (!process) {
            DWORD openError = GetLastError();
            reason = openError == ERROR_ACCESS_DENIED ?
                "Windows denied access required for the selected application's standard loader" :
                "Windows could not open the selected process for its standard loader";
            return openError;
        }
        FILETIME a, b, ignored1, ignored2, ignored3;
        if (!GetProcessTimes(originalProcess, &a, &ignored1, &ignored2, &ignored3) ||
            !GetProcessTimes(process, &b, &ignored1, &ignored2, &ignored3) || CompareFileTime(&a, &b) != 0)
            return ERROR_INVALID_OWNER;
        BootstrapFile hookFile;
        DWORD error = ERROR_SUCCESS;
        if (!hookFile.Open(dllPath, error)) { reason = "Cannot lock and verify the fullscreen DLL file before loading"; return error; }
        std::wstring loadedLocalPath;
        if (!BootstrapModulePath(dll, loadedLocalPath, error) || !SameAbsolutePath(loadedLocalPath.c_str(), dllPath.c_str()))
            return error ? error : ERROR_INVALID_NAME;
        std::vector<BootstrapModule> modules;
        if (!BootstrapModules(pid, modules, error)) return error;
        BootstrapModule targetDll = {}; size_t hookCount = 0;
        {
            // Even if the callback raced us and loaded this exact DLL, take a
            // normal loader reference before invoking its export. Unhooking a
            // callback after a timeout must not invalidate a queued entry point.
            FARPROC loadLibrary = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
            HMODULE owner = NULL; std::wstring ownerPath;
            if (!loadLibrary || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    reinterpret_cast<LPCWSTR>(loadLibrary), &owner) || !BootstrapModulePath(owner, ownerPath, error))
                return error ? error : ERROR_PROC_NOT_FOUND;
            BootstrapModule remoteOwner = {}; size_t matches = 0;
            for (size_t i = 0; i < modules.size(); ++i)
                if (SameAbsolutePath(modules[i].path.c_str(), ownerPath.c_str())) { remoteOwner = modules[i]; ++matches; }
            uintptr_t address = 0;
            if (matches != 1 || !BootstrapResolve(process, owner, loadLibrary, remoteOwner, address, error)) {
                reason = "Cannot verify the target's Windows loader module and address";
                return error ? error : ERROR_MOD_NOT_FOUND;
            }
            SIZE_T bytes = (dllPath.size() + 1) * sizeof(wchar_t), written = 0;
            pathBuffer = VirtualAllocEx(process, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!pathBuffer) return GetLastError();
            if (!WriteProcessMemory(process, pathBuffer, dllPath.c_str(), bytes, &written) || written != bytes)
                return GetLastError() ? GetLastError() : ERROR_PARTIAL_COPY;
            error = RunThread(address, pathBuffer, "LoadLibraryW", reason);
            if (error) return error;
            BootstrapObservation observed = {};
            observed.pid = pid;
            // Capture before closing/replacing the thread handle or doing any
            // module inspection. GetLastError here belongs only to this host.
            if (!GetExitCodeThread(thread, &observed.threadResult)) {
                DWORD queryError = GetLastError();
                reason = "Cannot read the completed Windows loader thread's result; initialization was not invoked";
                return queryError;
            }
            observed.processWait = WaitForSingleObject(process, 0);
            if (observed.processWait == WAIT_FAILED) {
                DWORD waitError = GetLastError();
                reason = BootstrapDiagnostic(observed);
                return waitError;
            }
            if (observed.processWait == WAIT_OBJECT_0) {
                if (!GetExitCodeProcess(process, &observed.processExit)) {
                    DWORD queryError = GetLastError();
                    reason = "The selected process exited, but its exit status could not be read; initialization was not invoked";
                    return queryError;
                }
                reason = BootstrapDiagnostic(observed);
                return ERROR_PROCESS_ABORTED;
            }
            if (observed.processWait != WAIT_TIMEOUT) {
                reason = BootstrapDiagnostic(observed); return ERROR_INVALID_DATA;
            }
            // The thread result is never a module base or remote Win32 error.
            // Even its low DWORD being zero does not override a verified module.
            VirtualFreeEx(process, pathBuffer, 0, MEM_RELEASE); pathBuffer = NULL;
            modules.clear();
            if (!BootstrapModules(pid, modules, error)) {
                reason = BootstrapDiagnostic(observed) + "; module enumeration failed";
                return error;
            }
            // Only this exact DLL counts: a RegionHook.dll from another folder is an earlier
            // build's resident hook, which stays pass-through beside this one.
            for (size_t i = 0; i < modules.size(); ++i)
                if (SameAbsolutePath(modules[i].path.c_str(), dllPath.c_str())) { targetDll = modules[i]; ++hookCount; }
            observed.modulesInspected = true;
            observed.hookCount = hookCount;
            observed.exactPath = hookCount == 1;
            BootstrapOutcome outcome = BootstrapClassify(observed);
            if (outcome != BootstrapVerified) {
                reason = BootstrapDiagnostic(observed);
                return outcome == BootstrapModuleMissing ? RS_ERROR_LOADER_MODULE_NOT_OBSERVED : RS_ERROR_LOADER_MODULE_IDENTITY;
            }
        }
        // Reopen and hash the actual enumerated DLL path while the original is
        // locked. This also rejects path redirection or an unexpected image file.
        BootstrapFile remoteFile;
        if (!remoteFile.Open(targetDll.path, error) || memcmp(hookFile.hash, remoteFile.hash, sizeof(hookFile.hash)) != 0) {
            reason = "The loaded fullscreen DLL file did not match the verified helper file";
            return error ? error : ERROR_CRC;
        }
        FARPROC initialize = GetProcAddress(dll, "RegionInitialize");
        if (!initialize) initialize = GetProcAddress(dll, "_RegionInitialize@4");
        if (!initialize) initialize = GetProcAddress(dll, "RegionInitialize@4");
        uintptr_t entry = 0;
        if (!initialize || !BootstrapResolve(process, dll, initialize, targetDll, entry, error)) {
            reason = "Cannot verify the loaded fullscreen DLL initialization entry point";
            return error ? error : ERROR_PROC_NOT_FOUND;
        }
        error = RunThread(entry, NULL, "RegionInitialize", reason);
        if (error) return error;
        DWORD exitCode = ERROR_SUCCESS;
        if (!GetExitCodeThread(thread, &exitCode)) return GetLastError();
        if (exitCode) { reason = "The loaded fullscreen DLL rejected initialization"; return exitCode; }
        reason.clear();
        return ERROR_SUCCESS;
    }
};
