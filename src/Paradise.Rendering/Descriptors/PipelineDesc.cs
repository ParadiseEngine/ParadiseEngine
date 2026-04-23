using System;

namespace Paradise.Rendering;

/// <summary>Render pipeline descriptor. Names <see cref="ShaderHandle"/>s for vertex and fragment
/// stages and pulls vertex layout + (optional) bind group layout from Slang-reflection-shaped
/// records in <see cref="ShaderProgramDesc"/> so the contract never has to be hand-coded.</summary>
/// <remarks>This is the cache key used by the WebGPU backend's pipeline cache; the
/// <see cref="ContentHash"/> helper hashes everything except <see cref="Name"/>. Two descriptors
/// with the same content (regardless of label) are guaranteed to hash equal.</remarks>
public readonly struct PipelineDesc : IEquatable<PipelineDesc>
{
    public string? Name { get; init; }
    public ShaderHandle VertexShader { get; init; }
    public string VertexEntryPoint { get; init; }
    public ShaderHandle FragmentShader { get; init; }
    public string FragmentEntryPoint { get; init; }
    public ReadOnlyMemory<VertexBufferLayoutDesc> VertexLayouts { get; init; }
    public PrimitiveTopology Topology { get; init; }
    public IndexFormat StripIndexFormat { get; init; }
    public TextureFormat ColorFormat { get; init; }
    public TextureFormat? DepthStencilFormat { get; init; }
    public PipelineLayoutDesc? Layout { get; init; }

    public bool Equals(PipelineDesc other)
    {
        if (!VertexShader.Equals(other.VertexShader)) return false;
        if (!string.Equals(VertexEntryPoint, other.VertexEntryPoint, StringComparison.Ordinal)) return false;
        if (!FragmentShader.Equals(other.FragmentShader)) return false;
        if (!string.Equals(FragmentEntryPoint, other.FragmentEntryPoint, StringComparison.Ordinal)) return false;
        if (Topology != other.Topology) return false;
        if (StripIndexFormat != other.StripIndexFormat) return false;
        if (ColorFormat != other.ColorFormat) return false;
        if (DepthStencilFormat != other.DepthStencilFormat) return false;
        if (!ReferenceEquals(Layout, other.Layout)) return false;
        return VertexLayoutsContentEquals(VertexLayouts.Span, other.VertexLayouts.Span);
    }

    public override bool Equals(object? obj) => obj is PipelineDesc d && Equals(d);

    /// <summary>Stable content hash excluding <see cref="Name"/>. Equal-by-content descriptors hash
    /// to the same value; name labels are debug aids only and never participate in cache identity.</summary>
    public int ContentHash()
    {
        var h = new HashCode();
        h.Add(VertexShader);
        h.Add(VertexEntryPoint, StringComparer.Ordinal);
        h.Add(FragmentShader);
        h.Add(FragmentEntryPoint, StringComparer.Ordinal);
        h.Add(Topology);
        h.Add(StripIndexFormat);
        h.Add(ColorFormat);
        h.Add(DepthStencilFormat);
        // Reference identity for Layout is intentional: two distinct PipelineLayoutDesc instances
        // produce distinct WebGPU layouts even when structurally equal, because the backend caches
        // BindGroupLayout objects per source record reference.
        h.Add(Layout);
        var vls = VertexLayouts.Span;
        h.Add(vls.Length);
        for (var i = 0; i < vls.Length; i++)
            HashVertexBufferLayout(ref h, vls[i]);
        return h.ToHashCode();
    }

    // Records' synthesized GetHashCode/Equals on array-typed properties uses reference identity,
    // so we walk the per-attribute fields explicitly. Two layouts with the same stride / step mode
    // / attribute content hash equally even when their backing arrays are distinct allocations.
    private static void HashVertexBufferLayout(ref HashCode h, VertexBufferLayoutDesc layout)
    {
        h.Add(layout.Stride);
        h.Add(layout.StepMode);
        var attrs = layout.Attributes;
        h.Add(attrs.Length);
        for (var i = 0; i < attrs.Length; i++)
        {
            h.Add(attrs[i].ShaderLocation);
            h.Add(attrs[i].Format);
            h.Add(attrs[i].Offset);
        }
    }

    private static bool VertexLayoutsContentEquals(ReadOnlySpan<VertexBufferLayoutDesc> a, ReadOnlySpan<VertexBufferLayoutDesc> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Stride != y.Stride) return false;
            if (x.StepMode != y.StepMode) return false;
            if (x.Attributes.Length != y.Attributes.Length) return false;
            for (var k = 0; k < x.Attributes.Length; k++)
            {
                if (x.Attributes[k].ShaderLocation != y.Attributes[k].ShaderLocation) return false;
                if (x.Attributes[k].Format != y.Attributes[k].Format) return false;
                if (x.Attributes[k].Offset != y.Attributes[k].Offset) return false;
            }
        }
        return true;
    }

    public override int GetHashCode() => ContentHash();

    public static bool operator ==(PipelineDesc a, PipelineDesc b) => a.Equals(b);
    public static bool operator !=(PipelineDesc a, PipelineDesc b) => !a.Equals(b);
}
