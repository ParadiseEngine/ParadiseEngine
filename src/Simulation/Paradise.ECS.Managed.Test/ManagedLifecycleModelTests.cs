namespace Paradise.ECS.Managed.Test;

[ManagedComponent]
public sealed partial class ModelPayload
{
    public int Value { get; init; }
}

[Component]
public partial struct ModelPosition
{
    public int Value;
}

public sealed class ManagedLifecycleModelTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "A fixed seed reproduces the structural-operation sequence in this regression test.")]
    [Test]
    [Arguments(178)]
    [Arguments(914)]
    public async Task MixedStructuralChangesAndSnapshots_MatchIndependentEntityModel(int seed)
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var snapshot = shared.CreateWorld();
        var random = new Random(seed);
        var entities = new List<Entity>();
        var expected = new Dictionary<Entity, ModelState>();

        for (int step = 0; step < 2000; step++)
        {
            if (entities.Count == 0 || random.Next(6) == 0)
            {
                var mask = ComponentMask.Empty;
                bool hasManaged = random.Next(2) == 0;
                if (hasManaged)
                    mask = mask.Set(ModelPayload.SlotTypeId);
                var spawned = world.CreateEntity(in mask);
                entities.Add(spawned);
                expected.Add(spawned, new ModelState { HasManaged = hasManaged });
            }
            else
            {
                int index = random.Next(entities.Count);
                var entity = entities[index];
                var state = expected[entity];
                switch (random.Next(7))
                {
                    case 0:
                        if (state.HasManaged)
                            world.RemoveManaged<ModelPayload>(entity);
                        else
                            world.AddManaged<ModelPayload>(entity, null);
                        state.HasManaged = !state.HasManaged;
                        state.Payload = null;
                        break;
                    case 1:
                        if (state.HasManaged)
                        {
                            state.Payload = random.Next(3) == 0 ? null : new ModelPayload { Value = step };
                            world.SetManaged(entity, state.Payload);
                        }
                        break;
                    case 2:
                        if (state.HasPosition)
                            world.RemoveComponent<ModelPosition>(entity);
                        else
                            world.AddComponent(entity, new ModelPosition { Value = step });
                        state.HasPosition = !state.HasPosition;
                        state.Position = step;
                        break;
                    case 3:
                        world.OverwriteEntity(entity, EntityBuilder.Create().Add(new ModelPosition { Value = step }));
                        state.HasManaged = false;
                        state.Payload = null;
                        state.HasPosition = true;
                        state.Position = step;
                        break;
                    case 4:
                        world.AddComponents(entity, EntityBuilder.Create().Add(new ModelPosition { Value = step }));
                        state.HasPosition = true;
                        state.Position = step;
                        break;
                    case 5:
                        if (!world.Despawn(entity))
                            throw new InvalidOperationException($"Seed {seed}, step {step}: live entity did not despawn.");
                        entities.RemoveAt(index);
                        expected.Remove(entity);
                        if (world.HasManaged<ModelPayload>(entity))
                            throw new InvalidOperationException($"Seed {seed}, step {step}: a dead entity retained presence.");
                        break;
                    case 6:
                        snapshot.CopyFrom(world);
                        Check(snapshot, expected, seed, step);
                        // Recycled snapshots must retain valid allocation/free-list state when made writable again.
                        world.CopyFrom(snapshot);
                        break;
                }
            }

            Check(world, expected, seed, step);
        }

        world.Clear();
        snapshot.CopyFrom(world);
        await Assert.That(world.EntityCount).IsEqualTo(0);
        await Assert.That(snapshot.EntityCount).IsEqualTo(0);
    }

    private static void Check(World world, Dictionary<Entity, ModelState> expected, int seed, int step)
    {
        if (world.EntityCount != expected.Count)
            throw new InvalidOperationException($"Seed {seed}, step {step}: entity count differs.");
        foreach (var (entity, state) in expected)
        {
            if (!world.IsAlive(entity) || world.HasManaged<ModelPayload>(entity) != state.HasManaged ||
                world.HasComponent<ModelPosition>(entity) != state.HasPosition)
                throw new InvalidOperationException($"Seed {seed}, step {step}: component presence differs for {entity}.");
            if (state.HasManaged && !ReferenceEquals(world.GetManaged<ModelPayload>(entity), state.Payload))
                throw new InvalidOperationException($"Seed {seed}, step {step}: managed identity differs for {entity}.");
            if (state.HasPosition && world.GetComponent<ModelPosition>(entity).Value != state.Position)
                throw new InvalidOperationException($"Seed {seed}, step {step}: unmanaged value differs for {entity}.");
        }
    }

    private sealed class ModelState
    {
        public bool HasManaged { get; set; }
        public ModelPayload? Payload { get; set; }
        public bool HasPosition { get; set; }
        public int Position { get; set; }
    }
}
