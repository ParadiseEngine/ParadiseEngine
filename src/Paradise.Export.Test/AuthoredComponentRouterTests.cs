using System.Runtime.InteropServices;
using System.Text.Json;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>
/// The router is what lets the engine's components be DECLARED without the exported document
/// changing shape. An editor knows only ids and JSON; these tests pin that each id still reaches
/// the typed slot the runtime reads, and that a game's own component is left alone.
/// </summary>
public class AuthoredComponentRouterTests
{
    /// <summary>A game's own component: an id the engine does not know, plus the type name that
    /// makes the payload identifiable when the id resolves to nothing.</summary>
    private static readonly Guid LedgeId = new("f0000000-0000-4000-8000-000000000001");

    private const string LedgeType = "Paradise.Export.Tests.LedgeFixture";

    private static AuthoredComponentData Payload(Guid id, string json, string? type = null) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    private static LevelEntityData Route(params AuthoredComponentData[] components)
    {
        var entity = new LevelEntityData { Id = "Thing" };
        AuthoredComponentRouter.ApplyAll(entity, components);
        return entity;
    }

    /// <summary>Identity is what an entity IS, so it spreads onto the entity itself rather than
    /// appearing under Components.</summary>
    [Test]
    public async Task identity_lands_on_the_entity_not_under_components()
    {
        var entity = Route(Payload(ParadiseComponentIds.Identity,
            """{"Kind":"Door","IsActive":false,"InitialAnimation":"Open","DisplayName":"Front door"}"""));

        await Assert.That(entity.Kind).IsEqualTo("Door");
        await Assert.That(entity.IsActive).IsFalse();
        await Assert.That(entity.InitialAnimation).IsEqualTo("Open");
        await Assert.That(entity.DisplayName).IsEqualTo("Front door");
        // Identity is spread onto the entity's own fields and leaves NO entry behind — it is what
        // the entity is, not something it has.
        await Assert.That(entity.Components).IsEmpty();
    }

    /// <summary>An unauthored DisplayName must not overwrite the exporter's own default with
    /// null — the node name is a better answer than nothing.</summary>
    [Test]
    public async Task identity_leaves_unauthored_optional_fields_alone()
    {
        var entity = new LevelEntityData { Id = "Thing", DisplayName = "From the node", SpawnPhase = "LevelStart" };
        AuthoredComponentRouter.Apply(entity, Payload(ParadiseComponentIds.Identity, """{"Kind":"Prop"}"""));

        await Assert.That(entity.DisplayName).IsEqualTo("From the node");
        await Assert.That(entity.SpawnPhase).IsEqualTo("LevelStart");
    }

