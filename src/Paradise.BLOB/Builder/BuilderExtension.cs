using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Paradise.BLOB;

public static partial class BuilderExtension
{
    public static ManagedBlobAssetReference<T> CreateManagedBlobAssetReference<T>(this IBuilder<T> builder) where T : unmanaged
    {
        return new ManagedBlobAssetReference<T>(builder.CreateBlob());
    }

    public static ManagedBlobAssetReference CreateManagedBlobAssetReference(this IBuilder builder, int alignment = 0)
    {
        return new ManagedBlobAssetReference(builder.CreateBlob(alignment));
    }

    public static byte[] CreateBlob(this IBuilder builder, int alignment = 0)
    {
        using var stream = new BlobMemoryStream();
        builder.Build(stream);
        stream.Length = (int)Utilities.Align(stream.Length, stream.GetAlignment(alignment));
        return stream.ToArray();
    }

    public static byte[] CreateBlob<T>(this IBuilder<T> builder) where T : unmanaged
    {
        using var stream = new BlobMemoryStream();
        builder.Build(stream);
        stream.Length = (int)Utilities.Align<T>(stream.Length);
        return stream.ToArray();
    }

#region SetPointer of StructBuilder
    public static PtrBuilderWithRefBuilder<TValue> SetPointer<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobPtr<TValue> field,
        IBuilder<TValue> refBuilder
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var ptrBuilder = new PtrBuilderWithRefBuilder<TValue>(refBuilder);
        builder.SetBuilder(ref field, ptrBuilder);
        return ptrBuilder;
    }

    public static PtrBuilderWithNewValue<TValue> SetPointer<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobPtr<TValue> field,
        TValue value
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var ptrBuilder = new PtrBuilderWithNewValue<TValue>(value);
        builder.SetBuilder(ref field, ptrBuilder);
        return ptrBuilder;
    }
#endregion

#region SetArray of StructBuilder
    public static ArrayBuilder<TValue> SetArray<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobArray<TValue> field,
        IEnumerable<TValue> items,
        int alignment = 0
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        return builder.SetArray(ref field, items.ToArray(), alignment);
    }

    public static ArrayBuilder<TValue> SetArray<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobArray<TValue> field,
        TValue[] items,
        int alignment = 0
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var arrayBuilder = new ArrayBuilder<TValue>(items) { PatchAlignment = alignment };
        builder.SetBuilder(ref field, arrayBuilder);
        return arrayBuilder;
    }

    public static ArrayBuilderWithItemBuilders<TValue> SetArray<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobArray<TValue> field,
        IEnumerable<IBuilder<TValue>> itemBuilders,
        int alignment = 0
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var arrayBuilder = new ArrayBuilderWithItemBuilders<TValue>(itemBuilders) { PatchAlignment = alignment };
        builder.SetBuilder(ref field, arrayBuilder);
        return arrayBuilder;
    }
#endregion

#region SetValue of StructBuilder
    public static ValueBuilder<TField> SetValue<T, TField>(
        this StructBuilder<T> builder,
        ref TField field,
        TField value
    )
        where T : unmanaged
        where TField : unmanaged
    {
        var valueBuilder = new ValueBuilder<TField>(value);
        builder.SetBuilder(ref field, valueBuilder);
        field = value;
        return valueBuilder;
    }
#endregion

#region SetString of StructBuilder
    public static StringBuilder<TEncoding> SetString<T, TEncoding>(
        this StructBuilder<T> builder,
        ref BlobString<TEncoding> field,
        string value
    )
        where T : unmanaged
        where TEncoding : Encoding, new()
    {
        var stringBuilder = new StringBuilder<TEncoding>(value);
        builder.SetBuilder(ref field, stringBuilder);
        return stringBuilder;
    }
#endregion

#region SetTree of StructBuilder
    public static TreeBuilder<TValue> SetTree<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobTree<TValue> field,
        ITreeNode<TValue> root,
        int alignment = 0
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var treeBuilder = new TreeBuilder<TValue>(root) { Alignment = alignment };
        builder.SetBuilder(ref field, treeBuilder);
        return treeBuilder;
    }
#endregion

#region SetPointerAny of StructBuilder
    public static AnyPtrBuilder<TValue> SetPointerAny<T, TValue>(
        this StructBuilder<T> builder,
        ref BlobPtrAny field,
        TValue value
    )
        where T : unmanaged
        where TValue : unmanaged
    {
        var ptrBuilder = new AnyPtrBuilder<TValue>(value);
        builder.SetBuilder(ref field, ptrBuilder);
        return ptrBuilder;
    }
#endregion

#region SetTreeAny of StructBuilder
    public static AnyTreeBuilder SetTreeAny<T>(
        this StructBuilder<T> builder,
        ref BlobTreeAny field,
        ITreeNode root,
        int alignment = 0
    )
        where T : unmanaged
    {
        var treeBuilder = new AnyTreeBuilder(root) { PatchAlignment = alignment };
        builder.SetBuilder(ref field, treeBuilder);
        return treeBuilder;
    }
#endregion
}
