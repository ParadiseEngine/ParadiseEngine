using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Paradise.Ui.Noesis;

/// <summary>Static description of Noesis's shader surface plus WGSL generation for every
/// variant. The tables mirror <c>Noesis.Shader</c>'s own helper methods (asserted equal in
/// tests) and the WGSL bodies are a faithful port of the reference GLSL templates embedded in
/// libNoesis 3.2.12 (vertex: row-vector <c>pos * proj</c>, flat color/rect/tile; fragment:
/// exact paint/effect formulas including the radial-gradient conic solve and the SDF
/// constants). Colors and ramp texels arrive PREMULTIPLIED from Noesis — no premultiplication
/// happens in shaders; SrcOver blending is One / OneMinusSrcAlpha.</summary>
public static class NoesisShaderCatalog
{
    [Flags]
    public enum Attr : byte
    {
        Pos = 1,
        Color = 2,
        Tex0 = 4,
        Tex1 = 8,
        Coverage = 16,
        Rect = 32,
        Tile = 64,
        ImagePos = 128,
    }

    public enum Paint
    {
        None,
        Solid,
        Linear,
        Radial,
        Pattern,
        PatternClamp,
        PatternRepeat,
        PatternMirrorU,
        PatternMirrorV,
        PatternMirror,
    }

    public enum Effect
    {
        Rgba,
        Mask,
        Clear,
        Path,
        PathAa,
        Sdf,
        Opacity,
        Upsample,
        Downsample,
        Shadow,
        Blur,
        Custom,
    }

    public readonly record struct Variant(int Index, string Name, Effect Effect, Paint Paint, Attr Attrs, int Stride);

    /// <summary>(attribute, byte size) in buffer order — interleaved offsets accumulate in
    /// this exact order (matches the reference devices).</summary>
    public static readonly (Attr Attr, int Size)[] AttrSizes =
    [
        (Attr.Pos, 8),        // f32x2
        (Attr.Color, 4),      // unorm8x4
        (Attr.Tex0, 8),       // f32x2
        (Attr.Tex1, 8),       // f32x2
        (Attr.Coverage, 4),   // f32
        (Attr.Rect, 8),       // unorm16x4
        (Attr.Tile, 16),      // f32x4
        (Attr.ImagePos, 16),  // f32x4
    ];

    /// <summary>All 53 shader variants in Noesis 3.2.12 enum order. Attr masks and strides are
    /// the values reported by <c>Noesis.Shader.AttributesForFormat</c>/<c>SizeForFormat</c>
    /// (a test asserts parity so an SDK upgrade that shifts the tables fails loudly).</summary>
    public static readonly Variant[] Variants = Build();

    private static Variant[] Build()
    {
        var list = new List<Variant>();
        void Add(string name, Effect effect, Paint paint, Attr attrs)
        {
            var stride = 0;
            foreach (var (attr, size) in AttrSizes)
            {
                if ((attrs & attr) != 0) stride += size;
            }
            list.Add(new Variant(list.Count, name, effect, paint, attrs, stride));
        }

        Attr P(Paint paint, Attr baseAttrs) => paint switch
        {
            Paint.Solid => baseAttrs | Attr.Color,
            Paint.Linear or Paint.Radial or Paint.Pattern => baseAttrs | Attr.Tex0,
            Paint.PatternClamp => baseAttrs | Attr.Tex0 | Attr.Rect,
            _ => baseAttrs | Attr.Tex0 | Attr.Rect | Attr.Tile, // repeat/mirror
        };

        var paints = new[]
        {
            Paint.Solid, Paint.Linear, Paint.Radial, Paint.Pattern,
            Paint.PatternClamp, Paint.PatternRepeat, Paint.PatternMirrorU, Paint.PatternMirrorV, Paint.PatternMirror,
        };
        string PaintSuffix(Paint p) => p switch
        {
            Paint.Solid => "Solid",
            Paint.Linear => "Linear",
            Paint.Radial => "Radial",
            Paint.Pattern => "Pattern",
            Paint.PatternClamp => "Pattern_Clamp",
            Paint.PatternRepeat => "Pattern_Repeat",
            Paint.PatternMirrorU => "Pattern_MirrorU",
            Paint.PatternMirrorV => "Pattern_MirrorV",
            _ => "Pattern_Mirror",
        };

        Add("RGBA", Effect.Rgba, Paint.None, Attr.Pos);
        Add("Mask", Effect.Mask, Paint.None, Attr.Pos);
        Add("Clear", Effect.Clear, Paint.None, Attr.Pos);
        foreach (var p in paints) Add($"Path_{PaintSuffix(p)}", Effect.Path, p, P(p, Attr.Pos));
        foreach (var p in paints) Add($"Path_AA_{PaintSuffix(p)}", Effect.PathAa, p, P(p, Attr.Pos | Attr.Coverage));
        foreach (var p in paints) Add($"SDF_{PaintSuffix(p)}", Effect.Sdf, p, P(p, Attr.Pos | Attr.Tex1));
        foreach (var p in paints) Add($"SDF_LCD_{PaintSuffix(p)}", Effect.Sdf, p, P(p, Attr.Pos | Attr.Tex1));
        foreach (var p in paints) Add($"Opacity_{PaintSuffix(p)}", Effect.Opacity, p, P(p, Attr.Pos | Attr.Tex1));
        Add("Upsample", Effect.Upsample, Paint.None, Attr.Pos | Attr.Color | Attr.Tex0 | Attr.Tex1);
        Add("Downsample", Effect.Downsample, Paint.None, Attr.Pos | Attr.Tex0 | Attr.Tex1);
        Add("Shadow", Effect.Shadow, Paint.Solid, Attr.Pos | Attr.Color | Attr.Tex1 | Attr.Rect);
        Add("Blur", Effect.Blur, Paint.Solid, Attr.Pos | Attr.Color | Attr.Tex1);
        Add("Custom_Effect", Effect.Custom, Paint.Pattern, Attr.Pos | Attr.Color | Attr.Tex0 | Attr.Rect | Attr.ImagePos);
        return list.ToArray();
    }

