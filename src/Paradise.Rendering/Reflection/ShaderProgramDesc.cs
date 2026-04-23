namespace Paradise.Rendering;

/// <summary>One shader module within a <see cref="ShaderProgramDesc"/>: WGSL source plus stage + entry point.</summary>
public sealed record ShaderModuleDesc(
    string Wgsl,
    string EntryPoint,
    ShaderStage Stage);

/// <summary>One binding entry in a bind group layout — maps a binding slot to a resource type and visibility.</summary>
public sealed record BindGroupLayoutEntryDesc(
    uint Binding,
    ShaderStage Visibility,
    BindingResourceType Type,
    ulong MinBufferSize = 0);

/// <summary>One bind group layout (group index + ordered binding entries).</summary>
public sealed record BindGroupLayoutDesc(
    uint GroupIndex,
    BindGroupLayoutEntryDesc[] Entries);

/// <summary>Push constant range visible to a set of stages.</summary>
public sealed record PushConstantRangeDesc(
    ShaderStage Visibility,
    uint Offset,
    uint Size);

/// <summary>Pipeline layout: ordered bind groups and push constant ranges.</summary>
public sealed record PipelineLayoutDesc(
    BindGroupLayoutDesc[] Groups,
    PushConstantRangeDesc[] PushConstants);

/// <summary>One vertex attribute within a buffer layout: shader location, format, byte offset.</summary>
public sealed record VertexAttributeDesc(
    uint ShaderLocation,
    VertexFormat Format,
    ulong Offset);

/// <summary>One vertex buffer layout: stride, step mode, and the attributes it carries.</summary>
public sealed record VertexBufferLayoutDesc(
    ulong Stride,
    VertexStepMode StepMode,
    VertexAttributeDesc[] Attributes);

/// <summary>Slang-reflection-shaped shader program: modules, pipeline layout, vertex inputs.</summary>
public sealed record ShaderProgramDesc(
    ShaderModuleDesc[] Modules,
    PipelineLayoutDesc Layout,
    VertexBufferLayoutDesc[] VertexBuffers);
