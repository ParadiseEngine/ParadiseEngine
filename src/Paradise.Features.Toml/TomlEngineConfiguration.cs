using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tomlyn;
using Tomlyn.Serialization;
using Tomlyn.Model;

// The namespace is Paradise.Features, NOT Paradise.Features.Toml, even though the assembly is:
// a namespace ending in `Toml` is in scope here and then beats the imported `Tomlyn.Toml` type,
// which is the same trap that renamed Paradise.Configuration. It also reads better — a host with
// `using Paradise.Features;` gets the reader with the model.
namespace Paradise.Features;

/// <summary>Reads <c>engine.toml</c> into an <see cref="EngineConfiguration"/>.
///
/// <code>
/// # engine.toml — the integrated GPU cannot afford the probe trace.
/// [features]
/// "rendering.globalIllumination" = false
/// "game.weather" = true
///
/// [settings."game.weather"]
/// intensity = 0.6
/// windMetresPerSecond = 3.5
/// </code>
///
/// <para><b>The feature name is one key, quoted.</b> TOML would otherwise read
/// <c>rendering.globalIllumination = false</c> as a table <c>rendering</c> holding
/// <c>globalIllumination</c>, and under <c>[settings]</c> that nesting is genuinely ambiguous —
/// <c>[settings.game.weather]</c> cannot be told apart from a feature <c>game</c> with a setting
/// <c>weather</c>. One rule for both sections, so nothing is ambiguous in either: quote the name.
/// A table where a feature's state belongs is refused with the quoted form in the
/// message.</para>
///
/// <para><b>It reads text, not a path.</b> Every other reader in the engine takes a Zio
/// <c>IFileSystem</c>; this one takes the stream, because a host that reads its configuration
/// before it has mounted anything is the normal case — the mount is a decision made one layer up,
/// and it already has the file open.</para></summary>
public static class TomlEngineConfiguration
{
    /// <summary>The name a host looks for by convention, next to the game's other data.</summary>
    public const string DefaultFileName = "engine.toml";

    /// <summary>Reads a configuration document.</summary>
    /// <exception cref="FormatException">The document is not valid TOML, a section is not a
    /// table, a feature's state is not a boolean, or a feature's settings are not a table.</exception>
    public static EngineConfiguration Read(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        // Parsed to syntax first, then bound through a SOURCE-GENERATED context, the way every
        // other TOML reader here does it: TomlSerializer's reflection path would take NativeAOT
        // and trimming with it.
        var syntax = Tomlyn.Parsing.SyntaxParser.Parse(toml, sourceName: null, validate: false);
        if (syntax.HasErrors)
        {
            throw new FormatException(
                "The engine configuration is not valid TOML: " +
                string.Join("; ", syntax.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }

        TomlTable? root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(toml, UntypedToml.Default);
        }
        catch (TomlException error)
        {
            throw new FormatException($"The engine configuration is not valid TOML: {error.Message}", error);
        }
        return From(root ?? new TomlTable());
    }

    /// <inheritdoc cref="Read(string)"/>
    public static EngineConfiguration Read(Stream toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        using var reader = new StreamReader(toml);
        return Read(reader.ReadToEnd());
    }

    private static EngineConfiguration From(TomlTable root) => new()
    {
        Features = ReadFeatures(root),
        Settings = ReadSettings(root),
    };

    private static FeatureOverrides ReadFeatures(TomlTable root)
    {
        if (!TryGetTable(root, "features", out var features)) return FeatureOverrides.None;

        var states = new List<KeyValuePair<string, bool>>();
        foreach (var (name, value) in features)
        {
            var enabled = value switch
            {
                bool state => state,
                TomlTable => throw new FormatException(
                    $"'{name}' is a table; a feature is true or false and its name is one key — write " +
                    $"\"{name}.<feature>\" = false, and put what a feature is configured with under " +
                    "[settings.\"<feature>\"]."),
                _ => throw new FormatException(
                    $"'{name}' is {Describe(value)}; a feature is true or false."),
            };
            states.Add(new KeyValuePair<string, bool>(name, enabled));
        }
        return FeatureOverrides.From(states);
    }

    private static IReadOnlyDictionary<string, FeatureSettings> ReadSettings(TomlTable root)
    {
        if (!TryGetTable(root, "settings", out var settings)) return EngineConfiguration.Empty.Settings;

        var read = new List<KeyValuePair<string, FeatureSettings>>();
        foreach (var (name, value) in settings)
        {
            if (value is not TomlTable table)
            {
                throw new FormatException(
                    $"The settings for '{name}' are {Describe(value)}; a feature's settings are a table. " +
                    "Whether the feature is ON belongs under [features].");
            }
            read.Add(new KeyValuePair<string, FeatureSettings>(name, TomlJson.SettingsOf(name, table)));
        }
        return EngineConfiguration.FromSettings(read).Settings;
    }

    private static bool TryGetTable(TomlTable root, string name, out TomlTable table)
    {
        if (!root.TryGetValue(name, out var value))
        {
            table = null!;
            return false;
        }
        table = value as TomlTable
            ?? throw new FormatException($"'{name}' must be a table, not {Describe(value)}.");
        return true;
    }

    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        TomlArray or TomlTableArray => "an array",
        string => "a string",
        bool => "a boolean",
        long or double => "a number",
        _ => value.GetType().Name,
    };
}

/// <summary>Untyped binding, source-generated: the document is a tree of tables and scalars, and
/// what a feature's settings mean is the game's business, not this reader's. Top-level because the
/// generator only emits for a type it can see at namespace scope.</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class UntypedToml : TomlSerializerContext;
