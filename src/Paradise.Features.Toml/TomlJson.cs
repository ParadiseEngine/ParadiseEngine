using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Tomlyn.Model;

namespace Paradise.Features;

/// <summary>Turns a feature's settings table into the JSON payload
/// <see cref="FeatureSettings.Read{T}"/> binds from.
///
/// <para>The conversion exists because <c>Paradise.Features</c> holds no format reader and must
/// bind the payload with the BCL alone — see <see cref="FeatureSettings"/> for why binding
/// straight from TOML was the rejected alternative. Every TOML scalar kind has a JSON spelling;
/// dates and times become the ISO strings a <c>DateTimeOffset</c> property reads back.</para></summary>
internal static class TomlJson
{
    public static FeatureSettings SettingsOf(string name, TomlTable table)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTable(writer, name, table);
        }
        return new FeatureSettings(name, System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteTable(Utf8JsonWriter writer, string feature, TomlTable table)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in table)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, feature, key, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, string feature, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case TomlTable nested:
                WriteTable(writer, feature, nested);
                break;
            case IEnumerable<object?> items: // TomlArray and TomlTableArray both enumerate
                writer.WriteStartArray();
                foreach (var item in items) WriteValue(writer, feature, key, item);
                writer.WriteEndArray();
                break;
            case DateTime date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset offset:
                writer.WriteStringValue(offset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan time:
                writer.WriteStringValue(time.ToString("c", CultureInfo.InvariantCulture));
                break;
            default:
                // Every kind TOML can hold is above; a new one arriving with a Tomlyn upgrade
                // should say so rather than land in the payload as a ToString() nobody expected.
                throw new FormatException(
                    $"The setting '{feature}.{key}' is a {value.GetType().Name}, which the engine " +
                    "configuration does not carry.");
        }
    }
}
