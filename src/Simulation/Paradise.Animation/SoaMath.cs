using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Paradise.Animation;

/// <summary>Four-lane quaternion arithmetic shared by the pose blend, override and additive passes.</summary>
internal static class SoaMath
{
    public static Vector128<float> SignBit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create(unchecked((int)0x80000000)).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Dot(in SoaQuaternion a, in SoaQuaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    /// <summary>The sign bit of each lane whose rotation lies on the far hemisphere from <paramref name="reference"/>; xor it into <paramref name="q"/> to take the short arc.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> HemisphereFlip(in SoaQuaternion reference, in SoaQuaternion q) => Dot(reference, q) & SignBit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Normalize(Vector128<float> x, Vector128<float> y, Vector128<float> z, Vector128<float> w, ref SoaQuaternion output)
    {
        var inverseLength = Vector128<float>.One / Vector128.Sqrt(x * x + y * y + z * z + w * w);
        output.X = x * inverseLength;
        output.Y = y * inverseLength;
        output.Z = z * inverseLength;
        output.W = w * inverseLength;
    }

    /// <summary>The Hamilton product <c>a × b</c> per lane — <c>b</c> applied first, as <see cref="System.Numerics.Quaternion"/>'s operator and ozz order it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Multiply(in SoaQuaternion a, Vector128<float> bx, Vector128<float> by, Vector128<float> bz, Vector128<float> bw, ref SoaQuaternion output)
    {
        var x = a.W * bx + a.X * bw + a.Y * bz - a.Z * by;
        var y = a.W * by + a.Y * bw + a.Z * bx - a.X * bz;
        var z = a.W * bz + a.Z * bw + a.X * by - a.Y * bx;
        var w = a.W * bw - a.X * bx - a.Y * by - a.Z * bz;
        output.X = x;
        output.Y = y;
        output.Z = z;
        output.W = w;
    }
}
