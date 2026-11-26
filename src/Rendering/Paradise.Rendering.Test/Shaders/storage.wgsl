struct Item { origin: vec3<f32>, count: u32, uv: vec2<f32>, flags: u32 }
struct Pair { a: vec2<f32>, b: u32 }
@group(0) @binding(0) var<storage, read> items: array<Item>;
@group(0) @binding(1) var<storage, read_write> pairs: array<Pair>;
@group(0) @binding(2) var<storage, read> weights: array<vec4<f32>>;
@compute @workgroup_size(1) fn computeMain() { pairs[0].b = items[0].count + u32(weights[0].x); }
