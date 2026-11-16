namespace Paradise.Rendering.Pbr;

/// <summary>Indexes a pass's pipeline table by skinning and <see cref="CullMode"/>.</summary>
/// <remarks>The one place that knows how many cull modes exist; a test pins <see cref="CullModes"/>
/// to the enum, so adding a member fails there rather than indexing past a table.</remarks>
internal static class PipelineVariants
{
    /// <summary>The members of <see cref="CullMode"/>, valued 0 to this minus one.</summary>
    public const int CullModes = 3;

    /// <summary>Rigid and skinned, each in every cull mode.</summary>
    public const int Count = 2 * CullModes;

    public static int Index(bool skinned, CullMode cull) => (skinned ? CullModes : 0) + (int)cull;

    public static bool IsSkinned(int variant) => variant >= CullModes;

    public static CullMode Cull(int variant) => (CullMode)(variant % CullModes);
}
