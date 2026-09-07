using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;

namespace Paradise.Features;

/// <summary>One configuration layer as a document: which features are on, and what each of them
/// is configured with.
///
/// <para>A record with sections rather than a bare <see cref="FeatureOverrides"/> because the FILE
/// is the thing being versioned. A third section is a property here and a key in the same file,
/// and every host that already reads <c>engine.json</c> keeps reading it.</para>
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

    /// <summary>Declared BEFORE <see cref="Empty"/> and load-bearing there: static initializers
    /// run in declaration order, so an <see cref="Empty"/> built above this line would read it as
    /// null and hand out a layer whose <see cref="Settings"/> is null — which nothing catches
    /// until something iterates it.</summary>
    internal static readonly ImmutableDictionary<string, FeatureSettings> EmptySettings =
        ImmutableDictionary<string, FeatureSettings>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>A layer that configures nothing.</summary>
    public static EngineConfiguration Empty { get; } = new();

    /// <summary>What this layer says about which features are on.</summary>
    public FeatureOverrides Features { get; init; } = FeatureOverrides.None;

    /// <summary>What this layer configures each feature with, by feature name. A name here need
    /// not appear in <see cref="Features"/>: settings and the switch are written independently,
    /// and a feature that ships on only ever needs the settings half.</summary>
    public IReadOnlyDictionary<string, FeatureSettings> Settings { get; init; } = EmptySettings;

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
    ///     "game.weather": true
    ///   },
    ///   "settings": {
    ///     "game.weather": { "intensity": 0.6, "windMetresPerSecond": 3.5 }
    ///   }
    /// }
    /// </code>
    /// Keys in both sections are the flat, dotted feature names — the same string a declaration, a
    /// <c>--features</c> flag and an error message all use. A nested
    /// <c>{"rendering": {"bloom": false}}</c> under <c>features</c> is refused rather than guessed
    /// at: two spellings of one name is how a config file starts disagreeing with itself.</summary>
    /// <exception cref="FormatException">The document is not an object, a section is not an
    /// object, a feature's state is not a boolean, or a feature's settings are not an
    /// object.</exception>
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

    /// <summary>This layer with <paramref name="later"/> applied over it, section by section.
    ///
    /// <para>A feature's settings are REPLACED wholesale, not deep-merged. A deep merge reads
    /// well in the two-file case and stops being predictable the moment a list or a nested object
    /// is involved — "which layer owns element 3" has no answer anyone wants to reason about at
    /// two in the morning. A later file that means to change one field writes the object it
    /// wants.</para></summary>
    public EngineConfiguration Merge(EngineConfiguration later)
    {
        ArgumentNullException.ThrowIfNull(later);
        return new EngineConfiguration
        {
            Features = Features.Merge(later.Features),
            Settings = later.Settings.Count == 0
                ? Settings
                : Settings.Count == 0
                    ? later.Settings
                    : EmptySettings.SetItems(Settings).SetItems(later.Settings),
        };
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

        return new EngineConfiguration
        {
            Features = ReadFeatures(root),
            Settings = ReadSettings(root),
        };
    }

    private static FeatureOverrides ReadFeatures(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features)) return FeatureOverrides.None;
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
                    $"'{entry.Name}' holds an object; a feature is true or false and its name is flat and " +
                    $"dotted — write \"{entry.Name}.<feature>\": false, and put what a feature is configured " +
                    "with under the top-level \"settings\" section."),
                _ => throw new FormatException($"'{entry.Name}' is {entry.Value.ValueKind}; a feature is true or false."),
            };
            states.Add(new KeyValuePair<string, bool>(entry.Name, enabled));
        }
        return FeatureOverrides.From(states);
    }

    private static ImmutableDictionary<string, FeatureSettings> ReadSettings(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var settings)) return EmptySettings;
        if (settings.ValueKind != JsonValueKind.Object)
            throw new FormatException(
                $"'settings' must be an object of feature name → settings object, not {settings.ValueKind}.");

        var builder = EmptySettings.ToBuilder();
        foreach (var entry in settings.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException(
                    $"The settings for '{entry.Name}' are {entry.Value.ValueKind}; a feature's settings are an " +
                    "object. Whether the feature is ON belongs in the \"features\" section.");
            }
            builder[entry.Name] = new FeatureSettings(entry.Name, entry.Value.GetRawText());
        }
        return builder.ToImmutable();
    }
}
