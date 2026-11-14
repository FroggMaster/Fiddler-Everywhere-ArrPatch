using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FiddlerEverywherePatcher.Managed;

/// <summary>
/// Replaces the licence lookup in the managed Web UI assembly
/// (<c>Fiddler.WebUi.dll</c>) with a routine that fabricates an offline licence.
/// </summary>
/// <remarks>
/// <para>
/// The target method is
/// <c>Fiddler.WebUi.Services.Backend.BackendService.ReadOfflineLicenseFile</c>. Its body is
/// replaced so that it always returns a freshly created
/// <c>FiddlerBackend.Contracts.OfflineLicenseFileDTO</c> containing one offline user:
/// </para>
/// <list type="bullet">
///   <item><description>e-mail and user principal e-mail <c>fiddler@telerik.com</c>, first name <c>fiddler</c>, last name empty</description></item>
///   <item><description>plan <c>enterprise</c>, period <c>annual</c></description></item>
///   <item><description>created / not before 2024-01-01, expires 2099-01-01</description></item>
///   <item><description>id <c>00112233-4455-6677-8899-aabbccddeeff</c></description></item>
///   <item><description><c>AdditionalData["users"]</c> holds the serialised user array</description></item>
/// </list>
/// <para>
/// The exact instruction sequence of the patch is kept in <c>PatchScript.json</c>, which is
/// embedded with the build. The IL produced here is checked against that list on every run,
/// so the two cannot drift apart.
/// </para>
/// </remarks>
internal static class WebUiPatcher
{
    private const string BackendServiceType = "Fiddler.WebUi.Services.Backend.BackendService";
    private const string OfflineUserType = "Fiddler.WebUi.Services.OfflineLicenses.OfflineUserDTO";
    private const string OfflineLicenseFileType = "FiddlerBackend.Contracts.OfflineLicenseFileDTO";
    private const string DictionaryType = "System.Collections.Generic.Dictionary`2";
    private const string IDictionaryType = "System.Collections.Generic.IDictionary`2";
    private const string JsonElementType = "System.Text.Json.JsonElement";
    private const string JsonDocumentType = "System.Text.Json.JsonDocument";
    private const string JsonDocumentOptionsType = "System.Text.Json.JsonDocumentOptions";
    private const string JsonSerializerOptionsType = "System.Text.Json.JsonSerializerOptions";
    private const string JsonSerializerType = "System.Text.Json.JsonSerializer";
    private const string DateTimeType = "System.DateTime";
    private const string GuidType = "System.Guid";
    private const string TaskType = "System.Threading.Tasks.Task";

    /// <summary>
    /// Patches the managed Web UI assembly in place.
    /// </summary>
    /// <returns><c>true</c> when the assembly was modified, <c>false</c> when it was already patched.</returns>
    public static bool Patch(string filePath)
    {
        // The module is loaded from memory so that the file is not memory mapped while it
        // is rewritten.
        ModuleDefMD? module = ModuleDefMD.Load(File.ReadAllBytes(filePath));
        if (module == null)
        {
            throw new BadImageFormatException("The file is not a managed assembly.");
        }

        using (module)
        {
            MethodDef? target = FindTargetMethod(module);
            if (target == null)
            {
                throw new InvalidDataException($"The assembly does not contain {BackendServiceType}.ReadOfflineLicenseFile.");
            }

            if (IsAlreadyPatched(target))
            {
                return false;
            }

            WritePatchedBody(module, target);
            SanitizeUnresolvableInstructions(module);
            module.Write(filePath);
            return true;
        }
    }

