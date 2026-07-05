using System.Buffers.Binary;
using Paradise.Assets.Gltf.Json;

namespace Paradise.Assets.Gltf;

/// <summary>Typed, bounds-checked reads of glTF accessor data out of the BIN chunk. Honors
/// bufferView byteStride (interleaved sources); supports the component types the export
/// contract produces — float32 vectors, normalized u8/u16 texcoords, u8/u16/u32 indices.</summary>
internal static class AccessorReader
{
    private const int ComponentByte = 5120;
    private const int ComponentUByte = 5121;
    private const int ComponentShort = 5122;
    private const int ComponentUShort = 5123;
    private const int ComponentUInt = 5125;
    private const int ComponentFloat = 5126;

    /// <summary>Read a VEC2/VEC3/VEC4/SCALAR float accessor into <paramref name="destination"/>
    /// as <paramref name="componentCount"/> floats per element. float32 is read verbatim;
    /// normalized u8/u16 are converted (texcoord/color cases). Everything else throws.</summary>
    public static void ReadFloats(
        GltfRoot root, ReadOnlyMemory<byte> bin, int accessorIndex, int componentCount, Span<float> destination)
    {
        var accessor = GetAccessor(root, accessorIndex);
        var expected = ExpectedComponents(accessor.Type);
        if (expected != componentCount)
            throw new InvalidDataException(
                $"Accessor {accessorIndex} is {accessor.Type} ({expected} components) but {componentCount} were requested.");
        if (destination.Length < accessor.Count * componentCount)
            throw new ArgumentException("Destination too small for accessor contents.", nameof(destination));

        var (data, stride) = ResolveView(root, bin, accessor, ElementSize(accessor.ComponentType) * componentCount);
        var span = data.Span;

        for (var i = 0; i < accessor.Count; i++)
        {
            var element = span.Slice(i * stride);
            for (var c = 0; c < componentCount; c++)
            {
                destination[i * componentCount + c] = accessor.ComponentType switch
                {
                    ComponentFloat => BinaryPrimitives.ReadSingleLittleEndian(element[(c * 4)..]),
                    ComponentUByte when accessor.Normalized => element[c] / 255f,
                    ComponentUShort when accessor.Normalized =>
                        BinaryPrimitives.ReadUInt16LittleEndian(element[(c * 2)..]) / 65535f,
                    _ => throw new NotSupportedException(
                        $"Accessor {accessorIndex}: component type {accessor.ComponentType} " +
                        $"(normalized={accessor.Normalized}) is not supported for float attributes."),
                };
            }
        }
    }

    /// <summary>Read a SCALAR index accessor (u8/u16/u32) widened to uint.</summary>
    public static uint[] ReadIndices(GltfRoot root, ReadOnlyMemory<byte> bin, int accessorIndex)
    {
        var accessor = GetAccessor(root, accessorIndex);
        if (accessor.Type != "SCALAR")
            throw new InvalidDataException($"Index accessor {accessorIndex} must be SCALAR, got '{accessor.Type}'.");

        var elementSize = accessor.ComponentType switch
        {
            ComponentUByte => 1,
            ComponentUShort => 2,
            ComponentUInt => 4,
            _ => throw new NotSupportedException(
                $"Index accessor {accessorIndex}: component type {accessor.ComponentType} is not a valid index type."),
        };

        var (data, stride) = ResolveView(root, bin, accessor, elementSize);
        var span = data.Span;
        var indices = new uint[accessor.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            var element = span.Slice(i * stride);
            indices[i] = elementSize switch
            {
                1 => element[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(element),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(element),
            };
        }
        return indices;
    }

    public static GltfAccessor GetAccessor(GltfRoot root, int index)
    {
        var accessors = root.Accessors
            ?? throw new InvalidDataException("glTF references an accessor but declares none.");
        if ((uint)index >= (uint)accessors.Length)
            throw new InvalidDataException($"Accessor index {index} out of range ({accessors.Length} declared).");
        var accessor = accessors[index];
        if (accessor.Sparse is not null)
            throw new NotSupportedException($"Accessor {index} is sparse — sparse accessors are not supported.");
        return accessor;
    }

    /// <summary>Slice the BIN chunk for a bufferView, bounds-checking the accessor's full range
    /// (offset + count×stride). Returns the accessor-based window plus the effective stride
    /// (bufferView byteStride for interleaved data, else the tightly-packed element size).</summary>
    private static (ReadOnlyMemory<byte> Data, int Stride) ResolveView(
        GltfRoot root, ReadOnlyMemory<byte> bin, GltfAccessor accessor, int tightElementSize)
    {
        var viewIndex = accessor.BufferView
            ?? throw new NotSupportedException("Accessors without a bufferView (zero-filled) are not supported.");
        var views = root.BufferViews
            ?? throw new InvalidDataException("glTF references a bufferView but declares none.");
        if ((uint)viewIndex >= (uint)views.Length)
            throw new InvalidDataException($"BufferView index {viewIndex} out of range ({views.Length} declared).");
        var view = views[viewIndex];

        var buffers = root.Buffers
            ?? throw new InvalidDataException("glTF references a buffer but declares none.");
        if ((uint)view.Buffer >= (uint)buffers.Length)
            throw new InvalidDataException($"Buffer index {view.Buffer} out of range ({buffers.Length} declared).");
        if (buffers[view.Buffer].Uri is not null)
            throw new NotSupportedException(
                "External buffer URIs are not supported — the contract embeds everything in the GLB BIN chunk.");

        var viewOffset = view.ByteOffset ?? 0;
        if (viewOffset < 0 || view.ByteLength < 0 || viewOffset + view.ByteLength > bin.Length)
            throw new InvalidDataException(
                $"BufferView {viewIndex} [{viewOffset}, {viewOffset + view.ByteLength}) exceeds the BIN chunk ({bin.Length} bytes).");

        var stride = view.ByteStride ?? tightElementSize;
        if (stride < tightElementSize)
            throw new InvalidDataException(
                $"BufferView {viewIndex} stride {stride} is smaller than the element size {tightElementSize}.");

        var accessorOffset = accessor.ByteOffset ?? 0;
        // Last element only occupies tightElementSize, not a full stride.
        var required = accessor.Count == 0
            ? 0
            : accessorOffset + (accessor.Count - 1) * stride + tightElementSize;
        if (accessorOffset < 0 || required > view.ByteLength)
            throw new InvalidDataException(
                $"Accessor range (offset {accessorOffset}, count {accessor.Count}, stride {stride}) " +
                $"exceeds bufferView {viewIndex} ({view.ByteLength} bytes).");

        return (bin.Slice(viewOffset + accessorOffset, required - accessorOffset), stride);
    }

    private static int ExpectedComponents(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new NotSupportedException($"Accessor type '{type}' is not supported for vertex attributes."),
    };

    private static int ElementSize(int componentType) => componentType switch
    {
        ComponentByte or ComponentUByte => 1,
        ComponentShort or ComponentUShort => 2,
        ComponentUInt or ComponentFloat => 4,
        _ => throw new NotSupportedException($"Component type {componentType} is not supported."),
    };
}
