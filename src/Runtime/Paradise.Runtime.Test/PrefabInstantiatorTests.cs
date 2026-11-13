using System.Numerics;
using System.Text.Json;
using Paradise.Export.Data;

namespace Paradise.Runtime.Test;

public class PrefabInstantiatorTests
{
    private static readonly Guid s_parent = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid s_child = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid s_game = Guid.Parse("22222222-0000-0000-0000-000000000001");

    private static AuthoredComponentData Payload(Guid id, string json, string? type = null) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    private static AuthoredComponentData Meta(Guid id, string name, Guid? parent = null) => Payload(
        WellKnownEntityComponents.MetaId,
        parent is { } p ? $$"""{"Guid":"{{id}}","Name":"{{name}}","Parent":"{{p}}"}""" : $$"""{"Guid":"{{id}}","Name":"{{name}}"}""");

    private static AuthoredComponentData Transform(Vector3 position, float yaw = 0f, float scale = 1f)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        return Payload(WellKnownEntityComponents.TransformId,
            $$"""{"Position":[{{position.X}},{{position.Y}},{{position.Z}}],"Rotation":[{{q.X}},{{q.Y}},{{q.Z}},{{q.W}}],"Scale":[{{scale}},{{scale}},{{scale}}]}""");
    }

    [Test]
    public async Task hierarchy_and_placement_resolve_whatever_the_document_order()
    {
        var prefab = new PrefabData
        {
            Entities =
            [
                // The child first: parents are found by identity, not by having been walked already.
                [Meta(s_child, "child", s_parent), Transform(new Vector3(0, 0, 2)), Payload(s_game, """{"Speed":3}""", "Game.Speed")],
                [Meta(s_parent, "parent"), Transform(new Vector3(1, 0, 0), MathF.PI / 2, 2f)],
            ],
        };
        var root = Matrix4x4.CreateTranslation(0, 5, 0);
        var created = new List<PrefabEntity>();

        var count = PrefabInstantiator.Instantiate(prefab, root, created.Add);

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(created.Select(entity => entity.Name!)).IsEquivalentTo(new[] { "child", "parent" });
        await Assert.That(created[0].Parent).IsEqualTo(1);
        await Assert.That(created[1].Parent).IsEqualTo(-1);
        await Assert.That(created[0].World).IsEqualTo(created[0].Local * created[1].World);
        await Assert.That(created[1].World).IsEqualTo(created[1].Local * root);
        // The child's local +Z, under a parent turned a quarter about Y and doubled, lands on world +X.
        var origin = Vector3.Transform(Vector3.Zero, created[0].World);
        await Assert.That(Vector3.Distance(origin, new Vector3(5, 5, 0))).IsLessThan(1e-4f);
        // Without the game's registry its component is reported, never dropped silently.
        await Assert.That(created[0].Components).IsEmpty();
        await Assert.That(created[0].Unresolved.Single().Id).IsEqualTo(s_game);
    }

    [Test]
    public async Task unknown_and_cyclic_parents_become_roots_and_are_reported()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var prefab = new PrefabData
        {
            Entities =
            [
                [Meta(a, "a", b)],
                [Meta(b, "b", a)],
                [Meta(Guid.NewGuid(), "orphan", Guid.NewGuid())],
                [Transform(Vector3.One)],
            ],
        };
        var created = new List<PrefabEntity>();
        var problems = new List<string>();

        PrefabInstantiator.Instantiate(prefab, Matrix4x4.Identity, created.Add, problems: problems);

        await Assert.That(created.Count).IsEqualTo(4);
        await Assert.That(created.Count(entity => entity.Parent == -1)).IsGreaterThanOrEqualTo(3);
        await Assert.That(created[2].Parent).IsEqualTo(-1);
        await Assert.That(problems.Count).IsEqualTo(2);
        await Assert.That(created[3].Id).IsEqualTo(Guid.Empty);
        await Assert.That(created[3].World).IsEqualTo(Matrix4x4.CreateTranslation(Vector3.One));
    }
}
