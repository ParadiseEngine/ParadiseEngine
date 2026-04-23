namespace Paradise.Rendering.Test;

public class HandleTests
{
    [Test]
    public async Task buffer_handle_default_is_invalid()
    {
        var h = default(BufferHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(BufferHandle.Invalid);
    }

    [Test]
    public async Task texture_handle_default_is_invalid()
    {
        var h = default(TextureHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(TextureHandle.Invalid);
    }

    [Test]
    public async Task sampler_handle_default_is_invalid()
    {
        var h = default(SamplerHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(SamplerHandle.Invalid);
    }

    [Test]
    public async Task pipeline_handle_default_is_invalid()
    {
        var h = default(PipelineHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(PipelineHandle.Invalid);
    }

    [Test]
    public async Task shader_handle_default_is_invalid()
    {
        var h = default(ShaderHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(ShaderHandle.Invalid);
    }

    [Test]
    public async Task render_view_handle_default_is_invalid()
    {
        var h = default(RenderViewHandle);
        await Assert.That(h.IsValid).IsFalse();
        await Assert.That(h).IsEqualTo(RenderViewHandle.Invalid);
    }

    [Test]
    public async Task buffer_handle_with_nonzero_generation_is_valid()
    {
        var h = new BufferHandle(7, 1);
        await Assert.That(h.IsValid).IsTrue();
        await Assert.That(h.Index).IsEqualTo(7u);
        await Assert.That(h.Generation).IsEqualTo(1u);
    }

    [Test]
    public async Task handles_with_same_index_and_generation_are_equal()
    {
        var a = new BufferHandle(3, 2);
        var b = new BufferHandle(3, 2);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != b).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task handles_with_different_index_or_generation_are_unequal()
    {
        var a = new BufferHandle(1, 2);
        var b = new BufferHandle(1, 3);
        var c = new BufferHandle(2, 2);
        await Assert.That(a == b).IsFalse();
        await Assert.That(a == c).IsFalse();
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task handles_of_different_kinds_have_independent_default_invalid()
    {
        await Assert.That(default(BufferHandle).IsValid).IsFalse();
        await Assert.That(default(TextureHandle).IsValid).IsFalse();
        await Assert.That(default(SamplerHandle).IsValid).IsFalse();
        await Assert.That(default(PipelineHandle).IsValid).IsFalse();
        await Assert.That(default(ShaderHandle).IsValid).IsFalse();
        await Assert.That(default(RenderViewHandle).IsValid).IsFalse();
    }
}
