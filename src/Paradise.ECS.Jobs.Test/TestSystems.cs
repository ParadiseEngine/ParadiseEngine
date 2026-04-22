namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Test system: adds velocity to position per entity.
/// </summary>
public ref partial struct TestMovementSystem : IEntitySystem
{
    public ref TestPosition Position;
    public ref readonly TestVelocity Velocity;

    public void Execute()
    {
        Position = new TestPosition { X = Position.X + Velocity.X, Y = Position.Y + Velocity.Y, Z = Position.Z + Velocity.Z };
    }
}

/// <summary>
/// Test system: multiplies velocity Y by 2.
/// </summary>
public ref partial struct TestGravitySystem : IEntitySystem
{
    public ref TestVelocity Velocity;

    public void Execute()
    {
        Velocity = new TestVelocity { X = Velocity.X, Y = Velocity.Y * 2, Z = Velocity.Z };
    }
}
