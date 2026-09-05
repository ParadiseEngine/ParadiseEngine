#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Paradise.Authoring;

namespace Paradise.Export.Data
{
    // Engine-neutral level/scene data produced by the Paradise Engine export tools
    // and consumed by the Paradise Engine runtime loader.
    //
    // Ported verbatim from ParadiseUnityEditor (Runtime/Data/LevelDocument.cs) — this is the
    // fixed export contract and must stay byte-comparable across the Unity and Godot tools.
    //
    // Serialization contract: these are plain C# objects. The JSON writer (ExportJsonWriter)
    // serializes them with System.Text.Json (source-generated) using the C# property names as keys, a
    // StringEnumConverter for enums, and a custom converter that emits System.Numerics
    // vectors/quaternions/matrices as float arrays and Color32 as an { r, g, b, a } object.
    // Matrices are written column-major.
    //
    // Convention: Y-up, right-handed (−Z forward, Godot/glTF-standard), meters. Matrices are
    // column-major float[16]. The Godot exporter writes its values verbatim — no handedness
    // conversion (see CONVENTIONS.md).
    public sealed record PrefabData
    {
        /// <summary>Bumped when the SHAPE of this document changes in a way an existing reader
        /// would misparse.
        ///
        /// v6 removes the last privileged components: THE ENGINE DECLARES NO AUTHORED COMPONENTS
        /// AT ALL. The bake no longer flattens — entities ship their authored payloads verbatim,
        /// including the well-known <c>meta</c> (identity, name, parent) and <c>transform</c>
        /// (local TRS) components (<see cref="WellKnownEntityComponents"/>) — so identity and
        /// hierarchy now SURVIVE into the contract and the loader composes world matrices itself.
        /// The engine's thirteen <c>*ComponentData</c> records (name, transform, renderable, …)
        /// are gone with the flatten; every component in a v6 document is the game's own
        /// declaration. A v5 document's baked <c>World</c> matrices have no reader here any more,
        /// which is exactly what the gate is for.
        ///
        /// v5 reduced the document to its entities and an entity to its authored components; v4
        /// moved material slots onto the renderable; v3 replaced named slots with one list. All
        /// below the floor, none shimmed.
        ///
        /// REJECTED on read — see <see cref="Serialization.ExportJsonReader.ReadLevel"/>.</summary>
        public const int CurrentSchemaVersion = 6;

        /// <summary>The oldest document this build still understands. Equal to
        /// <see cref="CurrentSchemaVersion"/>, and that is the point rather than an oversight.
        ///
        /// A shim is a second reading of the format that lives forever: every reader after it has
        /// to know both shapes, and the migration nobody is forced to do is the one nobody does.
        /// Re-export the scene from its editor.</summary>
        public const int MinimumSupportedVersion = 6;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// The scene: one entry per object, and an object IS its authored components.
        ///
        /// <b>There is no entity record any more, and no privileged components either.</b> An
        /// entity's identity, name, parent and local TRS travel as the well-known <c>meta</c> and
        /// <c>transform</c> payloads (<see cref="WellKnownEntityComponents"/>), passed through
        /// from the authoring document exactly like a game's own components; the loader is what
        /// gives them meaning. One rule for the whole document: a host writes components, a
        /// runtime reads components, and adding a fact about an object is adding a record with a
        /// <c>[Guid]</c> rather than a field here plus a mirror in every editor.
        ///
        /// ORDER IS THE DOCUMENT'S and is load-bearing: a runtime that assigns entity handles in
        /// walk order gets the same handle for the same object in every world it builds only
        /// because this list is a pure function of the export.
        /// </summary>
        public List<List<AuthoredComponentData>> Entities { get; set; } = new();
    }

    /// <summary>
    /// One game-defined component riding along with an entity: a stable id and an opaque payload.
    ///
    /// <see cref="Data"/> is a <see cref="JsonElement"/> on purpose. The engine cannot name the
    /// type — that is the entire point of the mechanism — so it carries the payload untouched and
    /// the GAME deserializes it into its own record through its own source-generated
    /// <c>JsonSerializerContext</c>. Handing it over as a live object instead would force a
    /// reflection serializer somewhere, which pins Godot's collectible AssemblyLoadContext and
    /// breaks C# hot-reload (godotengine/godot#78513) — the documented reason this whole contract
    /// is source-generated.
    /// </summary>
    public sealed record AuthoredComponentData
    {
        /// <summary>The <c>[Authored]</c> component id. What this payload is resolved by.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Fully qualified CLR name of the record, e.g. <c>Pingu.Core.Authoring.PoolConfig</c>.
        ///
        /// Written by every editor and read only when <see cref="Id"/> fails to resolve. It is what
        /// keeps this document diagnosable by a human: a GUID alone tells a reader — and the person
        /// staring at a payload that loaded as nothing — precisely nothing about what was meant.
        ///
        /// Optional on the wire so a document that predates it still reads.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>The serialized record, exactly as the editor wrote it.</summary>
        public JsonElement Data { get; set; }
    }

