# version.dll proxy

An alternative route to the Fiddler Everywhere patch. Instead of running a tool that edits
the installed files, a proxy DLL is placed next to the application and does the work from
inside the process.

This is a separate component from the patcher in `src/`. The .NET tool remains the
straightforward route: it patches the files and prints what it did. The proxy exists for the
case where the work has to happen automatically on every launch, and for the case where the
installed files must not be touched at all.

## Why version.dll works

Fiddler Everywhere is an Electron application and `Fiddler Everywhere.exe` imports
`VERSION.dll` statically:

```
VERSION.dll: 3 imports
  GetFileVersionInfoSizeW
  GetFileVersionInfoW
  VerQueryValueW
```

`version.dll` is not in `KnownDLLs`, so the loader searches the application directory before
System32 and loads the copy that sits next to the executable. That copy is loaded during
process initialisation, before any application code runs.

The same trick works for `winhttp.dll`, which is loaded dynamically in both the Electron
process and the backend. It was verified with a copy of the system DLL placed in each
application directory:

| Process | Application directory | Loaded from |
| --- | --- | --- |
| `Fiddler Everywhere.exe` (main) | install root | the copy in the install root |
| `Fiddler.WebUi.exe` (backend) | `resources/app.asar.unpacked/out/WebServer` | the copy in that folder |

`version.dll` is the better of the two for the parent process, because the static import
means it is loaded during process initialisation rather than whenever the network stack
happens to come up. The forwarding surface is also trivial: the API is only used to read
file version information.

## How the patch reaches the backend

The licence logic lives in the managed Web UI assembly, in the backend process. The proxy
cannot patch that process directly, so it prepares the environment instead:

1. The proxy sets `DOTNET_STARTUP_HOOKS` and `DOTNET_TieredCompilation=0` in its own
   environment while it is being loaded, which happens during the initialisation of the
   Electron process.
2. The backend inherits both when it is spawned, so the .NET runtime loads the managed
   startup hook before the application entry point runs.
3. The hook finds `Fiddler.WebUi.dll`, which is already loaded at that point, and replaces
   the licence methods inside the running process.

Tiered compilation is switched off because the hook replaces compiled code. A later
recompilation would silently undo the replacement.

## What the hook replaces

| Method | Replacement |
| --- | --- |
| `BackendService.ReadOfflineLicenseFile` | returns a licence file DTO that is built in memory |
| `BackendService.GetOfflineLicense` | returns a licence DTO with the enterprise plan, the annual period and an expiry in 2099 |
| `GetCurrentUserADEmail`, `GetCurrentUserADPrincipalName` | return the e-mail of the licence |
| `BackendService.Login` | forces the offline branch, then hands over to the original |

The last one is the flag that decides whether the application talks to the cloud or uses
the offline licence. It arrives as an argument, so the replacement changes it and calls the
original.

## How the replacement is done

The entry of a prepared method on x64 is a stub that jumps through a pointer, and behind
it sits a fixup block that loads the method descriptor into `r10` and jumps again:

```
entry:   jmp qword ptr [rip+disp32]        ; the pointer that holds the code
fixup:   mov r10, qword ptr [rip+disp32]    ; the method descriptor
         jmp qword ptr [rip+disp32]         ; the code
```

The hook follows that chain and writes the address of the replacement into the pointer that
holds the code. No compiled code is overwritten and the fixup block, which belongs to the
runtime, is left alone.

The pointer is set to the fixup block of the replacement rather than to its code, so the
call still runs the fixup of the replacement: the method descriptor in `r10` belongs to
the method that is actually running, and the call is an ordinary managed call. Pointing the
pointer at a reverse P/Invoke thunk does not work, because the runtime refuses to let
managed code call an unmanaged callable.

For the backend login the replacement has to call the original. That is done by putting the
pointer back to the original code for the duration of the call, because the original is
reached through the same pointer.

## Layout

```
proxy/
  build.ps1                     builds the proxy and its test harness
  src/version_proxy.cpp         the proxy
  src/version_proxy.def         the export table
  test/proxytest.cpp            compares the proxy with the system version.dll
  hook/                         the managed startup hook
    StartupHook.cs              the hook and the replacements
  bin/x64/                      build output
```

## Build

Requires Visual Studio with the x64 C++ tools for the proxy and the .NET SDK for the hook.
The script locates the Visual Studio installation through `vswhere`, so no environment
setup is needed:

```powershell
.\build.ps1
dotnet publish .\hook\FiddlerEverywherePatcher.Hook.csproj -c Release -o .\hook\bin\release
```

Output:

```
bin/x64/version.dll
bin/x64/proxytest.exe
hook/bin/release/FiddlerEverywherePatcher.Hook.dll
```

The proxy has to match the architecture of the target application, which is x64 for the
current Fiddler Everywhere builds.

## Install

Copy both files next to `Fiddler Everywhere.exe`:

```
%LOCALAPPDATA%\Programs\Fiddler Everywhere\version.dll
%LOCALAPPDATA%\Programs\Fiddler Everywhere\FiddlerEverywherePatcher.Hook.dll
```

The hook is only loaded when the proxy finds it next to itself, so the proxy can be
installed on its own.

## Verify

`proxytest` loads the proxy and the system DLL side by side and compares the raw version
block they return for a file:

```powershell
.\bin\x64\proxytest.exe .\bin\x64\version.dll "C:\path\to\Fiddler Everywhere.exe"
```

```
target : C:\path\to\Fiddler Everywhere.exe
proxy  : .\bin\x64\version.dll
  proxy  size=2012 checksum=239 file=0x00000000 product=0x00000000 ok=1
  system size=2012 checksum=239 file=0x00000000 product=0x00000000 ok=1
result: identical
```

With the proxy installed, the running processes show it in the module list:

```
PID 51716 Fiddler Everywhere: C:\...\Fiddler Everywhere\VERSION.dll | C:\WINDOWS\system32\version.dll
PID 61956 Fiddler Everywhere: C:\...\Fiddler Everywhere\VERSION.dll
```

The two paths in the first line are the proxy and the real DLL it loaded by absolute path.

The hook writes a log to `%TEMP%\fe-proxy-hook.log`, and the application log shows the
result:

```
[NETCore] User logged in with offline license
[Angular] Backend login successfully finished.
[Angular] Skipping notifications for offline license
```

## Things to keep in mind

* The proxy is loaded into every Electron process, including the sandboxed renderers. Any
  work it does has to be limited to the process that needs it.
* An unsigned DLL with a system name sitting next to an executable is a pattern that
  endpoint protection software flags. The file has to be excluded or signed before the
  route is usable on a managed machine.
* The product's updater and uninstaller do not know about the extra files.
* The startup hook is loaded into every .NET process the backend starts. A hook that cannot
  be loaded stops the runtime from starting, so the hook has to be defensive.
