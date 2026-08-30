using System.Reflection;
using System.Runtime.InteropServices;

namespace Paradise.BT;

internal readonly struct BehaviorNodeMetadata(Type nodeType)
{
    public Guid Guid { get; } = nodeType.GetNodeGuid();

    public NodeCardinality Cardinality { get; } = nodeType.GetCustomAttribute<BuilderAttribute>()?.Cardinality ?? NodeCardinality.Leaf;
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
