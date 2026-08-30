namespace Paradise.BT.Builder;

/// <summary>
/// One call from a tree type to its compiled form: <c>TTree.Build()</c> names only the ROOT;
/// these compile it into the shared layout a crowd of instances ticks against. The caller owns
/// the layout and disposes it once nothing does.
/// </summary>
public static class BehaviorTrees
{
    public static BehaviorTreeLayout<TTree> Compile<TTree>()
        where TTree : IBehaviorTreeBuilder
        => new(TTree.Build().Build());

    public static BehaviorTreeLayout<TTree> Compile<TTree, TArgs>(TArgs args)
        where TTree : IBehaviorTreeBuilder<TArgs>
        => new(TTree.Build(args).Build());
}
