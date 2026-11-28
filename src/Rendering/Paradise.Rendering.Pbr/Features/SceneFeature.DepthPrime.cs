using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Depth priming: the opaque bucket's depth, drawn in a depth-only pass before the main
/// pass shades anything.</summary>
/// <remarks>Without it an immediate-mode GPU shades every opaque layer a pixel's draws cover until
/// the nearest one is drawn. With it a shading fragment behind the nearest opaque surface fails
/// the depth test before it is shaded, so each visible opaque pixel is shaded once. The primed
/// depth sits a few ulps behind the surface (Shaders/Common/depthPrime.slang), so the shading
/// draws' own Less test against what they write still decides every pixel, equal-depth ties
/// included: the image is the one an unprimed pass draws.</remarks>
public sealed partial class SceneFeature
{
    /// <summary>Declares the priming pass ahead of the main pass; false where nothing is primed.</summary>
    private bool DeclareDepthPrime(FrameGraph graph, GraphTexture depth, GraphTexture shadows)
    {
        if (!_ctx.Scene.Visibility.DepthPrimingEnabled || _ctx.Opaque.Count == 0) return false;
        var prime = graph.AddRasterPass("Main.DepthPrime", RenderPassEvent.Opaque, -1)
            .Depth(depth, LoadOp.Clear, clear: 1f);
        if (_occlusion.Active) prime.Reads(_occlusion.Arguments);
        // Group 1 carries the joint palette skinned transforms read.
        DeclareFrameGroup(prime, shadows);
        prime.Record(this, RecordDepthPrime);
        return true;
    }

    private static void RecordDepthPrime(SceneFeature self, ref PassRecording pass, int _) => self.EncodeDepthPrime(ref pass);

    /// <summary>The opaque bucket's occluders, batched and drawn exactly as the main pass draws
    /// them, through the stock transform.</summary>
    /// <remarks>A custom program is primed only when it promises the stock geometry and coverage
    /// (<see cref="MaterialProgramOptions.OpaqueCoverage"/>); a displaced or discarding one is left
    /// to its own draw.</remarks>
    private void EncodeDepthPrime(ref PassRecording pass)
    {
        var ctx = _ctx;
        var opaque = ctx.Opaque;
        var materials = ctx.Materials;
        ref var encoder = ref pass.Encoder;
        pass.SetBindGroup(1);
        var activePipeline = default(PipelineHandle);
        for (var first = 0; first < opaque.Count;)
        {
            var primitive = opaque[first].Primitive;
            var visible = _frustum.OpaqueVisible(first);
            var indirect = _occlusion.Active;
            var batch = _instancing.Batch(opaque: true, first);
            var count = !visible || indirect ? 1 : batch.Count;
            var slot = first;
            first += count;
            if (!visible || !materials.IsOccluder(primitive.MaterialId)) continue;

            var pipeline = ctx.Programs.GetDepthPrime(count > 1, primitive.Skinned, materials.CullOf(primitive.MaterialId));
            if (activePipeline != pipeline)
            {
                encoder.SetPipeline(pipeline);
                activePipeline = pipeline;
            }
            if (count > 1) encoder.SetBindGroup(0, _instancing.Group);
            else encoder.SetBindGroup(0, ctx.DrawGroup, dynamicOffset: (uint)(slot * ctx.DrawStride));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            if (indirect)
                encoder.DrawIndexedIndirect(new DrawIndexedIndirectCommand(_occlusion.IndirectBuffer, (ulong)slot * _occlusion.Stride));
            else
                encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, (uint)count, 0, 0, count > 1 ? (uint)slot : 0));
        }
    }
}
