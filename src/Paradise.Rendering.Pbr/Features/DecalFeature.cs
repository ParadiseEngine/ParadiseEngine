using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Projects ordered decal materials in the forward lighting path using one shared texture array.</summary>
public sealed class DecalFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly DecalGpu[] _decals = new DecalGpu[PbrDecals.MaxDecals];
    private readonly List<(PbrDecal Decal, int Index)> _ordered = [];
    private readonly List<PbrDecalMaterial> _materials = [];
    private readonly List<PbrDecalMaterial> _nextMaterials = [];
    private readonly Dictionary<PbrDecalMaterial, int> _materialSlots = new(ReferenceEqualityComparer.Instance);
    private TextureHandle _texture;
    private int _textureSize;

    internal DecalFeature(PbrContext ctx)
    {
        _ctx = ctx;
        UniformBuffer = ctx.Renderer.CreateBuffer(new BufferDesc("PbrDecalUniforms", 16, BufferUsage.Uniform | BufferUsage.CopyDst));
        DecalBuffer = ctx.Renderer.CreateBuffer(new BufferDesc("PbrDecals", DecalBufferBytes, BufferUsage.Storage | BufferUsage.CopyDst));
        Sampler = ctx.Renderer.CreateSampler(new SamplerDesc(
            "PbrDecalSampler", SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Linear));
        UploadCount(0);
        RebuildAtlas(1);
    }

    public FeatureDefinition Definition => PbrFeatures.Decals;
    public FrameRequirements Requires => FrameRequirements.None;
    /// <summary>Number of valid, enabled decal volumes in the current frame.</summary>
    public int ActiveDecalCount { get; private set; }
    /// <summary>Number of distinct materials in the resident array, useful for tracking upload and memory cost.</summary>
    public int ResidentMaterialCount => _materials.Count;
    public uint TextureWidth { get; private set; }
    public uint TextureHeight { get; private set; }
    internal BufferHandle UniformBuffer { get; }
    internal BufferHandle DecalBuffer { get; }
    internal static ulong DecalBufferBytes => (ulong)(PbrDecals.MaxDecals * Unsafe.SizeOf<DecalGpu>());
    internal TextureViewHandle TextureView { get; private set; }
    internal SamplerHandle Sampler { get; }

    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) UploadCount(0);
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Decals;
        _ordered.Clear();
        if (!settings.Enabled)
        {
            UploadCount(0);
            return;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.TextureSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.TextureSize, 512);
        if (!BitOperations.IsPow2((uint)settings.TextureSize))
            throw new ArgumentException("Decal texture size must be a power of two.");
        for (var i = 0; i < settings.Volumes.Count; i++)
        {
            var decal = settings.Volumes[i];
            if (decal.Enabled && DecalPacking.TryPack(decal, 0, out _)) _ordered.Add((decal, i));
        }
        if (_ordered.Count > PbrDecals.MaxDecals)
            throw new InvalidOperationException($"A frame supports at most {PbrDecals.MaxDecals} valid enabled decal volumes; found {_ordered.Count}.");
        _ordered.Sort(static (a, b) => a.Decal.Order != b.Decal.Order ? a.Decal.Order.CompareTo(b.Decal.Order) : a.Index.CompareTo(b.Index));
        _materialSlots.Clear();
        _nextMaterials.Clear();
        for (var i = 0; i < _ordered.Count; i++)
        {
            var decal = _ordered[i].Decal;
            if (!_materialSlots.TryGetValue(decal.Material, out var slot))
            {
                slot = _nextMaterials.Count;
                _nextMaterials.Add(decal.Material);
                _materialSlots.Add(decal.Material, slot);
            }
            DecalPacking.TryPack(decal, slot * DecalAtlas.LayersPerMaterial, out _decals[i]);
        }
        var changed = settings.TextureSize != _textureSize || _nextMaterials.Count != _materials.Count;
        for (var i = 0; !changed && i < _materials.Count; i++) changed = !ReferenceEquals(_nextMaterials[i], _materials[i]);
        if (changed) RebuildAtlas(settings.TextureSize);
        if (_ordered.Count > 0) _ctx.Renderer.UpdateBuffer<DecalGpu>(DecalBuffer, 0, _decals.AsSpan(0, _ordered.Count));
        UploadCount(_ordered.Count);
    }

    private void UploadCount(int count)
    {
        ActiveDecalCount = count;
        var settings = new Vector4(count, 0f, 0f, 0f);
        _ctx.Renderer.UpdateBuffer<Vector4>(UniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref settings, 1));
    }

    private void RebuildAtlas(int textureSize)
    {
        var atlas = DecalAtlas.Build(_nextMaterials, textureSize);
        var renderer = _ctx.Renderer;
        var texture = renderer.CreateTexture(new TextureDesc(
            "PbrDecalAtlas", (uint)atlas.Width, (uint)atlas.Height, (uint)atlas.Layers, (uint)atlas.Mips.Count, 1,
            TextureDimension.D2, TextureFormat.Rgba16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        TextureViewHandle view = default;
        try
        {
            var width = atlas.Width;
            var height = atlas.Height;
            for (var mip = 0; mip < atlas.Mips.Count; mip++)
            {
                renderer.WriteTexture(texture, (uint)mip, MemoryMarshal.AsBytes<Half>(atlas.Mips[mip]),
                    (uint)(width * 8), (uint)height, (uint)width, (uint)height, (uint)atlas.Layers);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            view = renderer.CreateTextureView(new TextureViewDesc("PbrDecalAtlasView", texture, TextureViewDimension.D2Array, 0, (uint)atlas.Layers));
        }
        catch
        {
            if (view.IsValid) renderer.DestroyTextureView(view);
            renderer.DestroyTexture(texture);
            throw;
        }
        if (TextureView.IsValid) renderer.DestroyTextureView(TextureView);
        if (_texture.IsValid) renderer.DestroyTexture(_texture);
        _texture = texture;
        TextureView = view;
        TextureWidth = (uint)atlas.Width;
        TextureHeight = (uint)atlas.Height;
        _materials.Clear();
        _materials.AddRange(_nextMaterials);
        _textureSize = textureSize;
    }

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyTextureView(TextureView);
        renderer.DestroyTexture(_texture);
        renderer.DestroySampler(Sampler);
        renderer.DestroyBuffer(DecalBuffer);
        renderer.DestroyBuffer(UniformBuffer);
    }
}

/// <summary>Linear, premultiplied color plus linear data in one mipmapped RGBA16F array.</summary>
internal sealed record DecalAtlas(int Width, int Height, int Layers, List<Half[]> Mips)
{
    internal const int LayersPerMaterial = 3;

    internal static DecalAtlas Build(IReadOnlyList<PbrDecalMaterial> materials, int maxSize)
    {
        var width = 1;
        var height = 1;
        foreach (var material in materials)
        {
            Grow(material.ColorTexture);
            Grow(material.NormalTexture);
            Grow(material.MetallicRoughnessTexture);
        }
        // Powers of two give every mip an exact 2×2 footprint, including after downsampling.
        width = Math.Min(maxSize, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)width));
        height = Math.Min(maxSize, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)height));
        var layers = Math.Max(1, materials.Count) * LayersPerMaterial;
        var pixels = new Half[width * height * layers * 4];
        for (var layer = 0; layer < layers; layer++)
        {
            var material = materials.Count == 0 ? null : materials[layer / LayersPerMaterial];
            var kind = layer % LayersPerMaterial;
            var source = kind switch { 0 => material?.ColorTexture, 1 => material?.NormalTexture, _ => material?.MetallicRoughnessTexture };
            var fallback = kind == 1 ? new Vector4(0.5f, 0.5f, 1f, 1f) : Vector4.One;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = source is null ? fallback : Sample(source, (x + 0.5f) / width, (y + 0.5f) / height, kind == 0);
                Store(pixels, ((layer * height + y) * width + x) * 4, value);
            }
        }
        var mips = new List<Half[]> { pixels };
        var mipWidth = width;
        var mipHeight = height;
        while (mipWidth > 1 || mipHeight > 1)
        {
            var nextWidth = Math.Max(1, mipWidth / 2);
            var nextHeight = Math.Max(1, mipHeight / 2);
            var next = new Half[nextWidth * nextHeight * layers * 4];
            for (var layer = 0; layer < layers; layer++)
            for (var y = 0; y < nextHeight; y++)
            for (var x = 0; x < nextWidth; x++)
            {
                var sum = Vector4.Zero;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                    sum += Load(pixels, ((layer * mipHeight + Math.Min(y * 2 + dy, mipHeight - 1)) * mipWidth + Math.Min(x * 2 + dx, mipWidth - 1)) * 4);
                Store(next, ((layer * nextHeight + y) * nextWidth + x) * 4, sum * 0.25f);
            }
            mips.Add(next);
            pixels = next;
            mipWidth = nextWidth;
            mipHeight = nextHeight;
        }
        return new DecalAtlas(width, height, layers, mips);

        void Grow(PbrDecalTexture? texture)
        {
            if (texture is null) return;
            width = Math.Max(width, texture.Width);
            height = Math.Max(height, texture.Height);
        }
    }

    private static Vector4 Sample(PbrDecalTexture texture, float u, float v, bool srgb)
    {
        var x = u * texture.Width - 0.5f;
        var y = v * texture.Height - 0.5f;
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        return Vector4.Lerp(Vector4.Lerp(Texel(texture, ix, iy, srgb), Texel(texture, ix + 1, iy, srgb), x - ix),
            Vector4.Lerp(Texel(texture, ix, iy + 1, srgb), Texel(texture, ix + 1, iy + 1, srgb), x - ix), y - iy);
    }

    private static Vector4 Texel(PbrDecalTexture texture, int x, int y, bool srgb)
    {
        var i = (Math.Clamp(y, 0, texture.Height - 1) * texture.Width + Math.Clamp(x, 0, texture.Width - 1)) * 4;
        var data = texture.Pixels;
        var value = new Vector4(data[i], data[i + 1], data[i + 2], data[i + 3]) / 255f;
        if (srgb) value = new Vector4(Linear(value.X) * value.W, Linear(value.Y) * value.W, Linear(value.Z) * value.W, value.W);
        return value;
    }

    private static float Linear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static Vector4 Load(Half[] pixels, int i) => new((float)pixels[i], (float)pixels[i + 1], (float)pixels[i + 2], (float)pixels[i + 3]);
    private static void Store(Half[] pixels, int i, Vector4 value)
    {
        pixels[i] = (Half)value.X;
        pixels[i + 1] = (Half)value.Y;
        pixels[i + 2] = (Half)value.Z;
        pixels[i + 3] = (Half)value.W;
    }
}
