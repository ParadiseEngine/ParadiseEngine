using System.Text.RegularExpressions;

namespace Paradise.Architecture.Test;

/// <summary>Rules about how projects reference each other.</summary>
public class ProjectGraphTests
{
    /// <summary>Foundation packages any subsystem may use; each must stay free of engine references.</summary>
    private static readonly string[] s_leaves =
    [
        "Paradise.BLOB", "Paradise.Features", "Paradise.Geometry", "Paradise.Diagnostics", "Paradise.Windowing", "Paradise.Jobs", "Paradise.Audio",
        "Paradise.Authoring", "Paradise.Assets.Gltf", "Paradise.Assets.Packing",
    ];

    /// <summary>Build-time tooling a shipped game never needs.</summary>
    private static readonly string[] s_buildTooling =
    [
        "Paradise.Assets.Pipeline", "Paradise.Assets.Project", "Paradise.Assets.Documents", "Paradise.Assets.Gltf",
        // Document writers and the navmesh baker; a game reads documents through Paradise.Export.Contract.
        "Paradise.Export",
        "Paradise.Cli", "Paradise.Cli.Host", "Paradise.Cli.Extensibility", "Paradise.Editor.Core", "Paradise.Editor.ImGui",
    ];

    /// <summary>Packages that run inside a shipped game.</summary>
    private static readonly Regex s_runtime = new(
        @"^Paradise\.(BLOB|Features(\.Toml)?|Geometry|Diagnostics|Jobs|ECS(\..+)?|BT(\..+)?|Physics|Animation|Rendering(\..+)?|Hosting(\..+)?|Windowing(\..+)?|Ui(\..+)?|Audio(\..+)?|Assets\.(Mesh|Textures|Packing)|Export\.Contract)$",
        RegexOptions.CultureInvariant);

    [Test]
    public async Task project_references_form_no_cycle()
    {
        var cyclic = Repository.Projects.Keys.Where(name => Repository.Closure(name).Contains(name)).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(cyclic).IsEmpty();
    }

    [Test]
    public async Task foundation_packages_reference_no_engine_project()
    {
        var offenders = s_leaves
            .SelectMany(leaf => Repository.Projects[leaf].ProjectReferences.Select(reference => $"{leaf} -> {reference}"))
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task runtime_packages_never_reach_build_tooling()
    {
        var offenders = RuntimePackages()
            .SelectMany(runtime => Repository.Closure(runtime)
                .Where(reached => s_buildTooling.Contains(reached))
                .Select(reached => $"{runtime} -> {reached}"))
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>Recast builds navmeshes; a game only reads them, which Detour does.</summary>
    [Test]
    public async Task runtime_packages_never_reach_the_navmesh_baker()
    {
        var offenders = RuntimePackages()
            .Where(runtime => Repository.Closure(runtime).Append(runtime)
                .Any(reached => Repository.Projects.TryGetValue(reached, out var project)
                    && project.PackageReferences.Contains("DotRecast.Recast")))
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>The document contract is what a game reads; navmeshes are loaded by whatever runs them, so the contract carries no DotRecast at all.</summary>
    [Test]
    public async Task the_document_contract_carries_no_navmesh_code()
    {
        const string contract = "Paradise.Export.Contract";
        var offenders = Repository.Closure(contract).Append(contract)
            .Where(reached => Repository.Projects.TryGetValue(reached, out var project)
                && project.PackageReferences.Any(package => package.StartsWith("DotRecast.", StringComparison.Ordinal)))
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>A project outside the solution is neither built nor tested by CI.</summary>
    [Test]
    public async Task every_project_is_built_by_the_solution()
    {
        var solution = Repository.ReadText("ParadiseEngine.slnx");
        var inSolution = Repository.Projects.Values
            .Where(project => solution.Contains($"Path=\"{Repository.Relative(project.Path)}\"", StringComparison.Ordinal))
            .ToArray();
        // Fixtures the solution builds through a build-only reference count as built.
        var built = new HashSet<string>(inSolution.Select(project => project.Name), StringComparer.Ordinal);
        var pending = new Stack<string>(inSolution.SelectMany(project => project.BuildReferences));
        while (pending.TryPop(out var next))
        {
            if (built.Add(next) && Repository.Projects.TryGetValue(next, out var project))
            {
                foreach (var reference in project.BuildReferences) pending.Push(reference);
            }
        }

        var missing = Repository.Projects.Keys
            // Needs the wasm-tools workload, so it stays out of the solution (docs/asset-packs.md).
            .Where(name => name != "Paradise.Assets.Packing.BrowserSmoke")
            .Where(name => !built.Contains(name))
            .ToArray();
        await Assert.That(missing).IsEmpty();
    }

    private static IEnumerable<string> RuntimePackages() =>
        Repository.Projects.Values.Where(project => project.IsPackable && s_runtime.IsMatch(project.Name)).Select(project => project.Name);
}
