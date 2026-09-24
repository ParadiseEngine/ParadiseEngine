using System.IO;

namespace Paradise.Assets.Gltf.Test;

public class GlbContainerTests
{
    [Test]
    public async Task valid_glb_parses_json_and_bin_chunks()
    {
        var builder = GlbTestBuilder.FullQuad(out _);
        var glb = builder.Build();

        var container = GlbContainer.Parse(glb);
        await Assert.That(container.Json.Length).IsGreaterThan(0);
        await Assert.That(container.Bin.Length).IsGreaterThan(0);
        // JSON chunk must start with '{' (space-padded at the END per spec).
        await Assert.That((char)container.Json.Span[0]).IsEqualTo('{');
    }

    [Test]
    public async Task bad_magic_throws()
    {
        var glb = GlbTestBuilder.FullQuad(out _).Build(badMagic: true);
        await Assert.That(() => GlbContainer.Parse(glb)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task unsupported_version_throws()
    {
        var glb = GlbTestBuilder.FullQuad(out _).Build(version: 1);
        await Assert.That(() => GlbContainer.Parse(glb)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task truncated_glb_throws()
    {
        var glb = GlbTestBuilder.FullQuad(out _).Build(truncateBy: 16);
        await Assert.That(() => GlbContainer.Parse(glb)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task missing_json_chunk_throws()
    {
        var glb = GlbTestBuilder.FullQuad(out _).Build(omitJsonChunk: true);
        await Assert.That(() => GlbContainer.Parse(glb)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task tiny_input_throws()
    {
        await Assert.That(() => GlbContainer.Parse(new byte[4])).Throws<InvalidDataException>();
    }
}
