using System;
using System.Collections.Generic;

namespace Paradise.BLOB;

public static partial class BlobStreamExtension
{
    public static IBlobStream EnsureDataSize(this IBlobStream stream, int size, int alignment = 0)
    {
        var expectedPatchPosition = (int)Utilities.Align(stream.Position + size, stream.GetAlignment(alignment));
        stream.PatchPosition = Math.Max(stream.PatchPosition, expectedPatchPosition);
        // expand stream buffer by patch position
        if (stream.Length < stream.PatchPosition) stream.Length = stream.PatchPosition;
        return stream;
    }

    public static unsafe IBlobStream EnsureDataSize<T>(this IBlobStream stream) where T : unmanaged
    {
        return stream.EnsureDataSize(sizeof(T));
    }

    public static IBlobStream ExpandPatch(this IBlobStream stream, int size, int alignment = 0)
    {
        stream.PatchPosition = (int)Utilities.Align(stream.PatchPosition + size, stream.GetAlignment(alignment));
        return stream;
    }

    public static IBlobStream AlignPatch(this IBlobStream stream, int alignment = 0)
    {
        stream.PatchPosition = (int)Utilities.Align(stream.PatchPosition, stream.GetAlignment(alignment));
        return stream;
    }

    public static unsafe IBlobStream WriteValue<T>(this IBlobStream stream, T value, int alignment = 0) where T : unmanaged
    {
        var valuePtr = &value;
        var size = sizeof(T);
        stream.Write((byte*)valuePtr, size, alignment);
        return stream;
    }

    public static IBlobStream WriteValue(this IBlobStream stream, IBuilder builder)
    {
        builder.Build(stream);
        return stream;
    }

    public static IBlobStream WriteArray<T>(this IBlobStream stream, T[] array, int alignment = 0) where T : unmanaged
    {
        return stream.WriteArrayMeta(array.Length).ToPatchPosition().WriteArrayData(array, alignment);
    }

    public static unsafe IBlobStream WriteArray<T>(
        this IBlobStream stream,
        IReadOnlyList<IBuilder<T>> itemBuilders
    ) where T : unmanaged
    {
        return stream.WriteArray(itemBuilders, sizeof(T), stream.Alignment);
    }

    public static IBlobStream WriteArray(
        this IBlobStream stream,
        IReadOnlyList<IBuilder> itemBuilders,
        int itemSize,
        int alignment = 0
    )
    {
        return stream.WriteArrayMeta(itemBuilders.Count).ToPatchPosition().WriteArrayData(itemBuilders, itemSize, alignment);
    }

    public static IBlobStream WriteArrayMeta(this IBlobStream stream, int length)
    {
        return stream.WritePatchOffset().WriteValue(length);
    }

    public static IBlobStream WriteArrayMeta(this IBlobStream stream, int length, int patchOffset)
    {
        return stream.WriteValue(patchOffset).WriteValue(length);
    }

    public static unsafe IBlobStream WriteArrayData<T>(this IBlobStream stream, T[] array, int alignment = 0) where T : unmanaged
    {
        if (array.Length == 0) return stream;
        var valueSize = sizeof(T);
        var arraySize = valueSize * array.Length;
        fixed (void* arrayPtr = &array[0]) stream.Write((byte*) arrayPtr, arraySize, alignment);
        return stream;
    }

    public static unsafe IBlobStream WriteArrayData<T>(
        this IBlobStream stream,
        IReadOnlyList<IBuilder<T>> itemBuilders
    ) where T : unmanaged
    {
        return stream.WriteArrayData(itemBuilders, sizeof(T), stream.Alignment);
    }

    public static IBlobStream WriteArrayData(
        this IBlobStream stream,
        IReadOnlyList<IBuilder> itemBuilders,
        int itemSize,
        int alignment = 0
    )
    {
        var patchPosition = stream.PatchPosition;
        stream.ExpandPatch(itemSize * itemBuilders.Count, alignment);
        for (var i = 0; i < itemBuilders.Count; i++)
        {
            stream.Position = patchPosition + itemSize * i;
            itemBuilders[i].Build(stream);
        }
        return stream;
    }

    public static IBlobStream WriteOffset(this IBlobStream stream, int position)
    {
        var offset = stream.Offset(position);
        stream.WriteValue(offset);
        return stream;
    }

    public static IBlobStream WritePatchOffset(this IBlobStream stream)
    {
        return stream.WriteOffset(stream.PatchPosition);
    }

    public static IBlobStream ToPatchPosition(this IBlobStream stream)
    {
        return stream.ToPosition(stream.PatchPosition);
    }

    public static IBlobStream ToPosition(this IBlobStream stream, int position)
    {
        stream.Position = position;
        return stream;
    }

    public static int PatchOffset(this IBlobStream stream)
    {
        return stream.Offset(stream.PatchPosition);
    }
    
    public static int Offset(this IBlobStream stream, int position)
    {
        return position - stream.Position;
    }

    public static ref T As<T>(this IBlobStream stream, int position) where T : unmanaged
    {
        return ref new UnsafeBlobStreamValue<T>(stream, position).Value;
    }
    
    public static ref T DataAs<T>(this IBlobStream stream) where T : unmanaged
    {
        return ref As<T>(stream, stream.Position);
    }
    
    public static ref T PatchAs<T>(this IBlobStream stream) where T : unmanaged
    {
        return ref As<T>(stream, stream.PatchPosition);
    }
    
    public static ref T EnsureDataAs<T>(this IBlobStream stream) where T : unmanaged
    {
        return ref stream.EnsureDataAs<T>(stream.Alignment);
    }
    
    public static unsafe ref T EnsureDataAs<T>(this IBlobStream stream, int alignment) where T : unmanaged
    {
        return ref stream.EnsureDataSize(sizeof(T), alignment).DataAs<T>();
    }
}
