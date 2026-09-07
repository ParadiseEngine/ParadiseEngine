using System.Buffers;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

/// <summary>Compute passes and tracked buffers: the rules a probe-tracing feature rests on. A
/// compute pass has no attachments, so everything the graph knows about it comes from its bind
/// groups and declared writes — and those must feed culling, ordering and lowering exactly as
/// attachments do for raster passes.</summary>
public class FrameGraphComputeTests
{
    private static readonly PassRecorder Nothing = static (object _, ref PassRecording _, int _) => { };

    private static PassRecorder DispatchOf(uint groups) =>
        (object _, ref PassRecording pass, int _) => pass.Encoder.Dispatch(new DispatchCommand(groups, 1, 1));

    private static readonly PassRecorder BindGroupZero =
        static (object _, ref PassRecording pass, int _) => pass.SetBindGroup(0);

    private static readonly BindGroupLayoutDesc SomeLayout = new(0, []);

    private static (FrameGraph Graph, GraphTextureRegistry Textures, FakeBindGroupFactory Groups) BindingGraph()
    {
        var textures = new GraphTextureRegistry(new FakeTextureFactory());
        var groups = new FakeBindGroupFactory();
        return (new FrameGraph(textures, new BindGroupCache(groups)), textures, groups);
    }

    private static TextureDesc Storage() => new(
        null, 16, 16, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
        TextureUsage.StorageBinding | TextureUsage.TextureBinding);

    private static TextureDesc Color() => new(
        null, 16, 16, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
        TextureUsage.RenderAttachment | TextureUsage.TextureBinding);

    /// <summary>The lowering: a compute pass opens and closes a compute scope and occupies no
    /// entry in the pass table, so the raster passes around it keep contiguous BeginPass indices.</summary>
    [Test]
    public async Task a_compute_pass_lowers_to_a_compute_scope_and_takes_no_pass_table_slot()
    {
        var graph = new FrameGraph();
        var target = graph.ImportColor(new TextureViewHandle(7, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddRasterPass("shadow", RenderPassEvent.Shadows)
            .Color(0, target, LoadOp.Clear, StoreOp.Store).Record(graph, Nothing);
        graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination)
            .NeverCull().Record(graph, DispatchOf(12));
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Load, StoreOp.Store).Record(graph, Nothing);

        var stream = graph.Compile(writer);
        var passCount = stream.Passes.Length;
        var kinds = Kinds(stream);
        var dispatch = stream.Commands.Span[3].Dispatch.WorkgroupCountX;
        var lastBegin = stream.Commands.Span[5].BeginPass.PassIndex;

        await Assert.That(passCount).IsEqualTo(2);
        await Assert.That(kinds).IsEquivalentTo(new[]
        {
            RenderCommandKind.BeginPass, RenderCommandKind.EndPass,
            RenderCommandKind.BeginComputePass, RenderCommandKind.Dispatch, RenderCommandKind.EndComputePass,
            RenderCommandKind.BeginPass, RenderCommandKind.EndPass,
        });
        await Assert.That(dispatch).IsEqualTo(12u);
        await Assert.That(lastBegin).IsEqualTo(1);
    }

    /// <summary>A compute pass has no attachments; declaring one is a composition mistake the graph
    /// names rather than a pass that renders nowhere.</summary>
    [Test]
    public async Task a_compute_pass_refuses_attachments()
    {
        var graph = new FrameGraph();
        var target = graph.ImportColor(new TextureViewHandle(7, 1));
        var pass = graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination);

