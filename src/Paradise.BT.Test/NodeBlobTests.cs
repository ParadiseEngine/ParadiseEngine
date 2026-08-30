using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

/// <summary>
/// The layout is now a Paradise.BLOB asset rather than a hand-rolled block, so what needs guarding
/// is the WIRING: that each array landed in the right field, that the offsets still describe the
/// same layout, and that the defaults are still aligned. Everything about how a tree BEHAVES over
/// this blob is covered by <see cref="UnmanagedBlobTests"/>.
///
/// Values are read through a <c>BehaviorTreeLayout.LayoutBlob*</c> and copied out before any assertion: the blob's
/// arrays resolve against their own address, so a copy of the struct would read whatever follows
/// the copy, and a pointer cannot live across an <c>await</c>.
/// </summary>
public sealed class NodeBlobTests
{
    private static BTreeNode SampleTree() =>
        new Selector(
            new Sequence(
                new Success(),
                new Repeat(2, new Success()),
                new Success()),
            new Inverter(new Failure()));

    /// <summary>Read out of the blob in one go, so no ref outlives this call.</summary>
    private static unsafe (int Count, int[] Ends, Type[] Types, int[] Offsets, int DefaultLength, nint DefaultAddress)
        Read(BehaviorTreeLayout layout)
    {
        ref var blob = ref layout.Blob;
        int count = blob.Count;
        var ends = new int[count];
        var types = new Type[count];
        var offsets = new int[count + 1];

        for (int i = 0; i < count; i++)
        {
            ends[i] = blob.EndIndices[i];
            types[i] = NodeTypeRegistry.Invoker(blob.TypeGuid(i)).NodeType;
            offsets[i] = blob.Offsets[i];
        }

        offsets[count] = blob.Offsets[count];

        return (count, ends, types, offsets, blob.DefaultData.Length,
            (nint)blob.DefaultData.UnsafePtr);
    }

    [Test]
    public async Task Blob_Describes_The_Same_Tree()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(SampleTree());
        using BehaviorTreeLayout layout = BTreeNode.Build(SampleTree());
        var blob = Read(layout);

        await Assert.That(blob.Count).IsEqualTo(tree.Blob.Count);

        for (int i = 0; i < tree.Blob.Count; i++)
        {
            await Assert.That(blob.Ends[i]).IsEqualTo(tree.GetEndIndex(i));
            await Assert.That(blob.Types[i]).IsEqualTo(tree.GetNodeType(i));
        }
    }

    /// <summary>Offsets must rise by each node's reserved size and end at exactly the number of
    /// bytes an instance allocates — the two are read from different arrays, so a builder wired to
    /// the wrong field shows up here.</summary>
    [Test]
    public async Task Offsets_Cover_The_Runtime_Data_Exactly()
    {
        using BehaviorTreeLayout layout = BTreeNode.Build(SampleTree());
        var blob = Read(layout);

        await Assert.That(blob.Offsets[0]).IsEqualTo(0);

        for (int i = 0; i < blob.Count; i++)
        {
            int size = blob.Offsets[i + 1] - blob.Offsets[i];
            await Assert.That(size).IsGreaterThan(0);

            // Every node starts on a boundary its own type can be read at — and no wider.
            await Assert.That(blob.Offsets[i] % AlignmentOf(blob.Types[i])).IsEqualTo(0);
        }

        await Assert.That(blob.Offsets[blob.Count]).IsEqualTo(blob.DefaultLength);
        await Assert.That(blob.DefaultLength).IsEqualTo(layout.Blob.DataSize);
    }

    /// <summary>The offsets above are only worth their alignment if the block they index is itself
    /// aligned. Within a blob that is not automatic: it holds because every array before this one
    /// is padded to the same boundary.</summary>
    [Test]
    public async Task Default_Data_Block_Is_Aligned()
    {
        using BehaviorTreeLayout layout = BTreeNode.Build(SampleTree());
        var blob = Read(layout);

        await Assert.That(blob.DefaultAddress % 16).IsEqualTo((nint)0);
    }

    /// <summary>The alignment the layout is expected to give a node: its natural one. Reflection
    /// is fine here — the test project is not AOT.</summary>
    private static int AlignmentOf(Type nodeType) =>
        (int)typeof(NodeBlobTests)
            .GetMethod(nameof(AlignmentOfGeneric))!
            .MakeGenericMethod(nodeType)
            .Invoke(null, null)!;

    public static int AlignmentOfGeneric<T>() where T : unmanaged =>
        System.Runtime.CompilerServices.Unsafe.SizeOf<AlignmentProbe<T>>()
        - System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    private struct AlignmentProbe<T> where T : unmanaged
    {
        public byte Padding;
        public T Value;

        public AlignmentProbe(byte padding, T value)
        {
            Padding = padding;
            Value = value;
        }
    }

}
