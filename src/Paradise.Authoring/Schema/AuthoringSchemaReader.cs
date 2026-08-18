using System.Text.Json;

namespace Paradise.Authoring;

/// <summary>
/// Parses schema documents, and merges several into one.
///
/// An editor faces more than one source: the engine publishes a schema for its own components, and
/// the game publishes another for its. <see cref="Merge"/> exists so the editor presents a single
/// list rather than making every host reimplement the join.
/// </summary>
public static class AuthoringSchemaReader
{
    /// <summary>Parse one document. Throws <see cref="JsonException"/> on malformed input — a
    /// schema an editor cannot read is not something to paper over with an empty list, because the
    /// symptom would be "my component vanished from the dropdown" with no cause anywhere.</summary>
    public static AuthoringSchemaDocument Read(string json)
    {
        var document = JsonSerializer.Deserialize(json, AuthoringSchemaJsonContext.Default.AuthoringSchemaDocument)
            ?? throw new JsonException("Authoring schema document deserialized to null.");
        if (document.Version > AuthoringSchemaDocument.CurrentVersion)
        {
            throw new JsonException(
                $"Authoring schema is version {document.Version}, but this build understands at most "
                + $"{AuthoringSchemaDocument.CurrentVersion}. Update the editor.");
        }
        return document;
    }

    /// <summary>Serialize a document back out. Used by tests and by tooling that dumps the
    /// generated const to a file for a non-C# editor to read.</summary>
    public static string Write(AuthoringSchemaDocument document) =>
        JsonSerializer.Serialize(document, AuthoringSchemaJsonContext.Default.AuthoringSchemaDocument);

    /// <summary>
    /// Combine documents into one, earlier sources winning on a duplicate id.
    ///
    /// Earlier-wins so a host can pass the ENGINE schema first and have it be authoritative: a game
    /// that accidentally reuses <c>paradise.rigidbody</c> should not be able to redefine what the
    /// engine's own exporter will bake. Components come out ordered by id, so an editor's dropdown
    /// is stable across runs.
    /// </summary>
    public static AuthoringSchemaDocument Merge(params IEnumerable<AuthoringSchemaDocument> documents)
    {
        var byId = new Dictionary<string, AuthoredComponentSchema>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var component in document.Components)
            {
                if (component.Id.Length > 0)
                {
                    // TryAdd, not [], so the FIRST source of an id is the one that survives.
                    byId.TryAdd(component.Id, component);
                }
            }
        }

        return new AuthoringSchemaDocument
        {
            Components = [.. byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal)],
        };
    }
}