        await Assert.That(() => pass.Color(0, target, LoadOp.Clear)).Throws<InvalidOperationException>()
            .WithMessageContaining("trace");
        await Assert.That(() => pass.Depth(target, LoadOp.Clear)).Throws<InvalidOperationException>()
            .WithMessageContaining("trace");
    }

    /// <summary>The switch-off rule extends to buffers: a trace whose only output is a private hit
    /// buffer runs only while something binds that buffer for reading.</summary>
    [Test]
    public async Task a_compute_pass_filling_a_private_buffer_nobody_reads_is_culled()
    {
        var (graph, textures, _) = BindingGraph();
        textures.Ensure("out", Color());
        textures.Export("out");
        var hits = graph.ImportBuffer(new BufferHandle(3, 1), GraphResourceScope.GraphOnly);
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "g", SomeLayout, [GraphBinding.Buffer(0, hits, 0, 256, write: true)])
            .Record(graph, BindGroupZero);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, graph.Texture("out"), LoadOp.Clear).Record(graph, Nothing);

        var stream = graph.Compile(writer);
        var kinds = Kinds(stream);

        await Assert.That(graph.CulledPassCount).IsEqualTo(1);
        await Assert.That(kinds).IsEquivalentTo(new[] { RenderCommandKind.BeginPass, RenderCommandKind.EndPass });
    }

    /// <summary>...and a read binding of the same buffer revives it, with the group resolved to
    /// the buffer's handle and the window the declaration carried.</summary>
    [Test]
    public async Task binding_a_tracked_buffer_for_reading_keeps_its_producer_and_resolves_the_handle()
    {
        var (graph, textures, groups) = BindingGraph();
        textures.Ensure("out", Color());
        textures.Export("out");
        var handle = new BufferHandle(3, 1);
        var hits = graph.ImportBuffer(handle, GraphResourceScope.GraphOnly);
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "write", SomeLayout, [GraphBinding.Buffer(0, hits, 0, 256, write: true)])
            .Record(graph, BindGroupZero);
        graph.AddComputePass("blend", RenderPassEvent.GlobalIllumination, offset: 1)
            .BindGroup(0, "read", SomeLayout, [GraphBinding.Buffer(2, hits, 64, 128)])
            .NeverCull()
            .Record(graph, BindGroupZero);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, graph.Texture("out"), LoadOp.Clear).Record(graph, Nothing);

        graph.Compile(writer);

        await Assert.That(graph.CulledPassCount).IsEqualTo(0);
        var read = groups.Groups.Values.Single(g => g.Name == "read").Entries.Span[0];
        await Assert.That(read.Buffer).IsEqualTo(handle);
        await Assert.That(read.Binding).IsEqualTo(2u);
        await Assert.That(read.Offset).IsEqualTo(64ul);
        await Assert.That(read.Size).IsEqualTo(128ul);
    }

    /// <summary>A storage-texture binding is a WRITE: it makes the compute pass the producer a
    /// sampling raster pass depends on, and it resolves to the texture's own view.</summary>
    [Test]
    public async Task a_storage_texture_binding_makes_the_compute_pass_the_producer()
    {
        var (graph, textures, groups) = BindingGraph();
        textures.Ensure("atlas", Storage());
        textures.Ensure("out", Color());
        textures.Export("out");
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("blend", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "write", SomeLayout, [GraphBinding.StorageTexture(0, graph.Texture("atlas"))])
            .Record(graph, BindGroupZero);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, graph.Texture("out"), LoadOp.Clear)
            .BindGroup(0, "read", SomeLayout, [GraphBinding.Texture(0, graph.Texture("atlas"))])
            .Record(graph, BindGroupZero);

        graph.Compile(writer);

        await Assert.That(graph.CulledPassCount).IsEqualTo(0);
        var written = groups.Groups.Values.Single(g => g.Name == "write").Entries.Span[0];
        await Assert.That(written.Kind).IsEqualTo(BindGroupEntryKind.TextureView);
        await Assert.That(written.View).IsEqualTo(textures.View("atlas"));
    }

    /// <summary>Without the read, the storage write is dead and the pass goes with it — the same
    /// rule as an attachment nobody samples.</summary>
    [Test]
    public async Task a_storage_texture_nobody_samples_culls_its_writer()
    {
        var (graph, textures, _) = BindingGraph();
        textures.Ensure("atlas", Storage());
        textures.Ensure("out", Color());
        textures.Export("out");
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("blend", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "write", SomeLayout, [GraphBinding.StorageTexture(0, graph.Texture("atlas"))])
            .Record(graph, BindGroupZero);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, graph.Texture("out"), LoadOp.Clear).Record(graph, Nothing);

        graph.Compile(writer);
        await Assert.That(graph.CulledPassCount).IsEqualTo(1);
    }

    /// <summary>Writing an external buffer is observable — a host reads it back — so the pass is
    /// never culled, the same as writing an exported texture.</summary>
    [Test]
    public async Task writing_an_external_buffer_is_never_culled()
    {
        var graph = new FrameGraph();
        var readback = graph.ImportBuffer(new BufferHandle(9, 1));
        var target = graph.ImportColor(new TextureViewHandle(7, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("reduce", RenderPassEvent.AfterComposite)
            .Writes(readback).Record(graph, DispatchOf(1));
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(graph, Nothing);

        graph.Compile(writer);
        await Assert.That(graph.CulledPassCount).IsEqualTo(0);
    }

    /// <summary>Ordering is checked for buffers as for owned textures: a consumer placed before its
    /// producer would read last frame's hits, and only the event key put it there.</summary>
    [Test]
    public async Task reading_a_buffer_a_later_pass_writes_is_an_error_naming_both()
    {
        var graph = new FrameGraph();
        var hits = graph.ImportBuffer(new BufferHandle(3, 1), GraphResourceScope.GraphOnly);
        var target = graph.ImportColor(new TextureViewHandle(7, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("blend", RenderPassEvent.Shadows)
            .Reads(hits).NeverCull().Record(graph, Nothing);
        graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination)
            .Writes(hits).Record(graph, Nothing);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(graph, Nothing);

        await Assert.That(() => graph.Compile(writer)).Throws<InvalidOperationException>()
            .WithMessageContaining("blend").And.WithMessageContaining("trace");
    }

    /// <summary>A pass that reads and writes one buffer in the same dispatch is a hazard every
    /// backend would let through silently; the graph names it instead.</summary>
    [Test]
    public async Task reading_and_writing_the_same_buffer_in_one_pass_is_refused()
    {
        var graph = new FrameGraph();
        var hits = graph.ImportBuffer(new BufferHandle(3, 1));
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("trace", RenderPassEvent.GlobalIllumination)
            .Reads(hits).Writes(hits).Record(graph, Nothing);

        await Assert.That(() => graph.Compile(writer)).Throws<InvalidOperationException>()
            .WithMessageContaining("trace");
    }

    /// <summary>A history read of a storage texture keeps the previous frame's writer alive and is
    /// not an ordering error — the ping-pong a probe blend uses to read the old atlas while writing
    /// the new one.</summary>
    [Test]
    public async Task a_history_read_of_a_storage_texture_is_allowed_before_its_writer()
    {
        var (graph, textures, _) = BindingGraph();
        textures.Ensure("atlasA", Storage());
        textures.Ensure("atlasB", Storage());
        textures.Ensure("out", Color());
        textures.Export("out");
        var writer = new ArrayBufferWriter<RenderCommand>(64);

        graph.AddComputePass("blend", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "g", SomeLayout,
            [
                GraphBinding.Texture(0, graph.Texture("atlasA")),
                GraphBinding.StorageTexture(1, graph.Texture("atlasB")),
            ])
            .ReadsHistory(graph.Texture("atlasB"))
            .Record(graph, BindGroupZero);
        graph.AddRasterPass("main", RenderPassEvent.Opaque)
            .Color(0, graph.Texture("out"), LoadOp.Clear)
            .Reads(graph.Texture("atlasB"))
            .Record(graph, Nothing);

        graph.Compile(writer);
        await Assert.That(graph.CulledPassCount).IsEqualTo(0);
    }

    private static RenderCommandKind[] Kinds(in RenderCommandStream stream)
    {
        var kinds = new List<RenderCommandKind>();
        foreach (var cmd in stream.Commands.Span) kinds.Add(cmd.Kind);
        return [.. kinds];
    }
}
