using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using FiddlerEverywherePatcher.Common;
using FiddlerEverywherePatcher.Native;

namespace FiddlerEverywherePatcher.Status;

/// <summary>
/// Reports what state an installation is in, without changing anything.
/// </summary>
/// <remarks>
/// The tool patches files in place, so it is worth being able to tell at any moment whether
/// an installation still carries the patches or has been restored. The report covers the
/// native library (the byte at the patch location), the managed assembly (which of the
/// licence methods have been rewritten) and the backup files, so a restored installation
/// can be confirmed byte for byte.
/// </remarks>
internal static class InstallStatus
{
    private const string BackendServiceType = "Fiddler.WebUi.Services.Backend.BackendService";
    private const string OfflineUserTypeName = "OfflineUserDTO";
    private const string OfflineLicenseTypeName = "OfflineLicenseDTO";

    /// <summary>
    /// Prints the state of the installation under <paramref name="installRoot"/>.
    /// </summary>
    /// <returns>
    /// <c>0</c> when the installation is unmodified, <c>1</c> when it carries patches and
    /// <c>2</c> when the installation could not be found.
    /// </returns>
    public static int Report(string installRoot)
    {
        Console.WriteLine($"Fiddler Everywhere installation: {installRoot}");
        Console.WriteLine();

        bool patched = false;

        string? nativeLibrary = InstallLayout.FindFirstExisting(InstallLayout.NativeLibraryCandidates(installRoot));
        if (nativeLibrary is null)
        {
            Console.WriteLine("  native library : not found");
        }
        else
        {
            patched |= ReportNativeLibrary(nativeLibrary);
        }

        string? webUiAssembly = InstallLayout.FindFirstExisting(InstallLayout.WebUiAssemblyCandidates(installRoot));
        if (webUiAssembly is null)
        {
            Console.WriteLine("  Web UI assembly : not found");
        }
        else
        {
            patched |= ReportWebUiAssembly(webUiAssembly);
        }

        ReportProxy(installRoot);

        Console.WriteLine();
        Console.WriteLine(patched
            ? "  result          : the installation carries patches"
            : "  result          : the installation is unmodified");

        return patched ? 1 : 0;
    }

    private static bool ReportNativeLibrary(string path)
    {
        Console.WriteLine($"  native library : {path}");
        Console.WriteLine($"    sha256        : {Hash(path)}");

        byte? current = ReadPatchByte(path);
        if (current is null)
        {
            Console.WriteLine("    .text+0x3F0   : could not be read");
            return false;
        }

        string state = current switch
        {
            PePatcher.PatchByte => "patched (0xC3, the check routine returns immediately)",
            PePatcher.ExpectedOriginalByte => "unmodified (0x55, the original prologue)",
            _ => $"unexpected (0x{current:X2}), this build does not match the patched one",
        };

        Console.WriteLine($"    .text+0x3F0   : 0x{current:X2} {state}");

        string? backup = Common.Backup.FindExisting(path);
        if (backup is not null)
        {
            byte? backupByte = ReadPatchByte(backup);
            string backupState = backupByte switch
            {
                PePatcher.ExpectedOriginalByte => "unmodified (0x55, the original prologue)",
                PePatcher.PatchByte => "patched (0xC3)",
                null => "could not be read",
                _ => $"unexpected (0x{backupByte:X2})",
            };

            Console.WriteLine($"    backup        : {backup}");
            Console.WriteLine($"    backup sha256 : {Hash(backup)}");
            Console.WriteLine($"    backup .text+0x3F0 : {(backupByte is null ? "-" : $"0x{backupByte:X2}")} {backupState}");
            Console.WriteLine($"    file matches the backup: {(Hash(path) == Hash(backup) ? "yes" : "no")}");
        }
        else
        {
            Console.WriteLine("    backup        : none");
        }

        Console.WriteLine();

        return current == PePatcher.PatchByte;
    }

