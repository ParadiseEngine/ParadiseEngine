using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Paradise.Authoring.SchemaDump;

/// <summary>
/// Reads the <c>AuthoringSchema.Json</c> constant out of a compiled assembly's metadata and
/// writes it to a file — without loading the assembly. A <c>const string</c> is a literal in the
/// blob heap, so no dependency has to be resolvable and nothing runs; this works identically on a
/// desktop, wasm or test build of the game core.
/// </summary>
public static class SchemaDumper
{
    /// <summary>The generated holder's name. Its namespace is the game's root namespace, which
    /// this deliberately does not need to know.</summary>
    private const string TypeName = "AuthoringSchema";
    private const string FieldName = "Json";

    /// <summary>Writes <paramref name="assemblyPath"/>'s schema to <paramref name="outputPath"/>.
    /// Byte-compatible with what the schema's own producers write: the raw constant, UTF-8, no
    /// trailing newline — so re-dumping an unchanged schema is a no-op in git.</summary>
    public static void Run(string assemblyPath, string outputPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var portable = new PEReader(stream);
        var metadata = portable.GetMetadataReader();

        var matches = new List<(string Namespace, string Json)>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Name) != TypeName)
            {
                continue;
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.GetString(field.Name) != FieldName ||
                    (field.Attributes & System.Reflection.FieldAttributes.Literal) == 0)
                {
                    continue;
                }
                var constant = metadata.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode != ConstantTypeCode.String)
                {
                    continue;
                }
                var blob = metadata.GetBlobBytes(constant.Value);
                matches.Add((metadata.GetString(type.Namespace), Encoding.Unicode.GetString(blob)));
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{assemblyPath}' has no {TypeName}.{FieldName} constant. The schema generator "
                + "emits it for any assembly declaring [Authored] types — is this the game's core "
                + "assembly, and does it reference Paradise.Authoring?");
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{assemblyPath}' has {matches.Count} {TypeName} types ("
                + string.Join(", ", matches.Select(m => m.Namespace)) + "); expected one.");
        }

        // WriteAllText, not an append or a writer with a newline: producers of this file have
        // always written the bare constant, and the drift tests compare against it.
        File.WriteAllText(outputPath, matches[0].Json);
    }
}