    /// <summary>
    /// Replaces instructions whose operand cannot be resolved with a no-op.
    /// </summary>
    /// <remarks>
    /// The Web UI assembly is obfuscated and contains a call to a token that does not exist.
    /// The instruction is unreachable (it sits behind an unconditional branch) but a
    /// writer refuses to emit an instruction without an operand, so it is neutralised
    /// before the module is written.
    /// </remarks>
    private static void SanitizeUnresolvableInstructions(ModuleDef module)
    {
        foreach (TypeDef type in module.GetTypes())
        {
            foreach (MethodDef method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is null && instruction.OpCode.OperandType != OperandType.InlineNone)
                    {
                        instruction.OpCode = OpCodes.Nop;
                        instruction.Operand = null;
                    }
                }
            }
        }
    }

    private static MethodDef? FindTargetMethod(ModuleDef module)
    {
        foreach (TypeDef type in module.GetTypes())
        {
            if (type.FullName != BackendServiceType)
            {
                continue;
            }

            foreach (MethodDef method in type.Methods)
            {
                if (method.Name == "ReadOfflineLicenseFile")
                {
                    return method;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the state produced by a previous run: the patched body starts with a
    /// <c>newobj</c> for the offline user DTO.
    /// </summary>
    private static bool IsAlreadyPatched(MethodDef method)
    {
        IList<Instruction> instructions = method.Body.Instructions;
        if (instructions.Count == 0)
        {
            return false;
        }

        return instructions[0].OpCode == OpCodes.Newobj
            && instructions[0].Operand is IMethod constructor
            && constructor.DeclaringType?.Name == "OfflineUserDTO";
    }

    private static void WritePatchedBody(ModuleDef module, MethodDef target)
    {
        ITypeDefOrRef userType = ResolveType(module, OfflineUserType);
        ITypeDefOrRef licenseType = ResolveType(module, OfflineLicenseFileType);
        ITypeDefOrRef idictionaryType = ResolveType(module, IDictionaryType);
        ITypeDefOrRef dictionaryType = ResolveType(module, DictionaryType);
        ITypeDefOrRef jsonElementType = ResolveType(module, JsonElementType);
        ITypeDefOrRef jsonDocumentType = ResolveType(module, JsonDocumentType);
        ITypeDefOrRef jsonDocumentOptionsType = ResolveType(module, JsonDocumentOptionsType);
        ITypeDefOrRef jsonSerializerOptionsType = ResolveType(module, JsonSerializerOptionsType);
        ITypeDefOrRef jsonSerializerType = ResolveType(module, JsonSerializerType);
        ITypeDefOrRef dateTimeType = ResolveType(module, DateTimeType);
        ITypeDefOrRef guidType = ResolveType(module, GuidType);
        ITypeDefOrRef taskType = ResolveType(module, TaskType);

        GenericInstSig dictionarySig = new(new ClassSig(dictionaryType), module.CorLibTypes.String, module.CorLibTypes.Object);
        TypeSig userArraySig = new SZArraySig(new ClassSig(userType));
        ITypeDefOrRef userArrayType = userType;

        MethodSig voidCtor = MethodSig.CreateInstance(module.CorLibTypes.Void);
        MethodSig voidString = MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String);
        MethodSig voidDateTime = MethodSig.CreateInstance(module.CorLibTypes.Void, new ValueTypeSig(dateTimeType));
        MethodSig voidGuid = MethodSig.CreateInstance(module.CorLibTypes.Void, new ValueTypeSig(guidType));
        MethodSig voidDictionaryItem = MethodSig.CreateInstance(module.CorLibTypes.Void, new GenericVar(0), new GenericVar(1));
        MethodSig voidAdditionalData = MethodSig.CreateInstance(module.CorLibTypes.Void, new GenericInstSig(new ClassSig(idictionaryType), module.CorLibTypes.String, module.CorLibTypes.Object));

        // JsonSerializer.Serialize<T>(T value, JsonSerializerOptions? options)
        MethodSig serializeSig = new(
            CallingConvention.Generic,
            1,
            module.CorLibTypes.String,
            new GenericMVar(0),
            jsonSerializerOptionsType.ToTypeSig());
        MethodSpecUser serializeSpec = new(
            MethodRefOrCreate(module, "JsonSerializer", "Serialize", () => serializeSig, jsonSerializerType),
            new GenericInstMethodSig(new TypeSig[] { userArraySig }));

        MethodSig parseSig = MethodSig.CreateStatic(jsonDocumentType.ToTypeSig(), module.CorLibTypes.String, new ValueTypeSig(jsonDocumentOptionsType));
        MethodSig rootElementSig = MethodSig.CreateInstance(new ValueTypeSig(jsonElementType));
        MethodSig dateTimeParseSig = MethodSig.CreateStatic(new ValueTypeSig(dateTimeType), module.CorLibTypes.String);
        MethodSig guidParseSig = MethodSig.CreateStatic(new ValueTypeSig(guidType), module.CorLibTypes.String);
        // Task.FromResult<T>(T result)
        MethodSig fromResultSig = new(
            CallingConvention.Generic,
            1,
            new GenericInstSig(new ClassSig(taskType), new GenericMVar(0)),
            new GenericMVar(0));
        MethodSpecUser fromResultSpec = new(
            MethodRefOrCreate(module, "Task", "FromResult", () => fromResultSig, taskType),
            new GenericInstMethodSig(new TypeSig[] { licenseType.ToTypeSig() }));

        MethodDef body = target;
        body.Body.Instructions.Clear();
        body.Body.ExceptionHandlers.Clear();
        body.Body.Variables.Clear();
        body.Body.InitLocals = true;
        body.Body.MaxStack = 7;

        body.Body.Variables.Add(new dnlib.DotNet.Emit.Local(userType.ToTypeSig()));
        body.Body.Variables.Add(new dnlib.DotNet.Emit.Local(licenseType.ToTypeSig()));
        body.Body.Variables.Add(new dnlib.DotNet.Emit.Local(dictionarySig));
        body.Body.Variables.Add(new dnlib.DotNet.Emit.Local(new ValueTypeSig(jsonDocumentOptionsType)));

        IList<Instruction> il = body.Body.Instructions;

        // var user = new OfflineUserDTO { Email = "fiddler@telerik.com", FirstName = "fiddler",
        //                                  LastName = "", UserPrincipalEmail = "fiddler@telerik.com" };
        il.Add(Instruction.Create(OpCodes.Newobj, MemberRef(module, ".ctor", voidCtor, userType)));
        il.Add(Instruction.Create(OpCodes.Dup));
        il.Add(Instruction.Create(OpCodes.Ldstr, "fiddler@telerik.com"));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_Email", voidString, userType)));
        il.Add(Instruction.Create(OpCodes.Dup));
        il.Add(Instruction.Create(OpCodes.Ldstr, "fiddler"));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_FirstName", voidString, userType)));
        il.Add(Instruction.Create(OpCodes.Dup));
        il.Add(Instruction.Create(OpCodes.Ldstr, string.Empty));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_LastName", voidString, userType)));
        il.Add(Instruction.Create(OpCodes.Dup));
        il.Add(Instruction.Create(OpCodes.Ldstr, "fiddler@telerik.com"));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_UserPrincipalEmail", voidString, userType)));
        il.Add(Instruction.Create(OpCodes.Stloc_0));

        // var license = new OfflineLicenseFileDTO();
        il.Add(Instruction.Create(OpCodes.Newobj, MemberRef(module, ".ctor", voidCtor, licenseType)));
        il.Add(Instruction.Create(OpCodes.Stloc_1));

        // var additionalData = new Dictionary<string, object>();
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Newobj, MemberRef(module, ".ctor", voidCtor, dictionarySig.ToTypeDefOrRef())));
        il.Add(Instruction.Create(OpCodes.Stloc_2));

        // additionalData["users"] = JsonSerializer.Serialize(new[] { user });
        il.Add(Instruction.Create(OpCodes.Ldloc_2));
        il.Add(Instruction.Create(OpCodes.Ldstr, "users"));
        il.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Add(Instruction.Create(OpCodes.Newarr, userArrayType));
        il.Add(Instruction.Create(OpCodes.Dup));
        il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Add(Instruction.Create(OpCodes.Ldloc_0));
        il.Add(Instruction.Create(OpCodes.Stelem_Ref));
        il.Add(Instruction.Create(OpCodes.Ldnull));
        il.Add(Instruction.Create(OpCodes.Call, serializeSpec));
        il.Add(Instruction.Create(OpCodes.Ldloca_S, body.Body.Variables[3]));
        il.Add(Instruction.Create(OpCodes.Initobj, jsonDocumentOptionsType));
        il.Add(Instruction.Create(OpCodes.Ldloc_3));
        il.Add(Instruction.Create(OpCodes.Call, MemberRef(module, "Parse", parseSig, jsonDocumentType)));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "get_RootElement", rootElementSig, jsonDocumentType)));
        il.Add(Instruction.Create(OpCodes.Box, jsonElementType));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_Item", voidDictionaryItem, dictionarySig.ToTypeDefOrRef())));

        // license.AdditionalData = additionalData;
        il.Add(Instruction.Create(OpCodes.Ldloc_2));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_AdditionalData", voidAdditionalData, licenseType)));

        // license.CreatedAt = DateTime.Parse("2024/1/1");
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "2024/1/1"));
        il.Add(Instruction.Create(OpCodes.Call, MemberRef(module, "Parse", dateTimeParseSig, dateTimeType)));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_CreatedAt", voidDateTime, licenseType)));

        // license.ExpiresAt = DateTime.Parse("2099/1/1");
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "2099/1/1"));
        il.Add(Instruction.Create(OpCodes.Call, MemberRef(module, "Parse", dateTimeParseSig, dateTimeType)));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_ExpiresAt", voidDateTime, licenseType)));

        // license.Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "00112233-4455-6677-8899-aabbccddeeff"));
        il.Add(Instruction.Create(OpCodes.Call, MemberRef(module, "Parse", guidParseSig, guidType)));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_Id", voidGuid, licenseType)));

        // license.NotBefore = DateTime.Parse("2024/1/1");
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "2024/1/1"));
        il.Add(Instruction.Create(OpCodes.Call, MemberRef(module, "Parse", dateTimeParseSig, dateTimeType)));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_NotBefore", voidDateTime, licenseType)));

        // license.PlanName = "enterprise";
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "enterprise"));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_PlanName", voidString, licenseType)));

        // license.PlanPeriod = "annual";
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Ldstr, "annual"));
        il.Add(Instruction.Create(OpCodes.Callvirt, MemberRef(module, "set_PlanPeriod", voidString, licenseType)));

        // return Task.FromResult(license);
        il.Add(Instruction.Create(OpCodes.Ldloc_1));
        il.Add(Instruction.Create(OpCodes.Call, fromResultSpec));
        il.Add(Instruction.Create(OpCodes.Ret));

        VerifyAgainstScript(il);
    }

    /// <summary>
    /// Checks the generated instruction stream against the reference patch script.
    /// </summary>
    /// <remarks>
    /// <c>PatchScript.json</c> holds the instruction list for the patched method. It is
    /// embedded with the build and used here as the specification of the patch, so a
    /// change in the generated IL cannot silently drift away from it.
    /// </remarks>
    private static void VerifyAgainstScript(IList<Instruction> il)
    {
        PatchScript script = PatchScript.Load();
        PatchScript.MethodDefinition? definition = null;
        foreach (object value in script.Methods.Definitions.Values)
        {
            if (value is PatchScript.MethodDefinition candidate)
            {
                definition = candidate;
                break;
            }
        }

        if (definition == null)
        {
            return;
        }

        IReadOnlyList<PatchScript.Instruction> expected = definition.Body.Instructions;
        if (expected.Count != il.Count)
        {
            throw new InvalidDataException(
                $"The generated patch has {il.Count} instructions but the reference script has {expected.Count}.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(expected[i].OpCode, il[i].OpCode.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Instruction {i} is '{il[i].OpCode.Name}' but the reference script expects '{expected[i].OpCode}'.");
            }

            if (expected[i].StringValue != null && !string.Equals(expected[i].StringValue, il[i].Operand as string, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Instruction {i} has an unexpected string operand.");
            }
        }
    }

    /// <summary>
    /// Returns the assembly's own reference to <paramref name="methodName"/> on
    /// <paramref name="typeName"/> when it has one, so the operand is guaranteed to resolve
    /// at run time. Falls back to a freshly built reference otherwise.
    /// </summary>
    private static IMethodDefOrRef MethodRefOrCreate(
        ModuleDef module,
        string typeName,
        string methodName,
        Func<MethodSig> signatureFactory,
        ITypeDefOrRef declaringType)
    {
        foreach (MemberRef memberRef in module.GetMemberRefs())
        {
            if (memberRef.Name == methodName && memberRef.DeclaringType?.Name == typeName)
            {
                return memberRef;
            }
        }

        return MemberRef(module, methodName, signatureFactory(), declaringType);
    }

    /// <summary>
    /// Creates a member reference on <paramref name="declaringType"/>.
    /// </summary>
    private static MemberRefUser MemberRef(ModuleDef module, string name, MethodSig signature, ITypeDefOrRef declaringType)
    {
        MemberRefUser member = new(module, name, signature)
        {
            Class = declaringType,
        };

        return member;
    }

    /// <summary>
    /// Finds the type reference in the target module. The patched assembly already refers to
    /// every type the patch needs, so the reference can simply be reused.
    /// </summary>
    private static ITypeDefOrRef ResolveType(ModuleDef module, string fullName)
    {
        // Types of the patched assembly itself are defined in the module.
        foreach (TypeDef type in module.GetTypes())
        {
            if (type.FullName == fullName)
            {
                return type;
            }
        }

        // Everything else comes from a referenced assembly.
        foreach (TypeRef typeRef in module.GetTypeRefs())
        {
            if (typeRef.FullName == fullName)
            {
                return typeRef;
            }
        }

        throw new InvalidDataException($"The target assembly does not reference '{fullName}'.");
    }
}
