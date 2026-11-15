# Fiddler Everywhere Patcher

A small .NET 8 console application that patches a local Fiddler Everywhere installation so
it runs with an offline enterprise licence. It modifies two files: the native `libfiddler`
library, which verifies the integrity of the Web UI assemblies, and `Fiddler.WebUi.dll`,
which reads and validates the offline licence.

The tool resolves the installation directory, backs up every file before it touches it and
reports what it did. `dnlib` is the only package it depends on.

The patch points, the IL that is generated and the checks the application performs before
it accepts a licence are described in
[Technical patch analysis](docs/PATCH_ANALYSIS.md).

## What it patches

| File | Format | Change |
| --- | --- | --- |
| `libfiddler.dll`, `fiddler.dll` | PE | `0xC3` (`ret`) is written over the first byte of the integrity-check routine at `.text + 0x3F0`, so the routine returns without running the check |
| `libfiddler.so`, `libfiddler.dylib` | ELF | the same byte at the same section-relative offset, located by walking the ELF section table |
| `Fiddler.WebUi.dll` | .NET assembly | the body of `BackendService.ReadOfflineLicenseFile` is replaced with a routine that returns a locally built offline licence |

Every layout the product ships is tried, so the tool works with the older `resources/app/...`
tree and with the current `resources/app.asar.unpacked/...` tree.

## Requirements

* .NET 8 SDK to build and run.
* Windows, Linux or macOS. The Windows installation is the one the patch offsets were
  measured on; the ELF variant has not been exercised against a real Linux or macOS copy.

## Build and run

```powershell
dotnet build
dotnet run
```

The tool asks for the Fiddler Everywhere directory. An empty line uses the default
installation:

* Windows: `%LOCALAPPDATA%\Programs\Fiddler Everywhere`, then `C:\Program Files\Fiddler Everywhere`
* macOS: `/Applications/Fiddler Everywhere.app`
* Linux: `squashfs-root` (the extracted AppImage)

For each target it prints `Patching '<file>'...` and then either `Patched '<file>'.` or
`'<file>' was already patched.`. A missing target is reported and skipped, and a file that
does not look like the expected build is left alone. The run ends with `Done`.

To produce a standalone executable:

```powershell
dotnet publish -r win-x64 -c Release
```

## Another route: the version.dll proxy

The `proxy` directory holds a second way to apply the patch. A `version.dll` is placed next
to the executable, and it prepares the environment so that a managed startup hook replaces
the licence methods inside the running backend process. Nothing in the installation is
modified, and the patch is applied on every launch. See
[proxy/README.md](proxy/README.md).

## Checking and restoring an installation

The tool can report what state an installation is in without touching it, and it can put the
files back from their backups:

```powershell
dotnet run -- --status
dotnet run -- --restore
```

Both take an optional installation directory, so `dotnet run -- --status "C:\path\to\install"`
works as well. The status report says which of the patches are present, shows the hash of
each file and of its backup, and reads the patch byte in the backup as well, so a restored
installation can be confirmed byte for byte. It exits with `0` when the installation is
unmodified and `1` when it carries patches.

## Backups

Before a file is modified it is copied next to itself as
`<file>.<PE linker timestamp>.bak`, for example `fiddler.dll.6AEC86D5.bak`. The timestamp
comes from the COFF header, so a file that is not a PE image gets `00000000`. An existing
backup is never overwritten.

## Repository layout

```
FiddlerEverywherePatcher.csproj   project file
src/
  Program.cs                      entry point, install discovery, per-target patching
  Native/PePatcher.cs             native patch for PE images
  Native/ElfPatcher.cs            native patch for ELF images
  Managed/WebUiPatcher.cs         managed patch for Fiddler.WebUi.dll
  Managed/PatchScript.cs          model for the reference instruction list
  Managed/PatchScript.json        reference instruction list, embedded in the build
  Common/Backup.cs                backup file naming
docs/
  PATCH_ANALYSIS.md               technical detail on every change
```

## Current limits

* The native offset is tied to the build it was measured on. The patcher expects `0x55`
  (`push rbp`) at the patch location and refuses to write anything else, so a different
  build is not corrupted.
* The ELF offsets are derived from the PE measurements and are not verified against a
  Linux or macOS installation.
* Fiddler Everywhere 8.4.0 applies further conditions before it uses an offline licence: the
  profile must be in offline mode, the licence e-mail must match the current Windows
  account, and the licence lookup must return a DTO rather than null. Section 6 of the
  analysis describes all three and the edits that satisfy them. They are not part of this
  build yet.
