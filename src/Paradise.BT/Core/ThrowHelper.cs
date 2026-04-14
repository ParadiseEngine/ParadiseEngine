namespace Paradise.BT;

internal static class ThrowHelper
{
    public static void ThrowIfNull(object? value, string paramName)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, paramName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
#endif
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

    public static void ThrowInvalidNodeDefinition(Type nodeType, BehaviorNodeType nodeTypeKind, int childCount)
    {
        string message = nodeTypeKind switch
        {
            BehaviorNodeType.Action => $"Action node '{nodeType.Name}' cannot have children.",
            BehaviorNodeType.Decorate => $"Decorator node '{nodeType.Name}' must have exactly one child, but had {childCount}.",
            _ => $"Invalid node definition for '{nodeType.Name}'.",
        };

        throw new InvalidOperationException(message);
    }
}
