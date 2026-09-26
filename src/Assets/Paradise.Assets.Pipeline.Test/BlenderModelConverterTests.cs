using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

// The stamp logic is what decides whether a converted GLB is reused; it needs no Blender.
public class BlenderModelConverterTests
{
    private static readonly BlenderModelConverter.SourceStamp s_stamp = new("abc123", BlenderModelConverter.ConverterVersion, "Blender 4.2.0");

    [Test]
    public async Task stamp_round_trips_and_gates_reuse()
    {
        var glb = Glb();
        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", "Blender 4.2.0")).IsFalse();

        var stamped = BlenderModelConverter.Stamp(glb, s_stamp);
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", "Blender 4.2.0")).IsTrue();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "ABC123", "Blender 4.2.0")).IsTrue();

        // A new source or a new exporter makes it stale; with no Blender to ask, the one made is the best there is.
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "other", "Blender 4.2.0")).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", "Blender 4.3.0")).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "abc123", null)).IsTrue();
        await Assert.That(BlenderModelConverter.IsCurrent(stamped, "other", null)).IsFalse();

        // The stamp is additive: the rest of the GLB survives.
        await Assert.That(GlbBinary.TryRead(stamped, out var gltf, out var bin)).IsTrue();
        await Assert.That((string?)gltf["asset"]!["version"]).IsEqualTo("2.0");
        await Assert.That((string?)gltf["asset"]!["extras"]!["keep"]).IsEqualTo("me");
        await Assert.That(bin[0]).IsEqualTo((byte)7);
    }

    [Test]
    public async Task a_glb_from_another_converter_version_is_stale_even_without_blender()
    {
        var older = BlenderModelConverter.Stamp(Glb(), s_stamp with { ConverterVersion = BlenderModelConverter.ConverterVersion - 1 });

        await Assert.That(BlenderModelConverter.IsCurrent(older, "abc123", "Blender 4.2.0")).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(older, "abc123", null)).IsFalse();
    }

    [Test]
    public async Task a_glb_stamped_without_a_blender_version_is_stale_when_blender_is_there()
    {
        GlbBinary.TryRead(Glb(), out var gltf, out var bin);
        gltf["asset"]!["extras"] = new JsonObject { ["paradiseSourceSha256"] = "abc123", ["paradiseConverterVersion"] = BlenderModelConverter.ConverterVersion };
        var glb = GlbBinary.Write(gltf, bin);

        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", "Blender 4.2.0")).IsFalse();
        await Assert.That(BlenderModelConverter.IsCurrent(glb, "abc123", null)).IsTrue();
    }

    [Test]
    public async Task a_corrupt_glb_is_never_current_and_cannot_be_stamped()
    {
        byte[] corrupt = "not a glb"u8.ToArray();

        await Assert.That(BlenderModelConverter.IsCurrent(corrupt, "abc123", null)).IsFalse();
        await Assert.That(() => BlenderModelConverter.Stamp(corrupt, s_stamp)).Throws<InvalidDataException>();
    }

    private static byte[] Glb()
        => GlbBinary.Write(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0", ["extras"] = new JsonObject { ["keep"] = "me" } } }, [7, 8, 9, 10]);
}
