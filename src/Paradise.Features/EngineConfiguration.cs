using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Paradise.Features;

/// <summary>One configuration layer as a document: today, the features it switches.
///
/// <para>A record with one property rather than a bare <see cref="FeatureOverrides"/> because the
/// FILE is the thing being versioned. A second section — quality levels, a subsystem's tuning —
/// is a property here and a key in the same file, and every host that already reads
/// <c>engine.json</c> keeps reading it.</para>
///
/// <para><b>It reads text, not a path.</b> Every other reader in the engine takes a Zio
/// <c>IFileSystem</c>; this one cannot, because the assembly it lives in is referenced by the
/// renderer and by the ECS and must stay free of package dependencies (see the csproj). The host
/// already knows what its content is mounted over — it opens the file and hands over the
/// stream — and that is the same decision, made one layer up.</para></summary>
public sealed record EngineConfiguration
{
    /// <summary>The name a host looks for by convention, next to the game's other runtime data.</summary>
    public const string DefaultFileName = "engine.json";

    /// <summary>A layer that configures nothing.</summary>
    public static EngineConfiguration Empty { get; } = new();

    /// <summary>What this layer says about features.</summary>
    public FeatureOverrides Features { get; init; } = FeatureOverrides.None;

    /// <summary>Hand-edited by design: comments and a trailing comma are allowed, the same
    /// latitude the engine's other hand-edited documents get.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a configuration document:
    /// <code>
    /// {
    ///   "features": {
    ///     "rendering.bloom": false,
    ///     "rendering.globalIllumination": true
    ///   }
    /// }
    /// </code>
    /// Keys are the flat, dotted feature names — the same string a declaration, a
    /// <c>--features</c> flag and an error message all use. A nested
    /// <c>{"rendering": {"bloom": false}}</c> is refused rather than guessed at: two spellings of
    /// one name is how a config file starts disagreeing with itself.</summary>
    /// <exception cref="FormatException">The document is not an object, <c>features</c> is not an
    /// object, or one of its values is not a boolean.</exception>
    public static EngineConfiguration Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = Parse(() => JsonDocument.Parse(json, DocumentOptions));
        return From(document);
    }

    /// <inheritdoc cref="Read(string)"/>
    public static EngineConfiguration Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = Parse(() => JsonDocument.Parse(json, DocumentOptions));
        return From(document);
    }

    /// <summary>This layer with <paramref name="later"/> applied over it, section by section.</summary>
    public EngineConfiguration Merge(EngineConfiguration later)
    {
        ArgumentNullException.ThrowIfNull(later);
        return new EngineConfiguration { Features = Features.Merge(later.Features) };
    }

    private static JsonDocument Parse(Func<JsonDocument> parse)
    {
        try
        {
            return parse();
        }
        catch (JsonException error)
        {
            throw new FormatException($"The engine configuration is not valid JSON: {error.Message}", error);
        }
    }

    private static EngineConfiguration From(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"The engine configuration must be a JSON object, not {root.ValueKind}.");
        if (!root.TryGetProperty("features", out var features)) return Empty;
        if (features.ValueKind != JsonValueKind.Object)
            throw new FormatException($"'features' must be an object of name → true/false, not {features.ValueKind}.");

        var states = new List<KeyValuePair<string, bool>>();
        foreach (var entry in features.EnumerateObject())
        {
            var enabled = entry.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => throw new FormatException(
                    $"'{entry.Name}' holds an object; feature names are flat and dotted — write \"{entry.Name}.<feature>\": false."),
                _ => throw new FormatException($"'{entry.Name}' is {entry.Value.ValueKind}; a feature is true or false."),
            };
            states.Add(new KeyValuePair<string, bool>(entry.Name, enabled));
        }
        return new EngineConfiguration { Features = FeatureOverrides.From(states) };
    }
}
