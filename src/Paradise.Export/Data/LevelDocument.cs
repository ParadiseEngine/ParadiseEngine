#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Paradise.Authoring;

// Ask the generator for this assembly's own AuthoredComponents registry.
//
// The attribute's own docs used to name Paradise.Export as the example of an assembly that
// should NOT have one — it published a schema for editors, and its components arrived already
// typed in their slots, so a loader would have had nothing to load. Schema v3 removed the slots:
// the engine's components come back as payloads exactly like a game's, and something has to read
// them. That something is now the same generated registry a game gets, rather than a hand-written
// dispatch that had to be kept in step with the component list by hand.
[assembly: Paradise.Authoring.AuthoredRegistry]

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
        /// <summary>Bumped when the SHAPE of this document changes in a way an existing reader
        /// would misparse.
        ///
        /// v4 moved the entity's <c>Materials</c> slot list onto
        /// <see cref="RenderableComponentData"/>. A v3 document parses cleanly here and loses the
        /// slots in silence — every entity keeps its mesh and renders in the GLB's own embedded
        /// materials — which is exactly the failure a version gate exists to prevent, so v3 is
        /// refused rather than read.
        ///
        /// v3 replaced the entity's nine named component slots with ONE list of
        /// <see cref="AuthoredComponentData"/>. Not additive in either direction: a v2 document's
        /// <c>"Components": {"Rigidbody": {...}}</c> is an object where this build expects an
        /// array.
        ///
        /// Both are REJECTED on read — see
        /// <see cref="Serialization.ExportJsonReader.ReadLevel"/>.</summary>
        public const int CurrentSchemaVersion = 4;

        /// <summary>The oldest document this build still understands. Equal to
        /// <see cref="CurrentSchemaVersion"/>, and that is the point rather than an oversight.
        ///
        /// v3 COULD have been read with a shim — moving a key is mechanical, unlike v2's named
        /// slots. It is refused anyway, because a shim is a second reading of the format that
        /// lives forever: every reader after it has to know both shapes, and the migration nobody
        /// is forced to do is the one nobody does. <c>tools/migrate_level_v3_to_v4.py</c> converts
        /// a document in one pass, and re-exporting is better still.</summary>
        public const int MinimumSupportedVersion = 4;

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
        // Materials used to sit here. It moved onto RenderableComponentData in v4 — see that
        // record for why. Nothing replaces it at this level: an entity that renders nothing has
        // no slots to override.
        public PrefabOverrideData Overrides { get; set; } = new();
        /// <summary>Every component authored on this entity, engine's and game's alike, each as
        /// an id + type + opaque payload.
        ///
        /// One list rather than the named slots this used to be. A slot could only ever exist for
        /// a component the CONTRACT knew about, which made the engine's components a privileged
        /// tier and every game's components a second one — and meant adding an engine component
        /// changed this record, both editors, and the router in each of them. Now it changes
        /// nothing: a component is a record with a <c>[Guid]</c>.
        ///
        /// Read one with <see cref="LevelEntityExtensions.Get{T}"/>, or all of them at once with
        /// <see cref="AuthoredComponentRouter.Materialize"/>.</summary>
        public List<AuthoredComponentData> Components { get; set; } = new();
    }

    public sealed record EntityParentData
    {
        public string Id { get; set; } = "";
        public string? BonePath { get; set; }
        public int BoneIndex { get; set; } = -1;
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

    // ---- the components ---------------------------------------------------------------------
    //
    // Each carries its identity in its own [Guid], and that attribute is the ONLY place the id
    // exists: the Roslyn generator in Paradise.Authoring.Generators reads it to build the registry
    // and the authoring schema, and every runtime comparison goes through typeof(T).GUID. There
    // was a ParadiseComponentIds table holding a second copy of all ten; it was the last remnant
    // of the two-tier design v3 deleted, and a second copy of an identity is a thing that can
    // disagree with the first.
    //
    // ADDING ONE: run `uuidgen` (or Guid.NewGuid()) and paste the result. Never hand-type a value
    // and never continue a visible pattern. These were a counted sequence once — same prefix,
    // last digit incremented — and the sequence read as an invitation: the obvious way to add the
    // eleventh component was to type the next number, which is both how the tenth got its id and
    // how a game repo would mint one that collides with the engine. A generated id has no next.

    /// <summary>
    /// Renderable marker, mesh reference and material slots. <see cref="Mesh"/> is a GLB path
    /// relative to <c>data/</c> (e.g. <c>meshes/&lt;key&gt;.glb</c>) holding the entity's visual
    /// subtree in ENTITY-LOCAL space (the entity's WorldMatrix places it). Textures inside the GLB
    /// are ALWAYS KTX2 (the toktx pass is mandatory for textured meshes; the engine reader rejects
    /// PNG/JPEG). <see cref="MeshNode"/> optionally names a single node inside the GLB (reserved;
    /// null = whole default scene).
    ///
    /// <see cref="Materials"/> joined this record in schema v4. It was a field on the ENTITY, one
    /// level up, which made the contract's central rule — slot order equals the GLB's primitive
    /// order — a statement about two fields that nothing held together: an entity could carry
    /// slots with no mesh to index them against, and every reader had to fetch the renderable
    /// anyway to know what the slots meant. They are one thing, so they are one record.
    /// </summary>
    [Guid("f2c0357e-94dd-4a5a-9803-518066cb54b2")]
    [Authored(DisplayName = "Renderable")]
    public sealed record RenderableComponentData
    {
        /// <summary>
        /// Authored by picking the source GLB, and BAKED to the data-relative path the runtime
        /// resolves.
        ///
        /// An ASSET rather than a mesh-node reference, because that is how it was actually
        /// authored: the field this replaces was a file picker, and in the sample scenes only 6 of
        /// 28 entities with a mesh had a node to point at at all — the rest named a file. A node
        /// reference would have been unauthorable for most of them.
        /// </summary>
        [AuthoredByHost(AuthoredBySources.Asset)]
        [AuthorAssetKinds(".glb", ".gltf")]
        [AuthorDoc("The source GLB this entity renders.")]
        public string? Mesh { get; set; }

        [AuthorDoc("Optional node inside the GLB; empty means its whole default scene.")]
        public string? MeshNode { get; set; }

        /// <summary>
        /// Per-primitive material overrides, positionally paired with <see cref="Mesh"/>.
        ///
        /// <b>Slot order IS the contract:</b> the GLB's primitive order equals this list's order —
        /// both hosts walk the same MeshInstance3D traversal to produce it. A null slot means the
        /// GLB's own embedded material is authoritative, which is why the list is of nullable
        /// strings and why a shorter list is legal: it simply overrides fewer primitives.
        ///
        /// DERIVED, not typed. Both editors read it off the object's material slots at export and
        /// overwrite whatever is here, so an authored edit does not survive a re-export.
        /// </summary>
        [AuthoredByHost(AuthoredBySources.Mesh)]
        [AuthorDoc("Material overrides, one per GLB primitive. Derived from the mesh at export.")]
        public List<string?> Materials { get; set; } = new();
    }

    [Guid("e1cd1bc8-86f2-4225-adc9-4a324c70ebf9")]
    [Authored(DisplayName = "Collider")]
    public sealed record ColliderComponentData
    {
        /// <summary>A list of shape references. Each is edited with the host's own handles and
        /// baked into the numbers below it at export.</summary>
        [AuthorDoc("Collision shapes, edited with the host's own handles.")]
        public List<ColliderShapeData> Colliders { get; set; } = new();
    }

    /// <summary>The first engine component to declare its own authoring surface, and the template
    /// the other eight followed.</summary>
    [Guid("b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11")]
    [Authored(DisplayName = "Rigidbody")]
    public sealed record RigidbodyComponentData
    {
        [AuthorDoc("Static bodies never move; dynamic ones are simulated.")]
        public PhysicsBodyType BodyType { get; set; }

        [Kilograms, AuthorRange(0.001, 10000)]
        // The guard EntityExport could not express: mass means nothing on a static body, and a
        // field that is meaningless most of the time is a field authors mis-set.
        [AuthorVisibleWhen(nameof(BodyType), PhysicsBodyType.Dynamic)]
        [AuthorDoc("Mass in kilograms. Ignored for static bodies.")]
        public float Mass { get; set; } = 1f;

        [AuthorRange(0, 100), AuthorDoc("Linear velocity bleed-off per second.")]
        public float LinearDamping { get; set; } = 0.2f;

        [Unit01, AuthorDoc("Bounciness: 0 absorbs the impact, 1 returns it.")]
        public float Restitution { get; set; } = 0.2f;

        [Unit01, AuthorDoc("Surface friction: 0 is ice, 1 is grippy.")]
        public float Friction { get; set; } = 0.5f;

        [AuthorDoc("Collision layer index. Prefer LayerName where the project defines one.")]
        public int Layer { get; set; }

        [AuthorDoc("Named collision layer, resolved against the project's layer contract.")]
        public string? LayerName { get; set; } = "";
    }

    [Guid("5801915b-3d0c-4940-8970-7d1487b991cf")]
    [Authored(DisplayName = "Agent (movement)")]
    public sealed record AgentComponentData
    {
        [AuthorRange(0.01, 100), AuthorDoc("Movement speed in metres per second.")]
        public float MoveSpeed { get; set; } = 1.4f;

        [AuthorRange(0.01, 1000), AuthorDoc("How hard the agent accelerates toward its speed.")]
        public float Acceleration { get; set; } = 40f;

        /// <summary>Defaulted here rather than substituted at export. The old authoring layer
        /// swapped a blank clip for these names on the way out, which meant the fallback was
        /// invisible to anyone reading the record — and unreachable to any editor but Godot's.</summary>
        [AuthorDoc("Animation clip played while standing still.")]
        public string? IdleClip { get; set; } = "Idle";

        [AuthorDoc("Animation clip played while moving.")]
        public string? WalkClip { get; set; } = "Walk";
    }

    [Guid("0283ee5f-775b-412b-a91c-03ecd9b61165")]
    [Authored(DisplayName = "Interactable")]
    public sealed record EntityInteractableComponentData
    {
        [AuthorDoc("Name shown to the player when this can be interacted with.")]
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
    [Guid("d3e53cd4-89c6-4ca8-851e-7596da889c68")]
    [Authored(DisplayName = "Sprite animation")]
    // The WHOLE record is authored by pointing at a sprite in the host: its sheet, grid and quad
    // size are read off that object at export rather than retyped here.
    [AuthoredByHost(AuthoredBySources.Sprite)]
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
    [Guid("1b4d1bdd-dea1-4b86-9b6a-879c46346b9e")]
    [Authored(DisplayName = "Particle emitter")]
    public sealed record ParticleEmitterComponentData
    {
        [AuthorDoc("Sprite = camera-facing flipbook quads; Voxel = solid tinted cubes.")]
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
        [AuthorDoc("Same seed, same particle stream in every host.")]
        public uint Seed { get; set; } = 1;
        /// <summary>Tint (Sprite: multiplies the sheet; Voxel: the cube albedo).</summary>
        public Color32 Color { get; set; } = Color32.FromRgba(1f, 1f, 1f);

        // Sprite kind only: flipbook sheet (same conventions as SpriteAnimationComponentData).
        // Fps 0 stretches the flipbook once over each particle's lifetime.
        [AuthoredByHost(AuthoredBySources.Asset), AuthorAssetKinds(".png", ".jpg", ".jpeg")]
        [AuthorVisibleWhen(nameof(Kind), ParticleRenderKind.Sprite)]
        [AuthorDoc("Flipbook spritesheet for the particles.")]
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

    /// <summary>
    /// A positional sound source. The entity's world position is the emitter's position; the
    /// runtime registers one audio-engine object per emitter and keeps it in sync.
    ///
    /// EVENTS ARE NAMED, NOT RESOLVED HERE. <see cref="StartEvent"/> and <see cref="StopEvent"/>
    /// are authoring-tool event names (Wwise, in the shipped integration), and the contract
    /// deliberately carries the string rather than a resolved id: ids are produced by hashing the
    /// name at bank-generation time, so resolving at export would pin the scene to one particular
    /// soundbank build. The runtime hashes the name instead, which is stable across regenerations.
    ///
    /// A name that matches nothing plays nothing. That is the audio middleware's model — event
    /// names only exist inside the audio project, which the exporter cannot see — so an emitter
    /// whose event was renamed goes quiet rather than failing the export.
    /// </summary>
    [Guid("e6ec7f42-df09-4ec9-af06-128ddf3eda8e")]
    [Authored(DisplayName = "Audio emitter")]
    public sealed record AudioEmitterComponentData
    {
        /// <summary>Event posted for this emitter. Null or empty means the emitter exists as a
        /// positioned object but plays nothing until game code posts to it.</summary>
        [AuthorDoc("Event posted for this emitter.")]
        public string? StartEvent { get; set; }

        /// <summary>Event posted to stop it. Optional: a one-shot needs none, and a loop can also
        /// be stopped by its playing id, which is what the runtime does when this is absent.</summary>
        public string? StopEvent { get; set; }

        /// <summary>Post <see cref="StartEvent"/> as soon as the scene loads. Ambience and looping
        /// machinery want this; a door creak does not.</summary>
        public bool PlayOnStart { get; set; } = true;

        /// <summary>False makes the emitter 2D — positioned in the scene for authoring
        /// convenience, but heard at full level regardless of where the listener is. Music and
        /// narration are the cases that want it.</summary>
        [AuthorDoc("Off makes it 2D: positioned for convenience, heard at full level everywhere.")]
        public bool Is3D { get; set; } = true;

        /// <summary>Scales the attenuation curve authored on the sound, so one authored falloff
        /// can serve emitters of different physical size. 1 is the authored distance.</summary>
        [AuthorRange(0.01, 100)]
        [AuthorVisibleWhen(nameof(Is3D), true)]
        [AuthorDoc("Scales the sound's authored falloff; 1 is the authored distance.")]
        public float AttenuationScale { get; set; } = 1f;

        public void ValidateAndNormalize()
        {
            // A non-finite or non-positive scale would collapse the attenuation curve and make
            // the emitter either silent everywhere or audible everywhere — both read as a broken
            // sound rather than a bad number, so clamp rather than trusting the authored value.
            AttenuationScale =
                float.IsFinite(AttenuationScale) && AttenuationScale > 0f ? AttenuationScale : 1f;
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

        /// <summary>Per-layer shadow map resolution the scene asks its renderer for, in texels
        /// (authored in the Blender panel's Lighting section). Null leaves the renderer's own
        /// default in place. Scene-wide rather than per lighting state: it sizes a GPU resource,
        /// not a mood.</summary>
        public int? ShadowMapSize { get; set; }

        /// <summary>Soft-shadow blur: the PCF disk radius in shadow texels — the penumbra width
        /// of every shadow edge (authored beside <see cref="ShadowMapSize"/>). Null leaves the
        /// renderer's default.</summary>
        public float? ShadowBlur { get; set; }

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

    /// <summary>
    /// A light. Reachable two ways, deliberately: as a scene-level entry under
    /// <see cref="LightingStateData.Lights"/>, and as a component on an entity that AUTHORED one by
    /// pointing at it.
    ///
    /// A light under an entity belongs to that entity and is not also listed at scene level, so a
    /// light is only ever described once. Aiming is done by ROTATING the referenced object —
    /// <see cref="Direction"/> is baked from its orientation, not typed.
    /// </summary>
    [Guid("fc886b84-c48c-4415-afd9-b03d6faf5ab7")]
    [Authored(DisplayName = "Light")]
    [AuthoredByHost(AuthoredBySources.Light)]
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

    /// <summary>
    /// What an entity IS, as an authored component.
    ///
    /// Spread onto <see cref="LevelEntityData"/>'s own fields rather than kept in its component
    /// list — these are not things an entity HAS. It exists as a
    /// component so identity is declared in the same place as everything else and every editor
    /// builds its control for it from the same document, rather than each hardcoding a "Kind" box.
    ///
    /// The entity's GUID is deliberately absent: minting one and keeping it unique across a scene
    /// is behaviour, and behaviour is the one thing a schema cannot carry.
    /// </summary>
    [Guid("0c068bf4-495f-495b-be8d-9b02042a41c2")]
    [Authored(DisplayName = "Identity")]
    public sealed record IdentityComponentData
    {
        [AuthorDoc("Free-form label the runtime groups by, e.g. Prop, Character, Door.")]
        public string Kind { get; set; } = "Prop";

        [AuthorDoc("Spawn this entity when the level loads.")]
        public bool IsActive { get; set; } = true;

        [AuthorDoc("Animation clip to start on spawn; empty for none.")]
        public string? InitialAnimation { get; set; }

        /// <summary>
        /// The source asset this entity was placed from — provenance, not what renders.
        ///
        /// It lives on IDENTITY rather than on Renderable for a blunt reason: adding a field to
        /// Renderable would change the shape of every exported document, and this had to move
        /// without doing that. Identity never appears under Components (the router spreads it onto
        /// the entity), so it is the one place a new authored field costs nothing.
        /// </summary>
        [AuthorAssetKinds(".glb", ".gltf", ".tscn", ".scn")]
        [AuthorDoc("Source asset this entity was placed from.")]
        public string? Prefab { get; set; }

        [AuthorDoc("Name shown in tools; defaults to the node's own name.")]
        public string? DisplayName { get; set; }

        [AuthorDoc("When in the load sequence this entity appears.")]
        public string? SpawnPhase { get; set; }
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

    /// <summary>
    /// One collision shape, AUTHORED by pointing at the host's own shape object and edited with its
    /// native handles — every field below is baked out of that object at export.
    ///
    /// Note this is a class with a subclass (<see cref="InteractableColliderData"/>). The authoring
    /// schema has no notion of inheritance, so it describes the base only; interaction colliders
    /// carry no extra geometry today, so nothing is lost yet.
    /// </summary>
    [AuthoredByHost(AuthoredBySources.Shape)]
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
