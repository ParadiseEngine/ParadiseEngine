namespace Paradise.Rendering.Test;

public class ShaderConstantTests
{
    [Test]
    public async Task ValuesAndStagesParticipateInPipelineIdentity()
    {
        var original = new PipelineDesc { FragmentConstants = new ShaderConstant[] { new("19000", 256) } };
        var duplicate = original with { FragmentConstants = new ShaderConstant[] { new("19000", 256) } };
        var changed = original with { FragmentConstants = new ShaderConstant[] { new("19000", 511) } };
        var vertex = original with { FragmentConstants = default, VertexConstants = original.FragmentConstants };
        await Assert.That(original).IsEqualTo(duplicate);
        await Assert.That(original.ContentHash()).IsEqualTo(duplicate.ContentHash());
        await Assert.That(original).IsNotEqualTo(changed);
        await Assert.That(original).IsNotEqualTo(vertex);
    }

    [Test]
    public async Task InvalidConstantsAreRejectedBeforeSerialization()
    {
        await Assert.That(() => ShaderConstant.Validate(new ShaderConstant[] { new("", 0) })).Throws<ArgumentException>();
        await Assert.That(() => ShaderConstant.Validate(new ShaderConstant[] { new("a", double.NaN) })).Throws<ArgumentException>();
        await Assert.That(() => ShaderConstant.Validate(new ShaderConstant[] { new("a", double.PositiveInfinity) })).Throws<ArgumentException>();
        await Assert.That(() => ShaderConstant.Validate(new ShaderConstant[] { new("a", 0), new("a", 1) })).Throws<ArgumentException>();
        ShaderConstant.Validate(new ShaderConstant[] { new("19000", 0), new("other", 1) });
    }
}
