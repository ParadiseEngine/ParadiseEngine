using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>A preorder light BVH node, matching <c>GiLightNode</c> in giLights.slang.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct GiLightNodeGpu
{
    public Vector3 Min;
    public uint Escape;
    public Vector3 Max;
    public uint Light;
}

[InlineArray(GiLightHierarchy.MaxNodes)]
internal struct GiLightNodeArray
{
    private GiLightNodeGpu _element0;
}

/// <summary>World-space light bounds and unbounded-light masks, matching <c>GiLightTree</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GiLightTreeGpu
{
    public uint NodeCount;
    public uint GlobalMask0;
    public uint GlobalMask1;
    private uint _padding;
    public GiLightNodeArray Nodes;
}

/// <summary>Conservatively selects lights for arbitrary world-space GI ray hits.</summary>
/// <remarks>Directionals and local lights with nonpositive range are unbounded in lighting.slang.
/// Finite local lights form a balanced BVH; preorder escape links avoid a traversal stack. The
/// resulting mask retains scene slots so the shader sums contributing lights in original order.</remarks>
internal sealed class GiLightHierarchy
{
    public const int MaxNodes = FrameUniformsGpu.MaxSceneLights * 2 - 1;
    public const uint Branch = uint.MaxValue;

    private struct LightBounds
    {
        public Vector3 Min;
        public Vector3 Max;
        public Vector3 Centre;
        public uint Slot;
    }

    private readonly struct AxisComparer(int axis) : IComparer<LightBounds>
    {
        public int Compare(LightBounds x, LightBounds y)
        {
            var comparison = x.Centre[axis].CompareTo(y.Centre[axis]);
            return comparison != 0 ? comparison : x.Slot.CompareTo(y.Slot);
        }
    }

    private readonly LightBounds[] _lights = new LightBounds[FrameUniformsGpu.MaxSceneLights];
    private GiLightTreeGpu _data;

    public ref readonly GiLightTreeGpu Data => ref _data;

    public void Build(IReadOnlyList<PbrLight> lights, bool spatialCulling = true)
    {
        _data.NodeCount = 0;
        _data.GlobalMask0 = 0;
        _data.GlobalMask1 = 0;
        var bounded = 0;
        for (var slot = 0; slot < Math.Min(lights.Count, FrameUniformsGpu.MaxSceneLights); slot++)
        {
            var light = lights[slot];
            // ToGpu clamps negative indirect energy to zero. Intensity and colour may be signed;
            // retain their contributions rather than assuming physically positive authored data.
            if (light.IndirectEnergy <= 0f || light.Intensity == 0f || light.Color == Vector3.Zero) continue;
            if (!spatialCulling || light.Type == PbrLightType.Directional || light.Range <= 0f || !float.IsFinite(light.Range)
                || !Finite(light.Position))
            {
                if (slot < 32) _data.GlobalMask0 |= 1u << slot;
                else _data.GlobalMask1 |= 1u << (slot - 32);
                continue;
            }

            var min = light.Position - new Vector3(light.Range);
            var max = light.Position + new Vector3(light.Range);
            // Round outward: a float subtraction used for a bound must not reject a point whose
            // independently rounded shader distance is still inside the light's range.
            _lights[bounded++] = new LightBounds
            {
                Min = new Vector3(MathF.BitDecrement(min.X), MathF.BitDecrement(min.Y), MathF.BitDecrement(min.Z)),
                Max = new Vector3(MathF.BitIncrement(max.X), MathF.BitIncrement(max.Y), MathF.BitIncrement(max.Z)),
                Centre = light.Position,
                Slot = (uint)slot,
            };
        }
        if (bounded > 0) BuildNode(_lights.AsSpan(0, bounded));
    }

    private int BuildNode(Span<LightBounds> lights)
    {
        var index = (int)_data.NodeCount++;
        var min = lights[0].Min;
        var max = lights[0].Max;
        var centreMin = lights[0].Centre;
        var centreMax = centreMin;
        foreach (var light in lights[1..])
        {
            min = Vector3.Min(min, light.Min);
            max = Vector3.Max(max, light.Max);
            centreMin = Vector3.Min(centreMin, light.Centre);
            centreMax = Vector3.Max(centreMax, light.Centre);
        }
        if (lights.Length > 1)
        {
            var extent = centreMax - centreMin;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            lights.Sort(new AxisComparer(axis));
            var middle = lights.Length / 2;
            BuildNode(lights[..middle]);
            BuildNode(lights[middle..]);
        }
        _data.Nodes[index] = new GiLightNodeGpu
        {
            Min = min,
            Max = max,
            Escape = _data.NodeCount,
            Light = lights.Length == 1 ? lights[0].Slot : Branch,
        };
        return index;
    }

    /// <summary>The CPU twin of <c>giLightMask</c>, including the number of bounds inspected.</summary>
    public ulong Query(Vector3 position, out int visited)
    {
        var mask = (ulong)_data.GlobalMask0 | ((ulong)_data.GlobalMask1 << 32);
        var index = 0;
        visited = 0;
        while (index < _data.NodeCount)
        {
            ref readonly var node = ref _data.Nodes[index];
            visited++;
            if (position.X < node.Min.X || position.Y < node.Min.Y || position.Z < node.Min.Z
                || position.X > node.Max.X || position.Y > node.Max.Y || position.Z > node.Max.Z)
            {
                index = (int)node.Escape;
                continue;
            }
            if (node.Light != Branch) mask |= 1ul << (int)node.Light;
            index++;
        }
        return mask;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
