namespace Paradise.BT.Builder;

/// <summary>
/// One call from a tree type to its compiled form. <c>TTree.Build()</c> names only the ROOT;
/// these walk the rest — definition, flat <see cref="BehaviorTree"/>, shared layout — so a call
/// site never chains three Builds.
/// </summary>
public static class BehaviorTrees
{
    public static BehaviorTree Compile<TTree>()
        where TTree : IBehaviorTreeBuilder
        => TTree.Build().Build();

    public static BehaviorTree Compile<TTree, TArgs>(TArgs args)
        where TTree : IBehaviorTreeBuilder<TArgs>
        => TTree.Build(args).Build();

    /// <summary>The shared native layout a crowd of instances ticks against. The caller owns it
    /// and disposes it once nothing does.</summary>
    public static BehaviorTreeLayout CompileLayout<TTree>()
        where TTree : IBehaviorTreeBuilder
        => BehaviorTreeLayout.Build(Compile<TTree>());

    /// <inheritdoc cref="CompileLayout{TTree}"/>
    public static BehaviorTreeLayout CompileLayout<TTree, TArgs>(TArgs args)
        where TTree : IBehaviorTreeBuilder<TArgs>
        => BehaviorTreeLayout.Build(Compile<TTree, TArgs>(args));
}
