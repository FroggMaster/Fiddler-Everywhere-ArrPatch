namespace FiddlerEverywherePatcher.Common;

/// <summary>
/// Finds the files that make up a Fiddler Everywhere installation.
/// </summary>
/// <remarks>
/// The product ships the native library and the Web UI assembly in different places
/// depending on the platform and on the release, so every known location is tried:
/// <list type="bullet">
///   <item><description>native library: <c>libfiddler.dll</c> or <c>fiddler.dll</c> (Windows), <c>Contents/Frameworks/libfiddler.dylib</c> (macOS), <c>libfiddler.so</c> (Linux)</description></item>
///   <item><description>Web UI assembly: <c>resources/app/out/WebServer/</c> and <c>resources/app.asar.unpacked/out/WebServer/</c>, plus the <c>Contents/resources/...</c> variant on macOS</description></item>
/// </list>
/// </remarks>
internal static class InstallLayout
{
    /// <summary>The candidate locations of the native library inside an installation directory.</summary>
    public static IEnumerable<string> NativeLibraryCandidates(string installRoot)
    {
        foreach (string relative in NativeLibraryNames())
        {
            yield return Path.Combine(installRoot, relative);
        }
    }

    /// <summary>The candidate locations of the managed Web UI assembly.</summary>
    public static IEnumerable<string> WebUiAssemblyCandidates(string installRoot)
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
    public static string? FindFirstExisting(IEnumerable<string> candidates)
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
    /// Resolves the default installation directory. On Windows the per-user installation is
    /// preferred and the machine-wide one is the fallback; on macOS the application bundle is
    /// used; on Linux the extracted AppImage directory is used.
    /// </summary>
    public static string ResolveDefaultInstallRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string root in PerUserInstallRoots())
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

    /// <summary>The candidate names of the native library for the current platform.</summary>
    private static IEnumerable<string> NativeLibraryNames()
    {
        if (OperatingSystem.IsWindows())
        {
            // Newer Fiddler Everywhere builds ship "libfiddler.dll", older ones "fiddler.dll".
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
    /// The per-user installation roots to look at. The environment variable comes first,
    /// because that is what the original tool uses, and the well known folder is the fallback
    /// for shells that do not export it.
    /// </summary>
    private static IEnumerable<string> PerUserInstallRoots()
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
}
