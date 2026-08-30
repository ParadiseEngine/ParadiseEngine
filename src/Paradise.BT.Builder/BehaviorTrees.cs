namespace Paradise.BT.Builder;

/// <summary>
/// One call from a tree type to its compiled form: <c>TTree.Build()</c> names only the ROOT;
/// these compile it into the shared layout a crowd of instances ticks against. The caller owns
/// the layout and disposes it once nothing does.
/// </summary>
public static class BehaviorTrees
{
    public static BehaviorTreeLayout Compile<TTree>()
        where TTree : IBehaviorTreeBuilder
        => TTree.Build().Build();

    public static BehaviorTreeLayout Compile<TTree, TArgs>(TArgs args)
        where TTree : IBehaviorTreeBuilder<TArgs>
        => TTree.Build(args).Build();
}
