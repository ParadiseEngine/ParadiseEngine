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
/// <para><b>One entry per feature, holding everything about it.</b> This is the shape an authored
/// component already has in a <c>*.prefab</c> — reserved keys and a payload
/// (<c>PrefabComponent.ReservedKeys</c>) — and it is that shape for the same reasons. The name is
/// a VALUE, not a key, so a dotted name needs no quoting rule and cannot be confused with table
/// nesting; and the switch sits with the settings, which is where a person looks when they want to
/// know what a feature is doing.</para>
///
/// <para><b><see cref="ReservedKeys"/> are the reader's; every other key is a setting.</b> That is
/// the cost of the shape, and the same cost a prefab component pays: a game whose settings want a
/// key called <c>name</c> cannot have one, and a boolean called <c>enabled</c> is read as the
/// switch. <c>enabled</c> is optional — an entry may configure a feature without saying anything
/// about whether it runs, and a feature that ships on then needs only its settings.</para>
///
/// <para><b>It reads text, not a path.</b> Every other reader in the engine takes a Zio
/// <c>IFileSystem</c>; this one takes the stream, because a host that reads its configuration
/// before it has mounted anything is the normal case — the mount is a decision made one layer up,
/// and it already has the file open.</para></summary>
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
                // Last-wins would drop the first entry in silence, and two blocks for one feature
                // is a copy-paste rather than an intention worth guessing at.
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

    /// <summary>The entry's own keys, minus the reserved ones, back as the TOML they were written
    /// in — so the game's context binds what the file says and nothing is converted on the way
    /// through. Null when the entry configures nothing, so a feature carrying only a switch keeps
    /// the shared <see cref="FeatureSettings.None"/> rather than an entry holding an empty
    /// table.</summary>
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

/// <summary>Untyped binding, source-generated: the document is a tree of tables and scalars, and
/// what a feature's settings mean is the game's business, not this reader's. Top-level because the
/// generator only emits for a type it can see at namespace scope.</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class UntypedToml : TomlSerializerContext;