    [Test]
    public async Task every_engine_component_reaches_its_typed_slot()
    {
        var entity = Route(
            Payload(ParadiseComponentIds.Renderable, """{"Mesh":"Models/knight.glb"}"""),
            Payload(ParadiseComponentIds.Rigidbody, """{"BodyType":"Dynamic","Mass":2.5}"""),
            Payload(ParadiseComponentIds.Agent, """{"MoveSpeed":3.5,"IdleClip":"Idle"}"""),
            Payload(ParadiseComponentIds.Interactable, """{"DisplayName":"Lever"}"""),
            Payload(ParadiseComponentIds.SpriteAnimation, """{"Sheet":"sprites/torch.ktx2","Columns":4}"""),
            Payload(ParadiseComponentIds.ParticleEmitter, """{"Kind":"Voxel","EmitRate":12}"""),
            Payload(ParadiseComponentIds.AudioEmitter, """{"StartEvent":"Play_Torch","Is3D":true}"""),
            Payload(ParadiseComponentIds.Collider,
                """{"Colliders":[{"ShapeType":"Box","Size":[1,2,3],"IsTrigger":true}]}"""));

        await Assert.That(entity.Get<RenderableComponentData>()!.Mesh).IsEqualTo("Models/knight.glb");
        await Assert.That(entity.Get<RigidbodyComponentData>()!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(entity.Get<RigidbodyComponentData>()!.Mass).IsEqualTo(2.5f);
        await Assert.That(entity.Get<AgentComponentData>()!.MoveSpeed).IsEqualTo(3.5f);
        await Assert.That(entity.Get<EntityInteractableComponentData>()!.DisplayName).IsEqualTo("Lever");
        await Assert.That(entity.Get<SpriteAnimationComponentData>()!.Columns).IsEqualTo(4);
        await Assert.That(entity.Get<ParticleEmitterComponentData>()!.Kind).IsEqualTo(ParticleRenderKind.Voxel);
        await Assert.That(entity.Get<AudioEmitterComponentData>()!.StartEvent).IsEqualTo("Play_Torch");
        await Assert.That(entity.Get<ColliderComponentData>()!.Colliders.Single().ShapeType)
            .IsEqualTo(PhysicsShapeType.Box);
        await Assert.That(entity.Get<ColliderComponentData>()!.Colliders.Single().IsTrigger).IsTrue();

        // Eight payloads in, eight entries out. This used to assert the opposite shape — that none
        // of them "leaked into Custom" — back when an engine component landed in a typed slot and
        // Custom was where a game's went. There is one destination now, so the property worth
        // pinning is that routing neither drops nor duplicates.
        await Assert.That(entity.Components.Count).IsEqualTo(8);
    }

    /// <summary>The reason Custom exists. A game's component is carried verbatim, because the
    /// engine cannot name its type and does not try.</summary>
    [Test]
    public async Task an_unknown_id_is_carried_verbatim_in_custom()
    {
        var entity = Route(Payload(LedgeId, """{"Friction":0.35,"IsTrigger":false}""", LedgeType));

        var custom = entity.Components!.Single();
        await Assert.That(custom.Id).IsEqualTo(LedgeId);
        await Assert.That(custom.Type).IsEqualTo(LedgeType);
        await Assert.That(custom.Data.GetProperty("Friction").GetSingle()).IsEqualTo(0.35f);
        await Assert.That(custom.Data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
    }

    /// <summary>
    /// The loading half. Engine components arrive already typed; a game's come back through its
    /// registry — and the caller gets ONE list of records rather than typed properties mixed with
    /// raw JSON it has to remember to deserialize.
    /// </summary>
    [Test]
    public async Task materialize_returns_engine_and_game_components_as_instances()
    {
        var entity = Route(
            Payload(ParadiseComponentIds.Rigidbody, """{"BodyType":"Dynamic","Mass":2.5}"""),
            Payload(ParadiseComponentIds.Renderable, """{"Mesh":"Models/knight.glb"}"""),
            Payload(LedgeId, """{"Friction":0.35,"IsTrigger":true,"Label":"north"}"""));

        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry());

        await Assert.That(instances.OfType<RigidbodyComponentData>().Single().Mass).IsEqualTo(2.5f);
        await Assert.That(instances.OfType<RenderableComponentData>().Single().Mesh)
            .IsEqualTo("Models/knight.glb");
        await Assert.That(instances.OfType<LedgeFixture>().Single().Friction).IsEqualTo(0.35f);
    }

    /// <summary>Without a registry the engine components still materialize — it cannot name a
    /// game's types and does not pretend to.</summary>
    [Test]
    public async Task materialize_without_a_registry_still_yields_the_engine_components()
    {
        var entity = Route(
            Payload(ParadiseComponentIds.Agent, """{"MoveSpeed":3}"""),
            Payload(LedgeId, """{"Friction":0.1}"""));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, registry: null, unresolved);

        await Assert.That(instances.OfType<AgentComponentData>().Single().MoveSpeed).IsEqualTo(3f);
        await Assert.That(unresolved.Select(c => c.Id)).IsEquivalentTo(new[] { LedgeId });
    }

