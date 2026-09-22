using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Cli;

/// <summary>Loads the built game's assembly graph and invokes only declared inspector actions.</summary>
internal static class ActionAssembly
{
    private const string AuthoredAttribute = "Paradise.Authoring.AuthoredAttribute";
    private const string ButtonAttribute = "Paradise.Authoring.AuthoredButtonAttribute";
    private const string ToggleAttribute = "Paradise.Authoring.AuthoredToggleAttribute";

    public static int Invoke(IFileSystem fileSystem, AssetProjectLayout layout, UPath assembly,
        UPath document, Guid componentId, string action, Guid? entity, Action<string> error,
        bool? value = null, bool onSave = false, UPath? state = null, UPath? response = null)
    {
        ActionLoadContext? loadContext = null;
        try
        {
            if (!document.FullName.StartsWith(layout.Assets.FullName + "/", StringComparison.Ordinal)
                || !document.GetName().EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || !fileSystem.FileExists(document))
                throw new InvalidDataException("the action document must be an existing .prefab under assets/");
            if (response is { } output && (output == document || output == assembly || output == state))
                throw new InvalidDataException("response, document, assembly and state paths must be distinct");
            var toggles = state is { } input
                ? JsonSerializer.Deserialize(fileSystem.ReadAllText(input), ActionJsonContext.Default.DictionaryStringBoolean)
                    ?? throw new InvalidDataException("toggle state must be a JSON object")
                : new Dictionary<string, bool>(StringComparer.Ordinal);
            var path = ProjectPaths.Internal(fileSystem, assembly);
            loadContext = new ActionLoadContext(path);
            var host = loadContext.LoadFromAssemblyPath(path);
            var type = FindComponent(host, loadContext, componentId)
                ?? throw new InvalidDataException($"no [Authored] component with id {DocumentGuid.Format(componentId)} is reachable from '{assembly.GetName()}'; rebuild the host and schema");
            var named = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => method.Name == action).ToArray();
            if (named.Length != 1)
                throw new InvalidDataException($"'{type.FullName}.{action}' must identify exactly one declared method");
            var method = named[0];
            var attributes = method.GetCustomAttributesData().Where(attribute =>
                attribute.AttributeType.FullName is ButtonAttribute or ToggleAttribute).ToArray();
            if (attributes.Length != 1)
                throw new InvalidDataException($"'{type.FullName}.{action}' must declare exactly one [AuthoredButton] or [AuthoredToggle]");
            var toggle = attributes[0].AttributeType.FullName == ToggleAttribute;
            if (onSave && !attributes[0].NamedArguments.Any(argument =>
                argument.MemberName == "OnSave" && argument.TypedValue.Value is true))
                throw new InvalidDataException($"'{action}' is not declared OnSave");
            if (!toggle && value is not null)
                throw new InvalidDataException($"button '{action}' does not accept --value");
            if (toggle && value is null)
                throw new InvalidDataException($"toggle '{action}' requires --value true or false");
            var parameters = method.GetParameters();
            var hasContext = parameters.Length > 0 && parameters[0].ParameterType == typeof(AuthorActionContext);
            var validParameters = toggle
                ? parameters.Length == (hasContext ? 2 : 1) && parameters[^1].ParameterType == typeof(bool)
                : parameters.Length == (hasContext ? 1 : 0);
            if (!method.IsPublic || !method.IsStatic || method.IsAbstract || method.ContainsGenericParameters
                || method.ReturnType != typeof(void) || !validParameters
                || method.IsDefined(typeof(AsyncStateMachineAttribute), false)
                || parameters.Any(parameter => parameter.IsOut || parameter.ParameterType.IsByRef
                    || parameter.IsOptional || parameter.IsDefined(typeof(ParamArrayAttribute), false)))
                throw new InvalidDataException($"'{type.FullName}.{action}' has an invalid authored-action signature");
            var context = new AuthorActionContext
            {
                ProjectRoot = fileSystem.ConvertPathToInternal(layout.Root),
                Document = fileSystem.ConvertPathToInternal(document),
                ComponentId = DocumentGuid.Format(componentId),
                EntityId = entity is { } id ? DocumentGuid.Format(id) : null,
                Value = value,
                IsSave = onSave,
                ToggleValues = toggles,
            };
            if (toggle) context.Result.Toggles[action] = value!.Value;
            object?[] arguments = (hasContext, toggle) switch
            {
                (true, true) => [context, value!.Value],
                (true, false) => [context],
                (false, true) => [value!.Value],
                _ => [],
            };
            method.Invoke(null, arguments);
            if (response is { } responsePath)
            {
                var json = JsonSerializer.Serialize(context.Result, ActionJsonContext.Default.AuthorActionResult);
                fileSystem.CreateDirectory(responsePath.GetDirectory());
                fileSystem.WriteAllText(responsePath, json);
            }
            return 0;
        }
        catch (TargetInvocationException failure)
        {
            error($"invoke-action: {action} failed: {failure.InnerException?.Message ?? failure.Message}");
            return 1;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or BadImageFormatException or InvalidOperationException
            or ArgumentException or JsonException or UnauthorizedAccessException or TypeLoadException
            or ReflectionTypeLoadException)
        {
            error($"invoke-action: {FailureMessage(failure)}");
            return 1;
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static string FailureMessage(Exception failure)
    {
        if (failure is ReflectionTypeLoadException types)
            return string.Join("; ", types.LoaderExceptions.OfType<Exception>().Select(FailureMessage));
        return failure.InnerException is { } inner ? $"{failure.Message}: {FailureMessage(inner)}" : failure.Message;
    }

    private static Type? FindComponent(Assembly host, AssemblyLoadContext context, Guid id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { host.GetName().FullName };
        var queue = new Queue<Assembly>();
        Type? matched = null;
        queue.Enqueue(host);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (assembly.GetReferencedAssemblies().Any(reference => reference.Name == typeof(AuthorActionContext).Assembly.GetName().Name))
                foreach (var type in assembly.GetTypes())
                    if (ComponentId(type) == id)
                    {
                        if (matched is not null && matched != type)
                            throw new InvalidDataException($"component id {DocumentGuid.Format(id)} is ambiguous between '{matched.FullName}' and '{type.FullName}'");
                        matched = type;
                    }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is not { } name || name.StartsWith("System.", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name is "netstandard" or "mscorlib"
                    || !seen.Add(reference.FullName)) continue;
                // A load failure must keep its cause (especially an engine version mismatch),
                // not masquerade as a missing component in a stale schema.
                queue.Enqueue(context.LoadFromAssemblyName(reference));
            }
        }
        return matched;
    }

    private static Guid? ComponentId(Type type)
    {
        var authored = false;
        var id = Guid.Empty;
        foreach (var attribute in type.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName == AuthoredAttribute) authored = true;
            else if (attribute.AttributeType.FullName == typeof(GuidAttribute).FullName
                && attribute.ConstructorArguments is [{ Value: string text }]) Guid.TryParse(text, out id);
        }
        return authored && id != Guid.Empty ? id : null;
    }

    private sealed class ActionLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is { } simple && simple.StartsWith("Paradise.", StringComparison.Ordinal))
            {
                Assembly? hosted = null;
                try { hosted = Default.LoadFromAssemblyName(new AssemblyName(simple)); }
                catch (FileNotFoundException) { }
                if (hosted is not null)
                {
                    ValidateEngineVersion(name, hosted.GetName());
                    return hosted;
                }
            }
            else
            {
                // Public engine APIs also carry dependency types (Zio, DotRecast, logging).
                // Sharing only Paradise.* would make those argument types incompatible.
                try { return Default.LoadFromAssemblyName(name); }
                catch (FileNotFoundException) { }
            }
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string name) =>
            _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    internal static void ValidateEngineVersion(AssemblyName requested, AssemblyName available)
    {
        if (requested.Version is { } expected && available.Version is { } actual && expected > actual)
            throw new FileLoadException($"the action project requires {requested}, but this CLI provides {available}; upgrade the paradise tool to match");
    }
}

[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(AuthorActionResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ActionJsonContext : JsonSerializerContext;
