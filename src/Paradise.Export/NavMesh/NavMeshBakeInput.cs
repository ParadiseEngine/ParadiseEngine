using System.Text.Json.Serialization;

namespace Paradise.Export.NavMesh;

/// <summary>World-space, right-handed Y-up triangles and the agent that must traverse them.</summary>
public sealed class NavMeshBakeInput
{
    public float[][] Vertices { get; set; } = [];
    public int[] Indices { get; set; } = [];
    public NavMeshBakeSettings Settings { get; set; } = new();
}

/// <summary>Recast voxel and walkability settings in meters, with slope in degrees.</summary>
public sealed class NavMeshBakeSettings
{
    public float CellSize { get; set; } = 0.2f;
    public float CellHeight { get; set; } = 0.1f;
    public float AgentRadius { get; set; } = 0.35f;
    public float AgentHeight { get; set; } = 1.8f;
    public float MaxClimb { get; set; } = 0.3f;
    public float MaxSlope { get; set; } = 45f;
}

/// <summary>Triangles from the cooked walkable surface, in the input coordinate system.</summary>
public sealed class NavMeshPreview
{
    public float[][] Vertices { get; set; } = [];
    public int[] Indices { get; set; } = [];
}

/// <summary>A baked Detour MeshSet and its display geometry.</summary>
public sealed record NavMeshBakeResult(byte[] Bytes, NavMeshPreview Preview);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(NavMeshBakeInput))]
[JsonSerializable(typeof(NavMeshPreview))]
internal sealed partial class NavMeshBakeJsonContext : JsonSerializerContext;
