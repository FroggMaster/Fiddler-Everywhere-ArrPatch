using System.Reflection.PortableExecutable;

namespace FiddlerEverywherePatcher.Common;

/// <summary>
/// Creates the pre-patch backup that the original tool writes next to every file it
/// modifies.
/// </summary>
/// <remarks>
/// The original names the backup <c>&lt;file&gt;.&lt;PE TimeDateStamp in uppercase hex&gt;.bak</c>.
/// This was confirmed against the files on the analysis machine:
/// <list type="bullet">
///   <item><description><c>fiddler.dll</c> (linker stamp 0x6AEC86D5) produced <c>fiddler.dll.6AEC86D5.bak</c>.</description></item>
///   <item><description><c>Fiddler.WebUi.dll</c> (linker stamp 0x9EC07C25) produced <c>Fiddler.WebUi.dll.9EC07C25.bak</c>.</description></item>
///   <item><description>A file that is not a PE image produced <c>...00000000.bak</c>.</description></item>
/// </list>
/// </remarks>
internal static class Backup
{
    /// <summary>
    /// Copies <paramref name="path"/> to <c>&lt;path&gt;.&lt;stamp&gt;.bak</c> and returns the
    /// backup path.
    /// </summary>
    public static string Create(string path)
    {
        string backupPath = string.Concat(path, ".", ReadTimeDateStamp(path).ToString("X8"), ".bak");

        if (File.Exists(backupPath))
        {
            return backupPath;
        }

        File.Copy(path, backupPath);
        return backupPath;
    }

    /// <summary>
    /// Finds the backup that belongs to <paramref name="path"/>, or <c>null</c> when there is
    /// none.
    /// </summary>
    /// <remarks>
    /// The backup is recognised by the name the tool gives it: the name of the file, the PE
    /// timestamp of the backup itself in upper case hex, and the <c>.bak</c> extension. The
    /// timestamp is read from the backup, because rewriting the managed assembly can change
    /// the timestamp of the file it replaces, while the backup keeps the original one.
    /// Backups with any other name are only used when no proper one exists.
    /// </remarks>
    public static string? FindExisting(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileName(path);

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, name + ".*.bak");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        List<string> proper = new();
        foreach (string candidate in candidates)
        {
            string stamp = ReadTimeDateStamp(candidate).ToString("X8");
            string expected = string.Concat(name, ".", stamp, ".bak");
            if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.OrdinalIgnoreCase))
            {
                proper.Add(candidate);
            }
        }

        IEnumerable<string> pool = proper.Count > 0 ? proper : candidates;
        return pool.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    /// <summary>
    /// Restores <paramref name="path"/> from its backup.
    /// </summary>
    /// <returns>The backup that was used, or <c>null</c> when there was none.</returns>
    public static string? Restore(string path)
    {
        string? backup = FindExisting(path);
        if (backup is null)
        {
            return null;
        }

        File.Copy(backup, path, overwrite: true);
        return backup;
    }

    /// <summary>
    /// Reads the PE linker timestamp from the file header, or 0 when the file is not a PE image.
    /// </summary>
    public static uint ReadTimeDateStamp(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new PEReader(stream);
            return unchecked((uint)reader.PEHeaders.CoffHeader.TimeDateStamp);
        }
        catch (BadImageFormatException)
        {
            return 0u;
        }
        catch (IOException)
        {
            return 0u;
        }
    }
}
