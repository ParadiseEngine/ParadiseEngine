using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Paradise.Features;

/// <summary>One feature's settings: the object written under its name in the config file's
/// <c>settings</c> section, carried as the text it was written as and bound to a type on demand.
///
/// <para><b>Why the switch and the settings are separate sections.</b> A feature name is FLAT and
/// dotted, and letting a value under <c>features</c> be an object would make
/// <c>{"rendering": {"bloom": false}}</c> parse as a feature called <c>rendering</c> with a
/// setting called <c>bloom</c> — the nested spelling nobody meant, accepted silently. Keeping the
/// two sections apart lets <c>features</c> refuse every object and say what to write
/// instead.</para>
///
/// <para><b>Why a type and not a bag of getters.</b> Settings that deserve a name deserve a
/// record: one place holding the defaults, the units in the property names, and the whole shape
/// visible at once. <see cref="Read{T}"/> binds to it through a <see cref="JsonTypeInfo{T}"/> the
/// GAME supplies from its own source-generated context, so this stays AOT- and trim-clean without
/// a reflection path — a bag of <c>GetSingle("threshold", 1f)</c> calls would spread the defaults
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

    /// <summary>The settings object exactly as the file spelled it. For a game whose settings are
    /// not a record — a passthrough to another parser, or something to log.</summary>
    public string Raw => _json;

    /// <summary>Binds the settings to <typeparamref name="T"/>, or hands back
    /// <c>new T()</c> when the file said nothing.
    ///
    /// <para><paramref name="typeInfo"/> comes from the caller's own
    /// <c>JsonSerializerContext</c>, which is what keeps this AOT-safe: this assembly never
    /// reflects over a type it has not been handed the metadata for. Give
    /// <typeparamref name="T"/>'s properties their defaults as initializers and a partly written
    /// settings object fills in the rest.</para>
    ///
    /// <para><b>Two things about the settings type, both silent when got wrong.</b> Its properties
    /// must be <c>get; set;</c>: an <c>init</c> accessor makes System.Text.Json construct the
    /// object without running the parameterless constructor, so every property the file leaves out
    /// reads as <c>default</c> — 0, not the 1f the initializer says. And the context decides how a
    /// key is spelled, matching the C# property name exactly unless told otherwise, so
    /// <c>"intensity"</c> against an <c>Intensity</c> property binds nothing at all. Feature names
    /// in this file are camelCase (<c>rendering.globalIllumination</c>); match them:</para>
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
    /// <exception cref="FormatException">The settings object does not fit
    /// <typeparamref name="T"/>. The message names the feature, because the file that has to be
    /// fixed spells it that way.</exception>
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
