using Paradise.Authoring;
using Paradise.Editor.Core.Host;

namespace Paradise.Editor.Core.Schema;

/// <summary>The inspector's only source of truth about a component: the project's authoring schema,
/// as data, plus the mapping from a schema's <c>AuthoredBy</c> kind to the editor's host kind.</summary>
/// <remarks>No reflection and no game assembly in the process. A component the schema does not
/// know is still shown, as its raw table, so nothing is ever hidden.</remarks>
public interface ISchemaBinding
{
    AuthoredComponentSchema? ComponentFor(Guid componentId);

    HostKind? KindFor(string authoredBy);
}
