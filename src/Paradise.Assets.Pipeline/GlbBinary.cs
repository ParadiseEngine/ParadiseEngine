#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline
{
    /// <summary>Minimal GLB container read/write. Unknown chunk types fail rather than skip, unlike the runtime's <c>GlbContainer</c> (issue #207).</summary>
    public static class GlbBinary
    {
        public const uint Magic = 0x46546C67;
        public const uint JsonChunkType = 0x4E4F534A;
        public const uint BinChunkType = 0x004E4942;

        public static bool TryRead(string glbPath, out JsonObject gltf, out byte[] binChunk)
        {
            gltf = new JsonObject();
            binChunk = Array.Empty<byte>();

            if (!File.Exists(glbPath))
            {
                return false;
            }

            // Opening is inside the try: a Try* that throws for one cause and answers false for
            // another is a contract nobody can use.
            try
            {
                using var stream = File.OpenRead(glbPath);
                return TryRead(stream, out gltf, out binChunk);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The bytes overload is what the Zio-based build uses; the path overload delegates here.</summary>
        public static bool TryRead(byte[] glb, out JsonObject gltf, out byte[] binChunk)
            => TryRead(new MemoryStream(glb, writable: false), out gltf, out binChunk);

        private static bool TryRead(Stream stream, out JsonObject gltf, out byte[] binChunk)
        {
            gltf = new JsonObject();
            binChunk = Array.Empty<byte>();

            try
            {
                using var reader = new BinaryReader(stream);
                if (reader.BaseStream.Length < 20 || reader.ReadUInt32() != Magic || reader.ReadUInt32() != 2)
                {
                    return false;
                }

                reader.ReadUInt32(); // total length (ignored)
                uint jsonChunkLength = reader.ReadUInt32();
                if (reader.ReadUInt32() != JsonChunkType)
                {
                    return false;
                }

                string json = Encoding.UTF8.GetString(reader.ReadBytes((int)jsonChunkLength)).TrimEnd(' ', '\0');
                gltf = JsonNode.Parse(json)!.AsObject();

                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                {
                    return true;
                }

                uint binChunkLength = reader.ReadUInt32();
                if (reader.ReadUInt32() != BinChunkType)
                {
                    return false;
                }

                binChunk = reader.ReadBytes((int)binChunkLength);
                return true;
            }
            catch (Exception)
            {
                gltf = new JsonObject();
                binChunk = Array.Empty<byte>();
                return false;
            }
        }

        public static void Write(string glbPath, JsonObject gltf, byte[] binChunk)
            => File.WriteAllBytes(glbPath, Write(gltf, binChunk));

        public static byte[] Write(JsonObject gltf, byte[] binChunk)
        {
            string json = gltf.ToJsonString();
            byte[] jsonBytes = PadToFour(Encoding.UTF8.GetBytes(json), (byte)' ');
            byte[] paddedBin = binChunk.Length > 0 ? PadToFour(binChunk, 0x00) : binChunk;
            bool hasBin = paddedBin.Length > 0;
            uint totalLength = (uint)(12 + 8 + jsonBytes.Length + (hasBin ? 8 + paddedBin.Length : 0));

            var buffer = new MemoryStream((int)totalLength);
            using var writer = new BinaryWriter(buffer);
            writer.Write(Magic);
            writer.Write(2u);
            writer.Write(totalLength);
            writer.Write((uint)jsonBytes.Length);
            writer.Write(JsonChunkType);
            writer.Write(jsonBytes);
            if (hasBin)
            {
                writer.Write((uint)paddedBin.Length);
                writer.Write(BinChunkType);
                writer.Write(paddedBin);
            }

            writer.Flush();
            return buffer.ToArray();
        }

        public static int AlignToFour(int value) => (value + 3) & ~3;

        public static byte[] PadToFour(byte[] bytes, byte pad)
        {
            int alignedLength = AlignToFour(bytes.Length);
            if (alignedLength == bytes.Length)
            {
                return bytes;
            }

            byte[] padded = new byte[alignedLength];
            Array.Copy(bytes, padded, bytes.Length);
            for (int i = bytes.Length; i < padded.Length; i++)
            {
                padded[i] = pad;
            }

            return padded;
        }

        public static void WritePadding(Stream stream, byte pad)
        {
            int alignedPosition = AlignToFour((int)stream.Position);
            while (stream.Position < alignedPosition)
            {
                stream.WriteByte(pad);
            }
        }
    }
}
