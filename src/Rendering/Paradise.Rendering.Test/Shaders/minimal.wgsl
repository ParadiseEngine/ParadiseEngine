// Fixture for ShaderProgramLoaderTests — the loader carries this blob through verbatim into every
// ShaderModuleDesc, so its contents are opaque to the code under test. Paired with
// minimal.reflection.json; both are embedded (see the csproj) under `Shaders.minimal.*` so the
// test drives the real GetManifestResourceStream path rather than a string overload.
// Deliberately hand-written, not slangc output: the point of this suite is that the loader needs
// nothing but this package. The slangc-schema golden tests live in Paradise.Rendering.WebGPU.Test.
struct VsIn {
    @location(0) position: vec3<f32>,
    @location(1) uv: vec2<f32>,
};

@vertex
fn vertexMain(input: VsIn) -> @builtin(position) vec4<f32> {
    return vec4<f32>(input.position, 1.0);
}

@fragment
fn fragmentMain() -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 1.0, 1.0, 1.0);
}
