using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The converter script run by a real Blender over <c>.blend</c> files whose asset collections carry
/// a <c>paradise_guid</c>, lack one, or share one: only the first converts, each asset to the GLB its
/// GUID names. Skipped where no Blender is installed.
/// </summary>
[NotInParallel]
public class BlendAssetConversionTests
{
    private const string A = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string B = "bbbbbbbb-0000-4000-8000-000000000002";

    /// <summary>Two asset collections, <c>Lamp_A</c> and <c>Lamp_B</c>, each holding a triangle; <c>argv</c> after <c>--</c> is the output path and each collection's GUID, <c>-</c> for none.</summary>
    private const string BuildScript = """
        import sys
        import bpy
        out, guid_a, guid_b = sys.argv[sys.argv.index('--') + 1:][:3]
        bpy.ops.wm.read_factory_settings(use_empty=True)
        for name, guid in (('Lamp_A', guid_a), ('Lamp_B', guid_b)):
            collection = bpy.data.collections.new(name)
            bpy.context.scene.collection.children.link(collection)
            mesh = bpy.data.meshes.new(name)
            mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
            collection.objects.link(bpy.data.objects.new(name, mesh))
            collection.asset_mark()
            if guid != '-':
                collection['paradise_guid'] = guid
        bpy.ops.wm.save_as_mainfile(filepath=out)
        """;

    [Test]
    public async Task each_asset_collection_exports_to_the_glb_its_guid_names()
    {
        using var blend = Blend(A, B);
        if (blend.Blender is null) return;

        var export = BlenderModelConverter.Convert(blend.Blender, blend.Path);

        await Assert.That(export.Failure).IsNull();
        await Assert.That(export.Models!.Select(model => model.Asset!)).IsEquivalentTo(
            [new ModelAsset(Guid.Parse(A), "Lamp_A"), new ModelAsset(Guid.Parse(B), "Lamp_B")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task an_asset_collection_without_a_guid_fails_the_file()
    {
        using var blend = Blend(A, "-");
        if (blend.Blender is null) return;

        var export = BlenderModelConverter.Convert(blend.Blender, blend.Path);

        await Assert.That(export.Models).IsNull();
        await Assert.That(export.Failure).Contains("asset collection 'Lamp_B' in lamps.blend has no Paradise GUID; save it once in Blender with the Paradise Assets addon enabled");
    }

    [Test]
    public async Task asset_collections_sharing_a_guid_fail_the_file_naming_both()
    {
        using var blend = Blend(A, A);
        if (blend.Blender is null) return;

        var export = BlenderModelConverter.Convert(blend.Blender, blend.Path);

        await Assert.That(export.Models).IsNull();
        await Assert.That(export.Failure).Contains($"asset collections 'Lamp_A' and 'Lamp_B' in lamps.blend share Paradise GUID {A}");
    }

    /// <summary>A <c>lamps.blend</c> built by the installed Blender, or none (the test skips) when there is no Blender.</summary>
    private static TemporaryBlend Blend(string guidA, string guidB)
    {
        var blender = BlenderModelConverter.FindBlender();
        if (blender is null)
        {
            Skip.Test("needs Blender");
            return new TemporaryBlend(null, "");
        }

        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"paradise_blend_assets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = System.IO.Path.Combine(directory, "build.py");
        var path = System.IO.Path.Combine(directory, "lamps.blend");
        File.WriteAllText(script, BuildScript);
        var run = ProcessTools.Run(blender, string.Join(' ', [
            "--background", "--factory-startup", "--python-exit-code", "1",
            "--python", ProcessTools.QuoteArgument(script), "--", ProcessTools.QuoteArgument(path), guidA, guidB,
        ]), timeoutMilliseconds: 120_000);
        if (!run.Succeeded) throw new InvalidOperationException(run.Describe("Blender building lamps.blend", 120_000));
        return new TemporaryBlend(blender, path);
    }

    private sealed record TemporaryBlend(string? Blender, string Path) : IDisposable
    {
        public void Dispose()
        {
            if (Blender is not null) Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
        }
    }
}
