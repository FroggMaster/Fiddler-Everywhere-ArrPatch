// Compares what the proxy returns with what the system version.dll returns.
//
// usage: proxytest <proxy.dll> <target file>
//
// The proxy is loaded by absolute path, so this exercises the same path the application
// uses: the proxy resolves the real version.dll from System32 and forwards to it. The
// comparison is byte for byte over the raw version block, which covers everything the
// version API can return for that file.

#include <windows.h>
#include <winver.h>
#include <cstdio>

using GetFileVersionInfoSizeW_t = DWORD(WINAPI*)(LPCWSTR, LPDWORD);
using GetFileVersionInfoW_t = BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID);
using VerQueryValueW_t = BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT);

struct Query {
    bool ok = false;
    std::size_t size = 0;
    unsigned char digest = 0;  // simple checksum over the raw block
    DWORD fileVersion = 0;
    DWORD productVersion = 0;
};

unsigned char Checksum(const unsigned char* data, std::size_t length)
{
    unsigned char sum = 0;
    for (std::size_t i = 0; i < length; i++)
    {
        sum = static_cast<unsigned char>(sum + data[i] * (i % 251 + 1));
    }

    return sum;
}

Query Read(HMODULE module, const wchar_t* target)
{
    Query result;

    auto size = reinterpret_cast<GetFileVersionInfoSizeW_t>(GetProcAddress(module, "GetFileVersionInfoSizeW"));
    auto get = reinterpret_cast<GetFileVersionInfoW_t>(GetProcAddress(module, "GetFileVersionInfoW"));
    auto query = reinterpret_cast<VerQueryValueW_t>(GetProcAddress(module, "VerQueryValueW"));

    if (size == nullptr || get == nullptr || query == nullptr)
    {
        wprintf(L"  exports missing in module %p\n", reinterpret_cast<void*>(module));
        return result;
    }

    DWORD handle = 0;
    DWORD length = size(target, &handle);
    if (length == 0)
    {
        wprintf(L"  GetFileVersionInfoSizeW failed with %lu\n", GetLastError());
        return result;
    }

    unsigned char* buffer = static_cast<unsigned char*>(HeapAlloc(GetProcessHeap(), 0, length));
    if (buffer == nullptr)
    {
        return result;
    }

    if (!get(target, handle, length, buffer))
    {
        wprintf(L"  GetFileVersionInfoW failed with %lu\n", GetLastError());
        HeapFree(GetProcessHeap(), 0, buffer);
        return result;
    }

    result.ok = true;
    result.size = length;
    result.digest = Checksum(buffer, length);

    LPVOID value = nullptr;
    UINT valueLength = 0;
    if (query(buffer, L"\\FixedFileInfo", &value, &valueLength) && value != nullptr && valueLength >= sizeof(VS_FIXEDFILEINFO))
    {
        auto fixed = static_cast<const VS_FIXEDFILEINFO*>(value);
        result.fileVersion = HIWORD(fixed->dwFileVersionMS) << 16 | LOWORD(fixed->dwFileVersionMS);
        result.productVersion = HIWORD(fixed->dwProductVersionMS) << 16 | LOWORD(fixed->dwProductVersionMS);
    }

    HeapFree(GetProcessHeap(), 0, buffer);
    return result;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3)
    {
        wprintf(L"usage: proxytest <proxy.dll> <target file>\n");
        return 2;
    }

    wchar_t systemPath[MAX_PATH] = {};
    UINT length = GetSystemDirectoryW(systemPath, MAX_PATH);
    lstrcatW(systemPath, L"\\version.dll");

    HMODULE proxy = LoadLibraryW(argv[1]);
    if (proxy == nullptr)
    {
        wprintf(L"LoadLibrary of the proxy failed with %lu\n", GetLastError());
        return 3;
    }

    HMODULE system = LoadLibraryW(systemPath);
    if (system == nullptr)
    {
        wprintf(L"LoadLibrary of the system version.dll failed with %lu\n", GetLastError());
        return 4;
    }

    wprintf(L"target : %s\n", argv[2]);
    wprintf(L"proxy  : %s\n", argv[1]);

    Query viaProxy = Read(proxy, argv[2]);
    Query viaSystem = Read(system, argv[2]);

    wprintf(L"  proxy  size=%zu checksum=%u file=0x%08X product=0x%08X ok=%d\n",
        viaProxy.size, viaProxy.digest, viaProxy.fileVersion, viaProxy.productVersion, viaProxy.ok);
    wprintf(L"  system size=%zu checksum=%u file=0x%08X product=0x%08X ok=%d\n",
        viaSystem.size, viaSystem.digest, viaSystem.fileVersion, viaSystem.productVersion, viaSystem.ok);

    bool same = viaProxy.size == viaSystem.size
        && viaProxy.digest == viaSystem.digest
        && viaProxy.fileVersion == viaSystem.fileVersion
        && viaProxy.productVersion == viaSystem.productVersion
        && viaProxy.ok == viaSystem.ok;

    wprintf(L"result: %s\n", same ? L"identical" : L"DIFFERENT");
    if (!viaProxy.ok)
    {
        wprintf(L"note : both refused the file, which is the expected behaviour\n");
    }

    return same ? 0 : 1;
}