    public sealed record PhysicsSettingsData
    {
        public PhysicsCollisionMatrixData CollisionMatrix { get; set; } = new();
        public PhysicsDynamicsSettingsData Dynamics { get; set; } = new();
    }

    // Global dynamics-solver tuning authored in editor project settings (Paradise/Settings…)
    // and applied by the runtime simulation. Defaults are the values that were hardcoded in
    // the solver before the section existed, so a missing section behaves identically.
    public sealed record PhysicsDynamicsSettingsData
    {
        // Speeds below this snap to rest (m/s).
        public float MinSpeed { get; set; } = 0.005f;

        // Clearance kept between dynamic bodies and static surfaces (meters) — the
        // speculative-contact margin (PhysX contactOffset analog).
        public float Skin { get; set; } = 0.02f;

        // Scale applied to a kinematic pusher's velocity when injected into a body.
        public float PushStrength { get; set; } = 1.2f;

        // Body ↔ static bounce used when no static entity in the scene authors a
        // Restitution on an obstacle-layer collider (cushion-less scenes).
        public float DefaultStaticRestitution { get; set; } = 0.4f;

        // Gravity acceleration (m/s²) applied to every ball; vertical (−Y). Balls now rest on the
        // felt via contact, so this is what holds them down and drives draw/jump/masse.
        public float GravityY { get; set; } = -9.81f;

        // Coulomb friction coefficient for ball ↔ static (cushion/cloth) contacts — the coupling
        // that turns spin into path change (draw/follow/english/throw).
        public float StaticFriction { get; set; } = 0.2f;

        // Angular speeds below this settle to rest when a ball is supported (rad/s).
        public float MinAngularSpeed { get; set; } = 0.05f;

        public void ValidateAndNormalize()
        {
            MinSpeed = float.IsFinite(MinSpeed) && MinSpeed >= 0f ? MinSpeed : 0.005f;
            Skin = float.IsFinite(Skin) ? Math.Clamp(Skin, 0.001f, 0.5f) : 0.02f;
            PushStrength = float.IsFinite(PushStrength) && PushStrength >= 0f ? PushStrength : 1.2f;
            DefaultStaticRestitution = float.IsFinite(DefaultStaticRestitution)
                ? Math.Clamp(DefaultStaticRestitution, 0f, 1f)
                : 0.4f;
            // Must point DOWN — a positive value would silently invert gravity (balls fly up).
            GravityY = float.IsFinite(GravityY) && GravityY <= 0f ? GravityY : -9.81f;
            StaticFriction = float.IsFinite(StaticFriction) && StaticFriction >= 0f ? StaticFriction : 0.2f;
            MinAngularSpeed = float.IsFinite(MinAngularSpeed) && MinAngularSpeed >= 0f ? MinAngularSpeed : 0.05f;
        }
    }

    public sealed record PhysicsCollisionMatrixData
    {
        public List<int> LayerMasks { get; set; } = new();
    }

    // Renderer/quality settings authored in the editor and applied by the runtime renderer.
    // Engine-neutral; consumed as a puppet config.
    public sealed record RenderSettingsData
    {
        // Supersampling factor (SSAA). 1 = native.
        public float RenderScale { get; set; } = 1f;

        // MSAA sample count for the main pass: 1 = off, otherwise 4.
        public int MsaaSamples { get; set; } = 1;

        // Max anisotropic filtering for material textures (1 = off, up to 16).
        public int AnisotropicLevel { get; set; } = 16;

        // Geometric specular-AA strength (renderer-only).
        public float SpecularAaVariance { get; set; } = 0.5f;
        public float SpecularAaClamp { get; set; } = 0.25f;

        public void ValidateAndNormalize()
        {
            RenderScale = Math.Clamp(float.IsFinite(RenderScale) ? RenderScale : 1f, 1f, 4f);
            MsaaSamples = MsaaSamples >= 2 ? 4 : 1;
            AnisotropicLevel = Math.Clamp(AnisotropicLevel, 1, 16);
            SpecularAaVariance = Math.Max(0f, float.IsFinite(SpecularAaVariance) ? SpecularAaVariance : 0.5f);
            SpecularAaClamp = Math.Max(0f, float.IsFinite(SpecularAaClamp) ? SpecularAaClamp : 0.25f);
        }
    }

