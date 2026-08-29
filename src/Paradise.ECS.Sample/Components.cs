namespace Paradise.ECS.Sample;

// ============================================================================
// Component Definitions
// ============================================================================

/// <summary>
/// Position component for 2D game entities.
/// </summary>
[System.Runtime.InteropServices.Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
[Component]
public partial struct Position
{
    public float X;
    public float Y;

    public Position(float x, float y)
    {
        X = x;
        Y = y;
    }

    public override readonly string ToString() => $"({X}, {Y})";
}

/// <summary>
/// Velocity component for moving entities.
/// </summary>
[System.Runtime.InteropServices.Guid("B2C3D4E5-F678-90AB-CDEF-123456789012")]
[Component]
public partial struct Velocity
{
    public float X;
    public float Y;

    public Velocity(float x, float y)
    {
        X = x;
        Y = y;
    }

    public override readonly string ToString() => $"({X}, {Y})";
}

/// <summary>
/// Health component for damageable entities.
/// </summary>
[System.Runtime.InteropServices.Guid("C3D4E5F6-7890-ABCD-EF12-345678901234")]
[Component]
public partial struct Health
{
    public int Current;
    public int Max;

    public Health(int max)
    {
        Current = max;
        Max = max;
    }

    public override readonly string ToString() => $"{Current}/{Max}";
}

// Tags used as entity markers
[System.Runtime.InteropServices.Guid("D4E5F678-90AB-CDEF-1234-567890123456")]
[Tag]
public partial struct PlayerTag;

[System.Runtime.InteropServices.Guid("E5F67890-ABCD-EF12-3456-789012345678")]
[Tag]
public partial struct EnemyTag;

/// <summary>
/// Name component for named entities.
/// </summary>
[System.Runtime.InteropServices.Guid("F6789012-CDEF-1234-5678-901234567890")]
[Component]
public partial struct Name
{
    // Fixed-size name buffer for AOT compatibility (no managed strings)
    public unsafe fixed char Value[32];
    public int Length;

    public Name(ReadOnlySpan<char> name)
    {
        Length = Math.Min(name.Length, 32);
        unsafe
        {
            fixed (char* ptr = Value)
            {
                name[..Length].CopyTo(new Span<char>(ptr, Length));
            }
        }
    }

    public override readonly string ToString()
    {
        unsafe
        {
            fixed (char* ptr = Value)
            {
                return new string(ptr, 0, Length);
            }
        }
    }
}
