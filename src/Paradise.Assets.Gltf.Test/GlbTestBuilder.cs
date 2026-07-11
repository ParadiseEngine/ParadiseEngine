using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Gltf.Test;

/// <summary>Programmatic tiny-GLB construction for tests: append raw data to the BIN chunk,
/// declare glTF JSON structure via <see cref="JsonNode"/>s, assemble a spec-conformant (or
/// deliberately corrupted) GLB byte array. The workhorse behind every reader test — no binary
/// fixtures to regenerate when the schema subset grows.</summary>
internal sealed class GlbTestBuilder
{
    public const int Float = 5126;
    public const int UByte = 5121;
    public const int UShort = 5123;
    public const int UInt = 5125;

    private readonly MemoryStream _bin = new();
    private readonly JsonArray _bufferViews = [];
    private readonly JsonArray _accessors = [];
    private readonly JsonArray _meshes = [];
    private readonly JsonArray _nodes = [];
    private readonly JsonArray _materials = [];
    private readonly JsonArray _textures = [];
    private readonly JsonArray _images = [];
    private readonly JsonArray _skins = [];
    private readonly JsonArray _animations = [];
    private JsonArray _sceneRoots = [];
    private int? _sceneIndex;
    private string? _externalBufferUri;

    public void UseExternalBufferUri(string uri) => _externalBufferUri = uri;

