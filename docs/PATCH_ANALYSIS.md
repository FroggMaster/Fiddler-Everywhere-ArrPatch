# Technical patch analysis

This document explains what the patcher changes and why. It is based on Fiddler Everywhere
8.4.0 on Windows, installed per user from the official installer, plus the native and
managed files that installation ships.

Sections 1 to 5 describe the patch the tool applies today. Section 6 describes the extra
checks 8.4.0 performs before it accepts an offline licence, the edits that satisfy them and
which of those edits are in this repository. Section 7 lists the verification runs and
section 8 the parts that remain inferred.

## 1. Patch surface

| File | Format | Role |
| --- | --- | --- |
| `libfiddler.dll` / `fiddler.dll` | PE, x86-64 | loads the Web UI assemblies and verifies their integrity |
| `libfiddler.so` / `libfiddler.dylib` | ELF, x86-64 or arm64 | the same component on Linux and macOS |
| `Fiddler.WebUi.dll` | .NET assembly | the Web UI backend, including the offline licence reader |

The native file is not the licence check itself. It is the loader that reads
`WebServer\Fiddler.WebUi.dll` and the assemblies beside it, and it refuses to continue when
they no longer match what it expects. The licence logic lives in the managed assembly.

### 1.1 Installation layouts

| Platform | Native library | Web UI assembly |
| --- | --- | --- |
| Windows | `libfiddler.dll`, otherwise `fiddler.dll` at the install root | `resources\app\out\WebServer\Fiddler.WebUi.dll` or `resources\app.asar.unpacked\out\WebServer\Fiddler.WebUi.dll` |
| macOS | `Contents/Frameworks/libfiddler.dylib` | `Contents/resources/app/...` or `Contents/resources/app.asar.unpacked/...` |
| Linux | `libfiddler.so` at the install root | `resources/app/...` or `resources/app.asar.unpacked/...` |

Older releases ship the Web UI assemblies unpacked into `resources/app/`. Current releases
leave `resources/app/out/` empty and put the assemblies under `resources/app.asar.unpacked/`.
The patcher tries both, in that order, and skips the managed step when neither is present.

When both `libfiddler.dll` and `fiddler.dll` exist, `libfiddler.dll` is used. The dispatcher
also keeps the older `fiddler.dll` path, which is why the code has the `...V2` and
`UseLegacy...` variants.

## 2. The native patch

### 2.1 The routine

`fiddler.dll` is small (about 70 KB). Its strings are the relative paths of the Web UI
assemblies plus `Fiddler Everywhere integrity check failed for %s`. The routine at RVA
`0x13F0` is 278 bytes long and contains the reference to that message at RVA `0x14CC`, so it
is the routine that performs or reports the integrity check. It starts with
`push rbp; sub rsp, 0x380`.

### 2.2 The PE change

The patch is a single byte: `0xC3`, the x86-64 `ret`, written over the `push rbp`.
The routine then returns immediately and the check never runs.

| Property | Value |
| --- | --- |
| Target section | `.text` |
| Offset inside the section | `0x3F0` |
| File offset in this build | `0x9F0` (`PointerToRawData` + `0x3F0`) |
| Expected original byte | `0x55` (`push rbp`) |
| Written byte | `0xC3` (`ret`) |

The offset is section relative, which was established three ways:

* inserting `0x100` bytes of data in front of the section content moved the patched byte
  from `0x9F0` to `0xAF0`, so the write follows the section rather than a fixed file offset,
* zeroing the whole `.text` section still produced the byte at the same offset, so the
  write does not depend on the surrounding instructions,
* the installed build contained both an unpatched `fiddler.dll` and a patched copy, and the
  only difference between them was that one byte.

The file is parsed with `System.Reflection.PortableExecutable.PEReader`. A file that is not
a PE image fails with `BadImageFormatException: Invalid DOS signature`, which is the
message `PEReader` produces.

### 2.3 The ELF change

The ELF variant uses the same model: find `.text` in the section header table, then write
`0xC3` at `sh_offset + 0x3F0`. `ElfPatcher` reads the ELF header directly and supports both
ELF32 and ELF64 in either byte order. This avoids a dependency on `ELFSharp`, whose 2.x API
does not expose section headers.

