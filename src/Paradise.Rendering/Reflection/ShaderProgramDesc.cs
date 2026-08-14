namespace Paradise.Rendering;

// TODO(post-M0a): switch the array-typed properties below to ImmutableArray<T> (or
// IReadOnlyList<T>) before the contract is published. Held off in M0a because (a) the
// fixtures + System.Text.Json source-gen pipeline both target T[] today and (b) #45's
// Slang regression suite will exercise the contract end-to-end with real slangc output,
// which is the right time to lock down immutability.

/// <summary>One shader module within a <see cref="ShaderProgramDesc"/>: WGSL source plus stage + entry point.</summary>
public sealed record ShaderModuleDesc(
    string Wgsl,
    string EntryPoint,
    ShaderStage Stage);

/// <summary>One binding entry in a bind group layout — maps a binding slot to a resource type and
/// visibility. <paramref name="HasDynamicOffset"/> marks a uniform/storage buffer whose byte
/// offset is supplied per SetBindGroup (the draw-UBO-ring pattern); it is a LAYOUT property, so
/// consumers opting in must rebuild the layout, not just pass an offset.
/// <paramref name="StorageFormat"/>/<paramref name="Access"/> apply only to
/// <see cref="BindingResourceType.StorageTexture"/> entries, where WebGPU requires both in the
/// layout; a storage-texture entry with <see cref="TextureFormat.Undefined"/> format is rejected
/// at layout build.</summary>
public sealed record BindGroupLayoutEntryDesc(
    uint Binding,
    ShaderStage Visibility,
    BindingResourceType Type,
    ulong MinBufferSize = 0,
    bool HasDynamicOffset = false,
    TextureFormat StorageFormat = TextureFormat.Undefined,
    StorageTextureAccess Access = StorageTextureAccess.WriteOnly);

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

/// <summary>One field inside a reflected uniform block: name plus byte offset/size in the GPU
/// layout. Array fields appear once with Size = elementStride × count.</summary>
public sealed record UniformFieldDesc(
    string Name,
    uint Offset,
    uint Size);

/// <summary>One reflected constant buffer: its bind point plus the GPU byte layout of its fields.
/// Consumers validate their CPU mirror structs against this (never hand-trusted offsets).</summary>
public sealed record UniformBlockDesc(
    string Name,
    uint Group,
    uint Binding,
    uint SizeBytes,
    UniformFieldDesc[] Fields);

/// <summary>Slang-reflection-shaped shader program: modules, pipeline layout, vertex inputs, and
/// the uniform-block byte layouts backing the pipeline layout's buffer entries.</summary>
public sealed record ShaderProgramDesc(
    ShaderModuleDesc[] Modules,
    PipelineLayoutDesc Layout,
    VertexBufferLayoutDesc[] VertexBuffers)
{
    /// <summary>Reflected constant-buffer layouts, one per uniform-buffer binding in
    /// <see cref="Layout"/>. Empty for programs without uniforms.</summary>
    public UniformBlockDesc[] UniformBlocks { get; init; } = [];

    /// <summary>Vertex layout per vertex entry point, for programs that author more than one.
    /// <see cref="VertexBuffers"/> stays the FIRST entry point's layout, so every existing caller
    /// keeps its behaviour.
    ///
    /// This exists because a vertex layout belongs to an entry point, not to a program: a skinned
    /// variant reads joints and weights the rigid one does not. Selecting a vertex entry point
    /// without also selecting its layout silently feeds one shader's stride to another — which
    /// draws nothing rather than failing, so nothing tells you.</summary>
    public IReadOnlyDictionary<string, VertexBufferLayoutDesc[]> VertexBuffersByEntryPoint { get; init; } =
        new Dictionary<string, VertexBufferLayoutDesc[]>(StringComparer.Ordinal);
}
