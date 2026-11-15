// Proxy for the Windows version.dll.
//
// Fiddler Everywhere is an Electron application and its executable imports VERSION.dll
// statically. The loader resolves that import from the application directory before it
// looks at System32, so a file called version.dll placed next to the executable is loaded
// instead of the system one.
//
// This proxy forwards every call to the real System32\version.dll. The runtime patch
// work will be added here in later steps; for now the proxy is a pure forwarder so that
// the loading behaviour can be verified against the real application first.
//
// The real module is loaded by absolute path. Loading it by name would resolve to this
// very file, because the loader finds the application directory copy first.
//
// The exported functions are declared in winver.h, so the wrappers below are checked
// against the system prototypes by the compiler.

#include <windows.h>
#include <winver.h>

// GetFileVersionInfoByHandle is exported by version.dll but is not declared in the SDK
// headers, so the prototype is spelled out here.
using GetFileVersionInfoByHandle_t = BOOL(WINAPI*)(DWORD, HANDLE, DWORD, LPVOID);

namespace {

struct RealApi {
    HMODULE module = nullptr;
    decltype(&GetFileVersionInfoA) GetFileVersionInfoA = nullptr;
    decltype(&GetFileVersionInfoW) GetFileVersionInfoW = nullptr;
    GetFileVersionInfoByHandle_t GetFileVersionInfoByHandle = nullptr;
    decltype(&GetFileVersionInfoExA) GetFileVersionInfoExA = nullptr;
    decltype(&GetFileVersionInfoExW) GetFileVersionInfoExW = nullptr;
    decltype(&GetFileVersionInfoSizeA) GetFileVersionInfoSizeA = nullptr;
    decltype(&GetFileVersionInfoSizeW) GetFileVersionInfoSizeW = nullptr;
    decltype(&GetFileVersionInfoSizeExA) GetFileVersionInfoSizeExA = nullptr;
    decltype(&GetFileVersionInfoSizeExW) GetFileVersionInfoSizeExW = nullptr;
    decltype(&VerFindFileA) VerFindFileA = nullptr;
    decltype(&VerFindFileW) VerFindFileW = nullptr;
    decltype(&VerInstallFileA) VerInstallFileA = nullptr;
    decltype(&VerInstallFileW) VerInstallFileW = nullptr;
    decltype(&VerLanguageNameA) VerLanguageNameA = nullptr;
    decltype(&VerLanguageNameW) VerLanguageNameW = nullptr;
    decltype(&VerQueryValueA) VerQueryValueA = nullptr;
    decltype(&VerQueryValueW) VerQueryValueW = nullptr;
};

INIT_ONCE g_initOnce = INIT_ONCE_STATIC_INIT;
RealApi g_real;

BOOL CALLBACK InitializeReal(PINIT_ONCE, PVOID, PVOID*)
{
    wchar_t path[MAX_PATH] = {};
    UINT length = GetSystemDirectoryW(path, MAX_PATH);
    if (length == 0 || length + 13 > MAX_PATH)
    {
        return TRUE;
    }

    // Append "\version.dll" by hand so the proxy does not need the C runtime.
    path[length] = L'\\';
    path[length + 1] = L'v';
    path[length + 2] = L'e';
    path[length + 3] = L'r';
    path[length + 4] = L's';
    path[length + 5] = L'i';
    path[length + 6] = L'o';
    path[length + 7] = L'n';
    path[length + 8] = L'.';
    path[length + 9] = L'd';
    path[length + 10] = L'l';
    path[length + 11] = L'l';
    path[length + 12] = L'\0';

    g_real.module = LoadLibraryW(path);
    if (g_real.module == nullptr)
    {
        return TRUE;
    }

    g_real.GetFileVersionInfoA = reinterpret_cast<decltype(&GetFileVersionInfoA)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoA"));
    g_real.GetFileVersionInfoW = reinterpret_cast<decltype(&GetFileVersionInfoW)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoW"));
    g_real.GetFileVersionInfoByHandle = reinterpret_cast<GetFileVersionInfoByHandle_t>(
        GetProcAddress(g_real.module, "GetFileVersionInfoByHandle"));
    g_real.GetFileVersionInfoExA = reinterpret_cast<decltype(&GetFileVersionInfoExA)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoExA"));
    g_real.GetFileVersionInfoExW = reinterpret_cast<decltype(&GetFileVersionInfoExW)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoExW"));
    g_real.GetFileVersionInfoSizeA = reinterpret_cast<decltype(&GetFileVersionInfoSizeA)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoSizeA"));
    g_real.GetFileVersionInfoSizeW = reinterpret_cast<decltype(&GetFileVersionInfoSizeW)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoSizeW"));
    g_real.GetFileVersionInfoSizeExA = reinterpret_cast<decltype(&GetFileVersionInfoSizeExA)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoSizeExA"));
    g_real.GetFileVersionInfoSizeExW = reinterpret_cast<decltype(&GetFileVersionInfoSizeExW)>(
        GetProcAddress(g_real.module, "GetFileVersionInfoSizeExW"));
    g_real.VerFindFileA = reinterpret_cast<decltype(&VerFindFileA)>(
        GetProcAddress(g_real.module, "VerFindFileA"));
    g_real.VerFindFileW = reinterpret_cast<decltype(&VerFindFileW)>(
        GetProcAddress(g_real.module, "VerFindFileW"));
    g_real.VerInstallFileA = reinterpret_cast<decltype(&VerInstallFileA)>(
        GetProcAddress(g_real.module, "VerInstallFileA"));
    g_real.VerInstallFileW = reinterpret_cast<decltype(&VerInstallFileW)>(
        GetProcAddress(g_real.module, "VerInstallFileW"));
    g_real.VerLanguageNameA = reinterpret_cast<decltype(&VerLanguageNameA)>(
        GetProcAddress(g_real.module, "VerLanguageNameA"));
    g_real.VerLanguageNameW = reinterpret_cast<decltype(&VerLanguageNameW)>(
        GetProcAddress(g_real.module, "VerLanguageNameW"));
    g_real.VerQueryValueA = reinterpret_cast<decltype(&VerQueryValueA)>(
        GetProcAddress(g_real.module, "VerQueryValueA"));
    g_real.VerQueryValueW = reinterpret_cast<decltype(&VerQueryValueW)>(
        GetProcAddress(g_real.module, "VerQueryValueW"));

    return TRUE;
}

RealApi& Real()
{
    InitOnceExecuteOnce(&g_initOnce, InitializeReal, nullptr, nullptr);
    return g_real;
}

}  // namespace

