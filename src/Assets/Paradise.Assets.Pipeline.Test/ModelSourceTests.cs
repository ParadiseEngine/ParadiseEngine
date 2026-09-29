using System.Security.Cryptography;
using System.Text.Json.Nodes;

using TUnit.Assertions.Enums;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A <c>.blend</c>, <c>.obj</c> or any other converted format is a model source read through the
/// GLB Blender converted it to: extraction treats it like a GLB and never writes into it, and a
/// conversion that cannot be made current says what it needs. Blender is kept out by pointing
/// PARADISE_BLENDER_PATH at nothing, which is also what a machine without Blender sees.
/// </summary>
[NotInParallel]
public class ModelSourceTests
{
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static readonly byte[] s_blend = "BLENDER-v500 not a real one, never opened"u8.ToArray();

    private static readonly ModelAsset s_short = new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "Lamp_Short");
    private static readonly ModelAsset s_tall = new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "Lamp_Tall");

    private const string Schema = """
        {"version":3,"components":[
          {"id":"edee8bd8-9321-47db-819d-9bdadf010be4","type":"Game.StaticMesh","displayName":"Mesh","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]}
        ]}
        """;

    [Test]
    public async Task a_blend_extracts_through_its_converted_glb_without_being_written()
    {
        using var project = new Project();
        project.Seed(Sha256(s_blend));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        var source = SidecarMeta.Load(project.FileSystem, project.Blend + ".meta").Guid;
        var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Layout.Assets / "models/crate.mesh");
        await Assert.That(mesh.Source).IsEqualTo(new Paradise.Authoring.AssetReference(source, "models/crate.blend"));

        // The embedded image became a texture file, and the material binds it by identity even
        // though the source still embeds it.
        await Assert.That(project.FileSystem.ReadAllBytes(project.Layout.Assets / "models/crate_0.png")).IsEquivalentTo(s_png, CollectionOrdering.Matching);
        var png = SidecarMeta.Load(project.FileSystem, project.Layout.Assets / "models/crate_0.png.meta").Guid;
        var wood = MaterialDocument.Load(project.FileSystem, project.Layout.Assets / "models/crate.wood.material");
        await Assert.That(MaterialDocument.References(wood).Single().Reference.Guid).IsEqualTo(png);
        await Assert.That(project.FileSystem.ReadAllText(project.Layout.Assets / "models/crate.prefab")).Contains("models/crate.mesh");

        await Assert.That(project.FileSystem.ReadAllBytes(project.Blend)).IsEquivalentTo(s_blend, CollectionOrdering.Matching);
        await Assert.That(ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();

        var again = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
    }

    [Test]
    public async Task an_edited_material_of_a_blend_stands_until_the_source_changes_it()
    {
        using var project = new Project();
        project.Seed(Sha256(s_blend));
        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var material = project.Layout.Assets / "models/crate.wood.material";
        project.FileSystem.WriteAllText(material, "Shader = \"toon\"\n" + project.FileSystem.ReadAllText(material).Replace("MetallicFactor = 0.0", "MetallicFactor = 0.5"));

        var kept = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        // Nothing goes back into the .blend, and the edit is settled rather than re-raised.
        await Assert.That(kept.Errors).IsEmpty();
        await Assert.That(project.FileSystem.ReadAllBytes(project.Blend)).IsEquivalentTo(s_blend, CollectionOrdering.Matching);
        await Assert.That(MaterialDocument.Load(project.FileSystem, material).Value("MetallicFactor")).IsEqualTo(0.5);
        var settled = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(settled.Errors).IsEmpty();
        await Assert.That(settled.Written).IsEmpty();

        // The author re-saves the .blend with a new material: the source's values win, and the
        // field glTF cannot express survives.
        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.Seed(Sha256(resaved), metallic: 0.25);

        var reExtracted = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(reExtracted.Errors).IsEmpty();
        var merged = MaterialDocument.Load(project.FileSystem, material);
        await Assert.That(merged.Value("MetallicFactor")).IsEqualTo(0.25);
        await Assert.That(merged.Value("Shader")).IsEqualTo("toon");
    }

    [Test]
    public async Task a_stale_conversion_without_blender_names_the_setting_that_finds_it()
    {
        using var project = new Project();
        project.Seed(Sha256([.. s_blend, 1]));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("models/crate.blend");
        await Assert.That(result.Errors.Single()).Contains(BlenderModelConverter.BlenderPathEnvironmentVariable);
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.mesh")).IsFalse();

        project.FileSystem.DeleteFile(ModelSource.ConvertedPath(project.Layout, project.Blend));
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    [Test]
    public async Task a_changed_or_missing_dependency_makes_the_conversion_stale()
    {
        using var project = new Project();
        var mtl = project.Layout.Assets / "models/crate.mtl";
        var png = project.Layout.Assets / "textures/wood.png";
        project.FileSystem.CreateDirectory(png.GetDirectory());
        project.FileSystem.WriteAllText(mtl, "newmtl wood\nmap_Kd ../textures/wood.png\n");
        project.FileSystem.WriteAllBytes(png, s_png);
        project.Seed(Sha256(s_blend), dependencies: [new("crate.mtl", Sha256(project.FileSystem.ReadAllBytes(mtl))), new("../textures/wood.png", Sha256(s_png))]);

        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();

        // A re-saved texture is a new input the stored GLB was not made from; with no Blender to
        // remake it, the read fails rather than serving the old pixels.
        project.FileSystem.WriteAllBytes(png, [.. s_png, 9]);
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);

        project.FileSystem.WriteAllBytes(png, s_png);
        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend)).IsNotEmpty();

        project.FileSystem.DeleteFile(mtl);
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    [Test]
    public async Task a_failure_caused_by_a_file_the_import_read_is_retried_once_that_file_changes()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        var mtl = project.Layout.Assets / "models/crate.mtl";
        project.FileSystem.WriteAllText(mtl, "broken\n");
        // Lists what it read before failing, as the conversion script does.
        project.UseBlender(CrateGlb(0.0), """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "Blender 4.4.0"; exit 0; fi
            here=$(dirname "$0")
            while [ "$1" != "--" ]; do shift; done
            shift
            echo run >> "$here/runs"
            echo '["crate.mtl"]' > "$3"
            if grep -q broken "$(dirname "$1")/crate.mtl"; then echo "crate.mtl is malformed" >&2; exit 1; fi
            cp "$here/export.glb" "$2"
            """);

        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining("crate.mtl is malformed");
        // Remembered while nothing it read changed: the next document naming the source does not run Blender again.
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining("crate.mtl is malformed");
        await Assert.That(project.BlenderRuns).IsEqualTo(1);

        project.FileSystem.WriteAllText(mtl, "newmtl wood\n");

        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend)).IsNotEmpty();
        await Assert.That(project.BlenderRuns).IsEqualTo(2);
    }

    [Test]
    public async Task an_unreadable_converted_glb_is_no_conversion_at_all()
    {
        using var project = new Project();
        // A directory where the GLB belongs: reading it is denied, not missing.
        project.FileSystem.CreateDirectory(ModelSource.ConvertedPath(project.Layout, project.Blend));

        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    [Test]
    public async Task a_blend_with_asset_collections_extracts_one_model_per_asset()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.5));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        var source = new Paradise.Authoring.AssetReference(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta").Guid, "models/crate.blend");
        foreach (var asset in new[] { s_short, s_tall })
        {
            // Each asset is a model of its own, its new files named by its collection: its
            // documents, its materials and its prefab seed.
            var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Layout.Assets / $"models/{asset.Name}.mesh");
            await Assert.That(mesh.Source).IsEqualTo(source);
            await Assert.That(mesh.Asset).IsEqualTo(asset);
            await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / $"models/{asset.Name}.wood.material")).IsTrue();
            await Assert.That(project.FileSystem.ReadAllText(project.Layout.Assets / $"models/{asset.Name}.prefab")).Contains($"models/{asset.Name}.mesh");
        }

        // The file as a whole is no model: nothing is extracted for it, and reading it says why.
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.mesh")).IsFalse();
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.prefab")).IsFalse();
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining($"holds asset collections {s_short}, {s_tall}");

        // The record keys each model's parts by its asset's GUID.
        var parts = ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts;
        await Assert.That(string.Join(", ", parts.Where(part => part.Kind == ExtractKind.Meshes).Select(part => $"{part.Asset}: {part.Reference.Path}")))
            .IsEqualTo($"{s_short.Guid}: models/Lamp_Short.mesh, {s_tall.Guid}: models/Lamp_Tall.mesh");
        await Assert.That(parts.Where(part => part.Kind == ExtractKind.Materials).Select(part => part.Asset!.Value)).IsEquivalentTo([s_short.Guid, s_tall.Guid], CollectionOrdering.Matching);
        await Assert.That(ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();

        var again = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
    }

    [Test]
    public async Task a_renamed_asset_collection_keeps_every_document_and_updates_its_name_hint()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var before = project.Documents();

        // Renamed in Blender: the collection keeps the GUID it carries, so the conversion stamps
        // the same asset under its new name.
        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        var big = s_tall with { Name = "Lamp_Big" };
        project.SeedAssets(Sha256(resaved), (s_short, 0.0), (big, 0.0));

        // Until something re-extracts, the documents still resolve by GUID; only the hint is behind.
        var tall = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var findings = ProjectVerifier.Verify(project.FileSystem, project.Layout);
        await Assert.That(findings.Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
        await Assert.That(findings.Single(f => f.Path == tall).Message).Contains("the name hint says 'Lamp_Tall'").And.Contains("'Lamp_Big'");

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(result.Written.Select(file => file.ToString())).Contains("models/Lamp_Tall.mesh (updated: asset collection 'Lamp_Tall' is named 'Lamp_Big' now)");
        await Assert.That(project.Documents()).IsEquivalentTo(before);
        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, tall).Asset).IsEqualTo(big);
        await Assert.That(project.FileSystem.EnumerateFiles(project.Layout.Assets / "models").Any(path => path.GetName().StartsWith("Lamp_Big", StringComparison.Ordinal))).IsFalse();
        await Assert.That(ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Path == tall)).IsEmpty();

        // verify --fix catches the hint up just as it catches up a path.
        byte[] again = [.. s_blend, 2];
        project.FileSystem.WriteAllBytes(project.Blend, again);
        project.SeedAssets(Sha256(again), (s_short, 0.0), (s_tall with { Name = "Lamp_Huge" }, 0.0));
        var repaired = ReferenceRepair.Fix(project.FileSystem, project.Layout);
        await Assert.That(repaired.Single(document => document.Path == tall).Repointed).IsEquivalentTo(["asset 'Lamp_Big' -> 'Lamp_Huge'"]);
        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, tall).Asset!.Name).IsEqualTo("Lamp_Huge");
        await Assert.That(project.Documents()).IsEquivalentTo(before);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_removed_asset_collection_removes_its_tool_owned_documents(bool referencesOnly)
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var tall = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var surviving = project.Layout.Assets / "models/Lamp_Short.mesh";
        var identity = SidecarMeta.Load(project.FileSystem, surviving + ".meta").Guid;
        var authored = new[] { "Lamp_Tall.prefab", "Lamp_Tall.wood.material", "Lamp_Tall_0.png" }
            .ToDictionary(name => project.Layout.Assets / "models" / name, path => project.FileSystem.ReadAllBytes(project.Layout.Assets / "models" / path));

        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved), (s_short, 0.0));

        var result = referencesOnly
            ? AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend)
            : AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(project.FileSystem.FileExists(tall)).IsFalse();
        await Assert.That(project.FileSystem.FileExists(tall + ".meta")).IsFalse();
        await Assert.That(SidecarMeta.Load(project.FileSystem, surviving + ".meta").Guid).IsEqualTo(identity);
        await Assert.That(ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts.Any(part => part.Asset == s_tall.Guid)).IsFalse();
        foreach (var (path, bytes) in authored)
        {
            await Assert.That(project.FileSystem.ReadAllBytes(path)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        }

        // Authored placements still name the removed identity; deleting a model must not rewrite them.
        var findings = ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error).ToList();
        await Assert.That(findings.All(f => f.Path == project.Layout.Assets / "models/Lamp_Tall.prefab")).IsTrue();
        await Assert.That(findings.Any(f => f.Path == project.Layout.Assets / "models/Lamp_Tall.prefab")).IsTrue();
    }

    [Test]
    public async Task removed_models_are_pruned_even_after_their_record_was_forgotten()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var oldPath = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var moved = project.Layout.Assets / "models/moved.mesh";
        var document = MeshReferenceDocument.Load(project.FileSystem, oldPath);
        project.FileSystem.MoveFile(oldPath, moved);
        project.FileSystem.MoveFile(oldPath + ".meta", moved + ".meta");

        var skeleton = project.Layout.Assets / "models/removed.skeleton";
        var clip = project.Layout.Assets / "models/removed.anim";
        var skinned = project.Layout.Assets / "models/removed.skinnedmesh";
        var maintainer = new SidecarMaintainer(project.FileSystem, project.Layout);
        project.FileSystem.WriteAllBytes(skeleton, new MeshReferenceDocument(document.Source, MeshSlot.Skeleton, Asset: s_tall).WriteBytes());
        maintainer.Ensure(skeleton);
        var skeletonReference = new Paradise.Authoring.AssetReference(SidecarMeta.Load(project.FileSystem, skeleton + ".meta").Guid, "models/removed.skeleton");
        project.FileSystem.WriteAllBytes(clip, new MeshReferenceDocument(document.Source, MeshSlot.Clip, "Idle", Asset: s_tall).WriteBytes());
        project.FileSystem.WriteAllBytes(skinned, new MeshReferenceDocument(document.Source, MeshSlot.SkinnedMesh, Skeleton: skeletonReference, Asset: s_tall).WriteBytes());
        maintainer.Ensure(clip);
        maintainer.Ensure(skinned);

        // A stale path now belongs to a different source; only the document's GUIDs prove ownership.
        var foreign = document with { Source = document.Source with { Guid = Guid.NewGuid() } };
        project.FileSystem.WriteAllBytes(oldPath, foreign.WriteBytes());
        maintainer.Ensure(oldPath);
        var malformed = project.Layout.Assets / "models/unreadable.mesh";
        project.FileSystem.WriteAllText(malformed, "mid-edit");
        maintainer.Ensure(malformed);
        var meta = SidecarMeta.Load(project.FileSystem, project.Blend + ".meta");
        var record = ExtractionRecord.Read(meta);
        ExtractionRecord.Write(meta, GlbImportSettings.GlbImporterName, record with { Parts = [.. record.Parts.Where(part => part.Asset != s_tall.Guid)] });
        meta.Save(project.FileSystem, project.Blend + ".meta");

        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved), (s_short, 0.0));
        var result = AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        foreach (var path in new[] { moved, skeleton, clip, skinned })
        {
            await Assert.That(project.FileSystem.FileExists(path)).IsFalse();
            await Assert.That(project.FileSystem.FileExists(path + ".meta")).IsFalse();
        }

        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, oldPath)).IsEqualTo(foreign);
        await Assert.That(project.FileSystem.ReadAllText(malformed)).IsEqualTo("mid-edit");
    }

    [Test]
    public async Task replacing_a_collection_with_the_same_name_creates_a_new_mesh_identity()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_tall, 0.0));
        await Assert.That(AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var mesh = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var previous = SidecarMeta.Load(project.FileSystem, mesh + ".meta").Guid;
        var replacement = s_tall with { Guid = Guid.NewGuid() };
        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved), (replacement, 0.0));

        var result = AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, mesh).Asset).IsEqualTo(replacement);
        await Assert.That(SidecarMeta.Load(project.FileSystem, mesh + ".meta").Guid).IsNotEqualTo(previous);
        await Assert.That(ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts.Select(part => part.Asset!.Value))
            .IsEquivalentTo([replacement.Guid]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task the_watcher_removes_the_last_collections_documents_when_the_blend_is_empty(bool startup)
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        var now = DateTimeOffset.UtcNow;
        var maintainer = new SidecarMaintainer(project.FileSystem, project.Layout);
        using var watcher = new AssetWatcher(project.FileSystem, project.Layout, maintainer, now: () => now);
        watcher.MintReferences();

        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved));
        project.Seed(Sha256(resaved), glb: new GlbTestBuilder().Build());
        if (startup)
        {
            watcher.MintReferences();
        }
        else
        {
            watcher.ObserveRename(project.Blend + "@", project.Blend);
            now += AssetWatcher.Debounce;
            watcher.Drain();
        }

        foreach (var asset in new[] { s_short, s_tall })
        {
            var path = project.Layout.Assets / $"models/{asset.Name}.mesh";
            await Assert.That(project.FileSystem.FileExists(path)).IsFalse();
            await Assert.That(project.FileSystem.FileExists(path + ".meta")).IsFalse();
            watcher.ObserveDelete(path);
            watcher.ObserveDelete(path + ".meta");
        }

        now += AssetWatcher.Debounce;
        watcher.Drain();
        await Assert.That(watcher.MintReferences()).IsEqualTo(0);
        await Assert.That(ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts).IsEmpty();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, encoder: null).Errors).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task the_watcher_keeps_moved_mesh_identity_when_an_asset_collection_temporarily_becomes_empty(bool startup)
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        var now = DateTimeOffset.UtcNow;
        var maintainer = new SidecarMaintainer(project.FileSystem, project.Layout);
        using var watcher = new AssetWatcher(project.FileSystem, project.Layout, maintainer, now: () => now);
        watcher.MintReferences();
        var original = project.Layout.Assets / "models/Lamp_Short.mesh";
        var moved = project.Layout.Assets / "models/relocated.mesh";
        var removed = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var identity = SidecarMeta.Load(project.FileSystem, original + ".meta").Guid;
        project.FileSystem.MoveFile(original, moved);
        project.FileSystem.MoveFile(original + ".meta", moved + ".meta");
        watcher.MintReferences();

        // One collection is removed; the other keeps its GUID but temporarily has no geometry.
        byte[] empty = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, empty);
        project.SeedAssets(Sha256(empty), (s_short, 0.0));
        project.FileSystem.WriteAllBytes(ModelSource.ConvertedPath(project.Layout, project.Blend, s_short.Guid),
            BlenderModelConverter.Stamp(new GlbTestBuilder().Build(),
                new BlenderModelConverter.SourceStamp(Sha256(empty), BlenderModelConverter.ConverterVersion, "Blender 0.0.0", [], s_short, [s_short])));
        if (startup)
        {
            watcher.MintReferences();
        }
        else
        {
            watcher.Observe(project.Blend);
            now += AssetWatcher.Debounce;
            watcher.Drain();
        }

        await Assert.That(project.FileSystem.FileExists(removed)).IsFalse();
        await Assert.That(project.FileSystem.FileExists(removed + ".meta")).IsFalse();
        await Assert.That(GlbImportSettings.ReadExtraction(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta"), s_short.Guid).Mesh?.Guid).IsEqualTo(identity);

        byte[] restored = [.. s_blend, 2];
        project.FileSystem.WriteAllBytes(project.Blend, restored);
        project.SeedAssets(Sha256(restored), (s_short, 0.0));
        watcher.Observe(project.Blend);
        now += AssetWatcher.Debounce;
        watcher.Drain();

        await Assert.That(project.FileSystem.FileExists(original)).IsFalse();
        await Assert.That(SidecarMeta.Load(project.FileSystem, moved + ".meta").Guid).IsEqualTo(identity);
        await Assert.That(GlbImportSettings.ReadExtraction(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta"), s_short.Guid).Mesh?.Path).IsEqualTo("models/relocated.mesh");
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, encoder: null).Errors).IsEmpty();
    }

    [Test]
    public async Task cleanup_keeps_unreadable_documents_without_blocking_other_removals()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var unreadable = project.Layout.Assets / "models/Lamp_Short.mesh";
        var removed = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var document = project.FileSystem.ReadAllBytes(unreadable);
        var sidecar = project.FileSystem.ReadAllBytes(unreadable + ".meta");
        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved));
        project.Seed(Sha256(resaved), glb: new GlbTestBuilder().Build());
        using var fileSystem = new UnreadableDocumentFileSystem(project.FileSystem, unreadable);

        var result = AssetExtractor.MintReferences(fileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(project.FileSystem.FileExists(removed)).IsFalse();
        await Assert.That(project.FileSystem.FileExists(removed + ".meta")).IsFalse();
        await Assert.That(project.FileSystem.ReadAllBytes(unreadable)).IsEquivalentTo(document, CollectionOrdering.Matching);
        await Assert.That(project.FileSystem.ReadAllBytes(unreadable + ".meta")).IsEquivalentTo(sidecar, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_failed_conversion_preserves_every_extracted_document()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var before = project.Documents();
        var record = project.FileSystem.ReadAllBytes(project.Blend + ".meta");
        project.FileSystem.WriteAllBytes(project.Blend, [.. s_blend, 1]);

        var result = AssetExtractor.MintReferences(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(project.Documents()).IsEquivalentTo(before);
        await Assert.That(project.FileSystem.ReadAllBytes(project.Blend + ".meta")).IsEquivalentTo(record, CollectionOrdering.Matching);
    }

    [Test]
    public async Task one_conversion_stores_every_asset_by_guid_and_drops_the_whole_file_glb_it_replaces()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        project.Seed(Sha256([.. s_blend, 9]));
        var a = new ModelAsset(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"), "Lamp_A");
        var b = new ModelAsset(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"), "Lamp_B");
        project.UseBlender(CrateGlb(0.0), $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "Blender 4.4.0"; exit 0; fi
            here=$(dirname "$0")
            while [ "$1" != "--" ]; do shift; done
            shift
            echo run >> "$here/runs"
            echo '[]' > "$3"
            cp "$here/export.glb" "$4/{{b.Guid}}.glb"
            cp "$here/export.glb" "$4/{{a.Guid}}.glb"
            echo '[{"guid": "{{b.Guid}}", "name": "Lamp_B"}, {"guid": "{{a.Guid}}", "name": "Lamp_A"}]' > "$5"
            """);

        await Assert.That(ModelSource.Assets(project.FileSystem, project.Blend)).IsEquivalentTo([a, b], CollectionOrdering.Matching);
        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend, asset: b.Guid)).IsNotEmpty();
        var unknown = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend, asset: unknown)).Throws<InvalidDataException>().WithMessageContaining($"has no asset collection with guid {unknown}");
        await Assert.That(project.BlenderRuns).IsEqualTo(1);

        // Each asset's GLB is named by its GUID and stamped as one of the file's assets; the stale whole-file GLB is gone.
        await Assert.That(project.FileSystem.FileExists(ModelSource.ConvertedPath(project.Layout, project.Blend))).IsFalse();
        foreach (var asset in new[] { a, b })
        {
            var converted = ModelSource.ConvertedPath(project.Layout, project.Blend, asset.Guid);
            await Assert.That(converted.GetName()).IsEqualTo($"{asset.Guid}.glb");
            var stored = project.FileSystem.ReadAllBytes(converted);
            await Assert.That(BlenderModelConverter.IsCurrent(stored, Sha256(s_blend), "Blender 4.4.0", _ => null, asset.Guid)).IsTrue();
            await Assert.That(BlenderModelConverter.StampedAssets(stored)).IsEquivalentTo([a, b], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task the_assets_of_a_blend_are_current_only_while_every_one_of_their_glbs_is()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        await Assert.That(ModelSource.Assets(project.FileSystem, project.Blend)).IsEquivalentTo([s_short, s_tall], CollectionOrdering.Matching);

        // A conversion stopped between two of its writes, or a GLB deleted since: the current GLB
        // left cannot vouch for the others, so the source converts again, which here needs Blender.
        project.FileSystem.DeleteFile(ModelSource.ConvertedPath(project.Layout, project.Blend, s_tall.Guid));
        await Assert.That(() => ModelSource.Assets(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);

        project.SeedAssets(Sha256(s_blend), (s_short, 0.0), (s_tall, 0.0));
        project.FileSystem.WriteAllBytes(ModelSource.ConvertedPath(project.Layout, project.Blend, s_short.Guid), BlenderModelConverter.Stamp(
            CrateGlb(0.0), new BlenderModelConverter.SourceStamp(Sha256([.. s_blend, 1]), BlenderModelConverter.ConverterVersion, "Blender 0.0.0", [], s_short, [s_short, s_tall])));
        await Assert.That(() => ModelSource.Assets(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    private sealed class UnreadableDocumentFileSystem(IFileSystem fallback, UPath unreadable) : ComposeFileSystem(fallback, owned: false)
    {
        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (path == unreadable && (access & FileAccess.Read) != 0) throw new IOException("The document is being edited.");
            return base.OpenFileImpl(path, mode, access, share);
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A crate: one embedded PNG sampled by its one material.</summary>
    private static byte[] CrateGlb(double metallic)
    {
        var b = new GlbTestBuilder();
        var image = b.AddImage(s_png, "image/png");
        var texture = b.AddTexture(source: image);
        b.AddMaterial(new JsonObject { ["name"] = "wood", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = texture }, ["metallicFactor"] = metallic } });
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position, material: 0)), name: "Crate");
        b.SetSceneRoots(node);
        return b.Build();
    }

    /// <summary>A project on disk, the only kind whose conversions persist, with Blender out of reach for its lifetime unless <see cref="UseBlender"/> stands one in.</summary>
    private sealed class Project : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"paradise_model_{Guid.NewGuid():N}");
        private readonly string? _blender = Environment.GetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable);

        public Project()
        {
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, Path.Combine(_root, "no-blender"));
            Directory.CreateDirectory(Path.Combine(_root, "assets", "models"));
            Directory.CreateDirectory(Path.Combine(_root, ".editor"));
            File.WriteAllText(Path.Combine(_root, "assets", "project.toml"), "name = \"game\"\nschema_version = 1\n");
            File.WriteAllText(Path.Combine(_root, ".editor", "authoring-schema.json"), Schema);

            Layout = new AssetProjectLayout(FileSystem.ConvertPathFromInternal(_root));
            Blend = Layout.Assets / "models/crate.blend";
            FileSystem.WriteAllBytes(Blend, s_blend);
            Mint(Layout.Manifest);
            Mint(Blend);
        }

        public PhysicalFileSystem FileSystem { get; } = new();

        public AssetProjectLayout Layout { get; }

        public UPath Blend { get; }

        /// <summary>A conversion as Blender would have left it, stamped as made from a source with <paramref name="sourceSha256"/> and the <paramref name="dependencies"/> it read.</summary>
        public void Seed(string sourceSha256, double metallic = 0.0, BlenderModelConverter.Dependency[]? dependencies = null, byte[]? glb = null)
        {
            var converted = ModelSource.ConvertedPath(Layout, Blend);
            FileSystem.CreateDirectory(converted.GetDirectory());
            FileSystem.WriteAllBytes(converted, BlenderModelConverter.Stamp(
                glb ?? CrateGlb(metallic), new BlenderModelConverter.SourceStamp(sourceSha256, BlenderModelConverter.ConverterVersion, "Blender 0.0.0", dependencies ?? [])));
        }

        /// <summary>A conversion of a <c>.blend</c> with asset collections as Blender would have left it: one crate GLB per asset, and nothing else.</summary>
        public void SeedAssets(string sourceSha256, params (ModelAsset Asset, double Metallic)[] assets)
        {
            var directory = ModelSource.ConvertedDirectory(Layout, Blend);
            if (FileSystem.DirectoryExists(directory)) FileSystem.DeleteDirectory(directory, isRecursive: true);
            FileSystem.CreateDirectory(directory);
            ModelAsset[] all = [.. assets.Select(asset => asset.Asset)];
            foreach (var (asset, metallic) in assets)
            {
                FileSystem.WriteAllBytes(ModelSource.ConvertedPath(Layout, Blend, asset.Guid), BlenderModelConverter.Stamp(
                    CrateGlb(metallic), new BlenderModelConverter.SourceStamp(sourceSha256, BlenderModelConverter.ConverterVersion, "Blender 0.0.0", [], asset, all)));
            }
        }

        /// <summary>Every document under <c>assets/models</c> with the identity its sidecar carries.</summary>
        public Dictionary<string, Guid> Documents()
            => FileSystem.EnumerateFiles(Layout.Assets / "models")
                .Where(path => !SidecarMeta.IsSidecarPath(path) && path.GetExtensionWithDot() != ".blend" && FileSystem.FileExists(SidecarMeta.PathFor(path)))
                .ToDictionary(path => path.GetName(), path => SidecarMeta.Load(FileSystem, SidecarMeta.PathFor(path)).Guid, StringComparer.Ordinal);

        /// <summary>Runs <paramref name="script"/> as Blender for the project's lifetime, beside the <paramref name="export"/> it may copy out and the <c>runs</c> file it counts itself in.</summary>
        public void UseBlender(byte[] export, string script)
        {
            var blender = Path.Combine(_root, "blender");
            File.WriteAllText(blender, script + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(blender, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(Path.Combine(_root, "export.glb"), export);
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, blender);
        }

        public int BlenderRuns => File.Exists(Path.Combine(_root, "runs")) ? File.ReadAllLines(Path.Combine(_root, "runs")).Length : 0;

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
