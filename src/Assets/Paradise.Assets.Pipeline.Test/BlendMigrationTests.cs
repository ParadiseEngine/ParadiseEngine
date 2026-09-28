using System.Text.Json.Nodes;

using TUnit.Assertions.Enums;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// <c>to-blend</c> replaces GLB model sources with <c>.blend</c> sources and every document keeps its
/// identity. Blender is a stand-in: its "builder" writes each staged <c>.blend</c> as JSON naming the
/// GLBs it holds and the materials they share, and its "converter" exports each of those GLBs as it
/// is — or, for a GLB given an altered twin, that twin, which is a model that no longer builds the same.
/// </summary>
[NotInParallel]
public class BlendMigrationTests
{
    private const string Schema = """
        {"version":3,"components":[
          {"id":"edee8bd8-9321-47db-819d-9bdadf010be4","type":"Game.StaticMesh","displayName":"Mesh","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]}
        ]}
        """;

    private const string FakeBlender = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then echo "Blender 4.4.0"; exit 0; fi
        exec python3 "$(dirname "$0")/fake_blender.py" "$@"
        """;

    private const string FakeBlenderScript = """
        import json, os, shutil, sys
        here = os.path.dirname(os.path.abspath(__file__))
        args = sys.argv[1:]
        rest = args[args.index('--') + 1:]
        if args[args.index('--python') + 1].endswith('to_blend.py'):
            jobs_in, results_out = rest[:2]
            results = {}
            for job in json.load(open(jobs_in)):
                with open(job['staged'], 'w') as staged:
                    json.dump({'members': job['members'], 'shared': job['shared_materials']}, staged)
                results[job['staged']] = None
            json.dump(results, open(results_out, 'w'))
        else:
            source, glb_out, dependencies_out, assets_out, assets_list_out, extension = rest[:6]
            members = json.load(open(source))['members']
            for member in members:
                target = glb_out if member['asset'] is None else os.path.join(assets_out, member['guid'] + '.glb')
                altered = os.path.join(here, 'altered', os.path.basename(member['glb']))
                shutil.copyfile(altered if os.path.exists(altered) else member['glb'], target)
            assets = [{'guid': m['guid'], 'name': m['asset']} for m in members if m['asset'] is not None]
            if assets:
                json.dump(assets, open(assets_list_out, 'w'))
            json.dump([], open(dependencies_out, 'w'))
        """;

    [Test]
    public async Task a_dry_run_reports_the_plan_and_writes_nothing()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        var before = project.Snapshot();

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [project.Models], families: true, dryRun: true);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Targets.Select(target => target.Blend).ToList()).IsEquivalentTo(["models/crate.blend", "models/lamp.blend"], CollectionOrdering.Matching);
        await Assert.That(result.Kept.Single().Glb).IsEqualTo("models/lamp_deadbeef.glb");
        await Assert.That(result.Kept.Single().Reason).Contains("does not build the same");
        await Assert.That(project.Snapshot()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_glb_alone_becomes_a_blend_under_its_own_identity()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        var crate = project.Models / "crate.glb";
        var identity = SidecarMeta.Load(project.FileSystem, crate + ".meta").Guid;
        var record = ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, crate + ".meta"));

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [crate], families: false, dryRun: false);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(project.FileSystem.FileExists(crate)).IsFalse();
        await Assert.That(project.FileSystem.FileExists(crate + ".meta")).IsFalse();

        var blend = project.Models / "crate.blend";
        var meta = SidecarMeta.Load(project.FileSystem, blend + ".meta");
        await Assert.That(meta.Guid).IsEqualTo(identity);
        await Assert.That(ExtractionRecord.Read(meta).Parts).IsEquivalentTo(record.Parts, CollectionOrdering.Matching);

        var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Models / "crate.mesh");
        await Assert.That(mesh.Source).IsEqualTo(new AssetReference(identity, "models/crate.blend"));
        await Assert.That(mesh.Asset).IsNull();

        var settled = project.Settle(blend);
        await Assert.That(settled.Extracted.Errors).IsEmpty();
        await Assert.That(settled.Extracted.Written).IsEmpty();
        await Assert.That(settled.Errors).IsEmpty();
    }

    [Test]
    public async Task a_family_becomes_one_blend_holding_each_member_as_an_asset()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        string[] members = ["lamp_0123abcd", "lamp_89abcdef"];
        var meshes = members.ToDictionary(member => member, member => SidecarMeta.Load(project.FileSystem, project.Models / $"{member}.mesh.meta").Guid);
        var glbs = members.ToDictionary(member => member, member => SidecarMeta.Load(project.FileSystem, project.Models / $"{member}.glb.meta").Guid);
        var materials = members.ToDictionary(member => member, member => ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Models / $"{member}.glb.meta")).OfKind(ExtractKind.Materials).Single().Reference);

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [project.Models], families: true, dryRun: false);

        await Assert.That(result.Errors).IsEmpty();
        var lamp = result.Targets.Single(target => target.Blend == "models/lamp.blend");
        // Each member's asset is identified by the GUID its GLB had, so the identity stays traceable.
        await Assert.That(lamp.Members.Select(member => member.Asset!)).IsEquivalentTo(members.Select(member => new ModelAsset(glbs[member], member)), CollectionOrdering.Matching);

        // The member that would not build the same stays a GLB, and the family went ahead without it.
        await Assert.That(result.Kept.Single().Glb).IsEqualTo("models/lamp_deadbeef.glb");
        await Assert.That(project.FileSystem.FileExists(project.Models / "lamp_deadbeef.glb")).IsTrue();

        var blend = project.Models / "lamp.blend";
        var meta = SidecarMeta.Load(project.FileSystem, blend + ".meta");
        var parts = ExtractionRecord.Read(meta).Parts;
        foreach (var member in members)
        {
            await Assert.That(project.FileSystem.FileExists(project.Models / $"{member}.glb")).IsFalse();
            var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Models / $"{member}.mesh");
            await Assert.That(mesh.Source).IsEqualTo(new AssetReference(meta.Guid, "models/lamp.blend"));
            await Assert.That(mesh.Asset).IsEqualTo(new ModelAsset(glbs[member], member));
            await Assert.That(SidecarMeta.Load(project.FileSystem, project.Models / $"{member}.mesh.meta").Guid).IsEqualTo(meshes[member]);
            await Assert.That(parts.Single(part => part.Asset == glbs[member] && part.Kind == ExtractKind.Meshes).Reference.Guid).IsEqualTo(meshes[member]);
            await Assert.That(parts.Single(part => part.Asset == glbs[member] && part.Kind == ExtractKind.Materials).Reference).IsEqualTo(materials[member]);
        }

        await Assert.That(ModelSource.Assets(project.FileSystem, blend)).IsEquivalentTo(members.Select(member => new ModelAsset(glbs[member], member)), CollectionOrdering.Matching);
        var settled = project.Settle(blend);
        await Assert.That(settled.Extracted.Errors).IsEmpty();
        await Assert.That(settled.Extracted.Written).IsEmpty();
        await Assert.That(settled.Errors).IsEmpty();

        // Nothing is left to replace, and what could not be is refused again.
        var again = BlendMigration.Run(project.FileSystem, project.Layout, [project.Models], families: true, dryRun: false);
        await Assert.That(again.Targets).IsEmpty();
        await Assert.That(again.Kept.Single().Glb).IsEqualTo("models/lamp_deadbeef.glb");
    }

    [Test]
    public async Task glbs_of_different_names_become_assets_of_the_blend_they_are_put_into()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        string[] members = ["crate", "lamp_0123abcd"];
        var glbs = members.ToDictionary(member => member, member => SidecarMeta.Load(project.FileSystem, project.Models / $"{member}.glb.meta").Guid);
        var props = project.Models / "props.blend";

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [.. members.Select(member => project.Models / $"{member}.glb")],
            families: false, dryRun: false, into: props);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Kept).IsEmpty();
        var target = result.Targets.Single();
        await Assert.That(target.Blend).IsEqualTo("models/props.blend");
        await Assert.That(target.Members.Select(member => member.Asset!)).IsEquivalentTo(members.Select(member => new ModelAsset(glbs[member], member)), CollectionOrdering.Matching);
        var meta = SidecarMeta.Load(project.FileSystem, props + ".meta");
        foreach (var member in members)
        {
            await Assert.That(project.FileSystem.FileExists(project.Models / $"{member}.glb")).IsFalse();
            var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Models / $"{member}.mesh");
            await Assert.That(mesh.Source).IsEqualTo(new AssetReference(meta.Guid, "models/props.blend"));
            await Assert.That(mesh.Asset).IsEqualTo(new ModelAsset(glbs[member], member));
        }

        var settled = project.Settle(props);
        await Assert.That(settled.Extracted.Errors).IsEmpty();
        await Assert.That(settled.Errors).IsEmpty();
    }

    [Test]
    public async Task a_glb_outside_the_directory_of_the_blend_it_is_put_into_is_refused()
    {
        using var project = new Project();

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [project.Models / "crate.glb"],
            families: false, dryRun: false, into: project.Layout.Assets / "props.blend");

        await Assert.That(result.Errors.Single()).Contains("models/crate.glb");
        await Assert.That(project.FileSystem.FileExists(project.Models / "crate.glb")).IsTrue();
    }

    [Test]
    public async Task a_glb_whose_json_names_what_it_does_not_declare_is_kept_and_the_run_goes_on()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        var crate = project.Models / "crate.glb";
        if (!GlbBinary.TryRead(project.FileSystem.ReadAllBytes(crate), out var gltf, out var bin)) throw new InvalidOperationException("the crate is no GLB");
        gltf["images"] = new JsonArray(new JsonObject { ["bufferView"] = 99, ["mimeType"] = "image/png" });
        project.FileSystem.WriteAllBytes(crate, GlbBinary.Write(gltf, bin));

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [crate, project.Models / "lamp_0123abcd.glb"], families: false, dryRun: false);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Kept.Single().Glb).IsEqualTo("models/crate.glb");
        await Assert.That(result.Kept.Single().Reason).Contains("bufferView 99");
        await Assert.That(project.FileSystem.FileExists(crate)).IsTrue();
        await Assert.That(result.Targets.Single().Blend).IsEqualTo("models/lamp_0123abcd.blend");
    }

    [Test]
    public async Task a_glb_the_manifest_ignores_is_not_replaced_even_when_named()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        project.FileSystem.WriteAllText(project.Layout.Manifest, "name = \"game\"\nschema_version = 1\n\n[assets]\nignore = [\"models/crate.glb\"]\n");
        var crate = project.Models / "crate.glb";

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [crate], families: false, dryRun: false);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Targets).IsEmpty();
        await Assert.That(project.FileSystem.FileExists(crate)).IsTrue();
        await Assert.That(project.FileSystem.FileExists(project.Models / "crate.blend")).IsFalse();
    }

    [Test]
    public async Task family_members_share_a_material_whose_factors_differ_within_the_comparison_tolerance()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        project.FileSystem.WriteAllBytes(project.Models / "lamp_89abcdef.glb", Triangle(3f, "brass", metallic: 0.2504));

        var result = BlendMigration.Run(project.FileSystem, project.Layout, [project.Models], families: true, dryRun: false);

        await Assert.That(result.Errors).IsEmpty();
        var staged = JsonNode.Parse(project.FileSystem.ReadAllText(project.Models / "lamp.blend"))!;
        await Assert.That(staged["shared"]!.AsArray().Select(name => name!.GetValue<string>()).ToList()).IsEquivalentTo(["brass"]);
    }

    /// <summary>A triangle scaled by <paramref name="size"/>, drawn with one untextured material.</summary>
    private static byte[] Triangle(float size, string material, double metallic = 0.25)
    {
        var b = new GlbTestBuilder();
        b.AddMaterial(new JsonObject { ["name"] = material, ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = metallic } });
        var position = b.AddFloatAccessor([0f, 0f, 0f, size, 0f, 0f, 0f, size, 0f], "VEC3");
        b.SetSceneRoots(b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position, material: 0)), name: "Body"));
        return b.Build();
    }

    /// <summary>A project on disk with a crate and a lamp family of three, each extracted, and the stand-in Blender in charge.</summary>
    private sealed class Project : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"paradise_to_blend_{Guid.NewGuid():N}");
        private readonly string? _blender = Environment.GetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable);

        public Project()
        {
            Directory.CreateDirectory(Path.Combine(_root, "assets", "models"));
            Directory.CreateDirectory(Path.Combine(_root, ".editor"));
            Directory.CreateDirectory(Path.Combine(_root, "blender", "altered"));
            File.WriteAllText(Path.Combine(_root, "assets", "project.toml"), "name = \"game\"\nschema_version = 1\n");
            File.WriteAllText(Path.Combine(_root, ".editor", "authoring-schema.json"), Schema);

            var blender = Path.Combine(_root, "blender", "blender");
            File.WriteAllText(blender, FakeBlender + "\n");
            File.WriteAllText(Path.Combine(_root, "blender", "fake_blender.py"), FakeBlenderScript + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(blender, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, blender);

            Layout = new AssetProjectLayout(FileSystem.ConvertPathFromInternal(_root));
            Models = Layout.Assets / "models";
            Mint(Layout.Manifest);
            Add("crate", Triangle(1f, "wood"));
            Add("lamp_0123abcd", Triangle(2f, "brass"));
            Add("lamp_89abcdef", Triangle(3f, "brass"));
            Add("lamp_deadbeef", Triangle(4f, "brass"));
            File.WriteAllBytes(Path.Combine(_root, "blender", "altered", "lamp_deadbeef.glb"), Triangle(5f, "brass"));
        }

        public PhysicalFileSystem FileSystem { get; } = new();

        public AssetProjectLayout Layout { get; }

        public UPath Models { get; }

        /// <summary>Every file under <c>assets/</c> with its bytes, to tell a run that wrote from one that did not.</summary>
        public List<string> Snapshot()
            => [.. FileSystem.EnumerateFiles(Layout.Assets, "*", SearchOption.AllDirectories)
                .Select(path => $"{path} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(FileSystem.ReadAllBytes(path)))}")
                .Order(StringComparer.Ordinal)];

        /// <summary>Extracts the replaced source, which settles when it writes nothing its GLBs had not, and the tree's verify errors.</summary>
        public (ExtractResult Extracted, List<VerifyFinding> Errors) Settle(UPath blend)
            => (AssetExtractor.Extract(FileSystem, Layout, blend),
                [.. ProjectVerifier.Verify(FileSystem, Layout).Where(finding => finding.Severity == VerifySeverity.Error)]);

        private void Add(string stem, byte[] glb)
        {
            var path = Models / $"{stem}.glb";
            FileSystem.WriteAllBytes(path, glb);
            Mint(path);
            var extracted = AssetExtractor.Extract(FileSystem, Layout, path);
            if (!extracted.Succeeded) throw new InvalidOperationException(string.Join("\n", extracted.Errors));
        }

        private void Mint(UPath asset)
        {
            var meta = SidecarMeta.Mint();
            meta.Importer = ImporterChain.Claim(AssetImporters.All, new ImportCandidate(FileSystem, Layout, asset, null))?.Name;
            meta.Save(FileSystem, SidecarMeta.PathFor(asset));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, _blender);
            FileSystem.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
