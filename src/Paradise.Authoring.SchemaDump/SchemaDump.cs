using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// Relaxed escaping on purpose: the schema carries authored prose, and the default encoder
    /// escapes every non-ASCII character to <c>\uXXXX</c>, which would turn a Chinese doc string
    /// into an unreadable line in the very file this indenting exists to make readable. JSON is
    /// UTF-8 here, so the literal characters are correct and every parser reads them.
    /// </summary>
    private static readonly JsonSerializerOptions s_indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes <paramref name="assemblyPath"/>'s schema to <paramref name="outputPath"/>,
    /// INDENTED — the constant is one line because it is a string literal in an assembly, and a
    /// file people open, review and diff should not be. The two are the same document; only the
    /// whitespace differs, so the constant stays minified and every assembly embedding it stays
    /// small. Deterministic, so re-dumping an unchanged schema is still a no-op in git.</summary>
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

        File.WriteAllText(outputPath, Indent(matches[0].Json, assemblyPath));
    }

    /// <summary>
    /// Re-renders the constant with indentation. Parsing and re-writing rather than pretty-printing
    /// by hand is what keeps the two forms provably the same document — the dump tool's test asserts
    /// exactly that, by parsing the file back and comparing it to the constant.
    /// </summary>
    /// <remarks>
    /// A trailing newline is deliberate now that the file has more than one line: without it the
    /// last line has no terminator, which is what makes a diff show "\ No newline at end of file"
    /// on every schema change.
    /// </remarks>
    private static string Indent(string json, string assemblyPath)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            // The generator emitted something unparseable. Naming the assembly matters: the
            // constant is generated code nobody wrote, so the only useful lead is which build
            // produced it.
            throw new InvalidOperationException(
                $"'{assemblyPath}' has a {TypeName}.{FieldName} constant that is not valid JSON ({error.Message}).",
                error);
        }

        return document!.ToJsonString(s_indented) + "\n";
    }
}
