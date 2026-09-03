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
/// <para>One instance per renderer, reused every frame: <see cref="Reset"/> clears without
/// releasing, so a steady-state frame allocates nothing.</para></summary>
public sealed class FrameGraph
{
    private const int MaxColorAttachments = RenderPassDesc.MaxColorAttachments;

    private readonly List<Resource> _resources = [];
    private readonly List<Pass> _passes = [];
    private RenderPassDesc[] _descs = [];
    private int[] _order = [];

    public FrameGraph()
    {
        // Index 0 is always the backbuffer, so Reset never has to re-add it and Backbuffer needs
        // no null check at a declaration site.
        _resources.Add(new Resource(ResourceKind.Backbuffer, default, default));
    }

    /// <summary>The frame's presentation target.</summary>
    public static GraphTexture Backbuffer => new(0);

    /// <summary>Drop the previous frame's declarations. Capacity is kept.</summary>
    public void Reset()
    {
        _passes.Clear();
        _resources.RemoveRange(1, _resources.Count - 1);
    }

    /// <summary>Route a color attachment to a texture view the caller owns.</summary>
    public GraphTexture ImportColor(TextureViewHandle view)
    {
        _resources.Add(new Resource(ResourceKind.ImportedColor, view, default));
        return new GraphTexture(_resources.Count - 1);
    }

    /// <summary>Route a depth attachment to a texture the caller owns. <paramref name="view"/>
    /// selects one slice of an array (a shadow-map layer); default takes the texture's own view.</summary>
    public GraphTexture ImportDepth(TextureHandle texture, TextureViewHandle view = default)
    {
        _resources.Add(new Resource(ResourceKind.ImportedDepth, view, texture));
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

    /// <summary>Sort, resolve attachments, and record every pass into <paramref name="writer"/>.
    /// The returned stream borrows the writer's memory, so it is valid until the writer is next
    /// reset.</summary>
    /// <remarks>The concrete <see cref="ArrayBufferWriter{T}"/> rather than
    /// <see cref="IBufferWriter{T}"/>: the stream has to carry back what was recorded, and the
    /// interface has no way to read that. Pretending otherwise would only move the cast to a
    /// runtime failure.</remarks>
    public RenderCommandStream Compile(ArrayBufferWriter<RenderCommand> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var count = _passes.Count;
        if (_order.Length < count) _order = new int[Math.Max(count, 16)];
        if (_descs.Length < count) _descs = new RenderPassDesc[Math.Max(count, 16)];

        var passes = CollectionsMarshal.AsSpan(_passes);
        for (var i = 0; i < count; i++) _order[i] = i;

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
            if (pass.ColorCount == 0 && !pass.HasDepth)
                throw new InvalidOperationException(
                    $"Raster pass '{pass.Name}' declares no attachments; it would render nowhere.");
            if (pass.Recorder is null)
                throw new InvalidOperationException(
                    $"Pass '{pass.Name}' was declared but never given a recorder.");

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

    private readonly record struct Resource(ResourceKind Kind, TextureViewHandle View, TextureHandle Texture);

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
    }

    private ref Pass PassAt(int index) => ref CollectionsMarshal.AsSpan(_passes)[index];

    /// <summary>Fluent handle to the pass just declared. A struct over
    /// <c>(graph, index)</c> — it holds no state of its own, so passing it around copies nothing
    /// that matters.</summary>
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
