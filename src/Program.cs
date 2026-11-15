using System.Text;
using FiddlerEverywherePatcher.Common;
using FiddlerEverywherePatcher.Managed;
using FiddlerEverywherePatcher.Native;
using FiddlerEverywherePatcher.Status;

// Fiddler Everywhere patcher.
//
// The entry point uses top-level statements and keeps the patch flow in local functions
// (DoPatch, PatchLibfiddlerPE, GetMethods, ...), one per native layout plus the managed
// patch. Install discovery lives here; the file formats themselves live in Native/ and
// Managed/.
//
// Two behaviours are intentional, both described in docs/PATCH_ANALYSIS.md:
//
//   * the target files are looked up in every layout the product ships, and a target
//     that is not present is reported and skipped instead of aborting the run,
//   * a file whose contents do not match the expected build is reported and left alone.
//
// Besides the interactive patch, the tool understands two commands:
//
//   --status    report what state an installation is in, without changing anything
//   --restore   put the files back from their backups
//
// Both take an optional installation directory. Without one the default location is used.

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0)
{
    string command = args[0];

    if (command is "--status" or "--restore")
    {
        string installRoot = args.Length > 1 ? args[1] : InstallLayout.ResolveDefaultInstallRoot();

        return command == "--status"
            ? InstallStatus.Report(installRoot)
            : RestoreInstallation(installRoot);
    }

    // Any other argument is treated as the installation directory.
    PatchInstallation(command);
    return 0;
}

Console.WriteLine("If the path is not accessible, please run this tool as administrator.");
Console.WriteLine("Enter the path to the Fiddler Everywhere installation (press Enter to use the default location)");

string? entered = Console.ReadLine();
string root = string.IsNullOrWhiteSpace(entered)
    ? InstallLayout.ResolveDefaultInstallRoot()
    : entered.Trim();

PatchInstallation(root);
return 0;

// ---------------------------------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------------------------------

/// <summary>
/// Patches the native library and the managed Web UI assembly under
/// <paramref name="installRoot"/>, skipping the ones that are not present.
/// </summary>
void PatchInstallation(string installRoot)
{
    string? nativeLibrary = InstallLayout.FindFirstExisting(InstallLayout.NativeLibraryCandidates(installRoot));
    if (nativeLibrary != null)
    {
        DoPatch(nativeLibrary, PatchNativeLibrary);
    }
    else
    {
        Console.WriteLine($"No native library found under '{installRoot}', skipping the native patch.");
    }

    string? webUiAssembly = InstallLayout.FindFirstExisting(InstallLayout.WebUiAssemblyCandidates(installRoot));
    if (webUiAssembly != null)
    {
        DoPatch(webUiAssembly, PatchFiddlerWebUi);
    }
    else
    {
        Console.WriteLine($"No Web UI assembly found under '{installRoot}', skipping the Web UI patch.");
    }

    Console.WriteLine("Done");

    // The original ends with a key press so that the window stays open. That is only
    // possible when the console owns the input, so it is skipped when it is redirected.
    if (!Console.IsInputRedirected)
    {
        Console.ReadKey();
    }
}

/// <summary>
/// Puts the native library and the managed assembly back from their backups.
/// </summary>
/// <returns><c>0</c> when everything was restored, <c>1</c> when something was missing.</returns>
int RestoreInstallation(string installRoot)
{
    Console.WriteLine($"Restoring the installation under '{installRoot}'...");

    bool complete = true;

    string? nativeLibrary = InstallLayout.FindFirstExisting(InstallLayout.NativeLibraryCandidates(installRoot));
    if (nativeLibrary is null)
    {
        Console.WriteLine("  No native library found, nothing to restore.");
    }
    else
    {
        string? backup = RestoreFile(nativeLibrary);
        if (backup is null)
        {
            Console.WriteLine($"  No backup found for '{nativeLibrary}', it was left alone.");
            complete = false;
        }
        else
        {
            Console.WriteLine($"  Restored '{nativeLibrary}' from '{backup}'.");
        }
    }

    string? webUiAssembly = InstallLayout.FindFirstExisting(InstallLayout.WebUiAssemblyCandidates(installRoot));
    if (webUiAssembly is null)
    {
        Console.WriteLine("  No Web UI assembly found, nothing to restore.");
    }
    else
    {
        string? backup = RestoreFile(webUiAssembly);
        if (backup is null)
        {
            Console.WriteLine($"  No backup found for '{webUiAssembly}', it was left alone.");
            complete = false;
        }
        else
        {
            Console.WriteLine($"  Restored '{webUiAssembly}' from '{backup}'.");
        }
    }

    return complete ? 0 : 1;
}

