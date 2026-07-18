#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Paradise.Export.Pipeline
{
    /// <summary>
    /// Converts the PNG/JPEG textures embedded in a GLB to KTX2 (Basis Universal) via the Khronos
    /// <c>ktx create</c> CLI (KTX-Software v5 — the replacement for the removed legacy toktx),
    /// rewriting the GLB to reference them through <c>KHR_texture_basisu</c>. Engine-neutral port
    /// of the Unity GlbKtx2TextureProcessor core. Resolves <c>ktx</c> from
    /// <c>PARADISE_KTX_PATH</c>, a repo-local <c>third_party/tools/KTX-Software</c>, or PATH; when
    /// unavailable the conversion fails gracefully and the GLB is left as-is.
    /// </summary>
    public static class KtxCreate
    {
        public const string KtxPathEnvironmentVariable = "PARADISE_KTX_PATH";
        private const string Ktx2MimeType = "image/ktx2";
        private const string Ktx2ExtensionName = "KHR_texture_basisu";
        private const int KtxTimeoutMilliseconds = 30 * 60 * 1000;

        // All textures encode to UASTC (high quality, near-lossless) rather than ETC1S/basis-lz.
        // ETC1S is ~2 bpp and visibly degrades detailed/saturated colour maps (it lifts dark, saturated
        // texels), which diverged the .NET runtime's albedo from Godot's (Godot imports the source PNG
        // at full quality). UASTC transcodes to the same BC7 the engine already uses and matches Godot's
        // fidelity, unifying the two hosts on one high-quality format. Zstd supercompression keeps the
        // on-disk size reasonable.
        public enum TextureEncodingPreset
        {
            UastcColorSrgb,
            UastcColorLinear,
            UastcDataLinear,
            UastcNormalLinear,
        }

        public enum ConversionResult
        {
            NoConvertibleTextures,
            ConvertedAllTextures,
            ToolMissing,
            Failed,
        }

        /// <summary>
        /// Encode a STANDALONE source image (PNG/JPEG — a spritesheet) to a KTX2 sidecar next to
        /// it, for the .NET runtime; the Godot editor keeps rendering the source image. Same
        /// colour preset as GLB base-colour textures (UASTC, linear-tagged container — see
        /// <see cref="BuildCreateArguments"/>). Idempotent by timestamp: an output at least as
        /// new as its source returns <see cref="ConversionResult.NoConvertibleTextures"/>.
        /// </summary>
        public static ConversionResult ConvertImageFile(
            string sourceFullPath,
            string outputKtx2Path,
            string? repoRoot = null,
            Action<string>? log = null,
            Action<string>? error = null)
        {
            if (!File.Exists(sourceFullPath))
            {
                error?.Invoke($"Source image not found: '{sourceFullPath}'.");
                return ConversionResult.Failed;
            }

            if (File.Exists(outputKtx2Path) &&
                File.GetLastWriteTimeUtc(outputKtx2Path) >= File.GetLastWriteTimeUtc(sourceFullPath))
            {
                return ConversionResult.NoConvertibleTextures;
            }

            string? ktxPath = FindKtx(repoRoot);
            if (string.IsNullOrWhiteSpace(ktxPath))
            {
                error?.Invoke(
                    $"ktx not found. Set {KtxPathEnvironmentVariable}, vendor KTX-Software v5 under third_party/tools/KTX-Software, or add ktx to PATH.");
                return ConversionResult.ToolMissing;
            }

            string extension = Path.GetExtension(sourceFullPath).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? ".jpg"
                : ".png";
            if (!TryConvertImageBytes(
                    ktxPath, File.ReadAllBytes(sourceFullPath), extension,
                    TextureEncodingPreset.UastcColorSrgb, out byte[] ktx2Bytes, error))
            {
                return ConversionResult.Failed;
            }

            File.WriteAllBytes(outputKtx2Path, ktx2Bytes);
            log?.Invoke($"KTX2 image: {Path.GetFileName(sourceFullPath)} → {Path.GetFileName(outputKtx2Path)} ({ktx2Bytes.Length} bytes)");
            return ConversionResult.ConvertedAllTextures;
        }

        public static ConversionResult ConvertEmbeddedTextures(
            string glbFullPath,
            string? repoRoot = null,
            string? externalTextureRoot = null,
            Action<string>? log = null,
            Action<string>? error = null)
        {
            if (!File.Exists(glbFullPath) || !GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out byte[] binChunk))
            {
                error?.Invoke($"Failed to parse GLB '{glbFullPath}'.");
                return ConversionResult.Failed;
            }

            if (gltf["images"] is not JsonArray images || gltf["textures"] is not JsonArray textures ||
                gltf["bufferViews"] is not JsonArray bufferViews)
            {
                return ConversionResult.NoConvertibleTextures;
            }

            // Resolve the tool only once the GLB is known to embed convertible images —
            // textureless meshes must not fail on a missing encoder (ToolMissing is now a
            // meaningful signal: "textures exist and could not be converted").
            if (!HasConvertibleImages(images))
            {
                return ConversionResult.NoConvertibleTextures;
            }

            string? ktxPath = FindKtx(repoRoot);
            if (string.IsNullOrWhiteSpace(ktxPath))
            {
                error?.Invoke(
                    $"ktx not found. Set {KtxPathEnvironmentVariable}, vendor KTX-Software v5 under third_party/tools/KTX-Software, or add ktx to PATH.");
                return ConversionResult.ToolMissing;
            }

            var convertedImageIndices = new HashSet<int>();
            var bufferViewReplacements = new Dictionary<int, byte[]>();
            int convertibleImageCount = 0;
            Dictionary<int, TextureEncodingPreset> presets = GetImageEncodingPresets(gltf, textures, images);

            foreach (JsonObject image in images.OfType<JsonObject>())
            {
                int sourceImageIndex = images.IndexOf(image);
                string mimeType = image["mimeType"]?.GetValue<string>() ?? "";
                if (!IsPngOrJpeg(mimeType) || image["bufferView"] == null)
                {
                    continue;
                }

                convertibleImageCount++;
                if (!TryGetSourceImageBytes(image, bufferViews, binChunk, externalTextureRoot, out byte[] sourceBytes, out int sourceBufferViewIndex))
                {
                    error?.Invoke($"Could not read texture #{sourceImageIndex} in '{glbFullPath}'.");
                    continue;
                }

                string sourceExtension = string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
                TextureEncodingPreset preset = presets.TryGetValue(sourceImageIndex, out TextureEncodingPreset matched)
                    ? matched
                    : PresetFromImageName(image);

                if (!TryConvertImageBytes(ktxPath, sourceBytes, sourceExtension, preset, out byte[] ktx2Bytes, error))
                {
                    continue;
                }

                bufferViewReplacements[sourceBufferViewIndex] = ktx2Bytes;
                image["name"] = Ktx2ImageName(image, sourceImageIndex);
                image["mimeType"] = Ktx2MimeType;
                image.Remove("uri");
                convertedImageIndices.Add(sourceImageIndex);
            }

            if (convertedImageIndices.Count == 0)
            {
                return convertibleImageCount > 0 ? ConversionResult.Failed : ConversionResult.NoConvertibleTextures;
            }

            if (convertedImageIndices.Count != convertibleImageCount)
            {
                error?.Invoke(
                    $"Converted {convertedImageIndices.Count} of {convertibleImageCount} textures in '{glbFullPath}'; GLB not rewritten.");
                return ConversionResult.Failed;
            }

            binChunk = RebuildBinaryChunk(bufferViews, binChunk, bufferViewReplacements);
            ApplyBasisTextureExtensions(gltf, textures, convertedImageIndices);
            UpdateFirstBufferLength(gltf, binChunk.Length);
            GlbBinary.Write(glbFullPath, gltf, binChunk);
            log?.Invoke($"Converted {convertedImageIndices.Count} embedded texture(s) in '{glbFullPath}' to KTX2.");
            return ConversionResult.ConvertedAllTextures;
        }

        /// <summary>
        /// Rewrites a GLB so every texture is an EXTERNAL KTX2 sidecar file (<c>&lt;stem&gt;_&lt;i&gt;.ktx2</c>
        /// next to the GLB) referenced through <c>images[].uri</c>, and the image bytes are removed
        /// from the BIN chunk (the GLB shrinks to geometry). Embedded KTX2 images are extracted
        /// as-is; embedded PNG/JPEG are transcoded first (requires <c>ktx</c>). Already-external
        /// images are left untouched, so this is idempotent (re-running yields
        /// <see cref="ConversionResult.NoConvertibleTextures"/>). Both hosts read the sidecars:
        /// Godot's glTF importer and the runtime's <c>GltfSceneReader</c> external-image resolver.
        /// </summary>
        public static ConversionResult ExternalizeTextures(
            string glbFullPath,
            string? repoRoot = null,
            Action<string>? log = null,
            Action<string>? error = null)
        {
            if (!File.Exists(glbFullPath) || !GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out byte[] binChunk))
            {
                error?.Invoke($"Failed to parse GLB '{glbFullPath}'.");
                return ConversionResult.Failed;
            }

            if (gltf["images"] is not JsonArray images || gltf["textures"] is not JsonArray textures ||
                gltf["bufferViews"] is not JsonArray bufferViews)
            {
                return ConversionResult.NoConvertibleTextures;
            }

            // Embedded images (bufferView present) are the ones to externalize; already-external
            // (uri, no bufferView) images are skipped — this is what makes the pass idempotent.
            int embeddedImageCount = images.OfType<JsonObject>().Count(im => im["bufferView"] != null);
            if (embeddedImageCount == 0)
            {
                return ConversionResult.NoConvertibleTextures;
            }

            string directory = Path.GetDirectoryName(glbFullPath) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(glbFullPath);
            Dictionary<int, TextureEncodingPreset> presets = GetImageEncodingPresets(gltf, textures, images);
            string? ktxPath = null; // resolved lazily, only if a PNG/JPEG image needs transcoding

            var droppedViews = new HashSet<int>();
            var transcodedImageIndices = new HashSet<int>();
            int externalized = 0;

            foreach (JsonObject image in images.OfType<JsonObject>())
            {
                if (image["bufferView"] == null)
                {
                    continue; // already external
                }

                int imageIndex = images.IndexOf(image);
                if (!TryGetSourceImageBytes(image, bufferViews, binChunk, directory, out byte[] sourceBytes, out int bufferViewIndex))
                {
                    error?.Invoke($"Could not read texture #{imageIndex} in '{glbFullPath}'.");
                    return ConversionResult.Failed;
                }

                byte[] ktx2Bytes;
                if (IsKtx2Magic(sourceBytes))
                {
                    // Already KTX2 (pre-encoded upstream, e.g. Unity exports) — extract as-is but
                    // enforce the project's container convention: transfer = LINEAR even for
                    // sRGB-encoded texels, so Godot 4.x decodes exactly once (its basisu import
                    // path double-decodes sRGB-tagged containers) and the .NET runtime keeps
                    // choosing the GPU format by usage — the same convention --assign-tf linear
                    // gives the transcode path below.
                    ktx2Bytes = sourceBytes;
                    ForceLinearTransfer(ktx2Bytes);
                }
                else
                {
                    string mimeType = image["mimeType"]?.GetValue<string>() ?? "";
                    if (!IsPngOrJpeg(mimeType))
                    {
                        error?.Invoke($"Texture #{imageIndex} in '{glbFullPath}' is neither KTX2 nor PNG/JPEG; cannot externalize.");
                        return ConversionResult.Failed;
                    }

                    ktxPath ??= FindKtx(repoRoot);
                    if (string.IsNullOrWhiteSpace(ktxPath))
                    {
                        error?.Invoke(
                            $"ktx not found. Set {KtxPathEnvironmentVariable}, vendor KTX-Software v5 under third_party/tools/KTX-Software, or add ktx to PATH.");
                        return ConversionResult.ToolMissing;
                    }

                    string sourceExtension = string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
                    TextureEncodingPreset preset = presets.TryGetValue(imageIndex, out TextureEncodingPreset matched) ? matched : PresetFromImageName(image);
                    if (!TryConvertImageBytes(ktxPath, sourceBytes, sourceExtension, preset, out ktx2Bytes, error))
                    {
                        return ConversionResult.Failed;
                    }

                    transcodedImageIndices.Add(imageIndex);
                }

                string sidecarName = $"{stem}_{imageIndex}.ktx2";
                File.WriteAllBytes(Path.Combine(directory, sidecarName), ktx2Bytes);

                image.Remove("bufferView");
                image["uri"] = sidecarName;
                image["mimeType"] = Ktx2MimeType;
                image["name"] = Ktx2ImageName(image, imageIndex);
                droppedViews.Add(bufferViewIndex);
                externalized++;
            }

            // A PNG source that was transcoded needs the KHR_texture_basisu extension added; images
            // that were already embedded KTX2 already carry it.
            if (transcodedImageIndices.Count > 0)
            {
                ApplyBasisTextureExtensions(gltf, textures, transcodedImageIndices);
            }

            binChunk = RebuildBinaryChunkDropping(gltf, bufferViews, binChunk, droppedViews);
            UpdateFirstBufferLength(gltf, binChunk.Length);
            GlbBinary.Write(glbFullPath, gltf, binChunk);
            log?.Invoke($"Externalized {externalized} texture(s) from '{glbFullPath}' to sidecar .ktx2 file(s).");
            return ConversionResult.ConvertedAllTextures;
        }

        // Repacks the BIN chunk keeping only bufferViews NOT in <paramref name="droppedViews"/>,
        // then removes the dropped entries from the array and re-indexes every referrer (accessors,
        // sparse accessors, remaining embedded images) through the old→new map. Buffer-1+ views are
        // left untouched (external buffers aren't repacked into chunk 0).
        private static byte[] RebuildBinaryChunkDropping(JsonObject gltf, JsonArray bufferViews, byte[] sourceBin, ISet<int> droppedViews)
        {
            var kept = new List<int>();
            for (int i = 0; i < bufferViews.Count; i++)
            {
                if (!droppedViews.Contains(i))
                {
                    kept.Add(i);
                }
            }

            var newOffset = new Dictionary<int, int>();
            var newLength = new Dictionary<int, int>();
            byte[] newBin;
            using (var rebuilt = new MemoryStream())
            {
                foreach (int i in kept)
                {
                    if (bufferViews[i] is not JsonObject bv)
                    {
                        continue;
                    }

                    // Views on external buffers keep their offsets verbatim (not in chunk 0).
                    if ((bv["buffer"]?.GetValue<int>() ?? 0) != 0)
                    {
                        newOffset[i] = bv["byteOffset"]?.GetValue<int>() ?? 0;
                        newLength[i] = bv["byteLength"]?.GetValue<int>() ?? 0;
                        continue;
                    }

                    int off = bv["byteOffset"]?.GetValue<int>() ?? 0;
                    int len = bv["byteLength"]?.GetValue<int>() ?? 0;
                    GlbBinary.WritePadding(rebuilt, 0x00);
                    newOffset[i] = (int)rebuilt.Position;
                    newLength[i] = len;
                    if (len > 0 && off >= 0 && off + len <= sourceBin.Length)
                    {
                        rebuilt.Write(sourceBin, off, len);
                    }
                }

                GlbBinary.WritePadding(rebuilt, 0x00);
                newBin = rebuilt.ToArray();
            }

            var remap = new Dictionary<int, int>();
            var newViews = new JsonArray();
            for (int n = 0; n < kept.Count; n++)
            {
                int oldIndex = kept[n];
                remap[oldIndex] = n;
                var bv = (JsonObject)bufferViews[oldIndex]!.DeepClone();
                if ((bv["buffer"]?.GetValue<int>() ?? 0) == 0)
                {
                    bv["byteOffset"] = newOffset[oldIndex];
                    bv["byteLength"] = newLength[oldIndex];
                }
                // Cast to JsonNode: the Add<T> generic overload is AOT-unsafe (IL2026/IL3050).
                newViews.Add((JsonNode)bv);
            }
            gltf["bufferViews"] = newViews;

            if (gltf["accessors"] is JsonArray accessors)
            {
                foreach (JsonObject accessor in accessors.OfType<JsonObject>())
                {
                    RemapBufferView(accessor, remap);
                    if (accessor["sparse"] is JsonObject sparse)
                    {
                        if (sparse["indices"] is JsonObject indices) RemapBufferView(indices, remap);
                        if (sparse["values"] is JsonObject values) RemapBufferView(values, remap);
                    }
                }
            }

            foreach (JsonObject image in ((JsonArray)gltf["images"]!).OfType<JsonObject>())
            {
                if (image["bufferView"] != null) RemapBufferView(image, remap);
            }

            return newBin;
        }

        private static void RemapBufferView(JsonObject node, IReadOnlyDictionary<int, int> remap)
        {
            if (node["bufferView"] is JsonValue value && value.TryGetValue(out int old) && remap.TryGetValue(old, out int updated))
            {
                node["bufferView"] = updated;
            }
        }

        private static bool IsKtx2Magic(ReadOnlySpan<byte> bytes)
        {
            ReadOnlySpan<byte> magic = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
            return bytes.Length >= magic.Length && bytes[..magic.Length].SequenceEqual(magic);
        }

        /// <summary>Rewrite a KTX2 container's DFD transfer function to KHR_DF_TRANSFER_LINEAR in
        /// place (no-op when already linear). Texel data is untouched — this only changes how
        /// consumers are told to decode, per the project convention (see ExternalizeTextures).</summary>
        private static void ForceLinearTransfer(byte[] ktx2)
        {
            const int DfdByteOffsetField = 48; // KTX2 header: index section starts after 48-byte header
            const int TransferSrgb = 2;
            const int TransferLinear = 1;
            if (ktx2.Length < DfdByteOffsetField + 4)
            {
                return;
            }

            int dfdOffset = BitConverter.ToInt32(ktx2, DfdByteOffsetField);
            // Basic DFD block: 4B totalSize, then vendor/type (4B), version/blockSize (4B),
            // colorModel (1B), colorPrimaries (1B), transferFunction (1B).
            int transferOffset = dfdOffset + 4 + 8 + 2;
            if (dfdOffset <= 0 || transferOffset >= ktx2.Length)
            {
                return;
            }

            if (ktx2[transferOffset] == TransferSrgb)
            {
                ktx2[transferOffset] = TransferLinear;
            }
        }

        // ---- `ktx create` invocation --------------------------------------------------------------
        //
        // v5 differences from the removed toktx: KTX2 output is implicit (no --t2), top-left
        // origin is the default (no --upper_left_maps_to_s0t0), --format is mandatory, the
        // transfer function rides on the format (+ --assign-tf for the input), ETC1S is spelled
        // `basis-lz`, Zstandard is --zstd, and the positional order is INPUT then OUTPUT.

        public static string BuildCreateArguments(TextureEncodingPreset preset, string outputPath, string sourcePath)
        {
            var arguments = new List<string> { "create", "--generate-mipmap" };

            switch (preset)
            {
                case TextureEncodingPreset.UastcNormalLinear:
                    arguments.AddRange(new[] { "--format", "R8G8B8A8_UNORM", "--assign-tf", "linear", "--normal-mode", "--encode", "uastc", "--uastc-quality", "2", "--zstd", "10" });
                    break;
                case TextureEncodingPreset.UastcDataLinear:
                case TextureEncodingPreset.UastcColorLinear:
                    arguments.AddRange(new[] { "--format", "R8G8B8A8_UNORM", "--assign-tf", "linear", "--encode", "uastc", "--uastc-quality", "2", "--zstd", "10" });
                    break;
                default: // UastcColorSrgb — base colour / emissive
                    // The texels ARE sRGB-encoded, but the container is deliberately tagged LINEAR:
                    // Godot 4.7 double-decodes glTF KHR_texture_basisu KTX2s whose DFD says sRGB
                    // (one decode at import + one via the sRGB VRAM format), rendering colour
                    // textures visibly darker than authored (gray 188 read back as 128). A
                    // linear-tagged container makes Godot decode exactly once, and the .NET runtime
                    // picks its GPU format by USAGE (ColorSrgb → BC7-sRGB), ignoring the DFD — so
                    // both hosts show the authored colours. Revisit if Godot fixes the double decode.
                    arguments.AddRange(new[] { "--format", "R8G8B8A8_UNORM", "--assign-tf", "linear", "--encode", "uastc", "--uastc-quality", "2", "--zstd", "10" });
                    break;
            }

            arguments.Add(ProcessTools.QuoteArgument(sourcePath));
            arguments.Add(ProcessTools.QuoteArgument(outputPath));
            return string.Join(" ", arguments);
        }

        public static bool IsValidKtx2(byte[] bytes, out string error)
        {
            error = "";
            if (bytes.Length < 80)
            {
                error = $"file is too small ({bytes.Length} bytes).";
                return false;
            }

            ReadOnlySpan<byte> identifier = stackalloc byte[]
            {
                0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A,
            };
            if (!bytes.AsSpan(0, identifier.Length).SequenceEqual(identifier))
            {
                error = "missing KTX2 identifier.";
                return false;
            }

            uint pixelWidth = BitConverter.ToUInt32(bytes, 20);
            uint pixelHeight = BitConverter.ToUInt32(bytes, 24);
            uint levelCount = BitConverter.ToUInt32(bytes, 40);
            if (pixelWidth == 0 || pixelHeight == 0)
            {
                error = $"invalid dimensions {pixelWidth}x{pixelHeight}.";
                return false;
            }

            if (levelCount == 0)
            {
                error = "missing mip levels.";
                return false;
            }

            return true;
        }

        private static bool TryConvertImageBytes(
            string ktxPath,
            byte[] sourceBytes,
            string sourceExtension,
            TextureEncodingPreset preset,
            out byte[] ktx2Bytes,
            Action<string>? error)
        {
            ktx2Bytes = Array.Empty<byte>();
            string tempDirectory = Path.Combine(Path.GetTempPath(), "ParadiseKtx2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                string sourcePath = Path.Combine(tempDirectory, "source" + sourceExtension);
                string outputPath = Path.Combine(tempDirectory, "texture.ktx2");
                File.WriteAllBytes(sourcePath, sourceBytes);

                ProcessTools.ProcessResult run = ProcessTools.Run(
                    ktxPath,
                    BuildCreateArguments(preset, outputPath, sourcePath),
                    KtxTimeoutMilliseconds,
                    KtxEnvironment(ktxPath));

                if (!run.Succeeded || !File.Exists(outputPath))
                {
                    error?.Invoke($"ktx create failed (code {run.ExitCode}).\n{run.Stdout}{run.Stderr}");
                    return false;
                }

                ktx2Bytes = File.ReadAllBytes(outputPath);
                if (!IsValidKtx2(ktx2Bytes, out string validationError))
                {
                    error?.Invoke($"ktx create produced an invalid KTX2 texture: {validationError}");
                    ktx2Bytes = Array.Empty<byte>();
                    return false;
                }

                return true;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        // On macOS, point the dynamic loader at the vendored libktx next to the ktx binary.
        private static IReadOnlyDictionary<string, string>? KtxEnvironment(string ktxPath)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return null;
            }

            string? ktxDirectory = Path.GetDirectoryName(ktxPath);
            if (string.IsNullOrWhiteSpace(ktxDirectory))
            {
                return null;
            }

            string libDirectory = Path.GetFullPath(Path.Combine(ktxDirectory, "..", "lib"));
            if (!Directory.Exists(libDirectory))
            {
                return null;
            }

            var env = new Dictionary<string, string>();
            foreach (string variable in new[] { "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" })
            {
                string? existing = Environment.GetEnvironmentVariable(variable);
                env[variable] = string.IsNullOrWhiteSpace(existing) ? libDirectory : libDirectory + Path.PathSeparator + existing;
            }

            return env;
        }

        // ---- preset selection -------------------------------------------------------------------

        private static Dictionary<int, TextureEncodingPreset> GetImageEncodingPresets(JsonObject gltf, JsonArray textures, JsonArray images)
        {
            var presets = new Dictionary<int, TextureEncodingPreset>();
            if (gltf["materials"] is not JsonArray materials)
            {
                return presets;
            }

            foreach (JsonObject material in materials.OfType<JsonObject>())
            {
                var pbr = material["pbrMetallicRoughness"] as JsonObject;
                ApplyTexturePreset(pbr?["baseColorTexture"], textures, images, TextureEncodingPreset.UastcColorSrgb, presets);
                ApplyTexturePreset(material["emissiveTexture"], textures, images, TextureEncodingPreset.UastcColorSrgb, presets);
                ApplyTexturePreset(pbr?["metallicRoughnessTexture"], textures, images, TextureEncodingPreset.UastcDataLinear, presets);
                ApplyTexturePreset(material["normalTexture"], textures, images, TextureEncodingPreset.UastcNormalLinear, presets);
                ApplyTexturePreset(material["occlusionTexture"], textures, images, TextureEncodingPreset.UastcDataLinear, presets);
            }

            return presets;
        }

        private static void ApplyTexturePreset(JsonNode? textureInfo, JsonArray textures, JsonArray images, TextureEncodingPreset preset, Dictionary<int, TextureEncodingPreset> presets)
        {
            int? textureIndex = textureInfo?["index"]?.GetValue<int>();
            if (textureIndex == null || textureIndex.Value < 0 || textureIndex.Value >= textures.Count)
            {
                return;
            }

            if (textures[textureIndex.Value] is not JsonObject texture)
            {
                return;
            }

            int? imageIndex = texture["source"]?.GetValue<int>();
            if (imageIndex == null || imageIndex.Value < 0 || imageIndex.Value >= images.Count)
            {
                return;
            }

            presets[imageIndex.Value] = MergeEncodingPreset(
                presets.TryGetValue(imageIndex.Value, out TextureEncodingPreset existing) ? existing : TextureEncodingPreset.UastcColorSrgb,
                preset);
        }

        private static TextureEncodingPreset MergeEncodingPreset(TextureEncodingPreset existing, TextureEncodingPreset next)
        {
            if (existing == TextureEncodingPreset.UastcNormalLinear || next == TextureEncodingPreset.UastcNormalLinear)
            {
                return TextureEncodingPreset.UastcNormalLinear;
            }

            if (existing == TextureEncodingPreset.UastcDataLinear || next == TextureEncodingPreset.UastcDataLinear)
            {
                return TextureEncodingPreset.UastcDataLinear;
            }

            if (existing == TextureEncodingPreset.UastcColorLinear || next == TextureEncodingPreset.UastcColorLinear)
            {
                return TextureEncodingPreset.UastcColorLinear;
            }

            return TextureEncodingPreset.UastcColorSrgb;
        }

        public static TextureEncodingPreset PresetFromImageName(JsonObject image)
        {
            string imageName = image["name"]?.GetValue<string>() ?? "";
            if (ContainsAny(imageName, "Normal", "NormalMap", "Bump"))
            {
                return TextureEncodingPreset.UastcNormalLinear;
            }

            if (ContainsAny(imageName, "Metallic", "Metalness", "Roughness", "Gloss", "Occlusion", "AO"))
            {
                return TextureEncodingPreset.UastcDataLinear;
            }

            if (ContainsAny(imageName, "Mask", "Height", "Displacement"))
            {
                return TextureEncodingPreset.UastcColorLinear;
            }

            return TextureEncodingPreset.UastcColorSrgb;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ---- GLB rewrite ------------------------------------------------------------------------

        private static bool TryGetSourceImageBytes(JsonObject image, JsonArray bufferViews, byte[] binChunk, string? externalTextureRoot, out byte[] bytes, out int sourceBufferViewIndex)
        {
            bytes = Array.Empty<byte>();
            sourceBufferViewIndex = -1;

            string? uri = image["uri"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(uri) && TryGetExternalImageBytes(uri, externalTextureRoot, out bytes))
            {
                sourceBufferViewIndex = image["bufferView"]!.GetValue<int>();
                return true;
            }

            if (image["bufferView"] == null)
            {
                return false;
            }

            int bufferViewIndex = image["bufferView"]!.GetValue<int>();
            if (!TryGetBufferViewBytes(bufferViews, bufferViewIndex, binChunk, out bytes))
            {
                return false;
            }

            sourceBufferViewIndex = bufferViewIndex;
            return true;
        }

        private static bool TryGetExternalImageBytes(string uri, string? externalTextureRoot, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(externalTextureRoot) ||
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                Uri.TryCreate(uri, UriKind.Absolute, out _))
            {
                return false;
            }

            string normalizedUri = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(externalTextureRoot, normalizedUri));
            string fullRoot = Path.GetFullPath(externalTextureRoot);
            if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                return false;
            }

            bytes = File.ReadAllBytes(fullPath);
            return true;
        }

        private static bool TryGetBufferViewBytes(JsonArray bufferViews, int bufferViewIndex, byte[] binChunk, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count)
            {
                return false;
            }

            if (bufferViews[bufferViewIndex] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0)
            {
                return false;
            }

            int byteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
            int byteLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
            if (byteOffset < 0 || byteLength <= 0 || byteOffset + byteLength > binChunk.Length)
            {
                return false;
            }

            bytes = new byte[byteLength];
            Array.Copy(binChunk, byteOffset, bytes, 0, byteLength);
            return true;
        }

        private static byte[] RebuildBinaryChunk(JsonArray bufferViews, byte[] sourceBinChunk, IReadOnlyDictionary<int, byte[]> replacements)
        {
            using var rebuilt = new MemoryStream();
            for (int i = 0; i < bufferViews.Count; i++)
            {
                if (bufferViews[i] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0)
                {
                    continue;
                }

                int sourceOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
                int sourceLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
                if (sourceOffset < 0 || sourceLength <= 0 || sourceOffset + sourceLength > sourceBinChunk.Length)
                {
                    continue;
                }

                GlbBinary.WritePadding(rebuilt, 0x00);
                byte[] bytes = replacements.TryGetValue(i, out byte[]? replacement)
                    ? replacement
                    : CopyBytes(sourceBinChunk, sourceOffset, sourceLength);

                bufferView["byteOffset"] = (int)rebuilt.Position;
                bufferView["byteLength"] = bytes.Length;
                rebuilt.Write(bytes, 0, bytes.Length);
            }

            GlbBinary.WritePadding(rebuilt, 0x00);
            return rebuilt.ToArray();
        }

        private static void ApplyBasisTextureExtensions(JsonObject gltf, JsonArray textures, ISet<int> ktx2ImageIndices)
        {
            foreach (JsonObject texture in textures.OfType<JsonObject>())
            {
                if (texture["source"] == null)
                {
                    continue;
                }

                int source = texture["source"]!.GetValue<int>();
                if (!ktx2ImageIndices.Contains(source))
                {
                    continue;
                }

                if (texture["extensions"] is not JsonObject extensions)
                {
                    extensions = new JsonObject();
                    texture["extensions"] = extensions;
                }

                extensions[Ktx2ExtensionName] = new JsonObject { ["source"] = source };
                texture.Remove("source");
            }

            AddExtensionName(gltf, "extensionsUsed");
            AddExtensionName(gltf, "extensionsRequired");
        }

        private static void AddExtensionName(JsonObject gltf, string propertyName)
        {
            if (gltf[propertyName] is not JsonArray extensions)
            {
                extensions = new JsonArray();
                gltf[propertyName] = extensions;
            }

            // Match by value only on string entries — GetValue<string>() throws on non-string nodes,
            // and a malformed GLB may carry numeric/object entries in extensionsUsed/Required.
            if (!extensions.Any(n => n is JsonValue v && v.TryGetValue(out string? s)
                    && string.Equals(s, Ktx2ExtensionName, StringComparison.Ordinal)))
            {
                extensions.Add((JsonNode)Ktx2ExtensionName);
            }
        }

        private static void UpdateFirstBufferLength(JsonObject gltf, int byteLength)
        {
            var buffers = gltf["buffers"] as JsonArray;
            if (buffers?.Count > 0 && buffers[0] is JsonObject buffer)
            {
                buffer["byteLength"] = byteLength;
                return;
            }

            gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = byteLength });
        }

        // ---- tool resolution --------------------------------------------------------------------

        public static string? FindKtx(string? repoRoot = null) =>
            ProcessTools.FindExecutable(
                Environment.GetEnvironmentVariable(KtxPathEnvironmentVariable),
                RepositoryKtxPaths(repoRoot),
                "ktx");

        private static IEnumerable<string> RepositoryKtxPaths(string? repoRoot)
        {
            string root = Path.GetFullPath(Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), "third_party", "tools", "KTX-Software"));
            if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(root, "Darwin-arm64", "bin", "ktx");
            }

            if (!Directory.Exists(root))
            {
                yield break;
            }

            string[] fileNames = OperatingSystem.IsWindows()
                ? new[] { "ktx.exe" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "ktx" }
                    : new[] { "ktx", "ktx.exe" };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in fileNames)
            {
                foreach (string candidate in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private static bool HasConvertibleImages(JsonArray images)
        {
            foreach (JsonObject image in images.OfType<JsonObject>())
            {
                string mimeType = image["mimeType"]?.GetValue<string>() ?? "";
                if (IsPngOrJpeg(mimeType) && image["bufferView"] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPngOrJpeg(string mimeType) =>
            string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

        private static string Ktx2ImageName(JsonObject sourceImage, int sourceImageIndex)
        {
            string sourceName = Path.GetFileNameWithoutExtension(sourceImage["name"]?.GetValue<string>() ?? $"Texture_{sourceImageIndex}");
            return $"{sourceName}_KTX2.ktx2";
        }

        private static byte[] CopyBytes(byte[] source, int offset, int length)
        {
            byte[] copy = new byte[length];
            Array.Copy(source, offset, copy, 0, length);
            return copy;
        }
    }
}
