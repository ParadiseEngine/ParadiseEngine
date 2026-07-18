#nullable enable
using System;

namespace Paradise.Export.Data
{
    /// <summary>
    /// Marks a DTO that maps to a future Paradise ECS component, carrying that component's
    /// stable GUID. Purely descriptive today; a forward hook for a future BLOB/ECS writer that
    /// resolves these GUIDs against the engine's component registry. Ported verbatim from
    /// ParadiseUnityEditor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class ParadiseComponentAttribute : Attribute
    {
        public ParadiseComponentAttribute(string guid)
        {
            Guid = Guid.Parse(guid);
        }

        public Guid Guid { get; }
    }
}
