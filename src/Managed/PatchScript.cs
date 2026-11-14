using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiddlerEverywherePatcher.Managed;

/// <summary>
/// Data model for the dnlib patch script that the original tool embeds and applies to
/// <c>Fiddler.WebUi.dll</c>.
/// </summary>
/// <remarks>
/// The script is embedded as <c>PatchScript.json</c> and acts as the specification of the
/// managed patch. Its shape is dnlib's own serialisation of a module patch: a set of
/// type/method/field references plus one or more method bodies expressed as IL instruction
/// lists.
/// </remarks>
internal sealed class PatchScript
{
    [JsonPropertyName("Options")]
    public int Options { get; set; }

    [JsonPropertyName("Types")]
    public TypeSection Types { get; set; } = new();

    [JsonPropertyName("Fields")]
    public MemberSection Fields { get; set; } = new();

    [JsonPropertyName("Methods")]
    public MethodSection Methods { get; set; } = new();

    internal sealed class TypeSection
    {
        [JsonPropertyName("References")]
        public Dictionary<string, TypeReference> References { get; set; } = new();

        [JsonPropertyName("Definitions")]
        public Dictionary<string, object> Definitions { get; set; } = new();

        [JsonPropertyName("Orders")]
        public List<string> Orders { get; set; } = new();
    }

    internal class MemberSection
    {
        [JsonPropertyName("References")]
        public Dictionary<string, MemberReference> References { get; set; } = new();

        [JsonPropertyName("Definitions")]
        public Dictionary<string, object> Definitions { get; set; } = new();

        [JsonPropertyName("Orders")]
        public List<string> Orders { get; set; } = new();
    }

    internal sealed class MethodSection : MemberSection
    {
    }

    internal sealed class TypeReference
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Namespace")]
        public string Namespace { get; set; } = string.Empty;

        [JsonPropertyName("Assembly")]
        public string? Assembly { get; set; }
    }

    internal sealed class MemberReference
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Signature")]
        public string? Signature { get; set; }
    }

    internal sealed class MethodDefinition
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Signature")]
        public string? Signature { get; set; }

        [JsonPropertyName("Attributes")]
        public int Attributes { get; set; }

        [JsonPropertyName("ImplAttributes")]
        public int ImplAttributes { get; set; }

        [JsonPropertyName("Parameters")]
        public List<object> Parameters { get; set; } = new();

        [JsonPropertyName("Body")]
        public MethodBody Body { get; set; } = new();
    }

    internal sealed class MethodBody
    {
        [JsonPropertyName("Instructions")]
        public List<Instruction> Instructions { get; set; } = new();

        [JsonPropertyName("ExceptionHandlers")]
        public List<object> ExceptionHandlers { get; set; } = new();

        [JsonPropertyName("Variables")]
        public List<string> Variables { get; set; } = new();

        [JsonPropertyName("MaxStack")]
        public int MaxStack { get; set; }

        [JsonPropertyName("InitLocals")]
        public bool InitLocals { get; set; }
    }

    internal sealed class Instruction
    {
        [JsonPropertyName("OpCode")]
        public string OpCode { get; set; } = string.Empty;

        [JsonPropertyName("TypeValue")]
        public string? TypeValue { get; set; }

        [JsonPropertyName("StringValue")]
        public string? StringValue { get; set; }

        [JsonPropertyName("PrimitiveValue")]
        public int PrimitiveValue { get; set; }

        [JsonPropertyName("HasPrimitiveValue")]
        public bool HasPrimitiveValue { get; set; }
    }

    /// <summary>Loads the embedded patch script.</summary>
    public static PatchScript Load()
    {
        System.Reflection.Assembly assembly = typeof(PatchScript).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("FiddlerEverywherePatcher.Managed.PatchScript.json")
            ?? throw new InvalidOperationException("The embedded patch script is missing.");

        using StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<PatchScript>(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The embedded patch script could not be parsed.");
    }
}