The legacy and current code paths currently use the same offset. Only the PE variant could
be measured, because no Linux or macOS installation was available, so the ELF offset is
inferred from the PE result.

### 2.4 Safety behaviour

The patcher reads the byte before writing:

* `0x55`: write `0xC3` and report the file as patched,
* `0xC3`: report the file as already patched and do not write,
* anything else: raise an error and leave the file untouched.

The error names the byte and the offset, for example
`Unexpected byte 0x48 at .text+0x3F0; this build does not match the layout the patch was
derived from.` This is what keeps an unknown or newer build intact.

## 3. The managed patch

### 3.1 Target method

`Fiddler.WebUi.Services.Backend.BackendService.ReadOfflineLicenseFile` returns
`Task<OfflineLicenseFileDTO>`. In the unmodified assembly it reads the licence file from
disk and deserialises it. The patch replaces the whole body with a routine that builds the
same DTO in memory, so the rest of the backend sees a normal offline licence.

### 3.2 Licence contents

| Field | Value |
| --- | --- |
| `User.Email` | `fiddler@telerik.com` |
| `User.UserPrincipalEmail` | `fiddler@telerik.com` |
| `User.FirstName` | `fiddler` |
| `User.LastName` | empty |
| `PlanName` | `enterprise` |
| `PlanPeriod` | `annual` |
| `CreatedAt`, `NotBefore` | `2024/1/1` |
| `ExpiresAt` | `2099/1/1` |
| `Id` | `00112233-4455-6677-8899-aabbccddeeff` |
| `AdditionalData["users"]` | a JSON element holding the serialised `OfflineUserDTO[]` |

### 3.3 Generated IL

The body uses four locals (`OfflineUserDTO`, `OfflineLicenseFileDTO`,
`Dictionary<string, object>`, `JsonDocumentOptions`) and a maximum stack of 7:

1. create the `OfflineUserDTO` and set `Email`, `FirstName`, `LastName` and
   `UserPrincipalEmail` through the property setters,
2. create the `OfflineLicenseFileDTO`,
3. create a `Dictionary<string, object>`,
4. put the user into an `OfflineUserDTO[]`, serialise the array with
   `JsonSerializer.Serialize<T>`, parse the result with `JsonDocument.Parse` and store the
   boxed `RootElement` under the `"users"` key,
5. assign `AdditionalData`, `CreatedAt`, `ExpiresAt`, `Id`, `NotBefore`, `PlanName` and
   `PlanPeriod`,
6. return `Task.FromResult(license)`.

The JSON round trip is needed because `AdditionalData` is `IDictionary<string, object>`
while the application reads the entry as a `JsonElement` that it deserialises into
`OfflineUserDTO[]`. Storing a plain object array there would not survive that read.

### 3.4 Signatures

An instruction operand is only resolved if its signature matches the target exactly. Four
mistakes in an early version of the emitter caused the runtime to reject the method before
any of the licence logic ran:

| Mistake | Runtime error | Correct form |
| --- | --- | --- |
| `T[]` built as an `ARRAY` type specification with rank 0 | `BadImageFormatException: Array rank may not be zero` | `SZArraySig`, which is a rank-1 vector |
| generic method called without the `GENERIC` calling convention, and with `VAR` for its generic parameter | `MissingMethodException: Serialize` | `CallingConvention.Generic` plus `GenericMVar(0)` |
| `Dictionary<string, object>.set_Item` described as taking `string, object` | `MissingMethodException: set_Item` | `GenericVar(0), GenericVar(1)` on the generic type |
| newly built member references where the assembly already had one | resolution failed or picked the wrong overload | reuse the existing `MemberRef` when it matches |

`WebUiPatcher` now resolves each type from the target module, reuses an existing member
reference where one exists and only builds a new one otherwise.

### 3.5 Validation against the reference script

`src/Managed/PatchScript.json` is a 12,066 byte dnlib patch script: type, field and method
references plus the instruction list for the patched method. It is embedded in the build
with a fixed logical name, so its location in the source tree does not matter.