    public sealed record ProjectSettingsData
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public PhysicsSettingsData Physics { get; set; } = new();
        public RenderSettingsData Rendering { get; set; } = new();
    }

    public sealed record LevelMaterialData
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public Color32 BaseColorFactor { get; set; } = Color32.FromRgba(1f, 1f, 1f);
        public string? BaseColorTexture { get; set; }
        public float MetallicFactor { get; set; } = 1f;
        public float RoughnessFactor { get; set; } = 1f;
        public string? MetallicRoughnessTexture { get; set; }
        public Color32 EmissiveFactor { get; set; } = Color32.FromRgba(0f, 0f, 0f);
        public string? EmissiveTexture { get; set; }
        public float NormalScale { get; set; } = 1f;
        public string? NormalTexture { get; set; }
        public float OcclusionStrength { get; set; } = 1f;
        public string? OcclusionTexture { get; set; }
        // KHR_texture_transform on the base-colour uv set, which until materials were documents
        // only the glTF material inside a GLB could express.
        public float[] BaseColorUvOffset { get; set; } = [0f, 0f];
        public float[] BaseColorUvScale { get; set; } = [1f, 1f];
        public float BaseColorUvRotation { get; set; }
        public string AlphaMode { get; set; } = "Opaque";
        public int RenderQueue { get; set; } = -1;
        public float TransmissionFactor { get; set; }
        // Procedural animated material (noise recipe in the runtime shader). MaterialKind names the
        // recipe ("lava", "marble", "jade", "ice", "gem", "molten_metal", "obsidian", "amber",
        // "nebula"); "" = a normal PBR material. EmissiveStrength is an UNCLAMPED HDR multiplier on
        // EmissiveFactor (so lava can bloom past white). ColorA/B tint the tintable recipes.
        public string MaterialKind { get; set; } = "";
        public float EmissiveStrength { get; set; } = 1f;
        public float NoiseScale { get; set; } = 1f;
        public float FlowSpeed { get; set; } = 1f;
        public Color32 ColorA { get; set; } = Color32.FromRgba(1f, 1f, 1f);
        public Color32 ColorB { get; set; } = Color32.FromRgba(0f, 0f, 0f);
    }

    /// <summary>
    /// One collision shape, AUTHORED by pointing at the host's own shape object and edited with its
    /// native handles — every field below is baked out of that object at export.
    /// </summary>
    [AuthoredByHost<HostShape>]
    public class ColliderShapeData
    {
        public string? Id { get; set; }
        public string? Path { get; set; }
        public bool IsStatic { get; set; }
        public int Layer { get; set; }
        public string? LayerName { get; set; }
        public bool IsTrigger { get; set; }
        public PhysicsShapeType ShapeType { get; set; }
        public Vector3 LocalCenter { get; set; } = Vector3.Zero;
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 Size { get; set; } = Vector3.Zero;
        public float Radius { get; set; }
        public float Height { get; set; }
        public NavObstacleData? NavObstacle { get; set; }
    }

    public sealed class NavObstacleData
    {
        public string Shape { get; set; } = "";
        public Vector3 Center { get; set; } = Vector3.Zero;
        public Vector3 Size { get; set; } = Vector3.Zero;
        public float Radius { get; set; }
        public float Height { get; set; }
        public bool Carving { get; set; }
        public bool CarveOnlyStationary { get; set; }
        public float CarvingMoveThreshold { get; set; }
        public float CarvingTimeToStationary { get; set; }
    }

    // Engine-neutral physics enums (mirrors the Paradise Engine runtime contract).
    // Serialized by name via the JSON writer's StringEnumConverter.
    public enum PhysicsBodyType
    {
        None,
        Static,
        Kinematic,
        Dynamic,
    }

    public enum PhysicsShapeType
    {
        Box,
        Sphere,
        Capsule,
    }

    // Packed RGBA color (8 bits per channel). Float channel accessors feed the JSON
    // writer, which emits { r, g, b, a } objects.
    public readonly struct Color32
    {
        public readonly int Rgba;

        public Color32(int rgba)
        {
            Rgba = rgba;
        }

        public static Color32 FromRgba(float red, float green, float blue, float alpha = 1f) =>
            new(PackRgba(red, green, blue, alpha));

        public float R => ((uint)Rgba >> 24) / 255f;
        public float G => (((uint)Rgba >> 16) & 0xff) / 255f;
        public float B => (((uint)Rgba >> 8) & 0xff) / 255f;
        public float A => ((uint)Rgba & 0xff) / 255f;

        public Vector3 Rgb => new(R, G, B);
        public Vector4 ToVector4() => new(R, G, B, A);

        private static int PackRgba(float red, float green, float blue, float alpha) =>
            unchecked((int)(
                ((uint)ToByte(red) << 24) |
                ((uint)ToByte(green) << 16) |
                ((uint)ToByte(blue) << 8) |
                ToByte(alpha)));

        private static byte ToByte(float value)
        {
            if (float.IsNaN(value) || float.IsNegativeInfinity(value))
            {
                return 0;
            }

            if (float.IsPositiveInfinity(value))
            {
                return byte.MaxValue;
            }

            value = MathF.Min(MathF.Max(value, 0f), 1f);
            return (byte)MathF.Round(value * byte.MaxValue, MidpointRounding.AwayFromZero);
        }
    }
}
