using System.Reflection;
using System.Runtime.InteropServices;

namespace Paradise.BT;

internal readonly struct BehaviorNodeMetadata
{
    public BehaviorNodeMetadata(Guid guid, BehaviorNodeType type)
    {
        Guid = guid;
        Id = guid.GetHashCode();
        Type = type;
    }

    public Guid Guid { get; }

    public int Id { get; }

    public BehaviorNodeType Type { get; }
}

internal static class GuidAttributeExtensions
{
    public static Guid GetNodeGuid(this Type type)
    {
        ThrowHelper.ThrowIfNull(type, nameof(type));

        GuidAttribute? attribute = type.GetCustomAttribute<GuidAttribute>();
        if (attribute is null)
        {
            throw new InvalidOperationException($"Type '{type.FullName}' must define a GuidAttribute.");
        }

        return Guid.Parse(attribute.Value);
    }
}
