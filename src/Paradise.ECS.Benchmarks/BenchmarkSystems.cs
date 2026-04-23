namespace Paradise.ECS.Benchmarks;

/// <summary>
/// Benchmark system: adds velocity to position per entity.
/// </summary>
public ref partial struct BenchMovementSystem : IEntitySystem
{
    public ref BenchPosition Position;
    public ref readonly BenchVelocity Velocity;

    public void Execute()
    {
        Position = new BenchPosition
        {
            X = Position.X + Velocity.X,
            Y = Position.Y + Velocity.Y,
            Z = Position.Z + Velocity.Z
        };
    }
}
