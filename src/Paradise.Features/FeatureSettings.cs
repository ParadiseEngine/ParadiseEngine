using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Paradise.Features;

/// <summary>One feature's settings: the table written under its name in the configuration's
/// <c>settings</c> section, carried as a JSON payload and bound to a type on demand.
///
/// <para><b>Why the switch and the settings are separate sections.</b> A feature name is FLAT and
/// dotted, and letting a feature's value be a table would make <c>[features.rendering]</c> with
/// <c>bloom = false</c> mean a feature called <c>rendering</c> with a setting called
/// <c>bloom</c> — the nested spelling nobody meant, accepted silently. Keeping the two apart lets
/// <c>features</c> refuse every table and say what to write instead.</para>
///
/// <para><b>Why the payload is JSON when the file is TOML.</b> This assembly holds no format
/// reader — it cannot, the ECS references it — so the payload has to be text it can bind without
/// a package, and <see cref="JsonSerializer"/> with source-generated metadata is the BCL's only
/// AOT- and trim-clean typed binding. Binding straight from TOML was the alternative and works
/// (Tomlyn 2.10 has a source-generated context too), but it would put the binder in the reader
/// assembly, away from the type it belongs to, and make a game declare a Tomlyn context for its
/// settings and a JSON one for everything else. The conversion happens in the reader; nothing a
/// person writes is JSON.</para>
///
/// <para><b>Why a type and not a bag of getters.</b> Settings that deserve a name deserve a
/// record: one place holding the defaults, the units in the property names, and the whole shape
/// visible at once. A bag of <c>GetSingle("intensity", 1f)</c> calls would spread the defaults
/// across every call site instead.</para></summary>
public sealed class FeatureSettings
{
    /// <summary>No settings were written for this feature. Reading it hands back the type's own
    /// defaults, so a caller needs no branch — the same reason every collection-shaped result in
    /// this repo is empty rather than null.</summary>
    public static FeatureSettings None { get; } = new("", "");

    private readonly string _name;
    private readonly string _json;

    internal FeatureSettings(string name, string json)
    {
        _name = name;
        _json = json;
    }

    /// <summary>True when nothing was written: <see cref="Read{T}"/> answers with defaults.</summary>
    public bool IsEmpty => _json.Length == 0;

    /// <summary>The settings as the JSON payload they were normalized to — NOT the TOML a person
    /// wrote. For a game whose settings are not a record: something to hand to another parser, or
    /// to log.</summary>
    public string Json => _json;

    /// <summary>Binds the settings to <typeparamref name="T"/>, or hands back <c>new T()</c> when
    /// the configuration said nothing.
    ///
    /// <para><paramref name="typeInfo"/> comes from the caller's own
    /// <c>JsonSerializerContext</c>, which is what keeps this AOT-safe: this assembly never
    /// reflects over a type it has not been handed the metadata for.</para>
    ///
    /// <para><b>Two things about the settings type, both silent when got wrong.</b> Its properties
    /// must be <c>get; set;</c>: an <c>init</c> accessor makes System.Text.Json construct the
    /// object without running the parameterless constructor, so every property the file leaves out
    /// reads as <c>default</c> — 0, not the 1f the initializer says. And the context decides how a
    /// key is spelled, matching the C# property name exactly unless told otherwise, so
    /// <c>intensity</c> against an <c>Intensity</c> property binds nothing at all. Feature names
    /// are camelCase (<c>rendering.globalIllumination</c>); match them:</para>
    /// <code>
    /// public sealed record WeatherSettings
    /// {
    ///     public float Intensity { get; set; } = 1f;   // set, NOT init
    /// }
    ///
    /// [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    /// [JsonSerializable(typeof(WeatherSettings))]
    /// internal sealed partial class GameJson : JsonSerializerContext;
    /// </code></summary>
    /// <exception cref="FormatException">The settings do not fit <typeparamref name="T"/>. The
    /// message names the feature, because the file that has to be fixed spells it that
    /// way.</exception>
    public T Read<T>(JsonTypeInfo<T> typeInfo) where T : new()
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (IsEmpty) return new T();
        try
        {
            return JsonSerializer.Deserialize(_json, typeInfo) ?? new T();
        }
        catch (JsonException error)
        {
            throw new FormatException($"The settings for '{_name}' do not fit {typeof(T).Name}: {error.Message}", error);
        }
    }

    public override string ToString() => IsEmpty ? "(no settings)" : _json;
}
