#nullable enable
using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Paradise.Export.Data;

namespace Paradise.Export.Serialization.Converters
{
    // Hand-written (AOT-safe) converters for the contract's structural shapes: System.Numerics
    // vectors/quaternions/matrices as flat float arrays (matrices column-major), and Color32 as an
    // { r, g, b, a } object. Read implementations are the exact inverses (added for the runtime's
    // ExportJsonReader) — Write∘Read is the identity on every shape.

    internal static class ConverterShared
    {
        public static void ReadFloatArray(ref Utf8JsonReader reader, scoped Span<float> destination)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a float array.");
            for (int i = 0; i < destination.Length; i++)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    throw new JsonException($"Expected {destination.Length} numbers, got {i}.");
                destination[i] = reader.GetSingle();
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException($"Expected exactly {destination.Length} numbers.");
        }
    }

    public sealed class Color32Converter : JsonConverter<Color32>
    {
        public override Color32 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Color32 expects an object.");
            float r = 0f, g = 0f, b = 0f, a = 0f;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string? name = reader.GetString();
                reader.Read();
                float value = reader.GetSingle();
                switch (name)
                {
                    case "r": r = value; break;
                    case "g": g = value; break;
                    case "b": b = value; break;
                    case "a": a = value; break;
                }
            }
            return Color32.FromRgba(r, g, b, a);
        }

        public override void Write(Utf8JsonWriter writer, Color32 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("r", value.R);
            writer.WriteNumber("g", value.G);
            writer.WriteNumber("b", value.B);
            writer.WriteNumber("a", value.A);
            writer.WriteEndObject();
        }
    }

    public sealed class Vector2Converter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Span<float> f = stackalloc float[2];
            ConverterShared.ReadFloatArray(ref reader, f);
            return new Vector2(f[0], f[1]);
        }

        public override void Write(Utf8JsonWriter writer, Vector2 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteEndArray();
        }
    }

    public sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Span<float> f = stackalloc float[3];
            ConverterShared.ReadFloatArray(ref reader, f);
            return new Vector3(f[0], f[1], f[2]);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteNumberValue(v.Z);
            writer.WriteEndArray();
        }
    }

    public sealed class Vector4Converter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Span<float> f = stackalloc float[4];
            ConverterShared.ReadFloatArray(ref reader, f);
            return new Vector4(f[0], f[1], f[2], f[3]);
        }

        public override void Write(Utf8JsonWriter writer, Vector4 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteNumberValue(v.Z);
            writer.WriteNumberValue(v.W);
            writer.WriteEndArray();
        }
    }

    public sealed class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override Quaternion Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Span<float> f = stackalloc float[4];
            ConverterShared.ReadFloatArray(ref reader, f);
            return new Quaternion(f[0], f[1], f[2], f[3]);
        }

        public override void Write(Utf8JsonWriter writer, Quaternion q, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(q.X);
            writer.WriteNumberValue(q.Y);
            writer.WriteNumberValue(q.Z);
            writer.WriteNumberValue(q.W);
            writer.WriteEndArray();
        }
    }

    public sealed class Matrix4x4Converter : JsonConverter<Matrix4x4>
    {
        // Exact inverse of Write below: flat[3] returns to M41, flat[12] to M14, etc. The
        // result is the contract's column-vector-layout matrix — runtime consumers transpose
        // to get a System.Numerics row-vector-convention model matrix.
        public override Matrix4x4 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            Span<float> f = stackalloc float[16];
            ConverterShared.ReadFloatArray(ref reader, f);
            return new Matrix4x4(
                f[0], f[4], f[8], f[12],
                f[1], f[5], f[9], f[13],
                f[2], f[6], f[10], f[14],
                f[3], f[7], f[11], f[15]);
        }

        // Column-major flat float[16], matching the original Newtonsoft converter order.
        public override void Write(Utf8JsonWriter writer, Matrix4x4 m, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(m.M11); writer.WriteNumberValue(m.M21); writer.WriteNumberValue(m.M31); writer.WriteNumberValue(m.M41);
            writer.WriteNumberValue(m.M12); writer.WriteNumberValue(m.M22); writer.WriteNumberValue(m.M32); writer.WriteNumberValue(m.M42);
            writer.WriteNumberValue(m.M13); writer.WriteNumberValue(m.M23); writer.WriteNumberValue(m.M33); writer.WriteNumberValue(m.M43);
            writer.WriteNumberValue(m.M14); writer.WriteNumberValue(m.M24); writer.WriteNumberValue(m.M34); writer.WriteNumberValue(m.M44);
            writer.WriteEndArray();
        }
    }
}
