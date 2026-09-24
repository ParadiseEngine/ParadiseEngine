namespace Paradise.Rendering.Pbr;

/// <summary>What every PBR program needs done to its reflection before use.</summary>
internal static class ShaderPrograms
{
    public static ShaderProgramDesc Load(string name) =>
        ShaderProgramLoader.Load(typeof(PbrRenderer).Assembly, name);

    /// <summary>Group 0 becomes a dynamic-offset draw ring. That is a LAYOUT property, so the
    /// program's layout is rebuilt with the flag and both the pipelines and the bind group are
    /// created from the modified layout — the content-keyed layout cache keeps them compatible.</summary>
    public static ShaderProgramDesc WithDynamicDrawRing(ShaderProgramDesc program)
    {
        var groups = (BindGroupLayoutDesc[])program.Layout.Groups.Clone();
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i].GroupIndex != 0) continue;
            groups[i] = new BindGroupLayoutDesc(0, [groups[i].Entries[0] with { HasDynamicOffset = true }]);
        }
        return new ShaderProgramDesc(program.Modules, new PipelineLayoutDesc(groups, program.Layout.PushConstants), program.VertexBuffers)
        {
            UniformBlocks = program.UniformBlocks,
            // Must be carried across the rebuild: dropping it silently falls the skinned pipeline
            // back to the rigid 12-float layout, which draws nothing at all.
            VertexBuffersByEntryPoint = program.VertexBuffersByEntryPoint,
        };
    }

    public static BindGroupLayoutDesc? FindGroupOrNull(ShaderProgramDesc program, uint groupIndex)
    {
        foreach (var group in program.Layout.Groups)
            if (group.GroupIndex == groupIndex) return group;
        return null;
    }

    public static BindGroupLayoutDesc FindGroup(ShaderProgramDesc program, uint groupIndex) =>
        FindGroupOrNull(program, groupIndex)
        ?? throw new InvalidOperationException($"Program reflects no bind group {groupIndex}.");

    /// <summary>A position-only vertex layout over the full mesh stride, for the depth-only and
    /// position programs that declare location 0 alone.</summary>
    public static VertexBufferLayoutDesc[] PositionOnlyLayout(ulong meshStride) =>
    [
        new VertexBufferLayoutDesc(meshStride, VertexStepMode.Vertex,
            [new VertexAttributeDesc(0, VertexFormat.Float32x3, 0)]),
    ];
}
