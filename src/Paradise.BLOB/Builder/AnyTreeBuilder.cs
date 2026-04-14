using System.Collections.Generic;

namespace Paradise.BLOB;

public interface ITreeNode
{
    IBuilder ValueBuilder { get; }
    IReadOnlyList<ITreeNode> Children { get; }
}

public class AnyTreeBuilder : Builder<BlobTreeAny>
{
    public ITreeNode? Root { get; set; }

    public AnyArrayBuilder ArrayBuilder { get; } = new AnyArrayBuilder();

    public AnyTreeBuilder() {}
    public AnyTreeBuilder(ITreeNode root) => Root = root;

    protected override void BuildImpl(IBlobStream stream, ref BlobTreeAny data)
    {
        var (endIndices, valueBuilders) = Root is null
            ? (new List<int>(), new List<IBuilder>())
            : Flatten(Root);

        foreach (var valueBuilder in valueBuilders) ArrayBuilder.Add(valueBuilder);

        var builder = new StructBuilder<BlobTreeAny> { DataAlignment = DataAlignment, PatchAlignment = PatchAlignment };
        builder.SetArray(ref builder.Value.EndIndices, endIndices);
        builder.SetBuilder(ref builder.Value.Data, ArrayBuilder);
        builder.Build(stream);
    }

    private (List<int> endIndices, List<IBuilder> builders) Flatten(ITreeNode root)
    {
        var endIndices = new List<int>();
        var values = new List<IBuilder>();
        FlattenAndReturnEndIndex(root, 0);
        return (endIndices, values);

        int /*endIndex*/ FlattenAndReturnEndIndex(ITreeNode node, int index)
        {
            var valueIndex = values.Count;
            values.Add(node.ValueBuilder);
            endIndices.Add(-1);
            var endIndex = index + 1;
            foreach (var child in node.Children) endIndex = FlattenAndReturnEndIndex(child, endIndex);
            endIndices[valueIndex] = endIndex;
            return endIndex;
        }
    }
}
