namespace Paradise.BT;

internal static class ThrowHelper
{
    public static void ThrowIfNull(object? value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
    }

    public static void ThrowIfNodeIndexOutOfRange(int nodeIndex, int count)
    {
        if ((uint)nodeIndex >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeIndex), $"Node index {nodeIndex} is outside the range 0..{count - 1}.");
        }
    }

    public static void ThrowIfNegative(float value, string paramName)
    {
        if (value < 0f)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");
        }
    }

    public static void ThrowIfNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");
        }
    }

}
