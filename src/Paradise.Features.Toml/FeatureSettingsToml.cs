using System;
using Tomlyn;
using Tomlyn.Serialization;

namespace Paradise.Features;

/// <summary>Binds what a feature was configured with to the game's own settings type.</summary>
public static class FeatureSettingsToml
{
    /// <summary>Binds <paramref name="settings"/> to <typeparamref name="T"/>, or hands back
    /// <c>new T()</c> when the configuration said nothing about this feature.
    ///
    /// <para><paramref name="context"/> is the CALLER's source-generated
    /// <see cref="TomlSerializerContext"/>, which is what keeps this AOT- and trim-clean: nothing
    /// here reflects over a type it was not handed the metadata for.</para>
    ///
    /// <para><b>Set the naming policy, or the file binds nothing.</b> Without one the generator
    /// matches the C# property name exactly, so <c>intensity = 0.6</c> against an
    /// <c>Intensity</c> property reads as the default and says nothing about it. Feature names in
    /// this file are camelCase (<c>rendering.globalIllumination</c>); match them. The other half
    /// is that a settings type's properties must be <c>get; set;</c>, so a table that leaves one
    /// out keeps its initializer.</para>
    /// <code>
    /// public sealed record WeatherSettings
    /// {
    ///     public float Intensity { get; set; } = 1f;              // set, NOT init
    ///     public float WindMetresPerSecond { get; set; } = 2f;
    /// }
    ///
    /// [TomlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    /// [TomlSerializable(typeof(WeatherSettings))]
    /// internal sealed partial class GameToml : TomlSerializerContext;
    ///
    /// var weather = switches.SettingsFor(Weather.Id).Read&lt;WeatherSettings&gt;(GameToml.Default);
    /// </code></summary>
    /// <exception cref="FormatException">The settings do not fit <typeparamref name="T"/>. The
    /// message names the feature, because the file that has to be fixed spells it that
    /// way.</exception>
    public static T Read<T>(this FeatureSettings settings, TomlSerializerContext context) where T : new()
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        if (settings.IsEmpty) return new T();
        try
        {
            return TomlSerializer.Deserialize<T>(settings.Text, context) ?? new T();
        }
        catch (TomlException error)
        {
            throw new FormatException(
                $"The settings for '{settings.Name}' do not fit {typeof(T).Name}: {error.Message}", error);
        }
    }
}
