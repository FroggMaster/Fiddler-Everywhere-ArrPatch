using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Startup hook that runs inside the Fiddler Everywhere backend process.
/// </summary>
/// <remarks>
/// The runtime calls <see cref="Initialize"/> on a type named <c>StartupHook</c> in the
/// global namespace of every assembly listed in <c>DOTNET_STARTUP_HOOKS</c>, after the
/// runtime has started and before the entry point of the application is called. The proxy
/// sets that variable in its own environment, so the backend inherits it and the hook runs
/// inside the process that holds the licence logic.
///
/// Nothing on disk is modified. The licence methods are replaced in the running process:
/// the compiled code of the method is overwritten with a jump to a replacement, so every
/// later call lands in the replacement instead.
/// </remarks>
public static class StartupHook
{
    private const string WebUiAssemblyName = "Fiddler.WebUi";

    private static readonly object Gate = new();
    private static bool s_installed;

    // The replacements are kept in static fields: the thunk that exposes them to the
    // application has to stay alive for as long as the process runs. The delegate types are
    // declared here because Marshal refuses generic delegate types.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate object ObjectCallback(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate string StringCallback(IntPtr self);

    /// <summary>
    /// The signature of the backend login, which is where the offline flag enters the
    /// backend.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate Task BackendLoginCallback(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPWStr)] string apiUrl,
        [MarshalAs(UnmanagedType.LPWStr)] string identityUrl,
        [MarshalAs(UnmanagedType.LPWStr)] string idToken,
        [MarshalAs(UnmanagedType.LPWStr)] string installationId,
        bool isOffline,
        bool disableCloud,
        bool disableMcp,
        bool disableAssistant,
        bool disableScripting,
        bool disableCustomInspectors);

    private static readonly ObjectCallback s_readOfflineLicenseFile = ReplaceReadOfflineLicenseFile;
    private static readonly ObjectCallback s_getOfflineLicense = ReplaceGetOfflineLicense;
    private static readonly StringCallback s_adEmail = ReplaceAdEmail;

    /// <summary>The original login, kept so that the replacement can hand over to it.</summary>
    private static MethodInfo? s_originalBackendLogin;

    /// <summary>The pointer, the original code and the replacement of the backend login.</summary>
    private static IntPtr s_loginSlot;
    private static IntPtr s_loginOriginalCode;
    private static IntPtr s_loginReplacement;

    /// <summary>
    /// Entry point called by the runtime.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Log($"hook loaded, pid {Environment.ProcessId}, runtime {Environment.Version}");

            Install();
        }
        catch (Exception ex)
        {
            Log($"hook failed: {ex}");
        }
    }

    private static void Install()
    {
        Assembly? webUi = FindWebUiAssembly();
        if (webUi is null)
        {
            Log("Fiddler.WebUi.dll is not loaded yet, waiting for it");
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            return;
        }

        Log($"Fiddler.WebUi.dll is loaded, {webUi.GetTypes().Length} types");

        Patch(webUi);
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != WebUiAssemblyName)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        Log("Fiddler.WebUi.dll arrived late");

        try
        {
            Patch(args.LoadedAssembly);
        }
        catch (Exception ex)
        {
            Log($"late patch failed: {ex}");
        }
    }

    private static Assembly? FindWebUiAssembly()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == WebUiAssemblyName)
            {
                return assembly;
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces the licence methods of the Web UI assembly.
    /// </summary>
    private static void Patch(Assembly webUi)
    {
        lock (Gate)
        {
            if (s_installed)
            {
                return;
            }

            s_installed = true;
        }

        Licences.Use(webUi);

        // The set of redirections can be limited with FE_HOOK, which is a comma separated
        // list: licence, offline, ad, login. It is a debugging aid.
        string mask = Environment.GetEnvironmentVariable("FE_HOOK") ?? "licence,offline,ad,login";
        Log($"mask: {mask}");

        if (mask.Contains("licence", StringComparison.Ordinal))
        {
            // The licence file itself. The replacement builds the same DTO in memory.
            Replace(webUi, "Fiddler.WebUi.Services.Backend.BackendService", "ReadOfflineLicenseFile", nameof(ReplaceReadOfflineLicenseFile));
        }

        if (mask.Contains("offline", StringComparison.Ordinal))
        {
            // The licence the backend builds from it. On this build the original returns null.
            Replace(webUi, "Fiddler.WebUi.Services.Backend.BackendService", "GetOfflineLicense", nameof(ReplaceGetOfflineLicense));
        }

        if (mask.Contains("ad", StringComparison.Ordinal))
        {
            // The account lookups the eligibility check compares the licence against.
            foreach (string typeName in new[]
            {
                "Fiddler.WebUi.Services.OfflineLicenses.EmptyOfflineLicensesService",
                "Fiddler.WebUi.Services.OfflineLicenses.OfflineLicensesServiceWindows",
                "Fiddler.WebUi.Services.OfflineLicenses.OfflineLicensesServiceMac",
            })
            {
                Replace(webUi, typeName, "GetCurrentUserADEmail", nameof(ReplaceAdEmail));
                Replace(webUi, typeName, "GetCurrentUserADPrincipalName", nameof(ReplaceAdEmail));
            }
        }

        if (mask.Contains("login", StringComparison.Ordinal))
        {
            // The offline flag enters the backend here. The profile the user interface sends
            // carries it, and it is false whenever the application can reach the cloud.
            ReplaceBackendLogin(webUi);
        }
    }

    /// <summary>
    /// Redirects the backend login so that it always takes the offline branch.
    /// </summary>
    private static void ReplaceBackendLogin(Assembly webUi)
    {
        Type? type = webUi.GetType("Fiddler.WebUi.Services.Backend.BackendService");
        MethodInfo? method = type?.GetMethod(
            "Login",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (method is null)
        {
            Log("backend login not found");
            return;
        }

        Log($"backend login parameters: {string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name))}");

        MethodInfo? replacement = typeof(StartupHook).GetMethod(
            nameof(BackendLoginReplacement),
            BindingFlags.Static | BindingFlags.NonPublic);

        if (replacement is null)
        {
            Log("the replacement for the backend login is missing");
            return;
        }

        if (!Detour.Apply(method, replacement, out IntPtr originalCode, out IntPtr slot))
        {
            Log("could not redirect the backend login");
            return;
        }

        // The original is called as a managed method, so the runtime performs the
        // transition it expects. Calling the compiled code directly would not.
        s_originalBackendLogin = method;
        s_loginSlot = slot;
        s_loginOriginalCode = originalCode;
        s_loginReplacement = Detour.ResolveJumpTarget(replacement.MethodHandle.GetFunctionPointer());
        Log("redirected the backend login, the original is kept");
    }

    /// <summary>
    /// Redirects one method to a replacement.
    /// </summary>
    private static void Replace(Assembly webUi, string typeName, string methodName, string replacementName)
    {
        Type? type = webUi.GetType(typeName);
        MethodInfo? method = type?.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (method is null)
        {
            Log($"target not found: {typeName}::{methodName}");
            return;
        }

        MethodInfo? replacement = typeof(StartupHook).GetMethod(
            replacementName,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (replacement is null)
        {
            Log($"replacement not found: {replacementName}");
            return;
        }

        if (!Detour.Apply(method, replacement, out _, out _))
        {
            Log($"could not redirect: {typeName}::{methodName}");
            return;
        }

        Log($"redirected: {typeName}::{methodName}");
    }

    // -----------------------------------------------------------------------------------
    // Replacements
    // -----------------------------------------------------------------------------------

    /// <summary>Stands in for <c>ReadOfflineLicenseFile</c>.</summary>
    private static object ReplaceReadOfflineLicenseFile(IntPtr self)
    {
        try
        {
            Log("ReadOfflineLicenseFile called");
            object task = Licences.LicenseFileTask();
            Log("ReadOfflineLicenseFile answered");
            return task;
        }
        catch (Exception ex)
        {
            Log($"ReadOfflineLicenseFile replacement failed: {ex}");
            return null!;
        }
    }

    /// <summary>Stands in for <c>GetOfflineLicense</c>.</summary>
    private static object ReplaceGetOfflineLicense(IntPtr self)
    {
        try
        {
            Log("GetOfflineLicense called");
            object task = Licences.LicenseTask();
            Log("GetOfflineLicense answered");
            return task;
        }
        catch (Exception ex)
        {
            Log($"GetOfflineLicense replacement failed: {ex}");
            return null!;
        }
    }

    /// <summary>Stands in for the account e-mail lookups.</summary>
    private static string ReplaceAdEmail(IntPtr self)
    {
        return Licences.Email;
    }

    /// <summary>
    /// Stands in for the backend login and forces the offline branch.
    /// </summary>
    private static Task BackendLoginReplacement(
        object self,
        string apiUrl,
        string identityUrl,
        string idToken,
        string installationId,
        bool isOffline,
        bool disableCloud,
        bool disableMcp,
        bool disableAssistant,
        bool disableScripting,
        bool disableCustomInspectors)
    {
        MethodInfo? original = s_originalBackendLogin;
        if (original is null)
        {
            Log("the backend login has no original to call");
            return Task.CompletedTask;
        }

        Log($"backend login: isOffline {isOffline} is forced to true");

        // The original is reached through the same pointer that now points at the
        // replacement, so the pointer is put back for the duration of the call.
        Detour.SetSlot(s_loginSlot, s_loginOriginalCode);

        try
        {
            return (Task)original.Invoke(self, new object?[]
            {
                apiUrl,
                identityUrl,
                idToken,
                installationId,
                true,
                disableCloud,
                disableMcp,
                disableAssistant,
                disableScripting,
                disableCustomInspectors,
            })!;
        }
        finally
        {
            Detour.SetSlot(s_loginSlot, s_loginReplacement);
        }
    }

    // -----------------------------------------------------------------------------------
    // Logging
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Appends a line to the hook log in the temporary directory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Log(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "fe-proxy-hook.log");
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch (Exception)
        {
            // The log is a diagnostic aid, never a reason to disturb the application.
        }
    }
}

/// <summary>
/// Builds the licence objects the backend expects, using reflection so that the hook does
/// not need a reference to the application assembly.
/// </summary>
internal static class Licences
{
    /// <summary>The account the fabricated licence belongs to.</summary>
    public const string Email = "fiddler@telerik.com";

    private const string LicenceId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string PlanName = "enterprise";
    private const string PlanPeriod = "annual";
    private const string CreatedAt = "2024/1/1";
    private const string ExpiresAt = "2099/1/1";

    private static readonly object Gate = new();
    private static Assembly? s_webUi;

    private static Type? s_userType;
    private static Type? s_licenseFileType;
    private static Type? s_licenseType;

    private static object? s_licenseFileTask;
    private static object? s_licenseTask;

    // The document that backs the serialised users entry has to stay alive.
    private static JsonDocument? s_document;

    /// <summary>
    /// Prepares the types of the application assembly.
    /// </summary>
    public static void Use(Assembly webUi)
    {
        lock (Gate)
        {
            s_webUi = webUi;
            s_userType = FindType("Fiddler.WebUi.Services.OfflineLicenses.OfflineUserDTO");
            s_licenseFileType = FindType("FiddlerBackend.Contracts.OfflineLicenseFileDTO");
            s_licenseType = FindType("Fiddler.WebUi.Services.OfflineLicenses.OfflineLicenseDTO");

            StartupHook.Log(
                $"licence types: user={s_userType?.FullName ?? "missing"}, " +
                $"file={s_licenseFileType?.FullName ?? "missing"}, " +
                $"license={s_licenseType?.FullName ?? "missing"}");
        }
    }

    /// <summary>
    /// Finds a type in the loaded assemblies. The licence file DTO lives in the contracts
    /// assembly, not in the Web UI assembly.
    /// </summary>
    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // A dynamic assembly without a manifest is not a problem here.
            }
        }

        return null;
    }

    /// <summary>
    /// The task the licence file reader returns.
    /// </summary>
    public static object LicenseFileTask()
    {
        lock (Gate)
        {
            if (s_licenseFileTask is not null)
            {
                return s_licenseFileTask;
            }

            object license = CreateLicenseFile();
            s_licenseFileTask = FromResult(s_licenseFileType!, license);
            return s_licenseFileTask;
        }
    }

    /// <summary>
    /// The task the licence lookup returns.
    /// </summary>
    public static object LicenseTask()
    {
        lock (Gate)
        {
            if (s_licenseTask is not null)
            {
                return s_licenseTask;
            }

            object license = CreateLicense();
            s_licenseTask = FromResult(s_licenseType!, license);
            return s_licenseTask;
        }
    }

    /// <summary>
    /// Builds the licence file DTO.
    /// </summary>
    private static object CreateLicenseFile()
    {
        object license = Activator.CreateInstance(s_licenseFileType!)!;

        Set(license, "CreatedAt", DateTime.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture));
        Set(license, "ExpiresAt", DateTime.Parse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture));
        Set(license, "NotBefore", DateTime.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture));
        Set(license, "PlanName", PlanName);
        Set(license, "PlanPeriod", PlanPeriod);
        Set(license, "Id", Guid.Parse(LicenceId));
        Set(license, "AdditionalData", CreateAdditionalData());
        Set(license, "User", CreateUser());

        return license;
    }

    /// <summary>
    /// Builds the licence DTO the backend works with.
    /// </summary>
    private static object CreateLicense()
    {
        StartupHook.Log("building the licence");
        object license = Activator.CreateInstance(s_licenseType!)!;

        Set(license, "CreatedAt", DateTime.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture));
        Set(license, "ExpiresAt", DateTime.Parse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture));
        Set(license, "PlanName", PlanName);
        Set(license, "PlanPeriod", PlanPeriod);
        Set(license, "User", CreateUser());
        StartupHook.Log("the licence is built");

        return license;
    }

    /// <summary>
    /// Builds the offline user.
    /// </summary>
    private static object CreateUser()
    {
        object user = Activator.CreateInstance(s_userType!)!;

        Set(user, "Email", Email);
        Set(user, "UserPrincipalEmail", Email);
        Set(user, "FirstName", "fiddler");
        Set(user, "LastName", string.Empty);

        return user;
    }

    /// <summary>
    /// Builds the additional data dictionary. The users entry holds the serialised user
    /// array, wrapped in a JSON element, which is the shape the backend reads.
    /// </summary>
    private static object CreateAdditionalData()
    {
        Array users = Array.CreateInstance(s_userType!, 1);
        users.SetValue(CreateUser(), 0);

        string json = JsonSerializer.Serialize(users, s_userType!);
        s_document = JsonDocument.Parse(json);
        JsonElement element = s_document.RootElement;

        Dictionary<string, object> additionalData = new();
        additionalData["users"] = element;

        return additionalData;
    }

    /// <summary>
    /// Sets a property by name, ignoring the ones the build does not have.
    /// </summary>
    private static void Set(object target, string propertyName, object value)
    {
        PropertyInfo? property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            StartupHook.Log($"property not found: {target.GetType().Name}.{propertyName}");
            return;
        }

        try
        {
            property.SetValue(target, value);
        }
        catch (Exception ex)
        {
            StartupHook.Log($"could not set {target.GetType().Name}.{propertyName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a completed task of the given type.
    /// </summary>
    private static object FromResult(Type resultType, object value)
    {
        MethodInfo fromResult = typeof(Task).GetMethod("FromResult", BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo closed = fromResult.MakeGenericMethod(resultType);
        return closed.Invoke(null, new[] { value })!;
    }
}

/// <summary>
/// Redirects the compiled code of a method to a replacement.
/// </summary>
internal static class Detour
{
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    /// <summary>
    /// Redirects a method to another managed method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redirection goes through the pointer that the runtime itself uses to reach the
    /// code of the method, so no compiled code is overwritten. The entry of a prepared
    /// method on x64 is a stub that jumps through a pointer, and behind it sits a fixup
    /// block that loads the method descriptor into r10 and jumps again. The fixup block and
    /// the compiled code belong to the runtime and are left alone.
    /// </para>
    /// <para>
    /// The pointer is set to the fixup block of the replacement rather than to its code.
    /// That way the call still runs the fixup of the replacement, so the method descriptor
    /// in r10 belongs to the method that is actually running, and the call is an ordinary
    /// managed call rather than a call into an unmanaged callable.
    /// </para>
    /// </remarks>
    public static unsafe bool Apply(MethodInfo method, MethodInfo replacement, out IntPtr originalCode, out IntPtr codeSlot)
    {
        originalCode = IntPtr.Zero;
        codeSlot = IntPtr.Zero;

        IntPtr entry = method.MethodHandle.GetFunctionPointer();
        if (entry == IntPtr.Zero)
        {
            return false;
        }

        if (!TryFindCodeSlot(entry, out IntPtr slot, out originalCode, out IntPtr loadSlot))
        {
            StartupHook.Log($"could not find the code pointer of {entry:X}");
            return false;
        }

        codeSlot = slot;

        IntPtr replacementEntry = replacement.MethodHandle.GetFunctionPointer();
        if (replacementEntry == IntPtr.Zero)
        {
            return false;
        }

        IntPtr replacementTarget = ResolveJumpTarget(replacementEntry);

        uint oldProtect;
        if (!VirtualProtect(slot, (UIntPtr)8, PageReadWrite, out oldProtect))
        {
            StartupHook.Log($"VirtualProtect failed with {Marshal.GetLastWin32Error()}");
            return false;
        }

        *(IntPtr*)slot = replacementTarget;

        uint ignored;
        VirtualProtect(slot, (UIntPtr)8, oldProtect, out ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, (UIntPtr)8);

        StartupHook.Log(
            $"slot {slot:X} now points at {replacementTarget:X} " +
            $"(the code of {method.Name} was at {originalCode:X})");

        return true;
    }

    /// <summary>
    /// Writes a new target into the pointer that holds the address of the compiled code.
    /// </summary>
    public static unsafe bool SetSlot(IntPtr slot, IntPtr value)
    {
        uint oldProtect;
        if (!VirtualProtect(slot, (UIntPtr)8, PageReadWrite, out oldProtect))
        {
            StartupHook.Log($"VirtualProtect failed with {Marshal.GetLastWin32Error()}");
            return false;
        }

        *(IntPtr*)slot = value;

        uint ignored;
        VirtualProtect(slot, (UIntPtr)8, oldProtect, out ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, (UIntPtr)8);

        return true;
    }

    /// <summary>
    /// Returns the address a call has to jump to in order to run the method: its fixup
    /// block when it has one, and its code otherwise.
    /// </summary>
    public static unsafe IntPtr ResolveJumpTarget(IntPtr entry)
    {
        byte* p = (byte*)entry;

        if (p[0] == 0xFF && p[1] == 0x25)
        {
            int displacement = *(int*)(p + 2);
            IntPtr first = (IntPtr)(entry + 6 + displacement);
            IntPtr value = *(IntPtr*)first;
            StartupHook.Log($"the replacement jumps through {first:X} to {value:X}");
            return value;
        }

        return entry;
    }

    /// <summary>
    /// Finds the pointer that holds the address of the compiled code.
    /// </summary>
    private static unsafe bool TryFindCodeSlot(IntPtr entry, out IntPtr slot, out IntPtr code, out IntPtr loadSlot)
    {
        slot = IntPtr.Zero;
        code = IntPtr.Zero;
        loadSlot = IntPtr.Zero;

        byte* p = (byte*)entry;

        // jmp qword ptr [rip+disp32]
        if (p[0] == 0xFF && p[1] == 0x25)
        {
            int displacement = *(int*)(p + 2);
            IntPtr first = (IntPtr)(entry + 6 + displacement);
            IntPtr value = *(IntPtr*)first;

            StartupHook.Log($"entry {entry:X} jumps through {first:X} to {value:X}");

            byte* v = (byte*)value;
            if (value != IntPtr.Zero && v[0] == 0x4C && v[1] == 0x8B && v[2] == 0x15)
            {
                // mov r10, qword ptr [rip+disp32] (7 bytes) followed by
                // jmp qword ptr [rip+disp32] (6 bytes)
                int loadDisplacement = *(int*)(v + 3);
                loadSlot = (IntPtr)(value + 7 + loadDisplacement);
                int jumpDisplacement = *(int*)(v + 9);
                IntPtr jumpSlot = (IntPtr)(value + 13 + jumpDisplacement);

                StartupHook.Log(
                    $"fixup block {value:X} loads r10 from {loadSlot:X} ({*(IntPtr*)loadSlot:X}) " +
                    $"and jumps through {jumpSlot:X} to {*(IntPtr*)jumpSlot:X}");

                slot = jumpSlot;
                code = *(IntPtr*)jumpSlot;
                return true;
            }

            slot = first;
            code = value;
            return true;
        }

        // jmp rel32
        if (p[0] == 0xE9)
        {
            int displacement = *(int*)(p + 1);
            code = (IntPtr)(entry + 5 + displacement);
            StartupHook.Log($"entry {entry:X} is a jump to {code:X}");
            return false;
        }

        return false;
    }
}
