using System.Reflection;
using System.Runtime.InteropServices;

namespace Paradise.BT;

internal readonly struct BehaviorNodeMetadata
{
    public BehaviorNodeMetadata(Type nodeType)
    {
        Guid = nodeType.GetNodeGuid();
        Cardinality = nodeType.GetCustomAttribute<BuilderAttribute>()?.Cardinality ?? NodeCardinality.Leaf;
    }

    public Guid Guid { get; }

    public NodeCardinality Cardinality { get; }
}

internal static class GuidAttributeExtensions
{
    public static Guid GetNodeGuid(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        GuidAttribute? attribute = type.GetCustomAttribute<GuidAttribute>();
        if (attribute is null)
        {
            throw new InvalidOperationException($"Type '{type.FullName}' must define a GuidAttribute.");
        }

        return Guid.Parse(attribute.Value);
    }
}