    /// <summary>
    /// The repair path, end to end. A payload whose id nothing claims still materializes when it
    /// names its type — the reason a GUID id is survivable rather than a one-way door.
    /// </summary>
    [Test]
    public async Task a_payload_with_an_unknown_id_falls_back_to_its_type_name()
    {
        var entity = Route(Payload(
            Guid.NewGuid(), """{"Friction":0.5,"Label":"south"}""", LedgeType));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("south");
        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>
    /// And a payload with NO id at all repairs the same way, which is the case the type name was
    /// really added for: a document written before its component had an id.
    ///
    /// Worth pinning because the routing half is what makes or breaks it — dropping an id-less
    /// payload on the way in would leave the fallback below correct and unreachable.
    /// </summary>
    [Test]
    public async Task a_payload_with_no_id_at_all_still_repairs_by_type_name()
    {
        var entity = Route(Payload(Guid.Empty, """{"Friction":0.5,"Label":"north"}""", LedgeType));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("north");
        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>A payload with neither an id nor a type name is dropped: there is nothing to
    /// identify it by, and carrying it would only defer the same dead end to the caller.</summary>
    [Test]
    public async Task a_payload_with_neither_an_id_nor_a_type_is_dropped()
    {
        var entity = Route(Payload(Guid.Empty, """{"Friction":0.5}"""));

        await Assert.That(entity.Components).IsEmpty();
    }

    /// <summary>An id nobody claims is REPORTED. Silently dropping authored data is the failure
    /// this whole mechanism exists to prevent, so the loader must not fail that way either.</summary>
    [Test]
    public async Task materialize_reports_an_id_no_registry_claims()
    {
        var stranger = Guid.NewGuid();
        var entity = Route(Payload(stranger, """{"X":1}""", "Someone.Else"));
        var unresolved = new List<AuthoredComponentData>();

        await Assert.That(AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved))
            .IsEmpty();
        // Reported as the whole component, so the caller's message can name the type as well as
        // the id — "could not read <guid>" is not something anyone can act on.
        var reported = unresolved.Single();
        await Assert.That(reported.Id).IsEqualTo(stranger);
        await Assert.That(reported.Type).IsEqualTo("Someone.Else");
    }

    /// <summary>Stands in for the registry a game's [Authored] records generate.</summary>
    private sealed class LedgeRegistry : Paradise.Authoring.IAuthoredComponentRegistry
    {
        public IReadOnlyCollection<Guid> ComponentIds { get; } = new[] { LedgeId };

        public bool TryRead(Guid id, JsonElement data, out object? component)
        {
            if (id != LedgeId)
            {
                component = null;
                return false;
            }
            component = data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture);
            return true;
        }

        public bool TryReadByType(string fullTypeName, JsonElement data, out object? component)
        {
            if (fullTypeName != LedgeType)
            {
                component = null;
                return false;
            }
            component = data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture);
            return true;
        }
    }

    /// <summary>
    /// The id a record is AUTHORED under is the id the router DISPATCHES on.
    ///
    /// Two halves of the same wiring that live in different files: the schema and registry
    /// generators read the record's <c>[Guid]</c>, while the router switches on
    /// <see cref="ParadiseComponentIds"/>. Both spell it through the same constant today, so this
    /// only fails if someone pastes a literal into one of them — which is exactly the mistake that
    /// is otherwise invisible until a payload silently routes nowhere.
    /// </summary>
    [Test]
    public async Task an_engine_record_is_authored_under_the_id_the_router_dispatches_on()
    {
        var authored = (GuidAttribute)Attribute.GetCustomAttribute(
            typeof(RigidbodyComponentData), typeof(GuidAttribute))!;

        await Assert.That(Guid.Parse(authored.Value)).IsEqualTo(ParadiseComponentIds.Rigidbody);
    }

    /// <summary>A payload that cannot be read as the component it claims to be is REPORTED, not
    /// silently dropped — losing authored data without a word is the worst outcome here.
    ///
    /// WHERE it is reported moved with unification. Routing no longer deserializes anything (it
    /// appends an entry and is done), so ApplyAll cannot know a payload is bad; Materialize, which
    /// is the thing that actually reads, is where it surfaces. The guarantee is the same one —
    /// the bad component is named and the good ones still arrive — checked one step later.</summary>
    [Test]
    public async Task an_unreadable_engine_payload_is_reported()
    {
        var entity = new LevelEntityData { Id = "Thing" };
        var routed = AuthoredComponentRouter.ApplyAll(entity, new[]
        {
            Payload(ParadiseComponentIds.Rigidbody, """{"Mass":"not a number"}"""),
            Payload(ParadiseComponentIds.Agent, """{"MoveSpeed":3}"""),
        });

        // Nothing fails at route time any more, and both entries are on the entity.
        await Assert.That(routed).IsEmpty();
        await Assert.That(entity.Components.Count).IsEqualTo(2);

        var unresolved = new List<AuthoredComponentData>();
        IReadOnlyList<object> instances =
            AuthoredComponentRouter.Materialize(entity, registry: null, unresolved);

        await Assert.That(unresolved.Select(c => c.Id))
            .IsEquivalentTo(new[] { ParadiseComponentIds.Rigidbody });
        // The good one still materialized: one bad component does not cost the entity the rest.
        await Assert.That(instances.OfType<AgentComponentData>().Single().MoveSpeed).IsEqualTo(3f);
    }

    /// <summary>Routing must not disturb the written shape: an entity built this way serializes to
    /// exactly what a hand-built one does, which is what keeps every existing consumer working.</summary>
    [Test]
    public async Task a_routed_entity_serializes_identically_to_a_hand_built_one()
    {
        var routed = new LevelData();
        routed.Entities.Add(Route(
            Payload(ParadiseComponentIds.Identity, """{"Kind":"Prop","IsActive":true}"""),
            Payload(ParadiseComponentIds.Renderable, """{"Mesh":"Models/knight.glb"}""")));

        var handBuilt = new LevelData();
        handBuilt.Entities.Add(new LevelEntityData
        {
            Id = "Thing",
            Kind = "Prop",
            IsActive = true,
            // The SAME entry, not an equivalent one rebuilt from a record. Routing carries the
            // editor's payload verbatim; Entry(new RenderableComponentData { Mesh = ... }) would
            // re-serialize it and write every other property too (MeshNode: null). Both are
            // correct documents, and the difference is the point: routing does not rewrite what an
            // editor wrote.
            Components =
            {
                Payload(ParadiseComponentIds.Renderable, """{"Mesh":"Models/knight.glb"}"""),
            },
        });

        await Assert.That(ExportJsonWriter.SerializeToString(routed))
            .IsEqualTo(ExportJsonWriter.SerializeToString(handBuilt));
    }
}