    public int AddBufferView(ReadOnlySpan<byte> data, int? byteStride = null)
    {
        // 4-byte alignment keeps accessor component offsets legal regardless of what preceded.
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var offset = (int)_bin.Length;
        _bin.Write(data);
        var view = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = data.Length,
        };
        if (byteStride is { } stride) view["byteStride"] = stride;
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    public int AddBufferView(float[] data, int? byteStride = null)
    {
        var bytes = new byte[data.Length * 4];
        for (var i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), data[i]);
        return AddBufferView(bytes, byteStride);
    }

    public int AddAccessor(int bufferView, int componentType, string type, int count,
        int byteOffset = 0, bool normalized = false, bool sparse = false)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (byteOffset != 0) accessor["byteOffset"] = byteOffset;
        if (normalized) accessor["normalized"] = true;
        if (sparse) accessor["sparse"] = new JsonObject { ["count"] = 1 };
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    public int AddFloatAccessor(float[] data, string type, int? byteStride = null)
    {
        var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };
        var view = AddBufferView(data, byteStride);
        return AddAccessor(view, Float, type, data.Length / components);
    }

    public int AddIndexAccessor(uint[] indices, int componentType = UShort)
    {
        var elementSize = componentType switch { UByte => 1, UShort => 2, UInt => 4, _ => throw new ArgumentException(null, nameof(componentType)) };
        var bytes = new byte[indices.Length * elementSize];
        for (var i = 0; i < indices.Length; i++)
        {
            switch (componentType)
            {
                case UByte: bytes[i] = (byte)indices[i]; break;
                case UShort: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)indices[i]); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), indices[i]); break;
            }
        }
        var view = AddBufferView(bytes);
        return AddAccessor(view, componentType, "SCALAR", indices.Length);
    }

    public int AddMesh(params JsonObject[] primitives)
    {
        _meshes.Add(new JsonObject { ["primitives"] = new JsonArray(primitives) });
        return _meshes.Count - 1;
    }

    public static JsonObject Primitive(
        int position, int? normal = null, int? uv = null, int? tangent = null,
        int? indices = null, int? material = null, int? mode = null,
        int? joints = null, int? weights = null)
    {
        var attributes = new JsonObject { ["POSITION"] = position };
        if (normal is { } n) attributes["NORMAL"] = n;
        if (uv is { } u) attributes["TEXCOORD_0"] = u;
        if (tangent is { } t) attributes["TANGENT"] = t;
        if (joints is { } j) attributes["JOINTS_0"] = j;
        if (weights is { } w) attributes["WEIGHTS_0"] = w;
        var primitive = new JsonObject { ["attributes"] = attributes };
        if (indices is { } i) primitive["indices"] = i;
        if (material is { } m) primitive["material"] = m;
        if (mode is { } md) primitive["mode"] = md;
        return primitive;
    }

    public int AddNode(int? mesh = null, float[]? translation = null, float[]? rotation = null,
        float[]? scale = null, float[]? matrix = null, int[]? children = null, string? name = null,
        int? skin = null)
    {
        var node = new JsonObject();
        if (name is not null) node["name"] = name;
        if (mesh is { } m) node["mesh"] = m;
        if (skin is { } sk) node["skin"] = sk;
        if (matrix is not null) node["matrix"] = ToJsonArray(matrix);
        if (translation is not null) node["translation"] = ToJsonArray(translation);
        if (rotation is not null) node["rotation"] = ToJsonArray(rotation);
        if (scale is not null) node["scale"] = ToJsonArray(scale);
        if (children is not null)
        {
            var arr = new JsonArray();
            foreach (var c in children) arr.Add(c);
            node["children"] = arr;
        }
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    public int AddSkin(int[] joints, int? inverseBindMatrices = null, string? name = null)
    {
        var skin = new JsonObject();
        if (name is not null) skin["name"] = name;
        var arr = new JsonArray();
        foreach (var j in joints) arr.Add(j);
        skin["joints"] = arr;
        if (inverseBindMatrices is { } ibm) skin["inverseBindMatrices"] = ibm;
        _skins.Add(skin);
        return _skins.Count - 1;
    }

    public int AddAnimation(string? name, params (int Node, string Path, int Input, int Output, string? Interpolation)[] channels)
    {
        var samplers = new JsonArray();
        var channelArray = new JsonArray();
        foreach (var (node, path, input, output, interpolation) in channels)
        {
            var sampler = new JsonObject { ["input"] = input, ["output"] = output };
            if (interpolation is not null) sampler["interpolation"] = interpolation;
            samplers.Add(sampler);
            channelArray.Add(new JsonObject
            {
                ["sampler"] = samplers.Count - 1,
                ["target"] = new JsonObject { ["node"] = node, ["path"] = path },
            });
        }
        var animation = new JsonObject { ["channels"] = channelArray, ["samplers"] = samplers };
        if (name is not null) animation["name"] = name;
        _animations.Add(animation);
        return _animations.Count - 1;
    }

    public int AddMaterial(JsonObject material)
    {
        _materials.Add(material);
        return _materials.Count - 1;
    }

    public int AddTexture(int? source = null, int? basisuSource = null)
    {
        var texture = new JsonObject();
        if (source is { } s) texture["source"] = s;
        if (basisuSource is { } b)
            texture["extensions"] = new JsonObject
            {
                ["KHR_texture_basisu"] = new JsonObject { ["source"] = b },
            };
        _textures.Add(texture);
        return _textures.Count - 1;
    }

    public int AddImage(byte[] bytes, string? mimeType = null)
    {
        var view = AddBufferView(bytes);
        var image = new JsonObject { ["bufferView"] = view };
        if (mimeType is not null) image["mimeType"] = mimeType;
        _images.Add(image);
        return _images.Count - 1;
    }

    public int AddExternalImage(string uri)
    {
        _images.Add(new JsonObject { ["uri"] = uri });
        return _images.Count - 1;
    }

    /// <summary>Raw-JSON escape hatches for corruption tests that need structure the typed
    /// helpers refuse to produce (extra buffers, views on them, hand-rolled images).</summary>
    public int AddRawBufferView(JsonObject view)
    {
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    public int AddRawImage(JsonObject image)
    {
        _images.Add(image);
        return _images.Count - 1;
    }

    public JsonArray? ExtraBuffers { get; set; }

    public void SetSceneRoots(params int[] nodeIndices)
    {
        _sceneRoots = [];
        foreach (var n in nodeIndices) _sceneRoots.Add(n);
    }

    public void SetSceneIndex(int index) => _sceneIndex = index;

    public byte[] Build(bool badMagic = false, uint version = 2, bool omitJsonChunk = false, int truncateBy = 0, JsonArray? extraScenes = null)
    {
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
        };
        var scenes = new JsonArray { new JsonObject { ["nodes"] = _sceneRoots.DeepClone() } };
        if (extraScenes is not null)
        {
            foreach (var s in extraScenes) scenes.Add(s!.DeepClone());
        }
        root["scenes"] = scenes;
        root["scene"] = _sceneIndex ?? 0;
        if (_nodes.Count > 0) root["nodes"] = _nodes.DeepClone();
        if (_meshes.Count > 0) root["meshes"] = _meshes.DeepClone();
        if (_accessors.Count > 0) root["accessors"] = _accessors.DeepClone();
        if (_bufferViews.Count > 0) root["bufferViews"] = _bufferViews.DeepClone();
        if (_materials.Count > 0) root["materials"] = _materials.DeepClone();
        if (_textures.Count > 0) root["textures"] = _textures.DeepClone();
        if (_images.Count > 0) root["images"] = _images.DeepClone();
        if (_skins.Count > 0) root["skins"] = _skins.DeepClone();
        if (_animations.Count > 0) root["animations"] = _animations.DeepClone();
        if (_bin.Length > 0 || _externalBufferUri is not null || ExtraBuffers is not null)
        {
            var buffer = new JsonObject { ["byteLength"] = (int)_bin.Length };
            if (_externalBufferUri is not null) buffer["uri"] = _externalBufferUri;
            var buffersArray = new JsonArray { buffer };
            if (ExtraBuffers is not null)
            {
                foreach (var extra in ExtraBuffers) buffersArray.Add(extra!.DeepClone());
            }
            root["buffers"] = buffersArray;
        }

        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var jsonPadded = Pad(json, (byte)' ');
        var bin = _bin.ToArray();
        var binPadded = Pad(bin, 0);

        using var output = new MemoryStream();
        var total = 12 + (omitJsonChunk ? 0 : 8 + jsonPadded.Length) + (binPadded.Length > 0 ? 8 + binPadded.Length : 0);
        WriteU32(output, badMagic ? 0xDEADBEEF : 0x46546C67);
        WriteU32(output, version);
        WriteU32(output, (uint)total);
        if (!omitJsonChunk)
        {
            WriteU32(output, (uint)jsonPadded.Length);
            WriteU32(output, 0x4E4F534A);
            output.Write(jsonPadded);
        }
        if (binPadded.Length > 0)
        {
            WriteU32(output, (uint)binPadded.Length);
            WriteU32(output, 0x004E4942);
            output.Write(binPadded);
        }

        var result = output.ToArray();
        return truncateBy > 0 ? result[..^truncateBy] : result;
    }

    private static JsonArray ToJsonArray(float[] values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static byte[] Pad(byte[] data, byte padByte)
    {
        var padded = (data.Length + 3) & ~3;
        if (padded == data.Length) return data;
        var result = new byte[padded];
        data.CopyTo(result, 0);
        for (var i = data.Length; i < padded; i++) result[i] = padByte;
        return result;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    // -------- canned shapes --------

    /// <summary>A unit quad (4 verts, 2 triangles) with all four attributes, tightly packed in
    /// separate buffer views. The baseline happy-path asset.</summary>
    public static GlbTestBuilder FullQuad(out int meshIndex, int indexComponentType = UShort)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor(
            [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], "VEC3");
        var normal = b.AddFloatAccessor(
            [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], "VEC3");
        var uv = b.AddFloatAccessor(
            [0, 0, 1, 0, 1, 1, 0, 1], "VEC2");
        var tangent = b.AddFloatAccessor(
            [1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1], "VEC4");
        var indices = b.AddIndexAccessor([0, 1, 2, 0, 2, 3], indexComponentType);
        meshIndex = b.AddMesh(Primitive(position, normal, uv, tangent, indices));
        var node = b.AddNode(mesh: meshIndex, name: "quad");
        b.SetSceneRoots(node);
        return b;
    }
}
