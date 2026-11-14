using System.Reflection.PortableExecutable;

namespace FiddlerEverywherePatcher.Native;

/// <summary>
/// Patches the native Fiddler "libfiddler" PE image.
/// </summary>
/// <remarks>
/// <para>
/// The patch is a single byte write: <c>0xC3</c> (x86-64 <c>ret</c>) is stored over the
/// first byte of the routine that verifies the integrity of the Web UI assemblies. The
/// routine then returns immediately and the check never runs.
/// </para>
/// <para>
/// The offset was established by probing the patch location: the write follows
/// the section's raw data pointer, so it is a section-relative offset (0x3F0 in the
/// Fiddler Everywhere build this was measured on) and not a raw file offset.
/// </para>
/// </remarks>
internal static class PePatcher
{
    /// <summary>Offset of the patched byte relative to the start of the <c>.text</c> section data.</summary>
    public const int TextSectionPatchOffset = 0x3F0;

    /// <summary>The byte written over the patched location (<c>ret</c>).</summary>
    public const byte PatchByte = 0xC3;

    /// <summary>
    /// The byte the routine starts with in an unpatched build (<c>push rbp</c>).
    /// </summary>
    public const byte ExpectedOriginalByte = 0x55;

    /// <summary>Name of the section that is searched for the patch location.</summary>
    public const string TargetSectionName = ".text";

    /// <summary>
    /// Applies the patch to <paramref name="filePath"/> in place.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the file was modified, <c>false</c> when it was already patched.
    /// </returns>
    /// <exception cref="BadImageFormatException">The file is not a valid PE image.</exception>
    /// <exception cref="InvalidDataException">
    /// The byte at the patch location is neither the expected prologue nor the patched
    /// value, so the build is not the one this offset was derived from.
    /// </exception>
    public static bool Patch(string filePath)
    {
        byte[] image = File.ReadAllBytes(filePath);

        using var stream = new MemoryStream(image, writable: false);
        using var reader = new PEReader(stream);

        SectionHeader text = FindTextSection(reader);
        int patchOffset = text.PointerToRawData + TextSectionPatchOffset;
        if (patchOffset >= image.Length)
        {
            throw new InvalidDataException("The .text section is too small to contain the patch location.");
        }

        byte current = image[patchOffset];
        if (current == PatchByte)
        {
            return false;
        }

        if (current != ExpectedOriginalByte)
        {
            throw new InvalidDataException(
                $"Unexpected byte 0x{current:X2} at .text+0x{TextSectionPatchOffset:X}; " +
                "this build does not match the layout the patch was derived from.");
        }

        image[patchOffset] = PatchByte;
        File.WriteAllBytes(filePath, image);
        return true;
    }

    private static SectionHeader FindTextSection(PEReader reader)
    {
        foreach (SectionHeader section in reader.PEHeaders.SectionHeaders)
        {
            if (section.Name == TargetSectionName)
            {
                return section;
            }
        }

        throw new BadImageFormatException($"The PE image has no '{TargetSectionName}' section.");
    }
}
