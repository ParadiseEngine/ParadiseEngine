namespace Paradise.Rendering.Pbr;

/// <summary>The built-in PBR program, the game-registered extensions of it, and the pipeline
/// variants built from either: opaque or blend, rigid or skinned.</summary>
internal sealed class MaterialPrograms : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly Dictionary<(int ProgramId, BlendMode Blend), PipelineHandle> _pipelines = new();
    private readonly Dictionary<(int ProgramId, bool Skinned, BlendMode Blend), PipelineHandle> _instancedPipelines = [];
    // Skinned twins of the built-in pipelines, built lazily so a scene with nothing skinned never
    // compiles them.
    private readonly Dictionary<BlendMode, PipelineHandle> _skinnedPipelines = new();
    // Game-registered material programs (Register): index + 1 = programId; 0 is the built-in PBR
    // program. Each entry stores the MERGED desc (custom modules over the built-in pipeline
    // layout, see Register) plus its entry-point names.
    private readonly List<CustomProgram?> _customPrograms = [];
    private int _customProgramCount;
    private ShaderProgramDesc? _instanced;
    private bool _disposed;

    private sealed record CustomProgram(ShaderProgramDesc Program, string VertexEntry, string FragmentEntry,
        ShaderProgramDesc? InstancedProgram, string? InstancedVertexEntry, string? InstancedFragmentEntry);

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

    public ShaderProgramDesc Instanced
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _instanced ??= ShaderPrograms.Load("Shaders.pbrInstanced");
        }
    }

    /// <summary>The interleaved mesh stride every program's vertex stream shares.</summary>
    public ulong MeshStride => BuiltIn.VertexBuffers[0].Stride;

    public BindGroupLayoutDesc Group(uint groupIndex) => ShaderPrograms.FindGroup(BuiltIn, groupIndex);

    public int PipelineCount => _pipelines.Count;
    public int SkinnedPipelineCount => _skinnedPipelines.Count;
    public int CustomProgramCount => _customProgramCount;

    /// <summary>Register a game-supplied program. See <see cref="PbrRenderer.RegisterMaterialProgram(ShaderProgramDesc, MaterialProgramOptions, string, string)"/>
    /// for the contract; this is its body.</summary>
    public int Register(MaterialResourceCache materials, ShaderProgramDesc program, string vertexEntryPoint,
        string fragmentEntryPoint, MaterialProgramOptions options = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UniformLayoutValidator.Validate(program);
        ValidateEntry(program, vertexEntryPoint, ShaderStage.Vertex);
        ValidateEntry(program, fragmentEntryPoint, ShaderStage.Fragment);
        var instancedVertex = options.InstancedVertexEntryPoint;
        if (options.InstancedFragmentEntryPoint is not null && instancedVertex is null)
            throw new ArgumentException("An instanced fragment entry requires an instanced vertex entry.", nameof(options));
        var instancedFragment = instancedVertex is null ? null : options.InstancedFragmentEntryPoint ?? fragmentEntryPoint;
        if (instancedVertex is not null)
        {
            ValidateEntry(program, instancedVertex, ShaderStage.Vertex);
            ValidateEntry(program, instancedFragment!, ShaderStage.Fragment);
        }

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
                var instanceStorage = group.GroupIndex == 0 && entry.Binding == 1 && instancedVertex is not null;
                var actual = group.GroupIndex == 0 && !instanceStorage ? entry with { HasDynamicOffset = true } : entry;
                BindGroupLayoutEntryDesc? expected = null;
                var expectedGroup = instanceStorage ? ShaderPrograms.FindGroup(Instanced, 0) : builtIn;
                if (expectedGroup is not null)
                {
                    foreach (var candidate in expectedGroup.Entries)
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
        if (instancedVertex is not null)
        {
            if (!program.VertexBuffersByEntryPoint.TryGetValue(instancedVertex, out var instancedLayout)
                || instancedLayout.Length != vertexLayout.Length
                || !instancedLayout.Zip(vertexLayout).All(static layouts => SameVertexLayout(layouts.First, layouts.Second)))
                throw new InvalidOperationException(
                    $"Custom material program's '{instancedVertex}' must consume the same rigid vertex layout as '{vertexEntryPoint}'.");
            if (!program.Layout.Groups.Any(static group => group.GroupIndex == 0 && group.Entries.Any(static entry => entry.Binding == 1)))
                throw new InvalidOperationException("Custom instanced material programs must declare instance storage at group 0 binding 1.");
        }

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

        ShaderProgramDesc? instanced = null;
        if (instancedVertex is not null)
        {
            var instancedGroups = (BindGroupLayoutDesc[])mergedGroups.Clone();
            for (var i = 0; i < instancedGroups.Length; i++)
                if (instancedGroups[i].GroupIndex == 0) instancedGroups[i] = ShaderPrograms.FindGroup(Instanced, 0);
            instanced = merged with { Layout = new PipelineLayoutDesc(instancedGroups, BuiltIn.Layout.PushConstants) };
        }
        var custom = new CustomProgram(merged, vertexEntryPoint, fragmentEntryPoint,
            instanced, instancedVertex, instancedFragment);
        var programId = _customPrograms.Count + 1;
        materials.RegisterProgramLayout(programId, ShaderPrograms.FindGroup(merged, 2), options);
        try
        {
            _customPrograms.Add(custom);
        }
        catch
        {
            materials.ReleaseProgramLayout(programId);
            throw;
        }
        _customProgramCount++;
        return programId;
    }

    public bool Release(MaterialResourceCache materials, int programId)
    {
        if (_disposed || programId <= 0 || programId > _customPrograms.Count || _customPrograms[programId - 1] is null)
            return false;
        // Layout removal validates dependencies before any pipeline or program state changes.
        materials.ReleaseProgramLayout(programId);
        _customPrograms[programId - 1] = null;
        _customProgramCount--;
        foreach (var key in _pipelines.Keys.Where(key => key.ProgramId == programId).ToArray())
        {
            _pipelines.Remove(key, out var pipeline);
            _renderer.DestroyPipeline(pipeline);
        }
        foreach (var key in _instancedPipelines.Keys.Where(key => key.ProgramId == programId).ToArray())
        {
            _instancedPipelines.Remove(key, out var pipeline);
            _renderer.DestroyPipeline(pipeline);
        }
        return true;
    }

    private static void ValidateEntry(ShaderProgramDesc program, string entryPoint, ShaderStage stage)
    {
        if (!program.Modules.Any(module => module.Stage == stage && module.EntryPoint == entryPoint))
            throw new InvalidOperationException($"Custom material program has no {stage.ToString().ToLowerInvariant()} entry point '{entryPoint}'.");
    }

    private static bool SameVertexLayout(VertexBufferLayoutDesc first, VertexBufferLayoutDesc second) =>
        first.Stride == second.Stride && first.StepMode == second.StepMode && first.Attributes.SequenceEqual(second.Attributes);

    public (ShaderProgramDesc Program, string VertexEntry, string FragmentEntry) GetInstanced(int programId, bool skinned)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (programId == 0) return (Instanced, skinned ? "vertexMainSkinned" : "vertexMain", "fragmentMain");
        var custom = GetCustom(programId);
        if (skinned || custom.InstancedProgram is null)
            throw new ArgumentException($"Material program {programId} does not support this instanced vertex path.", nameof(programId));
        return (custom.InstancedProgram!, custom.InstancedVertexEntry!, custom.InstancedFragmentEntry!);
    }

    public PipelineHandle GetInstancedPipeline(int programId, bool skinned, BlendMode blend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instancedPipelines.TryGetValue((programId, skinned, blend), out var pipeline)) return pipeline;
        var (program, vertexEntry, fragmentEntry) = GetInstanced(programId, skinned);
        pipeline = _renderer.CreatePipeline(program, PbrTargets.HdrFormat,
            depthStencilFormat: TextureFormat.Depth32Float, blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque,
            vertexEntryPoint: vertexEntry, fragmentEntryPoint: fragmentEntry);
        _instancedPipelines.Add((programId, skinned, blend), pipeline);
        return pipeline;
    }

    private CustomProgram GetCustom(int programId)
    {
        if (programId <= 0 || programId > _customPrograms.Count || _customPrograms[programId - 1] is not { } custom)
            throw new ArgumentException($"Material program {programId} is unknown or has been released.", nameof(programId));
        return custom;
    }

    public PipelineHandle Get(int programId, BlendMode blend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipelines.TryGetValue((programId, blend), out var pipeline)) return pipeline;
        var custom = programId == 0 ? null : GetCustom(programId);
        var (program, vertexEntry, fragmentEntry) = custom is null
            ? (BuiltIn, "vertexMain", "fragmentMain")
            : (custom.Program, custom.VertexEntry, custom.FragmentEntry);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (_disposed) return;
        _disposed = true;
        foreach (var pipeline in _pipelines.Values) _renderer.DestroyPipeline(pipeline);
        foreach (var pipeline in _skinnedPipelines.Values) _renderer.DestroyPipeline(pipeline);
        foreach (var pipeline in _instancedPipelines.Values) _renderer.DestroyPipeline(pipeline);
        _pipelines.Clear();
        _skinnedPipelines.Clear();
        _instancedPipelines.Clear();
        _customPrograms.Clear();
        _customProgramCount = 0;
    }
}
