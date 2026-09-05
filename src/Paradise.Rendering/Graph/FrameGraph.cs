using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Graph;

/// <summary>Records one pass's commands during <see cref="FrameGraph.Compile"/>.
///
/// <para>The signature carries state instead of capturing it: <paramref name="context"/> is the
/// object that declared the pass and <paramref name="argument"/> the integer it supplied — a shadow
/// layer, a bloom mip. That keeps every declaration site a cached static method group rather than a
/// closure allocated per pass per frame, which matters in a path that runs sixty times a
/// second.</para></summary>
public delegate void PassRecorder(object context, ref RenderCommandEncoder encoder, int argument);

/// <summary>Who can observe a resource, which is what decides whether writing it is worth
/// doing.</summary>
public enum GraphResourceScope
{
    /// <summary>Something outside the graph reads this — it is presented, sampled by a host, or
    /// handed to game code. Writing it is observable, so a pass that writes it is never culled.
    /// The default, because it is the assumption that cannot silently lose work.</summary>
    External,

    /// <summary>Only passes in this graph consume it. A write nobody declares a
    /// <see cref="FrameGraph.PassBuilder.Reads"/> for is dead, and the pass producing it is culled.
    ///
    /// <para>This is information the declaring code has and the graph cannot derive: the read
    /// usually lives inside a bind group built once at resize, where nothing in the frame's
    /// declaration mentions it. Marking a resource graph-only is a promise that every consumer of
    /// it declares that read.</para></summary>
    GraphOnly,
}

/// <summary>A texture the graph can route a pass's attachment to. Opaque by design: it is an index
/// into the graph's resource table, not a GPU handle, so the same declaration can later resolve to
/// a pooled transient without the declaring code changing.</summary>
public readonly record struct GraphTexture(int Index)
{
    internal bool IsValid => Index >= 0;
    public static readonly GraphTexture Invalid = new(-1);
}

/// <summary>Builds one frame's pass list, then lowers it to a <see cref="RenderCommandStream"/>.
///
/// <para>The graph replaces hand-computed pass indices. Declaring a pass at a
/// <see cref="RenderPassEvent"/> says where it belongs; nothing is expressed relative to how many
/// passes precede it, so adding or removing one cannot silently shift another. Passes are sorted
/// before anything is recorded, which is what makes the index available at record time — and is
/// also the shape that lets recording move onto worker threads later, since each pass's commands
/// occupy their own contiguous run.</para>
///
/// <para>Between sorting and recording the graph culls: a pass whose every output is
/// <see cref="GraphResourceScope.GraphOnly"/> and read by nobody does not run. That moves the
/// decision to switch a feature off from the producer — which had to know how many passes to skip —
/// to the consumer, which only has to stop asking for the result.</para>
///
/// <para>One instance per renderer, reused every frame: <see cref="Reset"/> clears without
/// releasing, so a steady-state frame allocates nothing.</para></summary>
public sealed class FrameGraph
{
    private const int MaxColorAttachments = RenderPassDesc.MaxColorAttachments;

    private readonly List<Resource> _resources = [];
    private readonly List<Pass> _passes = [];
    private readonly List<ReadEdge> _reads = [];
    private readonly Stack<int> _liveStack = new();
    private RenderPassDesc[] _descs = [];
    private int[] _order = [];

    public FrameGraph()
    {
        // Index 0 is always the backbuffer, so Reset never has to re-add it and Backbuffer needs
        // no null check at a declaration site.
        _resources.Add(new Resource(ResourceKind.Backbuffer, GraphResourceScope.External, default, default));
    }

    /// <summary>The frame's presentation target. Always <see cref="GraphResourceScope.External"/>:
    /// the whole point of the frame is that somebody sees it.</summary>
    public static GraphTexture Backbuffer => new(0);

    /// <summary>How many passes the last <see cref="Compile"/> dropped as unreachable.</summary>
    public int CulledPassCount { get; private set; }

    /// <summary>Drop the previous frame's declarations. Capacity is kept.</summary>
    public void Reset()
    {
        _passes.Clear();
        _reads.Clear();
        _resources.RemoveRange(1, _resources.Count - 1);
    }

    /// <summary>Route a color attachment to a texture view the caller owns.</summary>
    public GraphTexture ImportColor(TextureViewHandle view, GraphResourceScope scope = GraphResourceScope.External)
    {
        _resources.Add(new Resource(ResourceKind.ImportedColor, scope, view, default));
        return new GraphTexture(_resources.Count - 1);
    }

    /// <summary>Route a depth attachment to a texture the caller owns. <paramref name="view"/>
    /// selects one slice of an array (a shadow-map layer); default takes the texture's own view.</summary>
    public GraphTexture ImportDepth(TextureHandle texture, TextureViewHandle view = default,
        GraphResourceScope scope = GraphResourceScope.External)
    {
        _resources.Add(new Resource(ResourceKind.ImportedDepth, scope, view, texture));
        return new GraphTexture(_resources.Count - 1);
    }