`VerifyAgainstScript` compares the generated IL with that list before anything is written.
Instruction count, opcode names and string operands must match, otherwise the patch throws
and the file is left unmodified. The script therefore acts as the specification of the
patch and the emitter cannot drift away from it silently.

### 3.6 Writing the obfuscated assembly

`Fiddler.WebUi.dll` is obfuscated. It contains one instruction whose operand token does not
exist; the instruction is unreachable because it sits behind an unconditional branch, but a
writer will not emit an instruction that has an operand type but no operand. Before writing,
`SanitizeUnresolvableInstructions` turns such instructions into `nop`. With that step the
stock `dnlib` 4.4.0 package can write the module; the original build used a private dnlib
fork, and whether that fork handles the case internally was not established.

The rewritten assembly keeps its full metadata (1,383 types in this build).

### 3.7 Repeat runs

The patched body starts with `newobj OfflineUserDTO`, which the original body does not, so
`IsAlreadyPatched` checks that first instruction. A second run reports
`'<file>' was already patched.` and writes nothing.

## 4. Backups

Every file is copied before it is modified:

```
<file name>.<PE linker timestamp in uppercase hex>.bak
```

| File | Timestamp | Backup |
| --- | --- | --- |
| `fiddler.dll` | `0x6AEC86D5` | `fiddler.dll.6AEC86D5.bak` |
| `Fiddler.WebUi.dll` | `0x9EC07C25` | `Fiddler.WebUi.dll.9EC07C25.bak` |
| any file that is not a PE image | none | `<file>.00000000.bak` |

The value is the `TimeDateStamp` field of the COFF header. The naming is applied to the
managed assembly too, even though the timestamp is not otherwise meaningful for it, so the
two backups look the same. An existing backup is reused, never overwritten, which keeps the
first pre-patch copy intact across repeated runs.

The patcher can also report the state of an installation and put the files back:

```
dotnet run -- --status
dotnet run -- --restore
```

The status report reads the patch byte in the file and in its backup, and inspects the
managed methods, so it shows exactly which of the patches are present. The restore picks
the backup whose name follows the naming scheme, which is the copy taken before the first
patch, and leaves the file alone when there is none.

## 5. Console behaviour

```
If the path is not accessible, please run this tool as administrator.
Enter the path to the Fiddler Everywhere installation (press Enter to use the default location)
Patching '<native library>'...
Patched '<native library>'.
Patching '<managed assembly>'...
Patched '<managed assembly>'.
Done
```

`DoPatch` wraps each target in its own `try` block, so a failure on one file does not stop
the other. Errors are printed as
`Failed to patch '<file>': <exception type>: <message>`. The process waits for a key press
at the end only when standard input is an interactive console, so redirected runs (scripts,
CI, test harnesses) terminate on their own.

## 6. Checks added in Fiddler Everywhere 8.4.0

The native and licence-body patches are what older releases needed. 8.4.0 adds three
conditions that have to be satisfied before the fabricated licence is used. All three were
found by invoking the assembly's methods in isolation and by running the application with
the patched assembly.

### 6.1 Offline mode

`UsersService.Login` passes `UserProfile.IsOffline` to `BackendService.Login` as its
`isOffline` argument. When it is false the app logs in against the cloud and reports the
account's trial, whatever the licence file contains. The edit replaces the
`ldarg.0 / ldfld / callvirt get_IsOffline` sequence in the login state machine with
`ldc.i4.1` and two `nop`s, so the offline branch is always taken.

### 6.2 User eligibility

`BackendService.EnsureUserEligibility` deserialises `AdditionalData["users"]` and compares
`OfflineUserDTO.UserPrincipalEmail` with `IOfflineLicensesService.GetCurrentUserADEmail()`
and `GetCurrentUserADPrincipalName()`. The resulting state distinguishes two failures:

* `NoADUserFound`: the lookups returned nothing, which is what happens on a machine that is
  not domain joined,
* `IsWrongUser`: the lookups returned an account that does not match the licence.

