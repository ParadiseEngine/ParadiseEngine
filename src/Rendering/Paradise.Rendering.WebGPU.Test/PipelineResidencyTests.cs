using System.Runtime.CompilerServices;

namespace Paradise.Rendering.WebGPU.Test;

public class PipelineResidencyTests
{
    [Test]
    public async Task identical_live_programs_share_native_pipelines_and_last_release_evicts_dependencies()
    {
        using var renderer = Backend();
        if (renderer is null) return;
        var program = Program("bindings");
        var first = renderer.CreatePipeline(program, renderer.ColorFormat);
        var second = renderer.CreatePipeline(program, renderer.ColorFormat);
        var device = renderer.DeviceForTest;
        await Assert.That(ReferenceEquals(device.ResolvePipeline(first), device.ResolvePipeline(second))).IsTrue();
        await Assert.That(renderer.PipelineCacheCountForTest).IsEqualTo(1);
        await Assert.That(device.ShaderCacheCount).IsEqualTo(2);
        await Assert.That(device.BindGroupLayoutCacheCount).IsEqualTo(2);
        await Assert.That(renderer.ShaderSlotCountForTest).IsEqualTo(0);

        renderer.DestroyPipeline(first);
        renderer.DestroyPipeline(first);
        await Assert.That(renderer.PipelineCacheCountForTest).IsEqualTo(1);
        await Assert.That(device.ShaderCacheCount).IsEqualTo(2);
        await Assert.That(() => device.ResolvePipeline(first)).Throws<StaleHandleException>();
        await Assert.That(device.ResolvePipeline(second)).IsNotNull();
        renderer.DestroyPipeline(second);
        await Assert.That(renderer.PipelineCacheCountForTest).IsEqualTo(0);
        await Assert.That(device.ShaderCacheCount).IsEqualTo(0);
        await Assert.That(device.BindGroupLayoutCacheCount).IsEqualTo(0);
    }

    [Test]
    public async Task bind_group_keeps_its_layout_after_render_pipeline_release()
    {
        using var renderer = Backend();
        if (renderer is null) return;
        var program = Program("bindings");
        var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat);
        var layout = program.Layout.Groups.Single(static group => group.GroupIndex == 0);
        var bytes = Math.Max(16UL, layout.Entries[0].MinBufferSize);
        var buffer = renderer.CreateBuffer(new BufferDesc("uniforms", bytes, BufferUsage.Uniform));
        var group = renderer.CreateBindGroup(new BindGroupDesc("shared layout", layout,
            new[] { BindGroupEntryDesc.ForBuffer(0, buffer, 0, bytes) }));
        renderer.DestroyPipeline(pipeline);
        await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(1);

        var recreated = renderer.CreatePipeline(program, renderer.ColorFormat);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(2);
        renderer.DestroyBindGroup(group);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(2);
        renderer.DestroyPipeline(recreated);
        renderer.DestroyBuffer(buffer);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(0);
    }

    [Test]
    public async Task compute_pipelines_and_public_shaders_retain_only_their_live_dependencies()
    {
        using var renderer = Backend();
        if (renderer is null) return;
        var program = Program("compute");
        var module = program.Modules.Single(static module => module.Stage == ShaderStage.Compute);
        var shader = renderer.CreateShader(module);
        var first = renderer.CreateComputePipeline(program);
        var second = renderer.CreateComputePipeline(program);
        await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(1);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(1);
        renderer.DestroyComputePipeline(first);
        renderer.DestroyComputePipeline(second);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(1);
        renderer.DestroyShader(shader);
        renderer.DestroyShader(shader);
        await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.ComputePipelines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task unique_program_reloads_do_not_accumulate_cache_entries_or_native_wrappers()
    {
        using var renderer = Backend();
        if (renderer is null) return;
        var original = Program("bindings");
        for (var i = 0; i < 4; i++)
        {
            var program = original with
            {
                Modules = original.Modules.Select(module => module with { Wgsl = module.Wgsl + $"\n// reload {i}\n" }).ToArray(),
            };
            var references = CreateAndRetire(renderer, program);
            await Assert.That(renderer.PipelineCacheCountForTest).IsEqualTo(0);
            await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(0);
            await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(0);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            foreach (var reference in references)
                await Assert.That(reference.IsAlive).IsFalse();
        }
        GC.KeepAlive(renderer);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task failed_pipeline_layout_creation_releases_partially_acquired_dependencies(bool compute)
    {
        using var renderer = Backend();
        if (renderer is null) return;
        var original = Program(compute ? "compute" : "bindings");
        var first = original.Layout.Groups[0];
        var broken = original with
        {
            Layout = new PipelineLayoutDesc(
                [first, new BindGroupLayoutDesc(first.GroupIndex + 1,
                    [new BindGroupLayoutEntryDesc(0, ShaderStage.Compute, BindingResourceType.StorageTexture)])], []),
        };
        if (compute)
            await Assert.That(() => renderer.CreateComputePipeline(broken)).Throws<InvalidOperationException>();
        else
            await Assert.That(() => renderer.CreatePipeline(broken, renderer.ColorFormat)).Throws<InvalidOperationException>();
        await Assert.That(renderer.ShaderSlotCountForTest).IsEqualTo(0);
        await Assert.That(renderer.PipelineCacheCountForTest).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.ShaderCacheCount).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.BindGroupLayoutCacheCount).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateAndRetire(WebGpuRenderer renderer, ShaderProgramDesc program)
    {
        var vertex = program.Modules.First(static module => module.Stage == ShaderStage.Vertex);
        var fragment = program.Modules.First(static module => module.Stage == ShaderStage.Fragment);
        var vs = renderer.CreateShader(vertex);
        var fs = renderer.CreateShader(fragment);
        var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat);
        WeakReference[] references =
        [
            new(renderer.DeviceForTest.ResolveShader(vs)),
            new(renderer.DeviceForTest.ResolveShader(fs)),
            new(renderer.DeviceForTest.ResolvePipeline(pipeline)),
        ];
        renderer.DestroyShader(vs);
        renderer.DestroyShader(fs);
        renderer.DestroyPipeline(pipeline);
        return references;
    }

    private static ShaderProgramDesc Program(string name) =>
        ShaderProgramLoader.Load(typeof(PipelineResidencyTests).Assembly, $"Shaders.{name}");

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"WebGPU unavailable: {error.Message}");
            return null;
        }
    }
}
