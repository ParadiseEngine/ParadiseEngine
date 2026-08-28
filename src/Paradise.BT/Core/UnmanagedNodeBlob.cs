using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// An <see cref="INodeBlob"/> with no managed state whatsoever: a borrowed
/// <see cref="BehaviorTreeLayoutHandle"/> for everything the tree shares, and two caller-owned
/// buffers for everything one instance owns — a <see cref="NodeState"/> per node, and the runtime
/// copy of every node's data.
///
/// <b>Why this exists.</b> <see cref="NodeBlob"/> keeps one boxed <c>RuntimeNode&lt;T&gt;</c> per
/// node and dispatches through <c>IRuntimeNode</c>, which makes a tree instance an object graph.
/// An object graph cannot be a field of a <c>ref struct</c>, cannot live in an ECS component,
/// and cannot be memcpy'd — so a game whose simulation state is components could hold a behavior
/// tree only OFF to the side, ticked by managed code around the schedule, with its timers absent
/// from every snapshot the world takes of itself. Put the same state in two blittable buffers and
/// all three problems go away at once.
///
/// <b>A <c>ref struct</c> over spans, which is what makes the caller's side safe.</b> It took two
/// raw pointers and a paragraph asking the caller to guarantee they stayed valid; it takes
/// <c>Span&lt;&gt;</c> now, so the lifetime is checked by the compiler instead of promised in a
/// comment. C# 13's `allows ref struct` is what permits it: <see cref="INodeBlob"/>'s consumers —
/// <see cref="VirtualMachine"/>, <see cref="NodeExtensions"/>, every node's <c>Tick</c> — declare
/// <c>where TNodeBlob : struct, INodeBlob, allows ref struct</c>, so this type satisfies them
/// while never being boxed.
///
/// Two consequences of being a ref struct, both of which a caller meets immediately: it cannot be
/// stored in a field or an array, and it cannot live across an <c>await</c> or <c>yield</c>
/// (CS4007). Build one where it is used — that is two span constructions — rather than holding it.
///
/// <b>One pointer survives, in <see cref="GetRuntimeDataPtr"/>.</b> <see cref="INodeBlob"/>
/// mandates that member and the dispatch path turns it straight back into a <c>ref byte</c>, so
/// the storage behind the runtime span still has to be non-moveable: ECS chunk memory and
/// <c>NativeMemory</c> qualify, a plain <c>byte[]</c> does not. That is now one documented member
/// rather than the type's whole contract.
///
/// Everything else — the composites, the decorators, <see cref="VirtualMachine"/>,
/// <see cref="NodeExtensions"/> — is generic over the blob type and works with this unchanged.
/// </summary>
public readonly unsafe ref struct UnmanagedNodeBlob : INodeBlob
{
    private readonly LayoutData* _layout;
    private readonly Span<NodeState> _states;
    private readonly Span<byte> _runtime;
    private readonly int _runtimeId;

    /// <summary>
    /// Point at an instance's memory. <paramref name="states"/> must have
    /// <see cref="BehaviorTreeLayoutHandle.NodeCount"/> entries and <paramref name="runtime"/>
    /// <see cref="BehaviorTreeLayoutHandle.RuntimeDataSize"/> bytes.
    ///
    /// This does NOT initialise them — see <see cref="Initialize"/>. Constructing over a buffer
    /// that was never initialised is legal and meaningful: zeroed memory is exactly "no node has
    /// run yet", and a node's data would then read as its zero default rather than its AUTHORED
    /// one, which is why <see cref="Initialize"/> exists and why a world builder must call it.
    /// </summary>
    public UnmanagedNodeBlob(
        BehaviorTreeLayoutHandle layout, Span<NodeState> states, Span<byte> runtime, int runtimeId = 0)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException(
                "Behavior tree layout handle is not valid. Build a BehaviorTreeLayout and publish "
                + "its Handle before constructing an instance over it.", nameof(layout));
        }

        _layout = layout.Data;
        _states = states;
        _runtime = runtime;
        _runtimeId = runtimeId;
    }

    /// <summary>
    /// Put an instance's buffers into their starting state: every node <c>0</c>, every node's
    /// runtime data a copy of the layout's authored default.
    ///
    /// The counterpart of <c>NodeBlob.Create</c>, which does the same thing by constructing one
    /// <c>RuntimeNode</c> per node from its factory's default.
    /// </summary>
    public static void Initialize(
        BehaviorTreeLayoutHandle layout, Span<NodeState> states, Span<byte> runtime)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException("Behavior tree layout handle is not valid.", nameof(layout));
        }

        LayoutData* data = layout.Data;
        states[..data->NodeCount].Clear();
        new ReadOnlySpan<byte>(data->DefaultData, data->RuntimeDataSize).CopyTo(runtime);
    }

    public int RuntimeId => _runtimeId;

    public int Count => _layout->NodeCount;

    public int GetTypeId(int nodeIndex) => _layout->TypeIds[nodeIndex];

    public int GetEndIndex(int nodeIndex) => _layout->EndIndices[nodeIndex];

    /// <summary>How many bytes <paramref name="count"/> nodes occupy from
    /// <paramref name="startNodeIndex"/> — the RESERVED span, so it includes the padding that
    /// keeps each node's data aligned.</summary>
    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        _layout->Offsets[startNodeIndex + count] - _layout->Offsets[startNodeIndex];

    public NodeState GetState(int nodeIndex) => _states[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => _states[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => _states.Slice(index, count).Clear();

    public IntPtr GetDefaultDataPtr(int nodeIndex) =>
        (IntPtr)(_layout->DefaultData + _layout->Offsets[nodeIndex]);

    /// <summary>
    /// <b>The one place a pointer is still taken, and it is taken from the SPAN.</b>
    /// <see cref="INodeBlob"/> mandates this member, and <see cref="VirtualMachine"/>'s dispatch
    /// turns the result straight back into a <c>ref byte</c> that is used inside the same tick.
    /// The address is therefore never held across anything that could move the buffer — but it
    /// is still an address, so the storage behind <c>runtime</c> must not be a moveable managed
    /// array. Native memory and ECS chunk storage, which is what both callers pass, qualify.
    /// </summary>
    public IntPtr GetRuntimeDataPtr(int nodeIndex) =>
        (IntPtr)Unsafe.AsPointer(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _layout->Offsets[nodeIndex]));

    /// <summary>Scope values are not implemented by any blob in this library — the concept came
    /// across with the EntitiesBT contract and has no consumer here yet.</summary>
    public IntPtr GetDefaultScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope values are not implemented by Paradise.BT.");

    /// <inheritdoc cref="GetDefaultScopeValuePtr"/>
    public IntPtr GetRuntimeScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope values are not implemented by Paradise.BT.");

    // NOTE: there is deliberately no dispatch method here. VirtualMachine ticks this blob through
    // GetTypeId + GetRuntimeDataPtr, which are INodeBlob members — so its unmanaged path is
    // generic over ANY blob that stores node data as bytes, rather than hard-wired to this type.
    //
    // INodeDataAccessor is NOT implemented either, and cannot be: reaching an interface member on
    // a ref struct means converting it to that interface, which is a box. NodeBlobExtensions'
    // GetNodeData/GetNodeDefaultData serve this blob through GetRuntimeDataPtr instead.
}
