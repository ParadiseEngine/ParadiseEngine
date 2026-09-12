# Behavior trees and blobs

`Paradise.BLOB` provides unmanaged blob builders with no external dependencies. `Paradise.BT`
builds the runtime on it; `Paradise.BT.Sample` in ParadiseSamples demonstrates usage and `*.Test` holds TUnit tests.

1. `[Builder]` generates builders; `LeafNode<T>`, `DecoratorNode<T>` and `CompositeNode<T>` also
   compose a `BTreeNode` graph directly.
2. `BTreeNode.Build()` validates arity (leaf 0, decorator 1; no attribute implies leaf) and
   compiles a shared `BehaviorTreeLayout`: end indices, GUIDs, aligned data offsets (up to 16)
   and defaults. `BehaviorTrees.Compile<TTree>()` returns its typed form. Trees compile from code.
3. Instances use caller-owned state/data buffers: `BehaviorTreeRef` for spans or
   `FixedBehaviorTree<TTree, TStates, TData>` for inline component storage. Pass the blackboard
   to each `Tick`; generated blackboards may be `ref struct`.
4. `VirtualMachine.Tick()` dispatches by node GUID through `NodeTypeRegistry`; the generator
   emits per-assembly registration in a module initializer.
5. Generated `IBlackboardFor<TTree>` bindings make mismatched tree/blackboard types compile errors.

Custom nodes are unmanaged structs implementing `INode`, identified by `[Guid]`, optionally
with `[Builder]`. `Tick<TBehaviorTree, TBlackboard>` permits `ref struct` arguments; `Reset` is
optional. Read node data through `blob.GetNodeData<MyNode>(index)` and shared state through
`bb.GetData<T>()`. `IBlackboard` uses `HasData`/`GetData`/`SetData`, without ref returns, so
read/write intent is statically checkable. `NodeState.None` means never ticked or reset.

BLOB's `BlobArray`, `BlobString`, `BlobPtr` and builders use relative pointers. Access every
`BlobArray`/`BlobString` through a **mutable `ref`**: copying a header, including through a readonly
reference, redirects its relative offset into the stack. In hot loops, take `field.ToSpan()` once.