BOOL WINAPI GetFileVersionInfoA(LPCSTR fileName, DWORD handle, DWORD length, LPVOID data)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoA != nullptr ? api.GetFileVersionInfoA(fileName, handle, length, data) : FALSE;
}

BOOL WINAPI GetFileVersionInfoW(LPCWSTR fileName, DWORD handle, DWORD length, LPVOID data)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoW != nullptr ? api.GetFileVersionInfoW(fileName, handle, length, data) : FALSE;
}

BOOL WINAPI GetFileVersionInfoByHandle(DWORD flags, HANDLE file, DWORD length, LPVOID data){
    RealApi& api = Real();
    return api.GetFileVersionInfoByHandle != nullptr ? api.GetFileVersionInfoByHandle(flags, file, length, data) : FALSE;
}

BOOL WINAPI GetFileVersionInfoExA(DWORD flags, LPCSTR fileName, DWORD handle, DWORD length, LPVOID data)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoExA != nullptr ? api.GetFileVersionInfoExA(flags, fileName, handle, length, data) : FALSE;
}

BOOL WINAPI GetFileVersionInfoExW(DWORD flags, LPCWSTR fileName, DWORD handle, DWORD length, LPVOID data)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoExW != nullptr ? api.GetFileVersionInfoExW(flags, fileName, handle, length, data) : FALSE;
}

DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR fileName, LPDWORD handle)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoSizeA != nullptr ? api.GetFileVersionInfoSizeA(fileName, handle) : 0;
}

DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR fileName, LPDWORD handle)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoSizeW != nullptr ? api.GetFileVersionInfoSizeW(fileName, handle) : 0;
}

DWORD WINAPI GetFileVersionInfoSizeExA(DWORD flags, LPCSTR fileName, LPDWORD handle)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoSizeExA != nullptr ? api.GetFileVersionInfoSizeExA(flags, fileName, handle) : 0;
}

DWORD WINAPI GetFileVersionInfoSizeExW(DWORD flags, LPCWSTR fileName, LPDWORD handle)
{
    RealApi& api = Real();
    return api.GetFileVersionInfoSizeExW != nullptr ? api.GetFileVersionInfoSizeExW(flags, fileName, handle) : 0;
}

