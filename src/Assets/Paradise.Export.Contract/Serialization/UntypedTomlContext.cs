using Tomlyn.Model;

namespace Paradise.Export.Serialization
{
    /// <summary>Source-generated context for the untyped model, so the TOML path stays AOT-clean.</summary>
    /// <remarks>Shared by <see cref="ExportTomlReader"/> and the writer in <c>Paradise.Export</c>.</remarks>
    [Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
    internal sealed partial class UntypedTomlContext : Tomlyn.Serialization.TomlSerializerContext;
}