    /// <summary>Declare a raster pass. <paramref name="offset"/> shifts it within the event's gap —
    /// <c>Opaque + 1</c> runs immediately after the built-in opaque pass and still before anything
    /// at <see cref="RenderPassEvent.AfterOpaque"/>.</summary>
    public PassBuilder AddRasterPass(string name, RenderPassEvent when, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        _passes.Add(new Pass
        {
            Name = name,
            SortKey = (int)when + offset,
        });
        return new PassBuilder(this, _passes.Count - 1);
    }

    /// <summary>Sort, cull, resolve attachments, and record every surviving pass into
    /// <paramref name="writer"/>. The returned stream borrows the writer's memory, so it is valid
    /// until the writer is next reset.</summary>
    /// <remarks>The concrete <see cref="ArrayBufferWriter{T}"/> rather than
    /// <see cref="IBufferWriter{T}"/>: the stream has to carry back what was recorded, and the
    /// interface has no way to read that. Pretending otherwise would only move the cast to a
    /// runtime failure.</remarks>
    public RenderCommandStream Compile(ArrayBufferWriter<RenderCommand> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var declared = _passes.Count;
        if (_order.Length < declared) _order = new int[Math.Max(declared, 16)];
        if (_descs.Length < declared) _descs = new RenderPassDesc[Math.Max(declared, 16)];

        var passes = CollectionsMarshal.AsSpan(_passes);
        Validate(passes, declared);
        MarkLive(passes, declared);

        var count = 0;
        for (var i = 0; i < declared; i++)
            if (passes[i].Live)
                _order[count++] = i;
        CulledPassCount = declared - count;

        // Insertion sort over the index array: stable (so declaration order breaks ties, which is
        // what makes "two features at the same event keep their registration order" true), and
        // allocation-free where Array.Sort with a comparer is not. Pass counts are tens, not
        // thousands — today's worst case is eighteen.
        for (var i = 1; i < count; i++)
        {
            var current = _order[i];
            var key = passes[current].SortKey;
            var j = i - 1;
            while (j >= 0 && passes[_order[j]].SortKey > key)
            {
                _order[j + 1] = _order[j];
                j--;
            }
            _order[j + 1] = current;
        }

        for (var slot = 0; slot < count; slot++)
        {
            ref var pass = ref passes[_order[slot]];
            ref var desc = ref _descs[slot];
            desc = new RenderPassDesc(pass.ColorCount, ResolveDepth(in pass));
            for (var c = 0; c < pass.ColorCount; c++)
                desc[c] = ResolveColor(pass.Colors[c]);
        }

        var encoder = new RenderCommandEncoder(writer);
        for (var slot = 0; slot < count; slot++)
        {
            ref var pass = ref passes[_order[slot]];
            encoder.BeginPass(slot);
            pass.Recorder!.Invoke(pass.Context!, ref encoder, pass.Argument);
            encoder.EndPass();
        }

        return new RenderCommandStream(writer.WrittenMemory, _descs.AsMemory(0, count));
    }

