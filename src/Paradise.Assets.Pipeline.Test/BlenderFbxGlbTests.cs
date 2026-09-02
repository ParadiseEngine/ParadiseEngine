using System.IO;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

// The stamp logic is what decides whether a GLB is re-converted; it needs no Blender.
public class BlenderFbxGlbTests
{
    [Test]
    public async Task stamp_round_trips_and_gates_the_skip()
    {
        string glb = TempGlb();
        try
        {
            var stamp = new BlenderFbxGlb.SourceStamp("abc123", "Blender 4.2.0");
            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, stamp)).IsFalse();

            await Assert.That(BlenderFbxGlb.WriteSourceStamp(glb, stamp, null)).IsTrue();
            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, stamp)).IsTrue();
            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, stamp with { FbxSha256 = "ABC123" })).IsTrue();

            // Either half changing means the GLB is stale: a new FBX, or a new exporter.
            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, stamp with { FbxSha256 = "other" })).IsFalse();
            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, stamp with { BlenderVersion = "Blender 4.3.0" })).IsFalse();

            // The stamp is additive: the rest of the GLB survives.
            await Assert.That(GlbBinary.TryRead(glb, out JsonObject gltf, out byte[] bin)).IsTrue();
            await Assert.That((string?)gltf["asset"]!["version"]).IsEqualTo("2.0");
            await Assert.That(bin[0]).IsEqualTo((byte)7);
        }
        finally
        {
            File.Delete(glb);
        }
    }

    [Test]
    public async Task a_glb_stamped_without_a_blender_version_is_stale()
    {
        string glb = TempGlb();
        try
        {
            GlbBinary.TryRead(glb, out JsonObject gltf, out byte[] bin);
            gltf["asset"]!["extras"] = new JsonObject { ["paradiseSourceFbxSha256"] = "abc123" };
            GlbBinary.Write(glb, gltf, bin);

            await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(glb, new BlenderFbxGlb.SourceStamp("abc123", "Blender 4.2.0"))).IsFalse();
        }
        finally
        {
            File.Delete(glb);
        }
    }

    [Test]
    public async Task missing_or_corrupt_glb_never_matches_and_cannot_be_stamped()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"paradise_absent_{Guid.NewGuid():N}.glb");
        await Assert.That(BlenderFbxGlb.GeneratedGlbMatchesStamp(missing, new BlenderFbxGlb.SourceStamp("x", "y"))).IsFalse();

        string corrupt = Path.Combine(Path.GetTempPath(), $"paradise_corrupt_{Guid.NewGuid():N}.glb");
        File.WriteAllText(corrupt, "not a glb");
        try
        {
            string? reported = null;
            await Assert.That(BlenderFbxGlb.WriteSourceStamp(corrupt, new BlenderFbxGlb.SourceStamp("x", "y"), message => reported = message)).IsFalse();
            await Assert.That(reported).IsNotNull();
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    private static string TempGlb()
    {
        string path = Path.Combine(Path.GetTempPath(), $"paradise_stamp_{Guid.NewGuid():N}.glb");
        GlbBinary.Write(path, new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } }, new byte[] { 7, 8, 9, 10 });
        return path;
    }
}
