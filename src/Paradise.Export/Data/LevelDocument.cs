#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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
    public sealed record LevelData
    {
        public const int UnversionedSchemaVersion = 1;
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public CameraData? Camera { get; set; }
        public LightingData? Lighting { get; set; }
        public NavMeshAgentData? NavMeshAgent { get; set; }
        public List<InteractableData> Interactables { get; set; } = new();
        public List<LevelEntityData> Entities { get; set; } = new();
        public string? NavMeshFile { get; set; }
        public List<LevelMaterialData> Materials { get; set; } = new();
    }

    public sealed record PrefabTemplateData
    {
        public string? DisplayName { get; set; }
        public string? Prefab { get; set; }
        public string? PrefabAssetPath { get; set; }
        public string? PrefabGuid { get; set; }
        public string? PrefabAssetType { get; set; }
        public List<string?> Materials { get; set; } = new();
        public List<LevelEntityData> Entities { get; set; } = new();
    }

    public sealed record CameraData
    {
        public Vector3 Position { get; set; } = Vector3.Zero;
        public Vector3 Rotation { get; set; } = Vector3.Zero;
        public float OrthographicSize { get; set; } = 10f;
        public Color32 BackgroundColor { get; set; } = Color32.FromRgba(0.72f, 0.69f, 0.67f);
    }

    public sealed record LevelEntityData
    {
        public string Id { get; set; } = "";
        public Guid EntityGuid { get; set; }
        public string? StableId { get; set; }
        public string? DisplayName { get; set; }
        public string? Kind { get; set; }
        public string? SpawnPhase { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Prefab { get; set; }
        public string? PrefabAssetPath { get; set; }
        public string? NearestInstanceRoot { get; set; }
        public string? PrefabGuid { get; set; }
        public string? PrefabAssetType { get; set; }
        public string? InitialAnimation { get; set; }
        public EntityParentData? Parent { get; set; }
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 LocalScale { get; set; } = Vector3.One;
        public Matrix4x4? LocalMatrix { get; set; }
        public Matrix4x4? WorldMatrix { get; set; }
        public List<string?> Materials { get; set; } = new();
        public PrefabOverrideData Overrides { get; set; } = new();
        public EntityComponentsData Components { get; set; } = new();
    }

    public sealed record EntityParentData
    {
        public string Id { get; set; } = "";
        public string? BonePath { get; set; }
        public int BoneIndex { get; set; } = -1;
    }

    public sealed record EntityComponentsData
    {
        public RenderableComponentData? Renderable { get; set; }
        public ColliderComponentData? Collider { get; set; }
        public RigidbodyComponentData? Rigidbody { get; set; }
        public EntityInteractableComponentData? Interactable { get; set; }
        public AgentComponentData? Agent { get; set; }
        public SpriteAnimationComponentData? SpriteAnimation { get; set; }
        public ParticleEmitterComponentData? ParticleEmitter { get; set; }
    }

    /// <summary>
    /// Renderable marker + mesh reference (schema v2). <see cref="Mesh"/> is a GLB path
    /// relative to <c>data/</c> (e.g. <c>meshes/&lt;key&gt;.glb</c>) holding the entity's visual
    /// subtree in ENTITY-LOCAL space (the entity's WorldMatrix places it). Contract rule: the
    /// GLB's primitive order equals <see cref="LevelEntityData.Materials"/> slot order — both
    /// walk the same MeshInstance3D traversal; a null slot means the GLB's own embedded
    /// material is authoritative. Textures inside the GLB are ALWAYS KTX2 (the toktx pass is
    /// mandatory for textured meshes; the engine reader rejects PNG/JPEG). <see cref="MeshNode"/>
    /// optionally names a single node inside the GLB (reserved; null = whole default scene).
    /// Schema v1 documents carry neither field (null = no mesh exported).
    /// </summary>
    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000001")]
    public sealed record RenderableComponentData
    {
        public string? Mesh { get; set; }
        public string? MeshNode { get; set; }
    }

    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000002")]
    public sealed record ColliderComponentData
    {
        public List<ColliderShapeData> Colliders { get; set; } = new();
    }

    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000003")]
    public sealed record RigidbodyComponentData
    {
        public PhysicsBodyType BodyType { get; set; }
        public float Mass { get; set; } = 1f;
        public float LinearDamping { get; set; } = 0.2f;
        public float Restitution { get; set; } = 0.2f;
        public float Friction { get; set; } = 0.5f;
        public int Layer { get; set; }
        public string? LayerName { get; set; }
    }

    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000004")]
    public sealed record AgentComponentData
    {
        public float MoveSpeed { get; set; } = 1.4f;
        public float Acceleration { get; set; } = 40f;
        public string? IdleClip { get; set; }
        public string? WalkClip { get; set; }
    }

    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000005")]
    public sealed record EntityInteractableComponentData
    {
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// Flipbook 2D animation on a world-space quad. <see cref="Sheet"/> is a spritesheet
    /// texture path relative to <c>data/</c> with the runtime (KTX2) extension
    /// (e.g. <c>sprites/torch.ktx2</c>) — the Godot editor renders the source image next to
    /// it; the .NET runtime reads the KTX2 sidecar produced by the data-ingest pass. Frames
    /// are laid out row-major, left-to-right then top-to-bottom; <see cref="FrameCount"/> 0
    /// means the full <see cref="Columns"/>×<see cref="Rows"/> grid. The SIMULATION owns the
    /// clock (frame index lives in the world snapshot) so both hosts show the same frame.
    /// </summary>
    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000006")]
    public sealed record SpriteAnimationComponentData
    {
        public string? Sheet { get; set; }
        public int Columns { get; set; } = 1;
        public int Rows { get; set; } = 1;
        public int FrameCount { get; set; }
        public float Fps { get; set; } = 10f;
        public bool Loop { get; set; } = true;
        /// <summary>World size of the quad (meters, X = width, Y = height).</summary>
        public Vector2 QuadSize { get; set; } = Vector2.One;
        /// <summary>Face the camera (Y-billboard is not modelled — full billboard or fixed).</summary>
        public bool Billboard { get; set; } = true;

        public void ValidateAndNormalize()
        {
            Columns = Math.Max(1, Columns);
            Rows = Math.Max(1, Rows);
            FrameCount = Math.Clamp(FrameCount <= 0 ? Columns * Rows : FrameCount, 1, Columns * Rows);
            Fps = float.IsFinite(Fps) && Fps > 0f ? Fps : 10f;
            QuadSize = new Vector2(
                float.IsFinite(QuadSize.X) && QuadSize.X > 0f ? QuadSize.X : 1f,
                float.IsFinite(QuadSize.Y) && QuadSize.Y > 0f ? QuadSize.Y : 1f);
        }
    }

    /// <summary>
    /// A deterministic particle emitter simulated by the shared runtime (seeded RNG, fixed
    /// tick — particle state lives in world snapshots, so both hosts render identical
    /// particles). <see cref="Kind"/> picks the render primitive: <c>Sprite</c> = camera-facing
    /// quads flipbook-animated from <see cref="Sheet"/> (2D particles);
    /// <c>Voxel</c> = solid cubes (3D particles), tinted by <see cref="Color"/>.
    /// Particles emit in a cone of <see cref="SpreadDegrees"/> half-angle around the entity's
    /// +Y axis and live in WORLD space (a moving emitter leaves a trail).
    /// </summary>
    [ParadiseComponent("a1d3f6b0-0000-4000-8000-000000000007")]
    public sealed record ParticleEmitterComponentData
    {
        public ParticleRenderKind Kind { get; set; } = ParticleRenderKind.Sprite;
        /// <summary>Live-particle cap; clamped to the runtime's per-emitter buffer (64).</summary>
        public int MaxParticles { get; set; } = 64;
        public float EmitRate { get; set; } = 8f;
        public float LifetimeSeconds { get; set; } = 1.5f;
        public float InitialSpeed { get; set; } = 2f;
        public float SpreadDegrees { get; set; } = 25f;
        /// <summary>Y acceleration (m/s²); negative pulls down.</summary>
        public float Gravity { get; set; } = -9.8f;
        /// <summary>Per-second linear damping applied to particle velocity.</summary>
        public float Drag { get; set; }
        /// <summary>World size at birth/death (quad edge for Sprite, cube edge for Voxel).</summary>
        public float StartSize { get; set; } = 0.25f;
        public float EndSize { get; set; } = 0.25f;
        /// <summary>RNG seed — same seed, same particle stream in both hosts.</summary>
        public uint Seed { get; set; } = 1;
        /// <summary>Tint (Sprite: multiplies the sheet; Voxel: the cube albedo).</summary>
        public Color32 Color { get; set; } = Color32.FromRgba(1f, 1f, 1f);

        // Sprite kind only: flipbook sheet (same conventions as SpriteAnimationComponentData).
        // Fps 0 stretches the flipbook once over each particle's lifetime.
        public string? Sheet { get; set; }
        public int Columns { get; set; } = 1;
        public int Rows { get; set; } = 1;
        public int FrameCount { get; set; }
        public float Fps { get; set; }

        public void ValidateAndNormalize()
        {
            MaxParticles = Math.Clamp(MaxParticles, 1, 64);
            EmitRate = float.IsFinite(EmitRate) && EmitRate > 0f ? EmitRate : 8f;
            LifetimeSeconds = float.IsFinite(LifetimeSeconds) && LifetimeSeconds > 0f ? LifetimeSeconds : 1.5f;
            InitialSpeed = float.IsFinite(InitialSpeed) && InitialSpeed >= 0f ? InitialSpeed : 2f;
            SpreadDegrees = float.IsFinite(SpreadDegrees) ? Math.Clamp(SpreadDegrees, 0f, 180f) : 25f;
            Gravity = float.IsFinite(Gravity) ? Gravity : -9.8f;
            Drag = float.IsFinite(Drag) && Drag >= 0f ? Drag : 0f;
            StartSize = float.IsFinite(StartSize) && StartSize > 0f ? StartSize : 0.25f;
            EndSize = float.IsFinite(EndSize) && EndSize > 0f ? EndSize : StartSize;
            Seed = Seed == 0 ? 1u : Seed;
            Columns = Math.Max(1, Columns);
            Rows = Math.Max(1, Rows);
            FrameCount = Math.Clamp(FrameCount <= 0 ? Columns * Rows : FrameCount, 1, Columns * Rows);
            Fps = float.IsFinite(Fps) && Fps >= 0f ? Fps : 0f;
        }
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

    public sealed record PrefabOverrideData
    {
        public bool Transform { get; set; }
        public List<int> MaterialSlots { get; set; } = new();
        public List<string> Colliders { get; set; } = new();
        public List<string> Metadata { get; set; } = new();
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

    public sealed record LightingData
    {
        public string ActiveState { get; set; } = "Default";
        public List<LightingStateData> States { get; set; } = new();

        public LightingStateData? ResolveActiveState()
        {
            if (States.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(ActiveState))
            {
                LightingStateData? named = States.FirstOrDefault(state =>
                    string.Equals(state.Name, ActiveState, StringComparison.OrdinalIgnoreCase));
                if (named != null)
                {
                    return named;
                }
            }

            return States[0];
        }
    }

    public sealed record LightingStateData
    {
        public string Name { get; set; } = "Default";
        public EnvironmentData Environment { get; set; } = new();
        public List<SceneLightData> Lights { get; set; } = new();
    }

    public sealed record EnvironmentData
    {
        public string AmbientMode { get; set; } = "Color";
        public Color32 AmbientColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
        public Color32 AmbientEquatorColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
        public Color32 AmbientGroundColor { get; set; } = Color32.FromRgba(0.2f, 0.19f, 0.18f);
        // L2 spherical-harmonic sky irradiance (E/π): 9 RGB coefficients (27 floats, Ramamoorthi
        // order, band factors Â=(1, 2/3, 1/4) premultiplied) — the per-normal ambient Godot's
        // sky-SH produces. Full-precision floats (SH coefficients can be negative, so the 8-bit
        // Color32 encoding does not apply). Null when AmbientMode is not "Skybox".
        public float[]? AmbientSh { get; set; }
        // Ambient SPECULAR from the sky (Godot Environment.reflected_light_source ≠ Disabled).
        public bool SkyReflections { get; set; }
        // ProceduralSky sun disk/halo params (cosine thresholds + curve), matching Godot's
        // sky_material.cpp uniforms. SizeCos = cos(light angular distance); disk never triggers at
        // the default 2 (sentinel > 1) when no sun was found. The runtime pairs these with the
        // first ENABLED directional light for direction/colour/energy.
        public float SkySunSizeCos { get; set; } = 2f;
        public float SkySunAngleMaxCos { get; set; } = 2f;
        public float SkySunInvCurve { get; set; } = 24f;
        public float Exposure { get; set; } = 1f;
        // Ambient light energy (Godot Environment.ambient_light_energy). Scales the hemisphere ambient.
        public float AmbientEnergy { get; set; } = 1f;
        // Resolved background/clear tone (from the sky when background_mode is Sky), used as the
        // runtime clear color so the .NET background matches Godot instead of a flat neutral. Only
        // authoritative when HasBackground is set (a WorldEnvironment was actually exported); a
        // default-constructed EnvironmentData must NOT override the camera-derived clear.
        public bool HasBackground { get; set; }
        public Color32 BackgroundColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
        // Procedural-sky background (Godot ProceduralSkyMaterial), colours linear + already tone-mapped,
        // set only for a Sky source. The runtime evaluates Godot's two-part gradient per view ray: sky
        // (top→horizon) above the horizon, ground (bottom→horizon) below. Curves are Godot's inverse
        // curves (inv_sky_curve = 0.6/sky_curve, inv_ground_curve = 0.6/ground_curve).
        public bool SkyGradient { get; set; }
        public Color32 SkyTopColor { get; set; } = Color32.FromRgba(0.03f, 0.024f, 0.016f);
        public Color32 SkyHorizonColor { get; set; } = Color32.FromRgba(0.2f, 0.2f, 0.21f);
        public Color32 SkyGroundBottomColor { get; set; } = Color32.FromRgba(0.03f, 0.024f, 0.016f);
        public Color32 SkyGroundHorizonColor { get; set; } = Color32.FromRgba(0.2f, 0.2f, 0.21f);
        public float SkySkyCurveInv { get; set; } = 4f;
        public float SkyGroundCurveInv { get; set; } = 30f;
        public bool FogEnabled { get; set; }
        public Color32 FogColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
        public float FogDensity { get; set; }

        // Screen-space ambient occlusion (Godot Environment.ssao_*). When enabled, the runtime runs a
        // world-position pre-pass and darkens the ambient term in creases/contacts.
        public bool SsaoEnabled { get; set; }
        public float SsaoRadius { get; set; } = 1f;
        public float SsaoIntensity { get; set; } = 2f;
        public float SsaoPower { get; set; } = 1.5f;

        // Tone mapping exported from Godot's Environment (Environment.tonemap_*). TonemapMode names
        // match Godot's ToneMapper enum (Linear, Reinhardt, Filmic, Aces, Agx). The runtime renderer
        // applies the matching operator before the sRGB encode so the .NET render matches Godot.
        public string TonemapMode { get; set; } = "Linear";
        public float TonemapExposure { get; set; } = 1f;
        public float TonemapWhite { get; set; } = 1f;

        // Bloom / glow (Godot Environment.glow_*). The runtime's HDR composite runs a threshold +
        // dual-filter bloom and adds it back scaled by intensity — the .NET analog of Godot's glow.
        public bool GlowEnabled { get; set; }
        public float GlowIntensity { get; set; } = 0.6f;
        public float GlowThreshold { get; set; } = 1f;
    }

    public sealed record SceneLightData
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public Vector3 Position { get; set; } = Vector3.Zero;
        public Vector3 Direction { get; set; } = Vector3.Zero;
        public Color32 Color { get; set; } = Color32.FromRgba(1f, 1f, 1f);
        public bool Enabled { get; set; } = true;
        public float Intensity { get; set; } = 1f;
        public bool UseColorTemperature { get; set; }
        public float ColorTemperature { get; set; } = 6570f;
        public float Range { get; set; }
        // Distance-falloff exponent (Godot LIGHT_PARAM_ATTENUATION / omni_/spot_attenuation). The
        // runtime applies pow(distance, -exponent) for point/spot lights; Godot's default 1.0 is
        // inverse-linear (not inverse-square). Unused by directionals.
        public float AttenuationExponent { get; set; } = 1f;
        public float SpotAngle { get; set; }
        public float InnerSpotAngle { get; set; }
        public Vector2 AreaSize { get; set; } = Vector2.Zero;
        public bool ShadowsEnabled { get; set; }
        public string ShadowType { get; set; } = "";
        public float ShadowStrength { get; set; } = 1f;
        // Godot Light3D LIGHT_PARAM_SPECULAR: scales only the specular lobe (Godot default 0.5).
        public float Specular { get; set; } = 0.5f;
        // Godot Light3D LIGHT_PARAM_SIZE (light_size / angular_distance): directional = angular
        // diameter in DEGREES; point/spot = world radius in meters. Softens specular highlights.
        public float Size { get; set; }
        public int LayerMask { get; set; }
        public int RenderingLayerMask { get; set; }
        public string Group { get; set; } = "";
    }

    public sealed record NavMeshAgentData
    {
        public float Speed { get; set; } = 2f;
        public float AngularSpeed { get; set; } = 720f;
        public float Acceleration { get; set; } = 40f;
    }

    public sealed record InteractableData
    {
        public string Id { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? PersonalityTag { get; set; }
        public float InteractionRadius { get; set; } = 2f;
        public List<InteractableColliderData> Colliders { get; set; } = new();
        public InteractablePresentationData? Presentation { get; set; }
    }

    public sealed record InteractablePresentationData
    {
        public string? AudioEvent { get; set; }
        public string? ParticleEffectId { get; set; }
        public string? TimelineId { get; set; }
        public Vector3 LocalOffset { get; set; } = new(0f, 1f, 0f);
        public float CooldownSeconds { get; set; } = 0.75f;
    }

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

    public sealed class InteractableColliderData : ColliderShapeData
    {
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

    // Render primitive of a particle emitter (serialized by name, like the physics enums).
    public enum ParticleRenderKind
    {
        Sprite,
        Voxel,
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
