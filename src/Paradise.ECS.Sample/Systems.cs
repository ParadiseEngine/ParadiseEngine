namespace Paradise.ECS.Sample;

// ============================================================================
// System Definitions - Demonstrating various system patterns
// ============================================================================

// ---- Inline Entity Systems ----

/// <summary>
/// Updates entity positions by adding velocity. Demonstrates inline entity system
/// with writable and read-only component refs.
/// </summary>
public ref partial struct MovementSystem : IEntitySystem
{
    public ref Position Position;
    public ref readonly Velocity Velocity;

    public void Execute()
    {
        Position = new(Position.X + Velocity.X, Position.Y + Velocity.Y);
    }
}

/// <summary>
/// Applies gravity to velocity. Demonstrates inline entity system with single writable ref.
/// </summary>
public ref partial struct GravitySystem : IEntitySystem
{
    public ref Velocity Velocity;

    public void Execute()
    {
        Velocity = new(Velocity.X, Velocity.Y - 9.8f);
    }
}

/// <summary>
/// Clamps position within bounds. Demonstrates [After] ordering attribute.
/// Always runs after MovementSystem.
/// </summary>
[After<MovementSystem>]
public ref partial struct BoundsSystem : IEntitySystem
{
    public ref Position Position;

    public void Execute()
    {
        Position = new(Math.Clamp(Position.X, 0, 1000), Math.Clamp(Position.Y, 0, 1000));
    }
}

// ---- Queryable Entity Systems ----

/// <summary>
/// Updates movable entity positions using queryable composition.
/// Demonstrates accessing components through a [Queryable] type's Data struct in an entity system.
/// The ref field is resolved by the generator to the Movable.Data type alias.
/// </summary>
public ref partial struct QueryableMovementSystem : IEntitySystem
{
    public MovableEntity Movable;

    public void Execute()
    {
        Movable.Position = new(Movable.Position.X + Movable.Velocity.X,
                               Movable.Position.Y + Movable.Velocity.Y);
    }
}

// ---- Inline Chunk Systems ----

/// <summary>
/// Applies gravity in batch using span access. Demonstrates inline chunk system.
/// </summary>
public ref partial struct GravityBatchSystem : IChunkSystem
{
    public Span<Velocity> Velocities;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Velocities.Length; i++)
            Velocities[i] = new(Velocities[i].X, Velocities[i].Y - 9.8f);
    }
}

// ---- Health Systems (independent from Position/Velocity — enables parallel waves) ----

/// <summary>
/// Regenerates health toward max. Only touches Health, so it can run in parallel
/// with any Position/Velocity system.
/// </summary>
public ref partial struct HealthRegenSystem : IEntitySystem
{
    public ref Health Health;

    public void Execute()
    {
        if (Health.Current < Health.Max)
            Health = new Health { Current = Health.Current + 1, Max = Health.Max };
    }
}

/// <summary>
/// Clamps health to valid range. Demonstrates [After] ordering within a parallel wave.
/// Runs after HealthRegenSystem but can still share a wave with Velocity-only systems.
/// </summary>
[After<HealthRegenSystem>]
public ref partial struct HealthClampSystem : IEntitySystem
{
    public ref Health Health;

    public void Execute()
    {
        Health = new Health { Current = Math.Clamp(Health.Current, 0, Health.Max), Max = Health.Max };
    }
}

// ---- Queryable Chunk Systems ----

/// <summary>
/// Applies gravity in batch using queryable composition.
/// Demonstrates accessing components through a [Queryable] type's ChunkData struct in a chunk system.
/// The ref readonly field is resolved by the generator to the Movable.ChunkData type alias.
/// </summary>
public ref partial struct QueryableGravityBatchSystem : IChunkSystem
{
    public MovableChunk Movable;

    public void ExecuteChunk()
    {
        var velocities = Movable.VelocitySpan;
        for (int i = 0; i < Movable.EntityCount; i++)
            velocities[i] = new(velocities[i].X, velocities[i].Y - 9.8f);
    }
}

// ---- Entity Handle Injection Systems ----

/// <summary>
/// Despawns entities with zero or negative health using the injected Entity handle.
/// Demonstrates entity handle injection in IEntitySystem for targeted ECB operations.
/// </summary>
public ref partial struct DespawnDeadSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly Health Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        if (Health.Current <= 0)
            Commands.Despawn(Entity);
    }
}

/// <summary>
/// Spawns a clone entity at the same position when health is low.
/// Demonstrates reading the current Entity handle alongside component data.
/// </summary>
public ref partial struct SpawnCloneWhenLowHealthSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly Position Position;
    public ref readonly Health Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        if (Health.Current > 0 && Health.Current <= 20)
        {
            var clone = Commands.Spawn();
            Commands.AddComponent(clone, new Position(Position.X, Position.Y));
            Commands.AddComponent(clone, new Health(Health.Max));
        }
    }
}

/// <summary>
/// Chunk system that despawns entities with zero health using ReadOnlySpan&lt;Entity&gt;.
/// Demonstrates entity span injection in IChunkSystem for batch ECB operations.
/// </summary>
public ref partial struct ChunkDespawnDeadSystem : IChunkSystem
{
    public ReadOnlySpan<Entity> Entities;
    public ReadOnlySpan<Health> Healths;
    public EntityCommandBuffer Commands;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Entities.Length; i++)
        {
            if (Healths[i].Current <= 0)
                Commands.Despawn(Entities[i]);
        }
    }
}
