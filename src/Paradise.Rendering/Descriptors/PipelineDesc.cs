namespace Paradise.Rendering;

/// <summary>Render pipeline descriptor placeholder. Filled in by M1 (#42) — present here so the
/// contract surface exists for backend stubs and slot tables to reference.</summary>
// TODO(M1 #42): expand with vertex/fragment shader handles, vertex layouts, color/depth targets,
//               and reflection-derived PipelineLayoutDesc.
public readonly record struct PipelineDesc(string? Name);
