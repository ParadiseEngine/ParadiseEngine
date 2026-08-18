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
    private static AuthoredComponentData Payload(string id, string json) =>
        new() { Id = id, Data = JsonDocument.Parse(json).RootElement.Clone() };

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
        var entity = Route(Payload("mygame.ledge", """{"Friction":0.35,"IsTrigger":false}"""));

        var custom = entity.Components.Custom!.Single();
        await Assert.That(custom.Id).IsEqualTo("mygame.ledge");
        await Assert.That(custom.Data.GetProperty("Friction").GetSingle()).IsEqualTo(0.35f);
        await Assert.That(custom.Data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
    }

    [Test]
    public async Task engine_ids_are_distinguishable_from_a_games_own()
    {
        await Assert.That(AuthoredComponentRouter.IsEngineComponent(ParadiseComponentIds.Rigidbody)).IsTrue();
        await Assert.That(AuthoredComponentRouter.IsEngineComponent("mygame.ledge")).IsFalse();
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

        await Assert.That(failed).IsEquivalentTo(new[] { ParadiseComponentIds.Rigidbody });
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