    private static bool ReportWebUiAssembly(string path)
    {
        Console.WriteLine($"  Web UI assembly: {path}");
        Console.WriteLine($"    sha256        : {Hash(path)}");

        bool patched = false;

        try
        {
            ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(path));

            TypeDef? backend = module.GetTypes().FirstOrDefault(t => t.FullName == BackendServiceType);
            if (backend is null)
            {
                Console.WriteLine("    licence methods: BackendService not found");
            }
            else
            {
                MethodDef? read = backend.Methods.FirstOrDefault(m => m.Name == "ReadOfflineLicenseFile");
                bool readPatched = StartsWithNewObject(read, OfflineUserTypeName);
                Console.WriteLine($"    ReadOfflineLicenseFile : {(readPatched ? "replaced by the patcher" : "original")}");
                patched |= readPatched;

                MethodDef? offline = backend.Methods.FirstOrDefault(m => m.Name == "GetOfflineLicense");
                bool offlinePatched = StartsWithNewObject(offline, OfflineLicenseTypeName);
                Console.WriteLine($"    GetOfflineLicense      : {(offlinePatched ? "rewritten by the compatibility edits" : "original")}");
                patched |= offlinePatched;
            }

            int rewritten = module.GetTypes()
                .SelectMany(t => t.Methods)
                .Count(m => (m.Name == "GetCurrentUserADEmail" || m.Name == "GetCurrentUserADPrincipalName")
                    && IsSingleStringReturn(m));
            Console.WriteLine($"    AD e-mail lookups      : {(rewritten > 0 ? $"{rewritten} rewritten by the compatibility edits" : "original")}");
            patched |= rewritten > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    licence methods: could not be read ({ex.GetType().Name}: {ex.Message})");
        }

        ReportBackup(path);
        Console.WriteLine();

        return patched;
    }

    private static void ReportProxy(string installRoot)
    {
        string? proxy = InstallLayout.FindFirstExisting(new[] { Path.Combine(installRoot, "version.dll") });
        Console.WriteLine($"  version.dll proxy: {(proxy is null ? "not installed" : proxy)}");
        Console.WriteLine();
    }

    /// <summary>Prints the backup file and whether it holds the original content.</summary>
    private static void ReportBackup(string path)
    {
        string? backup = Common.Backup.FindExisting(path);
        if (backup is null)
        {
            Console.WriteLine("    backup        : none");
            return;
        }

        string currentHash = Hash(path);
        string backupHash = Hash(backup);
        Console.WriteLine($"    backup        : {backup}");
        Console.WriteLine($"    backup sha256 : {backupHash}");
        Console.WriteLine($"    file matches the backup: {(currentHash == backupHash ? "yes" : "no")}");
    }

    /// <summary>Reads the byte at the native patch location without changing the file.</summary>
    private static byte? ReadPatchByte(string path)
    {
        try
        {
            byte[] image = File.ReadAllBytes(path);
            using var stream = new MemoryStream(image, writable: false);
            using var reader = new PEReader(stream);

            foreach (SectionHeader section in reader.PEHeaders.SectionHeaders)
            {
                if (section.Name != PePatcher.TargetSectionName)
                {
                    continue;
                }

                int offset = section.PointerToRawData + PePatcher.TextSectionPatchOffset;
                return offset < image.Length ? image[offset] : null;
            }
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Checks whether the method body starts by creating the given type.</summary>
    private static bool StartsWithNewObject(MethodDef? method, string typeName)
    {
        if (method is null || !method.HasBody || method.Body.Instructions.Count == 0)
        {
            return false;
        }

        Instruction first = method.Body.Instructions[0];
        return first.OpCode == OpCodes.Newobj
            && first.Operand is IMethod constructor
            && constructor.DeclaringType?.Name == typeName;
    }

    /// <summary>Checks whether the method body is a single string return.</summary>
    private static bool IsSingleStringReturn(MethodDef method)
    {
        if (!method.HasBody || method.Body.Instructions.Count != 2)
        {
            return false;
        }

        return method.Body.Instructions[0].OpCode == OpCodes.Ldstr
            && method.Body.Instructions[1].OpCode == OpCodes.Ret;
    }

    private static string Hash(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }
}
