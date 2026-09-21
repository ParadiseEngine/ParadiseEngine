namespace Paradise.Rendering.Pbr.Test;

public sealed class TextureCachePortabilityTests
{
    [Test]
    public async Task texture_cache_does_not_depend_on_platform_cryptography()
    {
        // A clear-color NativeAOT sample never exercised the SHA256 call which aborted on
        // Bionic without OpenSSL. This is a renderer boundary, not an integrity-check policy.
        var dependencies = typeof(MaterialResourceCache).Assembly.GetReferencedAssemblies();
        await Assert.That(dependencies.Any(name =>
            name.Name?.StartsWith("System.Security.Cryptography", StringComparison.Ordinal) == true)).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(16)]
    [Arguments(129)]
    [Arguments(241)]
    [Arguments(4096)]
    public async Task texture_identity_uses_complete_content_and_does_not_retain_source_memory(int length)
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        // Invalid KTX2 exercises the real identity/cache path and managed fallback without
        // requiring a native decoder or GPU on the host running this portability regression.
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
        var description = new PbrMaterialDesc { Name = "portable texture key" };
        var first = materials.AddMaterial(description, new PbrMaterialTextures { BaseColor = bytes });
        var padded = new byte[length + 24];
        bytes.CopyTo(padded, 11);
        var duplicate = materials.AddMaterial(description,
            new PbrMaterialTextures { BaseColor = padded.AsMemory(11, length) });
        await Assert.That(materials.TextureCount).IsEqualTo(1);

        bytes[^1] ^= 0x80;
        var changed = materials.AddMaterial(description, new PbrMaterialTextures { BaseColor = bytes });
        await Assert.That(materials.TextureCount).IsEqualTo(2);
        materials.ReleaseMaterial(first);
        await Assert.That(materials.TextureCount).IsEqualTo(2);
        materials.ReleaseMaterial(duplicate);
        await Assert.That(materials.TextureCount).IsEqualTo(1);
        materials.ReleaseMaterial(changed);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Count).IsEqualTo(2);
    }
}
