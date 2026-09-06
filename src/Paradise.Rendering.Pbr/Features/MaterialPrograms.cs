namespace Paradise.Rendering.Pbr;

/// <summary>The built-in PBR program, the game-registered extensions of it, and the pipeline
/// variants built from either: opaque or blend, rigid or skinned.</summary>
internal sealed class MaterialPrograms : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly Dictionary<(int ProgramId, BlendMode Blend), PipelineHandle> _pipelines = new();
    // Skinned twins of the built-in pipelines, built lazily so a scene with nothing skinned never
    // compiles them.
    private readonly Dictionary<BlendMode, PipelineHandle> _skinnedPipelines = new();
    // Game-registered material programs (Register): index + 1 = programId; 0 is the built-in PBR
    // program. Each entry stores the MERGED desc (custom modules over the built-in pipeline
    // layout, see Register) plus its entry-point names.
    private readonly List<(ShaderProgramDesc Program, string VertexEntry, string FragmentEntry)> _customPrograms = [];

    public MaterialPrograms(IRenderer renderer)
    {
        _renderer = renderer;
        var program = ShaderPrograms.Load("Shaders.pbr");
        UniformLayoutValidator.Validate(program);
        BuiltIn = ShaderPrograms.WithDynamicDrawRing(program);
    }

    /// <summary>The built-in program with its draw ring made dynamic-offset: the layout every
    /// pipeline and every engine bind group is created from.</summary>
    public ShaderProgramDesc BuiltIn { get; }

    /// <summary>The interleaved mesh stride every program's vertex stream shares.</summary>
    public ulong MeshStride => BuiltIn.VertexBuffers[0].Stride;

    public BindGroupLayoutDesc Group(uint groupIndex) => ShaderPrograms.FindGroup(BuiltIn, groupIndex);

    public int PipelineCount => _pipelines.Count;
    public int SkinnedPipelineCount => _skinnedPipelines.Count;
    public int CustomProgramCount => _customPrograms.Count;

    /// <summary>Register a game-supplied program. See <see cref="PbrRenderer.RegisterMaterialProgram"/>
    /// for the contract; this is its body.</summary>
    public int Register(MaterialResourceCache materials, ShaderProgramDesc program, string vertexEntryPoint, string fragmentEntryPoint)
    {
        UniformLayoutValidator.Validate(program);

        var hasFragmentEntry = false;
        foreach (var module in program.Modules)
        {
            hasFragmentEntry |= module.Stage == ShaderStage.Fragment && module.EntryPoint == fragmentEntryPoint;
        }
        if (!hasFragmentEntry)
            throw new InvalidOperationException($"Custom material program has no fragment entry point '{fragmentEntryPoint}'.");

        // Subset compatibility: every binding the custom program reflects in groups 0/1/3, and in
        // group 2 below the extension slots, must exist in the built-in program with an identical
        // entry. Group 0 is compared with the dynamic-offset ring flag applied, since the raw
        // reflection cannot know about that renderer-side rewrite.
        foreach (var group in program.Layout.Groups)
        {
            var builtIn = ShaderPrograms.FindGroupOrNull(BuiltIn, group.GroupIndex);
            foreach (var entry in group.Entries)
            {
                if (group.GroupIndex == 2 && entry.Binding >= MaterialResourceCache.StandardMaterialEntryCount)
                    continue; // the extension's own bindings
                var actual = group.GroupIndex == 0 ? entry with { HasDynamicOffset = true } : entry;
                BindGroupLayoutEntryDesc? expected = null;
                if (builtIn is not null)
                {
                    foreach (var candidate in builtIn.Entries)
                    {
                        if (candidate.Binding == entry.Binding) { expected = candidate; break; }
                    }
                }
                if (expected is null || expected != actual)
                    throw new InvalidOperationException(
                        $"Custom material program is not layout-compatible with the PBR frame: group {group.GroupIndex} " +
                        $"binding {entry.Binding} reflects [{actual}] but the built-in program expects " +
                        $"[{expected?.ToString() ?? "no such binding"}]. Extension shaders must #include Common/pbrCore.slang " +
                        "unmodified and add their own bindings only in group 2 at binding " +
                        $"{MaterialResourceCache.StandardMaterialEntryCount}+.");
            }
        }

        // The custom vertex entry must consume the standard rigid stream — primitives are uploaded
        // once and shared across programs.
        var vertexLayout = program.VertexBuffersByEntryPoint.TryGetValue(vertexEntryPoint, out var byEntry)
            ? byEntry
            : program.VertexBuffers;
        if (vertexLayout.Length == 0)
            throw new InvalidOperationException($"Custom material program reflects no vertex layout for entry point '{vertexEntryPoint}'.");
        if (vertexLayout[0].Stride != MeshStride)
            throw new InvalidOperationException(
                $"Custom material program's '{vertexEntryPoint}' consumes a {vertexLayout[0].Stride}-byte vertex, " +
                $"but PBR primitives are {MeshStride}-byte (pos3/normal3/uv2/tangent4). " +
                "Custom programs are rigid-only.");

        // Merge: built-in groups 0/1/3 verbatim (dynamic draw ring included); group 2 = the seven
        // standard entries plus the extension's extras, sorted by binding.
        var extras = new List<BindGroupLayoutEntryDesc>();
        foreach (var group in program.Layout.Groups)
        {
            if (group.GroupIndex != 2) continue;
            foreach (var entry in group.Entries)
            {
                if (entry.Binding >= MaterialResourceCache.StandardMaterialEntryCount) extras.Add(entry);
            }
        }
        extras.Sort(static (a, b) => a.Binding.CompareTo(b.Binding));

        var mergedGroups = (BindGroupLayoutDesc[])BuiltIn.Layout.Groups.Clone();
        for (var i = 0; i < mergedGroups.Length; i++)
        {
            if (mergedGroups[i].GroupIndex != 2) continue;
            var entries = new BindGroupLayoutEntryDesc[mergedGroups[i].Entries.Length + extras.Count];
            mergedGroups[i].Entries.CopyTo(entries, 0);
            extras.CopyTo(entries, mergedGroups[i].Entries.Length);
            mergedGroups[i] = new BindGroupLayoutDesc(2, entries);
        }
        var merged = new ShaderProgramDesc(
            program.Modules,
            new PipelineLayoutDesc(mergedGroups, BuiltIn.Layout.PushConstants),
            program.VertexBuffers)
        {
            UniformBlocks = program.UniformBlocks,
            VertexBuffersByEntryPoint = program.VertexBuffersByEntryPoint,
        };

        _customPrograms.Add((merged, vertexEntryPoint, fragmentEntryPoint));
        var programId = _customPrograms.Count;
        materials.RegisterProgramLayout(programId, ShaderPrograms.FindGroup(merged, 2));
        return programId;
    }

    public PipelineHandle Get(int programId, BlendMode blend)
    {
        if (_pipelines.TryGetValue((programId, blend), out var pipeline)) return pipeline;
        var (program, vertexEntry, fragmentEntry) = programId == 0
            ? (BuiltIn, "vertexMain", "fragmentMain")
            : _customPrograms[programId - 1];
        pipeline = _renderer.CreatePipeline(
            program,
            PbrTargets.HdrFormat, // the main pass emits LINEAR HDR; the composite pass tonemaps
            depthStencilFormat: TextureFormat.Depth32Float,
            blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque, // blended surfaces read but don't write depth
            fragmentEntryPoint: fragmentEntry, // always linear (the sRGB decision lives in composite)
            vertexEntryPoint: vertexEntry);
        _pipelines[(programId, blend)] = pipeline;
        return pipeline;
    }

    /// <summary>The skinned twin of <see cref="Get"/>. Identical except for the vertex entry
    /// point — which also selects its 20-float vertex layout, since the two are reflected
    /// together.</summary>
    public PipelineHandle GetSkinned(BlendMode blend)
    {
        if (_skinnedPipelines.TryGetValue(blend, out var pipeline)) return pipeline;
        pipeline = _renderer.CreatePipeline(
            BuiltIn,
            PbrTargets.HdrFormat,
            depthStencilFormat: TextureFormat.Depth32Float,
            blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque,
            fragmentEntryPoint: "fragmentMain",
            vertexEntryPoint: "vertexMainSkinned");
        _skinnedPipelines[blend] = pipeline;
        return pipeline;
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values) _renderer.DestroyPipeline(pipeline);
        foreach (var pipeline in _skinnedPipelines.Values) _renderer.DestroyPipeline(pipeline);
        _pipelines.Clear();
        _skinnedPipelines.Clear();
    }
}