The edit makes every `GetCurrentUserADEmail` and `GetCurrentUserADPrincipalName`
implementation (`EmptyOfflineLicensesService`, `OfflineLicensesServiceWindows`,
`OfflineLicensesServiceMac`) return `fiddler@telerik.com`, the same address the licence
carries. With that, the state reports `IsWrongUser = false` and `NoADUserFound = false`.

### 6.3 Licence construction

On 8.4.0 the remaining blocker was that `BackendService.GetOfflineLicense` itself returned
`null` even when `ReadOfflineLicenseFile` produced a valid DTO. `Login` then called
`CheckAdvancedStrategy(null)` and threw `NullReferenceException`, which the UI reports as
"Backend login failed".

The working installation therefore also rewrites `GetOfflineLicense` to build a populated
DTO directly and return it through `Task.FromResult`, skipping the obfuscated state
machine. Together with 6.1 and 6.2, this is the step that makes the login succeed on 8.4.0.

### 6.4 What is in this repository

| Edit | Applied to | Part of this build |
| --- | --- | --- |
| native `ret` patch | `libfiddler.dll` / ELF | yes |
| `ReadOfflineLicenseFile` body | `Fiddler.WebUi.dll` | yes |
| force `IsOffline` | `UsersService.Login` state machine | no |
| AD e-mail lookups | three service implementations | no |
| `GetOfflineLicense` body | `BackendService` | no |

The last three were developed and applied to the working installation with a separate
utility that is kept outside the repository, because they depend on 8.4.0's internal type
and state machine names. They belong in `WebUiPatcher` as optional edits keyed on the build
if the tool is to handle 8.4.0 on its own.

## 7. Verification

### 7.1 Sandbox

Both files were patched in a copy of the installation tree with a redirected
`%LOCALAPPDATA%`, then compared with the expected results:

| Aspect | Result |
| --- | --- |
| native patch | exactly one byte changed, `0x9F0` from `0x55` to `0xC3`, backup written |
| managed patch | `ReadOfflineLicenseFile` replaced, reference instruction list matches, backup written |
| second run | both files reported as already patched, no bytes changed |
| non-PE input | reported as `Invalid DOS signature`, file untouched |

### 7.2 Live installation

The same run against the installed application:

* `fiddler.dll`: one byte changed at `.text + 0x3F0`,
* `resources\app.asar.unpacked\out\WebServer\Fiddler.WebUi.dll`: rewritten from 3,181,480
  to 3,076,608 bytes, all 1,383 types retained, backup `Fiddler.WebUi.dll.9EC07C25.bak`,
* second run: no changes.

### 7.3 In-process harness

The licence methods were invoked in isolation, without starting the application:

* `ReadOfflineLicenseFile` returns `OfflineLicenseFileDTO`,
* `GetCurrentUserADEmail` and `GetCurrentUserADPrincipalName` return the licence e-mail,
* `EnsureUserEligibility` yields `User != null`, `IsWrongUser = false`,
  `NoADUserFound = false`,
* with the section 6 edits, `GetOfflineLicense` returns a populated `OfflineLicenseDTO`.

### 7.4 Application log

With all edits applied, a normal start logs:

```
[NETCore] User logged in with offline license
[Angular] Backend login successfully finished.
[Angular] Skipping notifications for offline license
```

There is no `Failed to invoke hub method 'Login'` and no exception afterwards.

## 8. Open items

| Item | State | Note |
| --- | --- | --- |
| ELF offsets (`libfiddler.so`, `libfiddler.dylib`) | inferred | carried over from the PE model; no Linux or macOS install was available to measure them |
| `PatchLibfiddlerPE` and `PatchLibfiddlerPEV2` offsets | inferred | only one PE layout was observed, so both code paths use `0x3F0` |
| macOS bundle layout | untested | paths are handled, but never exercised |
| dnlib fork behaviour | unresolved | the reference build used a private fork; stock dnlib needs the `nop` sanitisation step |
| 8.4.0 companion edits | not in this build | see section 6.4 |
| offset stability across releases | guarded | the patcher refuses any byte other than `0x55` or `0xC3` at the patch location |
