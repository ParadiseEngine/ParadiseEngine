using System.Reflection;
using System.Runtime.InteropServices;

namespace Paradise.BT;

internal readonly struct BehaviorNodeMetadata
{
    public BehaviorNodeMetadata(Type nodeType)
    {
        Guid = nodeType.GetNodeGuid();
        Cardinality = nodeType.GetCustomAttribute<BuilderAttribute>()?.Cardinality;
    }

    public Guid Guid { get; }

    /// <summary>From the node's <c>[Builder]</c> attribute; null when it carries none.</summary>
    public NodeCardinality? Cardinality { get; }
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
