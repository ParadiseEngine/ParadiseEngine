using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Maps a node type's <c>[Guid]</c> — its whole identity — to the invoker that ticks it from a
/// <c>ref byte</c>. A process-wide vtable: one entry per node TYPE, append-only, idempotent.
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<Guid, INodeInvoker> s_invokers = new();

    public static void Register<TNodeData>() where TNodeData : unmanaged, INode
    {
        Guid guid = typeof(TNodeData).GetNodeGuid();
        if (!s_invokers.TryAdd(guid, NodeInvoker<TNodeData>.Instance))
        {
            var existing = s_invokers[guid];
            if (existing.NodeType != typeof(TNodeData))
            {
                throw new InvalidOperationException(
                    $"Node GUID '{guid}' is already registered for "
                    + $"'{existing.NodeType.FullName}' and cannot also name "
                    + $"'{typeof(TNodeData).FullName}'.");
            }
        }
    }

    public static bool IsRegistered(Guid guid) => s_invokers.ContainsKey(guid);

    internal static INodeInvoker Invoker(Guid guid)
    {
        if (!s_invokers.TryGetValue(guid, out INodeInvoker? invoker))
        {
            throw new InvalidOperationException(
                $"Node GUID '{guid}' is not registered. Every node type in a tree must be passed "
                + $"to {nameof(NodeTypeRegistry)}.{nameof(Register)}<T>() before a "
                + $"{nameof(BehaviorTreeLayout)} is built or loaded from it.");
        }

        return invoker;
    }
}

internal interface INodeInvoker
{
    Type NodeType { get; }

    int Size { get; }

    NodeState Tick<TBehaviorTree, TBlackboard>(
        scoped ref byte data, int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    void Reset<TBehaviorTree, TBlackboard>(
        scoped ref byte data, int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;
}

internal sealed class NodeInvoker<TNodeData> : INodeInvoker
    where TNodeData : unmanaged, INode
{
    public static readonly NodeInvoker<TNodeData> Instance = new();

    public Type NodeType => typeof(TNodeData);

    public int Size => Unsafe.SizeOf<TNodeData>();

    public NodeState Tick<TBehaviorTree, TBlackboard>(
        scoped ref byte data, int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => Unsafe.As<byte, TNodeData>(ref data).Tick(index, blob, bb);

    public void Reset<TBehaviorTree, TBlackboard>(
        scoped ref byte data, int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TNodeData.Reset(index, blob, bb);
}