DWORD WINAPI VerFindFileA(
    DWORD flags,
    LPCSTR fileName,
    LPCSTR winDir,
    LPCSTR appDir,
    LPSTR currentDir,
    PUINT currentDirLength,
    LPSTR destDir,
    PUINT destDirLength)
{
    RealApi& api = Real();
    return api.VerFindFileA != nullptr
        ? api.VerFindFileA(flags, fileName, winDir, appDir, currentDir, currentDirLength, destDir, destDirLength)
        : 0;
}

DWORD WINAPI VerFindFileW(
    DWORD flags,
    LPCWSTR fileName,
    LPCWSTR winDir,
    LPCWSTR appDir,
    LPWSTR currentDir,
    PUINT currentDirLength,
    LPWSTR destDir,
    PUINT destDirLength)
{
    RealApi& api = Real();
    return api.VerFindFileW != nullptr
        ? api.VerFindFileW(flags, fileName, winDir, appDir, currentDir, currentDirLength, destDir, destDirLength)
        : 0;
}

DWORD WINAPI VerInstallFileA(
    DWORD flags,
    LPCSTR srcFileName,
    LPCSTR destFileName,
    LPCSTR srcDir,
    LPCSTR destDir,
    LPCSTR currentDir,
    LPSTR tempFile,
    PUINT tempFileLength)
{
    RealApi& api = Real();
    return api.VerInstallFileA != nullptr
        ? api.VerInstallFileA(flags, srcFileName, destFileName, srcDir, destDir, currentDir, tempFile, tempFileLength)
        : 0;
}

DWORD WINAPI VerInstallFileW(
    DWORD flags,
    LPCWSTR srcFileName,
    LPCWSTR destFileName,
    LPCWSTR srcDir,
    LPCWSTR destDir,
    LPCWSTR currentDir,
    LPWSTR tempFile,
    PUINT tempFileLength)
{
    RealApi& api = Real();
    return api.VerInstallFileW != nullptr
        ? api.VerInstallFileW(flags, srcFileName, destFileName, srcDir, destDir, currentDir, tempFile, tempFileLength)
        : 0;
}

DWORD WINAPI VerLanguageNameA(DWORD language, LPSTR buffer, DWORD length)
{
    RealApi& api = Real();
    return api.VerLanguageNameA != nullptr ? api.VerLanguageNameA(language, buffer, length) : 0;
}

DWORD WINAPI VerLanguageNameW(DWORD language, LPWSTR buffer, DWORD length)
{
    RealApi& api = Real();
    return api.VerLanguageNameW != nullptr ? api.VerLanguageNameW(language, buffer, length) : 0;
}

BOOL WINAPI VerQueryValueA(LPCVOID block, LPCSTR subBlock, LPVOID* buffer, PUINT length)
{
    RealApi& api = Real();
    return api.VerQueryValueA != nullptr ? api.VerQueryValueA(block, subBlock, buffer, length) : FALSE;
}

BOOL WINAPI VerQueryValueW(LPCVOID block, LPCWSTR subBlock, LPVOID* buffer, PUINT length)
{
    RealApi& api = Real();
    return api.VerQueryValueW != nullptr ? api.VerQueryValueW(block, subBlock, buffer, length) : FALSE;
}

namespace {

// Name of the managed startup hook that runs in the backend process.
constexpr wchar_t kHookFileName[] = L"FiddlerEverywherePatcher.Hook.dll";

// Prepares the environment of the child processes.
//
// The backend is a .NET application, so the runtime can be told to load our managed startup
// hook before the application starts. The variable is set here, during the initialisation
// of this process, and the backend inherits it when it is spawned.
//
// Tiered compilation is switched off because the hook replaces compiled code: a later
// recompilation would silently undo the replacement.
void InstallEnvironment(HINSTANCE instance)
{
    wchar_t path[MAX_PATH] = {};
    DWORD length = GetModuleFileNameW(instance, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return;
    }

    // Cut the file name off, keeping the directory.
    wchar_t* lastSeparator = wcsrchr(path, L'\\');
    if (lastSeparator == nullptr)
    {
        return;
    }

    lastSeparator[1] = L'\0';
    size_t directoryLength = wcslen(path);
    if (directoryLength + wcslen(kHookFileName) + 1 > MAX_PATH)
    {
        return;
    }

    wcscat_s(path, kHookFileName);
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES)
    {
        // The hook is not installed next to the proxy, so there is nothing to prepare.
        return;
    }

    SetEnvironmentVariableW(L"DOTNET_STARTUP_HOOKS", path);
    SetEnvironmentVariableW(L"DOTNET_TieredCompilation", L"0");
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        InstallEnvironment(instance);
    }

    return TRUE;
}
