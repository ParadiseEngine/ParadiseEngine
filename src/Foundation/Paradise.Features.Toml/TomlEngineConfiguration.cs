using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tomlyn;
using Tomlyn.Serialization;
using Tomlyn.Model;

// A Paradise.Features.Toml namespace would shadow the imported Tomlyn.Toml type.
namespace Paradise.Features;

/// <summary>Reads engine.toml into a format-neutral EngineConfiguration.</summary>
/// <remarks>
/// <code>
/// # engine.toml
/// [[features]]
/// name = "rendering.globalIllumination"
/// enabled = false                      # the integrated GPU cannot afford the probe trace
///
/// [[features]]
/// name = "game.weather"
/// enabled = true
/// intensity = 0.6                      # everything else is the feature's own settings
/// windMetresPerSecond = 3.5
/// </code>
///
/// <para>Each feature has one table: name identifies it, optional enabled controls its switch,
/// and all other keys are settings. Names are values so dotted names cannot become nested tables.</para>
/// <para>Hosts open the configuration before calling this reader; no filesystem mount is required.</para>
/// </remarks>
public static class TomlEngineConfiguration
{
    /// <summary>The name a host looks for by convention, next to the game's other data.</summary>
    public const string DefaultFileName = "engine.toml";

    /// <summary>The array of tables every feature entry belongs to.</summary>
    public const string FeaturesKey = "features";

    /// <summary>The reserved key naming the feature an entry is about.</summary>
    public const string NameKey = "name";

    /// <summary>The reserved key saying whether that feature runs. Optional.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>The keys a feature's settings may not use.</summary>
    public static readonly string[] ReservedKeys = [NameKey, EnabledKey];

    /// <summary>Whether <paramref name="key"/> is one of <see cref="ReservedKeys"/>.</summary>
    public static bool IsReserved(string key) => key is NameKey or EnabledKey;

    /// <summary>Reads a configuration document.</summary>
    /// <exception cref="FormatException">The document is not valid TOML, <c>features</c> is not an
    /// array of tables, an entry has no name or a name that is not a string, an entry's
    /// <c>enabled</c> is not a boolean, or two entries name one feature.</exception>
    public static EngineConfiguration Read(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        // Source-generated binding keeps the reader compatible with AOT and trimming.
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

    private static EngineConfiguration From(TomlTable root)
    {
        if (!root.TryGetValue(FeaturesKey, out var value)) return EngineConfiguration.Empty;
        if (value is not TomlTableArray entries)
        {
            throw new FormatException(
                $"'{FeaturesKey}' is {Describe(value)}; it is an array of tables — one [[{FeaturesKey}]] " +
                $"entry per feature, each naming it with {NameKey} = \"...\".");
        }

        var states = new List<KeyValuePair<string, bool>>();
        var settings = new List<KeyValuePair<string, FeatureSettings>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var name = ReadName(entry);
            if (!seen.Add(name))
            {
                throw new FormatException($"'{name}' has more than one [[{FeaturesKey}]] entry.");
            }

            if (entry.TryGetValue(EnabledKey, out var enabled))
            {
                states.Add(new KeyValuePair<string, bool>(name, enabled switch
                {
                    bool state => state,
                    string text => throw new FormatException(
                        $"'{name}' has {EnabledKey} = \"{text}\"; write it unquoted — {EnabledKey} = true."),
                    _ => throw new FormatException(
                        $"'{name}' has an {EnabledKey} that is {Describe(enabled)}; it is true or false."),
                }));
            }

            if (SettingsOf(name, entry) is { } configured)
            {
                settings.Add(new KeyValuePair<string, FeatureSettings>(name, configured));
            }
        }

        return new EngineConfiguration
        {
            Features = FeatureOverrides.From(states),
            Settings = EngineConfiguration.FromSettings(settings).Settings,
        };
    }

    private static string ReadName(TomlTable entry)
    {
        if (!entry.TryGetValue(NameKey, out var name))
        {
            throw new FormatException(
                $"A [[{FeaturesKey}]] entry has no {NameKey}; every entry names the feature it is " +
                $"about — {NameKey} = \"rendering.bloom\".");
        }
        return name as string
            ?? throw new FormatException(
                $"A [[{FeaturesKey}]] entry has a {NameKey} that is {Describe(name)}; it is a string.");
    }

    /// <summary>Serializes non-reserved keys, or returns null when no settings are present.</summary>
    private static FeatureSettings? SettingsOf(string name, TomlTable entry)
    {
        var configured = new TomlTable();
        foreach (var (key, value) in entry)
        {
            if (!IsReserved(key)) configured[key] = value!;
        }
        return configured.Count == 0
            ? null
            : new FeatureSettings(name, TomlSerializer.Serialize(configured, UntypedToml.Default));
    }

    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        TomlTableArray => "an array of tables",
        TomlArray => "an array",
        TomlTable => "a table",
        string => "a string",
        bool => "a boolean",
        long or double => "a number",
        _ => value.GetType().Name,
    };
}

/// <summary>Source-generated binding for tables and scalars.</summary>
/// <remarks>The generator requires namespace scope.</remarks>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class UntypedToml : TomlSerializerContext;
