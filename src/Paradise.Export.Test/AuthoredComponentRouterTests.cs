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
        await Assert.That(entity.Components.Custom).IsNull();
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

        var c = entity.Components;
        await Assert.That(c.Renderable!.Mesh).IsEqualTo("Models/knight.glb");
        await Assert.That(c.Rigidbody!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(c.Rigidbody!.Mass).IsEqualTo(2.5f);
        await Assert.That(c.Agent!.MoveSpeed).IsEqualTo(3.5f);
        await Assert.That(c.Interactable!.DisplayName).IsEqualTo("Lever");
        await Assert.That(c.SpriteAnimation!.Columns).IsEqualTo(4);
        await Assert.That(c.ParticleEmitter!.Kind).IsEqualTo(ParticleRenderKind.Voxel);
        await Assert.That(c.AudioEmitter!.StartEvent).IsEqualTo("Play_Torch");
        await Assert.That(c.Collider!.Colliders.Single().ShapeType).IsEqualTo(PhysicsShapeType.Box);
        await Assert.That(c.Collider!.Colliders.Single().IsTrigger).IsTrue();

        // ...and none of them leaked into Custom on the way.
        await Assert.That(c.Custom).IsNull();
    }

    /// <summary>The reason Custom exists. A game's component is carried verbatim, because the
    /// engine cannot name its type and does not try.</summary>
    [Test]
    public async Task an_unknown_id_is_carried_verbatim_in_custom()
    {
        var entity = Route(Payload(LedgeId, """{"Friction":0.35,"IsTrigger":false}""", LedgeType));

        var custom = entity.Components.Custom!.Single();
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

    [Test]
    public async Task engine_ids_are_distinguishable_from_a_games_own()
    {
        await Assert.That(AuthoredComponentRouter.IsEngineComponent(ParadiseComponentIds.Rigidbody)).IsTrue();
        await Assert.That(AuthoredComponentRouter.IsEngineComponent(LedgeId)).IsFalse();
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
    /// silently dropped — losing authored data without a word is the worst outcome here.</summary>
    [Test]
    public async Task an_unreadable_engine_payload_is_reported()
    {
        var entity = new LevelEntityData { Id = "Thing" };
        var failed = AuthoredComponentRouter.ApplyAll(entity, new[]
        {
            Payload(ParadiseComponentIds.Rigidbody, """{"Mass":"not a number"}"""),
            Payload(ParadiseComponentIds.Agent, """{"MoveSpeed":3}"""),
        });

        await Assert.That(failed.Select(c => c.Id))
            .IsEquivalentTo(new[] { ParadiseComponentIds.Rigidbody });
        await Assert.That(entity.Components.Rigidbody).IsNull();
        // The good one still applied: one bad component does not cost the entity the rest.
        await Assert.That(entity.Components.Agent!.MoveSpeed).IsEqualTo(3f);
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
            Components = new EntityComponentsData
            {
                Renderable = new RenderableComponentData { Mesh = "Models/knight.glb" },
            },
        });

        await Assert.That(ExportJsonWriter.SerializeToString(routed))
            .IsEqualTo(ExportJsonWriter.SerializeToString(handBuilt));
    }
}
