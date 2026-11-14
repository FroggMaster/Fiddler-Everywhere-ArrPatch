namespace FiddlerEverywherePatcher.Native;

/// <summary>
/// Patches the native Fiddler "libfiddler" ELF image (Linux <c>libfiddler.so</c> and
/// macOS <c>libfiddler.dylib</c>).
/// </summary>
/// <remarks>
/// <para>
/// The original binary links <c>ELFSharp</c>, so the ELF image is parsed rather than
/// patched blindly: the section table is walked and the patch byte is written at a fixed
/// offset inside the <c>.text</c> section, exactly like the PE variant.
/// </para>
/// <para>
/// Only the PE variant could be verified byte-for-byte, because no Linux or macOS copy of
/// Fiddler Everywhere was available on the analysis machine. The section-relative model
/// and the written byte value (0xC3 / <c>ret</c>) are carried over from the verified PE
/// implementation; the section offsets match the legacy and current ELF code paths and are
/// inferred rather than measured.
/// </para>
/// <para>
/// This implementation parses the ELF section table directly instead of taking the
/// <c>ELFSharp</c> dependency, because the public surface of ELFSharp 2.x does not expose
/// the section headers. The behaviour is identical for the section header layout that the
/// original relied on.
/// </para>
/// </remarks>
internal static class ElfPatcher
{
    /// <summary>Offset of the patched byte relative to the start of the <c>.text</c> section data.</summary>
    public const int LegacyTextSectionPatchOffset = 0x3F0;

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
    /// <param name="filePath">The ELF image to patch.</param>
    /// <param name="useLegacyOffset">
    /// When <c>true</c> the legacy offset is used, matching the original's
    /// <c>UseLegacyPatchLibfiddlerELF</c> code path.
    /// </param>
    /// <returns><c>true</c> when the file was modified, <c>false</c> when it was already patched.</returns>
    public static bool Patch(string filePath, bool useLegacyOffset)
    {
        byte[] image = File.ReadAllBytes(filePath);

        int patchOffset = FindPatchOffset(image, useLegacyOffset ? LegacyTextSectionPatchOffset : TextSectionPatchOffset);
        if (patchOffset < 0)
        {
            throw new InvalidDataException($"The ELF image has no '{TargetSectionName}' section.");
        }

        if (image[patchOffset] == PatchByte)
        {
            return false;
        }

        if (image[patchOffset] != ExpectedOriginalByte)
        {
            throw new InvalidDataException(
                $"Unexpected byte 0x{image[patchOffset]:X2} at .text+0x{(useLegacyOffset ? LegacyTextSectionPatchOffset : TextSectionPatchOffset):X}; " +
                "this build does not match the layout the patch was derived from.");
        }

        image[patchOffset] = PatchByte;
        File.WriteAllBytes(filePath, image);
        return true;
    }

    /// <summary>
    /// Resolves the absolute file offset of the patch location inside the ELF image.
    /// </summary>
    private static int FindPatchOffset(byte[] image, int sectionOffset)
    {
        if (image.Length < 64 || image[0] != 0x7F || image[1] != (byte)'E' || image[2] != (byte)'L' || image[3] != (byte)'F')
        {
            throw new InvalidDataException("The file is not an ELF image.");
        }

        bool is64Bit = image[4] == 2;
        bool bigEndian = image[5] == 2;

        long sectionHeaderOffset = is64Bit ? ReadInt64(image, 0x28, bigEndian) : ReadInt32(image, 0x20, bigEndian);
        int sectionHeaderEntrySize = ReadUInt16(image, is64Bit ? 0x3A : 0x2E, bigEndian);
        int sectionCount = ReadUInt16(image, is64Bit ? 0x3C : 0x30, bigEndian);
        int stringTableIndex = ReadUInt16(image, is64Bit ? 0x3E : 0x32, bigEndian);

        if (sectionHeaderOffset <= 0 || sectionCount <= 0 || stringTableIndex < 0 || stringTableIndex >= sectionCount)
        {
            throw new InvalidDataException("The ELF image has no usable section header table.");
        }

        int stringTableHeaderOffset = (int)(sectionHeaderOffset + (long)stringTableIndex * sectionHeaderEntrySize);
        long stringTableOffset = is64Bit ? ReadInt64(image, stringTableHeaderOffset + 0x18, bigEndian) : ReadInt32(image, stringTableHeaderOffset + 0x10, bigEndian);
        long stringTableSize = is64Bit ? ReadInt64(image, stringTableHeaderOffset + 0x20, bigEndian) : ReadInt32(image, stringTableHeaderOffset + 0x14, bigEndian);

        for (int i = 0; i < sectionCount; i++)
        {
            int headerOffset = (int)(sectionHeaderOffset + (long)i * sectionHeaderEntrySize);
            uint nameOffset = ReadUInt32(image, headerOffset, bigEndian);
            long dataOffset = is64Bit ? ReadInt64(image, headerOffset + 0x18, bigEndian) : ReadInt32(image, headerOffset + 0x10, bigEndian);

            string? name = ReadStringAt(image, stringTableOffset, stringTableSize, nameOffset, bigEndian);
            if (name != TargetSectionName)
            {
                continue;
            }

            long patchOffset = dataOffset + sectionOffset;
            if (patchOffset < 0 || patchOffset >= image.Length)
            {
                throw new InvalidDataException("The .text section is too small to contain the patch location.");
            }

            return (int)patchOffset;
        }

        return -1;
    }

    private static string? ReadStringAt(byte[] image, long tableOffset, long tableSize, uint nameOffset, bool bigEndian)
    {
        long start = tableOffset + nameOffset;
        if (start < 0 || start >= image.Length || start >= tableOffset + tableSize)
        {
            return null;
        }

        int end = (int)start;
        while (end < image.Length && image[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(image, (int)start, end - (int)start);
    }

    private static ushort ReadUInt16(byte[] image, int offset, bool bigEndian)
        => bigEndian
            ? (ushort)((image[offset] << 8) | image[offset + 1])
            : (ushort)((image[offset + 1] << 8) | image[offset]);

    private static uint ReadUInt32(byte[] image, int offset, bool bigEndian)
        => bigEndian
            ? ((uint)image[offset] << 24) | ((uint)image[offset + 1] << 16) | ((uint)image[offset + 2] << 8) | image[offset + 3]
            : ((uint)image[offset + 3] << 24) | ((uint)image[offset + 2] << 16) | ((uint)image[offset + 1] << 8) | image[offset];

    private static int ReadInt32(byte[] image, int offset, bool bigEndian) => (int)ReadUInt32(image, offset, bigEndian);

    private static long ReadInt64(byte[] image, int offset, bool bigEndian)
    {
        ulong value = 0;
        if (bigEndian)
        {
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | image[offset + i];
            }
        }
        else
        {
            for (int i = 7; i >= 0; i--)
            {
                value = (value << 8) | image[offset + i];
            }
        }

        return (long)value;
    }
}
