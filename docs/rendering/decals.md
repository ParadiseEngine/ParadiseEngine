# Projected material decals

Decals paint surface color, normals, metallic/roughness and emission before forward PBR
lighting. They work with stock rigid/skinned materials, instancing, and custom shaders using
`shadePbrCore` or `shadeSurface`. They leave the receiver's alpha and geometry unchanged.

```csharp
scene.Decals.Volumes.Add(new PbrDecal
{
    Material = new PbrDecalMaterial
    {
        Color = new Vector4(1, 0.2f, 0.05f, 1),
        Roughness = 0.5f,
        MaterialWeight = 1,
    },
    Model = Matrix4x4.CreateScale(2, 2, 0.3f)
        * Matrix4x4.CreateTranslation(0, 1, -3),
});
```

A projector is a unit cube centered at zero. It projects along local −Z onto surfaces
facing local +Z. Its model matrix supports invertible affine transforms, including nonuniform
scale, reflection and shear. Rotate −90° around X to project onto a Y-up floor. UVs are
`(local.x + 0.5, 0.5 - local.y)`; images are top-to-bottom RGBA8. Color RGB is sRGB, alpha is
linear, and normal/metallic-roughness images contain linear data. Metallic-roughness uses
glTF channels: G roughness, B metallic. Normal +X/+Y follow the projector's local XY axes.

`PbrDecalTexture` copies the supplied pixels. Materials are immutable; replacing a material
or its texture with a new material rebuilds the shared array. Missing maps use white color,
a flat normal and white metallic-roughness. Color mips are generated from premultiplied linear
RGBA so transparent RGB does not bleed. Color texture alpha, material alpha, opacity and
geometric facing/edge/depth fades share coverage across all channels. Channel weights select
which properties are painted; normal weight has no effect without a normal texture.

Greater `Order` paints later. Equal orders preserve list order. Set `instance.ReceivesDecals =
false` to opt out, including for one member of an instanced batch. Both the process switch
`rendering.decals` and `scene.Decals.Enabled` must allow decals. Switching either off retracts
the active count at the next frame. The resident atlas is retained across disable/enable
transitions to avoid repeated uploads. Singular, non-affine and non-finite projector transforms
are skipped; non-finite material factors are rejected.

A frame supports 32 valid enabled projectors. Exceeding that limit throws rather than silently
dropping decals. `TextureSize` is a power-of-two maximum array dimension from 1 to 512,
defaulting to 256. Each resident material uses three RGBA16F layers with a full mip chain;
32 distinct 256×256 materials require about 64 MiB. Smaller source maps allocate less.
`renderer.Pipeline.Find<DecalFeature>()` exposes active decal count, resident material count,
and texture dimensions.

Decals shade receivers in the forward pass; they do not modify shadow caster geometry, depth/
normal prepasses or the compute GI trace's material data. They are not baked into GI. The shader
loops over active projectors per fragment, so overlapping many volumes increases shading cost.

Run the floor/wall stencil example with fog and temporal AA:

```sh
dotnet run --project src/Paradise.Rendering.Sample -- --gi-demo --decals --fog --taa --no-gi
```

`DecalTests` checks affine projection, fading, immutable image ownership, premultiplied mips,
array-layer isolation, real GPU material channels, ordered materials, atlas replacement,
feature transitions, capacity limits and per-instance opt-out. Shader reflection tests require
a sampled 2D-array binding; a GPU fixture samples mip 1 through the actual bound atlas view. Native GPU rendering is tested; browser array upload and binding
support is implemented and builds, but browser runtime rendering has not been exercised.