/// <summary>
/// Restores one file from its backup and reports what happened.
/// </summary>
/// <returns>The backup that was used, or <c>null</c> when nothing was restored.</returns>
string? RestoreFile(string path)
{
    try
    {
        return Backup.Restore(path);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Could not restore '{path}': {ex.GetType().Name}: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Backs the file up, then applies <paramref name="patch"/> to it. Failures are reported
/// and do not abort the remaining work, which matches the observed behaviour of the
/// original tool (one broken target does not stop the others).
/// </summary>
void DoPatch(string path, Func<string, bool> patch)
{
    Console.WriteLine($"Patching '{path}'...");

    try
    {
        Backup.Create(path);

        if (patch(path))
        {
            Console.WriteLine($"Patched '{path}'.");
        }
        else
        {
            Console.WriteLine($"'{path}' was already patched.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to patch '{path}': {ex.GetType().FullName}: {ex.Message}");
    }
}

/// <summary>Windows: patches the native <c>libfiddler</c> PE image.</summary>
bool PatchLibfiddlerPE(string path) => PePatcher.Patch(path);

/// <summary>Windows: patches the native <c>libfiddler</c> PE image of newer builds.</summary>
bool PatchLibfiddlerPEV2(string path) => PePatcher.Patch(path);

/// <summary>Linux/macOS: patches the native ELF image using the legacy offset.</summary>
bool UseLegacyPatchLibfiddlerELF(string path) => ElfPatcher.Patch(path, useLegacyOffset: true);

/// <summary>Linux/macOS: patches the native ELF image.</summary>
bool PatchLibfiddlerELF(string path) => ElfPatcher.Patch(path, useLegacyOffset: false);

/// <summary>Linux/macOS: patches the native ELF image of newer builds.</summary>
bool PatchLibfiddlerELFV2(string path) => ElfPatcher.Patch(path, useLegacyOffset: false);

/// <summary>Replaces the licence lookup in the managed Web UI assembly.</summary>
bool PatchFiddlerWebUi(string path) => WebUiPatcher.Patch(path);

/// <summary>Dispatches to the patch routine that matches the file and the platform.</summary>
bool PatchNativeLibrary(string path)
{
    string name = Path.GetFileName(path);

    if (OperatingSystem.IsWindows())
    {
        // Newer Fiddler Everywhere builds ship "libfiddler.dll", older ones "fiddler.dll".
        return name.Equals("libfiddler.dll", StringComparison.OrdinalIgnoreCase)
            ? PatchLibfiddlerPE(path)
            : PatchLibfiddlerPEV2(path);
    }

    if (OperatingSystem.IsMacOS())
    {
        return name.Equals("libfiddler.dylib", StringComparison.OrdinalIgnoreCase)
            ? PatchLibfiddlerELFV2(path)
            : UseLegacyPatchLibfiddlerELF(path);
    }

    // The AppImage layout keeps the native library in a legacy position.
    return name.Equals("libfiddler.so", StringComparison.OrdinalIgnoreCase)
        ? PatchLibfiddlerELF(path)
        : UseLegacyPatchLibfiddlerELF(path);
}
