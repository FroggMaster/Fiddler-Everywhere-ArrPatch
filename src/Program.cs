using System.Text;
using FiddlerEverywherePatcher.Common;
using FiddlerEverywherePatcher.Managed;
using FiddlerEverywherePatcher.Native;

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

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("If the path is not accessible, please run this tool as administrator.");
Console.WriteLine("Enter the path to the Fiddler Everywhere installation (press Enter to use the default location)");

string? entered = Console.ReadLine();
string installRoot = string.IsNullOrWhiteSpace(entered)
    ? ResolveDefaultInstallRoot()
    : entered.Trim();

string? nativeLibrary = FindFirstExisting(GetNativeLibraryCandidates(installRoot));
if (nativeLibrary != null)
{
    DoPatch(nativeLibrary, PatchNativeLibrary);
}
else
{
    Console.WriteLine($"No native library found under '{installRoot}', skipping the native patch.");
}

string? webUiAssembly = FindFirstExisting(GetWebUiAssemblyCandidates(installRoot));
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

// ---------------------------------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------------------------------

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

/// <summary>
/// The candidate locations of the native library. Newer builds ship
/// <c>libfiddler.dll</c>, older ones <c>fiddler.dll</c>.
/// </summary>
IEnumerable<string> GetMethods()
{
    if (OperatingSystem.IsWindows())
    {
        yield return "libfiddler.dll";
        yield return "fiddler.dll";
        yield break;
    }

    if (OperatingSystem.IsMacOS())
    {
        yield return Path.Combine("Contents", "Frameworks", "libfiddler.dylib");
        yield break;
    }

    yield return "libfiddler.so";
}

/// <summary>
/// The candidate locations of the native library inside an installation directory.
/// </summary>
IEnumerable<string> GetNativeLibraryCandidates(string installRoot)
{
    foreach (string relative in GetMethods())
    {
        yield return Path.Combine(installRoot, relative);
    }
}

/// <summary>
/// The candidate locations of the managed Web UI assembly. The product ships it either
/// unpacked from the application archive or in the plain directory tree.
/// </summary>
IEnumerable<string> GetWebUiAssemblyCandidates(string installRoot)
{
    string[] roots = OperatingSystem.IsMacOS()
        ? new[] { Path.Combine("Contents", "resources") }
        : new[] { "resources" };

    string[] layouts =
    {
        Path.Combine("app", "out", "WebServer", "Fiddler.WebUi.dll"),
        Path.Combine("app.asar.unpacked", "out", "WebServer", "Fiddler.WebUi.dll"),
    };

    foreach (string root in roots)
    {
        foreach (string layout in layouts)
        {
            yield return Path.Combine(installRoot, root, layout);
        }
    }
}

/// <summary>Returns the first candidate that exists, or <c>null</c>.</summary>
string? FindFirstExisting(IEnumerable<string> candidates)
{
    foreach (string candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

/// <summary>
/// The per-user installation roots to look at. The environment variable comes first,
/// because that is what the original tool uses, and the well known folder is the fallback
/// for shells that do not export it.
/// </summary>
IEnumerable<string> GetPerUserInstallRoots()
{
    string expanded = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%");
    if (!string.IsNullOrEmpty(expanded) && !expanded.Contains('%', StringComparison.Ordinal))
    {
        yield return expanded;
    }

    string known = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrEmpty(known) && !known.Equals(expanded, StringComparison.OrdinalIgnoreCase))
    {
        yield return known;
    }
}

/// <summary>
/// Resolves the default installation directory. On Windows the per-user installation is
/// preferred and the machine-wide one is the fallback; on macOS the application bundle is
/// used; on Linux the extracted AppImage directory is used.
/// </summary>
string ResolveDefaultInstallRoot()
{
    if (OperatingSystem.IsWindows())
    {
        foreach (string root in GetPerUserInstallRoots())
        {
            string local = Path.Combine(root, "Programs", "Fiddler Everywhere");
            if (Directory.Exists(local))
            {
                return local;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fiddler Everywhere");
    }

    if (OperatingSystem.IsMacOS())
    {
        return "/Applications/Fiddler Everywhere.app";
    }

    return "squashfs-root";
}