    private static void Validate(Span<Pass> passes, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ref var pass = ref passes[i];
            if (pass.ColorCount == 0 && !pass.HasDepth)
                throw new InvalidOperationException(
                    $"Raster pass '{pass.Name}' declares no attachments; it would render nowhere.");
            if (pass.Recorder is null)
                throw new InvalidOperationException(
                    $"Pass '{pass.Name}' was declared but never given a recorder.");
        }
    }

    /// <summary>Reachability: start from the passes whose output somebody outside the graph can
    /// see, then walk backwards through declared reads to whatever produced what they consume.
    ///
    /// <para>Writing an <see cref="GraphResourceScope.External"/> resource roots a pass because the
    /// graph cannot know who else reads it — the same rule Filament states as "calling write() on
    /// an imported resource automatically adds a side-effect". A renderer that imports every target
    /// therefore culls nothing, which is correct rather than useless: culling only removes work
    /// once the declaring code has said which resources are its own.</para></summary>
    private void MarkLive(Span<Pass> passes, int count)
    {
        for (var i = 0; i < count; i++) passes[i].Live = false;

        _liveStack.Clear();
        for (var i = 0; i < count; i++)
            if (passes[i].NeverCull || WritesObservable(in passes[i]))
                _liveStack.Push(i);

        while (_liveStack.Count > 0)
        {
            var index = _liveStack.Pop();
            if (passes[index].Live) continue;
            passes[index].Live = true;

            foreach (var edge in _reads)
            {
                if (edge.Pass != index) continue;
                for (var producer = 0; producer < count; producer++)
                    if (!passes[producer].Live && Writes(in passes[producer], edge.Resource))
                        _liveStack.Push(producer);
            }
        }
    }

    private bool WritesObservable(in Pass pass)
    {
        for (var c = 0; c < pass.ColorCount; c++)
            if (_resources[pass.Colors[c].Target.Index].Scope == GraphResourceScope.External)
                return true;
        return pass.HasDepth && _resources[pass.Depth.Target.Index].Scope == GraphResourceScope.External;
    }

    private static bool Writes(in Pass pass, int resourceIndex)
    {
        for (var c = 0; c < pass.ColorCount; c++)
            if (pass.Colors[c].Target.Index == resourceIndex)
                return true;
        return pass.HasDepth && pass.Depth.Target.Index == resourceIndex;
    }

    private ColorAttachmentDesc ResolveColor(in Attachment a)
    {
        var resource = _resources[a.Target.Index];
        return resource.Kind == ResourceKind.Backbuffer
            ? new ColorAttachmentDesc(RenderViewHandle.Invalid, a.Load, a.Store, a.Clear)
            : new ColorAttachmentDesc(RenderViewHandle.Invalid, a.Load, a.Store, a.Clear, resource.View);
    }

    private DepthAttachmentDesc? ResolveDepth(in Pass pass)
    {
        if (!pass.HasDepth) return null;
        var a = pass.Depth;
        var resource = _resources[a.Target.Index];
        return new DepthAttachmentDesc(resource.Texture, a.Load, a.Store, a.ClearDepth, resource.View);
    }

    private enum ResourceKind : byte { Backbuffer, ImportedColor, ImportedDepth }

    private readonly record struct Resource(
        ResourceKind Kind, GraphResourceScope Scope, TextureViewHandle View, TextureHandle Texture);

    private readonly record struct ReadEdge(int Pass, int Resource);

    private struct Attachment
    {
        public GraphTexture Target;
        public LoadOp Load;
        public StoreOp Store;
        public ColorRgba Clear;
        public float ClearDepth;
    }

    [InlineArray(MaxColorAttachments)]
    private struct ColorSlots
    {
        private Attachment _slot0;
    }

    private struct Pass
    {
        public string Name;
        public int SortKey;
        public object? Context;
        public PassRecorder? Recorder;
        public int Argument;
        public int ColorCount;
        public ColorSlots Colors;
        public bool HasDepth;
        public Attachment Depth;
        public bool NeverCull;
        public bool Live;
    }

    private ref Pass PassAt(int index) => ref CollectionsMarshal.AsSpan(_passes)[index];

    /// <summary>Fluent handle to the pass just declared. A struct over <c>(graph, index)</c> — it
    /// holds no state of its own, so passing it around copies nothing that matters.</summary>
    public readonly struct PassBuilder
    {
        private readonly FrameGraph _graph;
        private readonly int _index;

        internal PassBuilder(FrameGraph graph, int index)
        {
            _graph = graph;
            _index = index;
        }

        /// <summary>Bind a color attachment. Slots must be filled from 0 upward.</summary>
        public PassBuilder Color(int slot, GraphTexture target, LoadOp load, StoreOp store, ColorRgba clear = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(slot);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, MaxColorAttachments);
            if (!target.IsValid) throw new ArgumentException("Attachment target is not a graph resource.", nameof(target));

            ref var pass = ref _graph.PassAt(_index);
            if (slot > pass.ColorCount)
                throw new ArgumentOutOfRangeException(nameof(slot),
                    $"Color slot {slot} skips slot {pass.ColorCount}; attachments must be bound in order.");
            pass.Colors[slot] = new Attachment { Target = target, Load = load, Store = store, Clear = clear };
            if (slot == pass.ColorCount) pass.ColorCount++;
            return this;
        }

        /// <summary>Bind the depth attachment.</summary>
        public PassBuilder Depth(GraphTexture target, LoadOp load, StoreOp store, float clear = 1f)
        {
            if (!target.IsValid) throw new ArgumentException("Attachment target is not a graph resource.", nameof(target));

            ref var pass = ref _graph.PassAt(_index);
            pass.HasDepth = true;
            pass.Depth = new Attachment { Target = target, Load = load, Store = store, ClearDepth = clear };
            return this;
        }

        /// <summary>Declare that this pass samples <paramref name="source"/>.
        ///
        /// <para>Almost always the read physically happens through a bind group the graph never
        /// sees, so this is the only way the dependency exists at all. Leaving it out on a
        /// <see cref="GraphResourceScope.GraphOnly"/> resource does not produce a slower frame — it
        /// produces a culled producer and a pass sampling stale contents.</para></summary>
        public PassBuilder Reads(GraphTexture source)
        {
            if (!source.IsValid) throw new ArgumentException("Read source is not a graph resource.", nameof(source));
            _graph._reads.Add(new ReadEdge(_index, source.Index));
            return this;
        }

        /// <summary>Keep this pass even when nothing reads its output — for work whose effect the
        /// graph cannot see, such as a readback or a side effect on a host-owned resource.</summary>
        public PassBuilder NeverCull()
        {
            _graph.PassAt(_index).NeverCull = true;
            return this;
        }

        /// <summary>Supply the callback that records this pass's commands. See
        /// <see cref="PassRecorder"/> for why the state travels as parameters.</summary>
        public PassBuilder Record(object context, PassRecorder recorder, int argument = 0)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(recorder);

            ref var pass = ref _graph.PassAt(_index);
            pass.Context = context;
            pass.Recorder = recorder;
            pass.Argument = argument;
            return this;
        }
    }
}