    /// <summary>Generate the complete WGSL module (vertex + fragment entry) for one variant.
    /// Bindings: 0 = vertex uniforms (proj + SDF st1 scale), 1 = cbuffer0_ps (8 floats),
    /// 2 = cbuffer1_ps (128 floats), 3..7 = pattern/ramps/image/glyphs/shadow textures,
    /// 8 = shared sampler (per-batch sampler states are bound by the device). Only the
    /// bindings a variant reads are declared — the shared bind group layout is a superset.</summary>
    public static string GenerateWgsl(in Variant v)
    {
        var sb = new StringBuilder(4096);
        var a = v.Attrs;
        bool has(Attr attr) => (a & attr) != 0;
        var sdf = v.Effect == Effect.Sdf;
        var downsample = v.Effect == Effect.Downsample;

        // ---- bindings ----
        sb.AppendLine("struct VsUniforms { proj: mat4x4<f32>, st1Scale: vec4<f32> }");
        sb.AppendLine("@group(0) @binding(0) var<uniform> vsU: VsUniforms;");
        var usesPs0 = v.Effect is Effect.Rgba || v.Paint is Paint.Linear or Paint.Radial
            || (v.Paint >= Paint.Pattern && v.Paint <= Paint.PatternMirror);
        if (usesPs0) sb.AppendLine("struct PsU0 { v: array<vec4<f32>, 2> }\n@group(0) @binding(1) var<uniform> ps0: PsU0;");
        var usesPs1 = v.Effect is Effect.Shadow or Effect.Blur;
        if (usesPs1) sb.AppendLine("struct PsU1 { v: array<vec4<f32>, 32> }\n@group(0) @binding(2) var<uniform> ps1: PsU1;");

        var usesPattern = v.Paint >= Paint.Pattern && v.Paint <= Paint.PatternMirror || downsample || v.Effect == Effect.Upsample;
        var usesRamps = v.Paint is Paint.Linear or Paint.Radial;
        var usesImage = v.Effect is Effect.Opacity or Effect.Shadow or Effect.Blur or Effect.Upsample;
        var usesGlyphs = sdf;
        var usesShadowTex = v.Effect is Effect.Shadow or Effect.Blur;
        if (usesPattern) sb.AppendLine("@group(0) @binding(3) var pattern: texture_2d<f32>;");
        if (usesRamps) sb.AppendLine("@group(0) @binding(4) var ramps: texture_2d<f32>;");
        if (usesImage) sb.AppendLine("@group(0) @binding(5) var image: texture_2d<f32>;");
        if (usesGlyphs) sb.AppendLine("@group(0) @binding(6) var glyphs: texture_2d<f32>;");
        if (usesShadowTex) sb.AppendLine("@group(0) @binding(7) var shadowTex: texture_2d<f32>;");
        if (usesPattern || usesRamps || usesImage || usesGlyphs || usesShadowTex)
            sb.AppendLine("@group(0) @binding(8) var samp: sampler;");

        // ---- vertex IO ----
        sb.AppendLine("struct VsIn {");
        var offset = 0;
        var location = 0;
        foreach (var (attr, size) in AttrSizes)
        {
            if (!has(attr)) continue;
            var (type, name) = attr switch
            {
                Attr.Pos => ("vec2<f32>", "pos"),
                Attr.Color => ("vec4<f32>", "color"),      // unorm8x4 auto-normalizes
                Attr.Tex0 => ("vec2<f32>", "uv0"),
                Attr.Tex1 => ("vec2<f32>", "uv1"),
                Attr.Coverage => ("f32", "coverage"),
                Attr.Rect => ("vec4<f32>", "rect"),        // unorm16x4
                Attr.Tile => ("vec4<f32>", "tile"),
                _ => ("vec4<f32>", "imagePos"),
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"  @location({location++}) {name}: {type},");
            offset += size;
        }
        sb.AppendLine("}");

        sb.AppendLine("struct VsOut {");
        sb.AppendLine("  @builtin(position) pos: vec4<f32>,");
        if (has(Attr.Color)) sb.AppendLine("  @location(0) @interpolate(flat) color: vec4<f32>,");
        if (has(Attr.Tex0) || downsample) sb.AppendLine("  @location(1) uv0: vec2<f32>,");
        if (has(Attr.Tex1)) sb.AppendLine("  @location(2) uv1: vec2<f32>,");
        if (downsample) sb.AppendLine("  @location(3) uv2: vec2<f32>,\n  @location(4) uv3: vec2<f32>,");
        if (sdf) sb.AppendLine("  @location(5) st1: vec2<f32>,");
        if (has(Attr.Rect)) sb.AppendLine("  @location(6) @interpolate(flat) rect: vec4<f32>,");
        if (has(Attr.Tile)) sb.AppendLine("  @location(7) @interpolate(flat) tile: vec4<f32>,");
        if (has(Attr.Coverage)) sb.AppendLine("  @location(8) coverage: f32,");
        if (has(Attr.ImagePos)) sb.AppendLine("  @location(9) imagePos: vec4<f32>,");
        sb.AppendLine("}");

        // ---- vertex main (row-vector: pos * proj, per the reference GLSL) ----
        sb.AppendLine("@vertex fn vs_main(i: VsIn) -> VsOut {");
        sb.AppendLine("  var o: VsOut;");
        sb.AppendLine("  o.pos = vec4<f32>(i.pos, 0.0, 1.0) * vsU.proj;");
        if (has(Attr.Color)) sb.AppendLine("  o.color = i.color;");
        if (downsample)
        {
            sb.AppendLine("""
                  o.uv0 = i.uv0 + vec2<f32>(i.uv1.x, i.uv1.y);
                  o.uv1 = i.uv0 + vec2<f32>(i.uv1.x, -i.uv1.y);
                  o.uv2 = i.uv0 + vec2<f32>(-i.uv1.x, i.uv1.y);
                  o.uv3 = i.uv0 + vec2<f32>(-i.uv1.x, -i.uv1.y);
                """);
        }
        else
        {
            if (has(Attr.Tex0)) sb.AppendLine("  o.uv0 = i.uv0;");
            if (has(Attr.Tex1)) sb.AppendLine("  o.uv1 = i.uv1;");
        }
        if (sdf) sb.AppendLine("  o.st1 = i.uv1 * vsU.st1Scale.xy;");
        if (has(Attr.Rect)) sb.AppendLine("  o.rect = i.rect;");
        if (has(Attr.Tile)) sb.AppendLine("  o.tile = i.tile;");
        if (has(Attr.Coverage)) sb.AppendLine("  o.coverage = i.coverage;");
        if (has(Attr.ImagePos)) sb.AppendLine("  o.imagePos = i.imagePos;");
        sb.AppendLine("  return o;\n}");

        // ---- fragment main (ported formula-for-formula from the GLSL template) ----
        sb.AppendLine("@fragment fn fs_main(i: VsOut) -> @location(0) vec4<f32> {");
        switch (v.Paint)
        {
            case Paint.Solid:
                sb.AppendLine("  let paint = i.color;\n  let opacity_ = 1.0;");
                break;
            case Paint.Linear:
                sb.AppendLine("  let paint = textureSample(ramps, samp, i.uv0);\n  let opacity_ = ps0.v[0].x;");
                break;
            case Paint.Radial:
                sb.AppendLine("""
                      let dd = ps0.v[1].x * i.uv0.x - ps0.v[1].y * i.uv0.y;
                      let u = ps0.v[0].x * i.uv0.x + ps0.v[0].y * i.uv0.y + ps0.v[0].z *
                          sqrt(max(i.uv0.x * i.uv0.x + i.uv0.y * i.uv0.y - dd * dd, 0.0));
                      let paint = textureSample(ramps, samp, vec2<f32>(u, ps0.v[1].z));
                      let opacity_ = ps0.v[0].w;
                    """);
                break;
            case Paint.Pattern:
                sb.AppendLine("  let paint = textureSample(pattern, samp, i.uv0);\n  let opacity_ = ps0.v[0].x;");
                break;
            case Paint.PatternClamp:
                sb.AppendLine("""
                      let insideC = f32(all(i.uv0 == clamp(i.uv0, i.rect.xy, i.rect.zw)));
                      let paint = insideC * textureSample(pattern, samp, i.uv0);
                      let opacity_ = ps0.v[0].x;
                    """);
                break;
            case Paint.PatternRepeat:
            case Paint.PatternMirrorU:
            case Paint.PatternMirrorV:
            case Paint.PatternMirror:
                var fold = v.Paint switch
                {
                    Paint.PatternRepeat => "uv = fract(uv);",
                    Paint.PatternMirrorU => "uv.x = abs(uv.x - 2.0 * floor((uv.x - 1.0) / 2.0) - 2.0); uv.y = fract(uv.y);",
                    Paint.PatternMirrorV => "uv.x = fract(uv.x); uv.y = abs(uv.y - 2.0 * floor((uv.y - 1.0) / 2.0) - 2.0);",
                    _ => "uv = abs(uv - 2.0 * floor((uv - 1.0) / 2.0) - 2.0);",
                };
                sb.AppendLine(CultureInfo.InvariantCulture, $"""
                      var uv = (i.uv0 - i.tile.xy) / i.tile.zw;
                      {fold}
                      uv = uv * i.tile.zw + i.tile.xy;
                      let insideT = f32(all(uv == clamp(uv, i.rect.xy, i.rect.zw)));
                      let paint = insideT * textureSampleGrad(pattern, samp, uv, dpdx(i.uv0), dpdy(i.uv0));
                      let opacity_ = ps0.v[0].x;
                    """);
                break;
            default:
                sb.AppendLine("  let paint = vec4<f32>(0.0);\n  let opacity_ = 1.0;");
                break;
        }

        var epilogue = v.Effect switch
        {
            Effect.Rgba => "return ps0.v[0];",
            Effect.Mask => "return vec4<f32>(1.0);",
            Effect.Clear => "return vec4<f32>(0.0);",
            Effect.Path => "return opacity_ * paint;",
            Effect.PathAa => "return (opacity_ * i.coverage) * paint;",
            Effect.Opacity => "return textureSample(image, samp, i.uv1) * (opacity_ * paint.a);",
            Effect.Shadow => """
                let shadowColor = ps1.v[0];
                let offs = vec2<f32>(ps1.v[1].x, -ps1.v[1].y);
                let suv = clamp(i.uv1 - offs, i.rect.xy, i.rect.zw);
                let alpha = mix(textureSample(image, samp, suv).a, textureSample(shadowTex, samp, suv).a, ps1.v[1].z);
                let img = textureSample(image, samp, clamp(i.uv1, i.rect.xy, i.rect.zw));
                return (img + (1.0 - img.a) * (shadowColor * alpha)) * (opacity_ * paint.a);
                """,
            Effect.Blur => "return mix(textureSample(image, samp, i.uv1), textureSample(shadowTex, samp, i.uv1), ps1.v[0].x) * (opacity_ * paint.a);",
            Effect.Sdf => """
                const SDF_SCALE = 7.96875;
                const SDF_BIAS = 0.50196078431;
                const SDF_AA_FACTOR = 0.65;
                const SDF_BASE_MIN = 0.125;
                const SDF_BASE_MAX = 0.25;
                const SDF_BASE_DEV = -0.65;
                let dist = SDF_SCALE * (textureSample(glyphs, samp, i.uv1).r - SDF_BIAS);
                let gradLen = length(dpdx(i.st1));
                let scale = 1.0 / max(gradLen, 1e-6);
                let base = SDF_BASE_DEV * (1.0 - (clamp(scale, SDF_BASE_MIN, SDF_BASE_MAX) - SDF_BASE_MIN) / (SDF_BASE_MAX - SDF_BASE_MIN));
                let range = SDF_AA_FACTOR * gradLen;
                let alpha = smoothstep(base - range, base + range, dist);
                return (alpha * opacity_) * paint;
                """,
            Effect.Downsample => "return (textureSample(pattern, samp, i.uv0) + textureSample(pattern, samp, i.uv1) + textureSample(pattern, samp, i.uv2) + textureSample(pattern, samp, i.uv3)) * 0.25;",
            Effect.Upsample => "return mix(textureSample(image, samp, i.uv1), textureSample(pattern, samp, i.uv0), i.color.a);",
            _ => "return vec4<f32>(1.0, 0.0, 1.0, 1.0); // Custom_Effect placeholder",
        };
        foreach (var line in epilogue.Split('\n'))
        {
            sb.Append("  ").AppendLine(line.TrimEnd());
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}
