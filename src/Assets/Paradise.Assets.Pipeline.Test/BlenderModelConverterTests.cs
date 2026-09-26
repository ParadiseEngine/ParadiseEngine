using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

// The stamp logic is what decides whether a converted GLB is reused; it needs no Blender.
public class BlenderModelConverterTests
{
    private static readonly BlenderModelConverter.SourceStamp s_stamp = new(
        "abc123", BlenderModelConverter.ConverterVersion, "Blender 4.2.0",
        [new("textures/wood.png", "aa11"), new("crate.mtl", "bb22")]);

    private static readonly Dictionary<string, string> s_onDisk = new(StringComparer.Ordinal) { ["textures/wood.png"] = "aa11", ["crate.mtl"] = "BB22" };

    private static string? OnDisk(string path) => s_onDisk.GetValueOrDefault(path);

    [Test]
    public async Task stamp_round_trips_and_gates_reuse()
    {
        var glb = Glb();
        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", "Blender 4.2.0", OnDisk)).IsFalse();

        var stamped = BlenderModelConverter.Stamp(glb, s_stamp);
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", "Blender 4.2.0", OnDisk)).IsTrue();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "ABC123", "Blender 4.2.0", OnDisk)).IsTrue();

        // A new source or a new exporter makes it stale; with no Blender to ask, the one made is the best there is.
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "other", "Blender 4.2.0", OnDisk)).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", "Blender 4.3.0", OnDisk)).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", null, OnDisk)).IsTrue();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "other", null, OnDisk)).IsFalse();

        // The stamp is additive: the rest of the GLB survives, and the dependencies are the addon's contract.
        await Assert.That(GlbBinary.TryRead(stamped, out var gltf, out var bin)).IsTrue();
        await Assert.That((string?)gltf["asset"]!["version"]).IsEqualTo("2.0");
        var extras = gltf["asset"]!["extras"]!;
        await Assert.That((string?)extras["keep"]).IsEqualTo("me");
        await Assert.That(extras["paradiseDependencies"]!.ToJsonString())
            .IsEqualTo("""[{"path":"crate.mtl","sha256":"bb22"},{"path":"textures/wood.png","sha256":"aa11"}]""");
        await Assert.That(bin[0]).IsEqualTo((byte)7);
    }

    [Test]
    public async Task a_changed_or_missing_dependency_makes_it_stale_even_without_blender()
    {
        var stamped = BlenderModelConverter.Stamp(Glb(), s_stamp);

        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", null, path => path == "crate.mtl" ? "cc33" : OnDisk(path))).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", null, path => path == "textures/wood.png" ? null : OnDisk(path))).IsFalse();
    }

    [Test]
    public async Task a_glb_from_another_converter_version_is_stale_even_without_blender()
    {
        var older = BlenderModelConverter.Stamp(Glb(), s_stamp with { ConverterVersion = BlenderModelConverter.ConverterVersion - 1 });

        await Assert.That(BlenderModelConverter.IsCurrent(older, "abc123", "Blender 4.2.0", OnDisk)).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(older, "abc123", null, OnDisk)).IsFalse();
    }

    [Test]
    public async Task a_glb_stamped_without_a_blender_version_is_stale_when_blender_is_there()
    {
        GlbBinary.TryRead(Glb(), out var gltf, out var bin);
        gltf["asset"]!["extras"] = new JsonObject
        {
            ["paradiseSourceSha256"] = "abc123",
            ["paradiseConverterVersion"] = BlenderModelConverter.ConverterVersion,
            ["paradiseDependencies"] = new JsonArray(),
        };
        var glb = GlbBinary.Write(gltf, bin);

        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", "Blender 4.2.0", OnDisk)).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", null, OnDisk)).IsTrue();
    }

    [Test]
    public async Task a_corrupt_glb_is_never_current_and_cannot_be_stamped()
    {
        byte[] corrupt = "not a glb"u8.ToArray();

        await Assert.That(BlenderModelConverter.IsCurrent(corrupt, "abc123", null, OnDisk)).IsFalse();
        await Assert.That(() => BlenderModelConverter.Stamp(corrupt, s_stamp)).Throws<InvalidDataException>();
    }

    private static byte[] Glb()
        => GlbBinary.Write(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0", ["extras"] = new JsonObject { ["keep"] = "me" } } }, [7, 8, 9, 10]);
}
