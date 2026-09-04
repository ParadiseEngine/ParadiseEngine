using Paradise.Authoring;

namespace Paradise.Assets.Documents.Test;

/// <summary>A material is a config whose texture slots are references: read as such, rewritten as such, and baked to the paths the runtime reads.</summary>
public class MaterialDocumentTests
{
    private const string Guid = "11111111-2222-4333-8444-555555555555";

    private const string Sample = """
        Name = "grass"
        MetallicFactor = 1.0
        BaseColorTexture = { guid = "11111111-2222-4333-8444-555555555555", path = "textures/grass.png" }
        NormalTexture = {}

        [BaseColorFactor]
        r = 0.5
        g = 1.0
        b = 0.25
        a = 1.0

        """;

    [Test]
    public async Task texture_slots_read_as_references_and_an_empty_slot_is_none()
    {
        var material = MaterialDocument.Parse(Sample, "grass.material");

        var references = MaterialDocument.References(material).ToList();

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].Key).IsEqualTo("BaseColorTexture");
        await Assert.That(references[0].Reference).IsEqualTo(new AssetReference(System.Guid.Parse(Guid), "textures/grass.png"));
    }

    [Test]
    public async Task a_texture_slot_that_is_not_a_reference_is_refused_by_name()
    {
        var error = await Assert.That(() => MaterialDocument.Parse("BaseColorTexture = \"textures/grass.png\"\n", "grass.material")).Throws<FormatException>();

        await Assert.That(error!.Message).Contains("BaseColorTexture");
        await Assert.That(error.Message).Contains("{ guid, path }");
    }

    [Test]
    public async Task rewrite_follows_a_slot_and_leaves_every_other_field_byte_for_byte()
    {
        var material = MaterialDocument.Parse(Sample, "grass.material");

        var updated = MaterialDocument.Rewrite(material, reference => reference with { Path = "textures/ground/grass.png" });

        await Assert.That(updated).IsNotNull();
        var text = CanonicalTomlWriter.WriteString(updated!);
        await Assert.That(text).Contains("path = \"textures/ground/grass.png\"");
        await Assert.That(text).Contains("MetallicFactor = 1.0");
        await Assert.That(text).Contains("[BaseColorFactor]");
        await Assert.That(MaterialDocument.Rewrite(material, reference => reference)).IsNull();
    }

    [Test]
    public async Task bake_replaces_each_slot_with_its_built_path_and_drops_an_empty_one()
    {
        var material = MaterialDocument.Parse(Sample, "grass.material");

        var baked = MaterialDocument.Bake(material, reference => "textures/grass.ktx2");

        await Assert.That(baked.Value("BaseColorTexture")).IsEqualTo("textures/grass.ktx2");
        await Assert.That(baked.ContainsKey("NormalTexture")).IsFalse();
        await Assert.That(baked.Value("Name")).IsEqualTo("grass");
        await Assert.That(baked.Value("BaseColorFactor")).IsNotNull();
    }
}
