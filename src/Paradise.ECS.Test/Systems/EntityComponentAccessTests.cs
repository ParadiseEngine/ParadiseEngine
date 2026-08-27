namespace Paradise.ECS.Test;

[Component]
public partial struct ArbitraryReaderSource
{
    public Entity Target;
    public float Observed;
}

[Component]
public partial struct ArbitraryWriterSource
{
    public Entity Target;
    public bool Applied;
}

public ref partial struct ArbitraryEntityReaderSystem : IEntitySystem
{
    public ref ArbitraryReaderSource Source;
    public EntityComponentReader<TestPosition> TargetPosition;

    public void Execute()
    {
        if (TargetPosition.TryGet(Source.Target, out var position))
            Source.Observed = position.X;
    }
}

public ref partial struct ArbitraryEntityWriterSystem : IEntitySystem
{
    public ref ArbitraryWriterSource Source;
    public EntityComponentWriter<TestPosition> TargetPosition;

    public void Execute()
    {
        Source.Applied = TargetPosition.TrySet(
            Source.Target,
            new TestPosition { X = 42f, Y = 24f, Z = 0f });
    }
}

public ref partial struct ArbitraryEntityReaderGraphSystem : IEntitySystem
{
    public Entity Entity;
    public EntityComponentReader<TestPosition> TargetPosition;

    public void Execute() => _ = TargetPosition.TryGet(Entity, out _);
}

public ref partial struct ArbitraryEntityWriterGraphSystem : IEntitySystem
{
    public Entity Entity;
    public EntityComponentWriter<TestPosition> TargetPosition;

    public void Execute() => _ = TargetPosition.TrySet(Entity, default);
}

public sealed class EntityComponentAccessTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public EntityComponentAccessTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose() => _sharedWorld.Dispose();

    [Test]
    public async Task arbitrary_entity_reader_does_not_filter_query()
    {
        var target = _world.Spawn();
        _world.AddComponent(target, new TestPosition { X = 7f, Y = 0f, Z = 0f });

        var source = _world.Spawn();
        _world.AddComponent(source, new ArbitraryReaderSource { Target = target });

        using var schedule = SystemSchedule.Create()
            .Add<ArbitraryEntityReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_world);

        await Assert.That(_world.GetComponent<ArbitraryReaderSource>(source).Observed).IsEqualTo(7f);
    }

    [Test]
    public async Task arbitrary_entity_reader_returns_false_for_stale_handle()
    {
        var target = _world.Spawn();
        _world.AddComponent(target, new TestPosition { X = 7f, Y = 0f, Z = 0f });

        var source = _world.Spawn();
        _world.AddComponent(source, new ArbitraryReaderSource { Target = target, Observed = -1f });
        _world.Despawn(target);

        using var schedule = SystemSchedule.Create()
            .Add<ArbitraryEntityReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_world);

        await Assert.That(_world.GetComponent<ArbitraryReaderSource>(source).Observed).IsEqualTo(-1f);
    }

    [Test]
    public async Task arbitrary_entity_writer_writes_target_without_filtering_query()
    {
        var target = _world.Spawn();
        _world.AddComponent(target, new TestPosition { X = 1f, Y = 2f, Z = 3f });

        var source = _world.Spawn();
        _world.AddComponent(source, new ArbitraryWriterSource { Target = target });

        using var schedule = SystemSchedule.Create()
            .Add<ArbitraryEntityWriterSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_world);

        await Assert.That(_world.GetComponent<ArbitraryWriterSource>(source).Applied).IsTrue();
        await Assert.That(_world.GetComponent<TestPosition>(target).X).IsEqualTo(42f);
    }

    [Test]
    public async Task arbitrary_entity_writer_returns_false_for_stale_handle()
    {
        var target = _world.Spawn();
        _world.AddComponent(target, new TestPosition { X = 1f, Y = 2f, Z = 3f });

        var source = _world.Spawn();
        _world.AddComponent(source, new ArbitraryWriterSource { Target = target });
        _world.Despawn(target);

        using var schedule = SystemSchedule.Create()
            .Add<ArbitraryEntityWriterSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_world);

        await Assert.That(_world.GetComponent<ArbitraryWriterSource>(source).Applied).IsFalse();
    }

    [Test]
    public async Task arbitrary_entity_access_flows_into_masks_but_not_query()
    {
        var reader = GetMetadata(typeof(ArbitraryEntityReaderSystem).FullName!);
        var writer = GetMetadata(typeof(ArbitraryEntityWriterSystem).FullName!);
        int positionId = TestPosition.TypeId.Value;

        await Assert.That(reader.ReadMask.Get(positionId)).IsTrue();
        await Assert.That(reader.WriteMask.Get(positionId)).IsFalse();
        await Assert.That(reader.QueryDescription.Value.All.Get(positionId)).IsFalse();

        await Assert.That(writer.ReadMask.Get(positionId)).IsTrue();
        await Assert.That(writer.WriteMask.Get(positionId)).IsTrue();
        await Assert.That(writer.QueryDescription.Value.All.Get(positionId)).IsFalse();
    }

    [Test]
    public async Task default_dag_groups_arbitrary_entity_readers()
    {
        var firstReader = GetMetadata(typeof(ArbitraryEntityReaderGraphSystem).FullName!);
        var secondReader = GetMetadata(typeof(ArbitraryEntityReaderGraphSystem).FullName!);

        var waves = new DefaultDagScheduler()
            .ComputeWaves<ComponentMask>([firstReader, secondReader]);

        await Assert.That(waves.Length).IsEqualTo(1);
        await Assert.That(waves[0].Length).IsEqualTo(2);
    }

    [Test]
    public async Task default_dag_separates_arbitrary_entity_reader_and_writer()
    {
        var reader = GetMetadata(typeof(ArbitraryEntityReaderGraphSystem).FullName!);
        var writer = GetMetadata(typeof(ArbitraryEntityWriterGraphSystem).FullName!);

        var waves = new DefaultDagScheduler()
            .ComputeWaves<ComponentMask>([reader, writer]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0]).Contains(0);
        await Assert.That(waves[1]).Contains(1);
    }

    [Test]
    public async Task default_dag_separates_arbitrary_entity_writers()
    {
        var firstWriter = GetMetadata(typeof(ArbitraryEntityWriterGraphSystem).FullName!);
        var secondWriter = GetMetadata(typeof(ArbitraryEntityWriterGraphSystem).FullName!);

        var waves = new DefaultDagScheduler()
            .ComputeWaves<ComponentMask>([firstWriter, secondWriter]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0]).Contains(0);
        await Assert.That(waves[1]).Contains(1);
    }

    private static SystemMetadata<ComponentMask> GetMetadata(string typeName)
    {
        foreach (ref readonly var metadata in SystemRegistry<ComponentMask>.Metadata)
        {
            if (metadata.TypeName == typeName)
                return metadata;
        }

        throw new InvalidOperationException($"System metadata for {typeName} was not found.");
    }
}
