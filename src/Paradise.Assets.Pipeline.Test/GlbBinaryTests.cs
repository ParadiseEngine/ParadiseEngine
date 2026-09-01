using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The byte-based container pair, and its agreement with the path-based one it now backs.
/// </summary>
public class GlbBinaryTests
{
    [Test]
    public async Task a_document_and_its_binary_chunk_survive_the_round_trip()
    {
        var gltf = JsonNode.Parse("""{"asset":{"version":"2.0"},"meshes":[{"name":"crate"}]}""")!.AsObject();
        var bin = new byte[] { 9, 8, 7, 6, 5 };

        var read = GlbBinary.TryRead(GlbBinary.Write(gltf, bin), out var round, out var chunk);

        await Assert.That(read).IsTrue();
        await Assert.That(round["meshes"]![0]!["name"]!.GetValue<string>()).IsEqualTo("crate");
        // Padded to a 4-byte boundary on write, per the GLB spec — the payload is a prefix of it.
        await Assert.That(chunk.Take(bin.Length)).IsEquivalentTo(bin);
    }

    [Test]
    public async Task a_glb_with_no_binary_chunk_round_trips()
    {
        var gltf = JsonNode.Parse("""{"asset":{"version":"2.0"}}""")!.AsObject();

        var read = GlbBinary.TryRead(GlbBinary.Write(gltf, []), out var round, out var chunk);

        await Assert.That(read).IsTrue();
        await Assert.That(chunk.Length).IsEqualTo(0);
        await Assert.That(round["asset"]!["version"]!.GetValue<string>()).IsEqualTo("2.0");
    }

    [Test]
    public async Task the_path_overloads_produce_the_same_bytes_as_the_byte_overloads()
    {
        // The point of the refactor: one container implementation. If these two ever diverge, a
        // GLB written by the build would differ from one written by the addon-side tools.
        var gltf = JsonNode.Parse("""{"images":[{"uri":"t.ktx2"}]}""")!.AsObject();
        var bin = new byte[] { 1, 2, 3 };
        var path = Path.Combine(Path.GetTempPath(), $"glbbinary-{Guid.NewGuid():N}.glb");

        try
        {
            GlbBinary.Write(path, gltf, bin);

            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(GlbBinary.Write(gltf, bin));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task bytes_that_are_not_a_glb_are_refused_rather_than_thrown_on()
    {
        await Assert.That(GlbBinary.TryRead([1, 2, 3], out _, out _)).IsFalse();
        await Assert.That(GlbBinary.TryRead(System.Text.Encoding.UTF8.GetBytes("not a mesh at all"), out _, out _)).IsFalse();
    }
}
