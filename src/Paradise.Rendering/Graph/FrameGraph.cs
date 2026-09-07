using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paradise.Rendering.Graph;

/// <summary>Records one pass's commands during <see cref="FrameGraph.Compile"/>.
///
/// <para>The signature carries state instead of capturing it: <paramref name="context"/> is the
/// object that declared the pass and <paramref name="argument"/> the integer it supplied — a shadow
/// layer, a bloom mip. That keeps every declaration site a cached static method group rather than a
/// closure allocated per pass per frame, which matters in a path that runs sixty times a
/// second.</para></summary>
public delegate void PassRecorder(object context, ref PassRecording pass, int argument);

/// <summary>The typed form of <see cref="PassRecorder"/>: the feature that declared the pass
/// arrives as itself, so a recorder reads its own fields instead of opening with a cast. The
/// integer stays for the one thing it is genuinely the argument of — a shadow layer, a bloom mip.</summary>
public delegate void PassRecorder<TFeature>(TFeature feature, ref PassRecording pass, int argument)
    where TFeature : class;

/// <summary>Who can observe a resource, which is what decides whether writing it is worth
/// doing.</summary>
public enum GraphResourceScope
{
    /// <summary>Something outside the graph reads this — it is presented, sampled by a host, or
    /// handed to game code. Writing it is observable, so a pass that writes it is never culled.
    /// The default, because it is the assumption that cannot silently lose work.</summary>
    External,

    /// <summary>Only passes in this graph consume it. A write nobody declares a
    /// <see cref="FrameGraph.PassBuilder.Reads(GraphTexture)"/> for is dead, and the pass producing it is culled.
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

/// <summary>A buffer the graph tracks reads and writes of, so a compute pass that fills it and
/// the pass that consumes it are ordered and culled by the same rules as textures. The graph
/// never allocates it; a feature imports the buffer it owns.</summary>
public readonly record struct GraphBuffer(int Index)
{
    internal bool IsValid => Index >= 0;
    public static readonly GraphBuffer Invalid = new(-1);
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
/// <para>An attachment declared without a store op gets one inferred: stored if anything after
/// the pass reads the resource or the resource is visible outside the graph, discarded otherwise.
/// On a tile GPU a discarded attachment is a tile flush that never happens, and it is the one
/// decision the declaring code is worst placed to make, since it depends on every pass that
/// follows.</para>
///
/// <para>One instance per renderer, reused every frame: <see cref="Reset"/> clears without
/// releasing, so a steady-state frame allocates nothing.</para></summary>
public sealed partial class FrameGraph
{
    private const int MaxColorAttachments = RenderPassDesc.MaxColorAttachments;
    private const int MaxBindGroups = 4;

    private readonly List<Resource> _resources = [];
    private readonly Dictionary<string, int> _owned = new(StringComparer.Ordinal);
    private readonly List<Pass> _passes = [];
    private readonly List<ReadEdge> _reads = [];
    private readonly List<WriteEdge> _writes = [];
    private readonly List<GraphBinding> _bindings = [];
    private readonly Stack<int> _liveStack = new();
    private readonly ILogger _log;
    private RenderPassDesc[] _descs = [];
    private int[] _order = [];

    /// <param name="textures">The targets this graph owns. Null for a graph that only routes
    /// imported resources.</param>
    /// <param name="bindGroups">Where a pass's declared bind groups are resolved. Null for a
    /// graph whose passes bind nothing through it.</param>
    /// <param name="logger">Where compile-time findings go. Null discards them.</param>
    public FrameGraph(GraphTextureRegistry? textures = null, BindGroupCache? bindGroups = null, ILogger? logger = null)
    {
        Textures = textures;
        BindGroups = bindGroups;
        _log = logger ?? NullLogger.Instance;
        // Index 0 is always the backbuffer, so Reset never has to re-add it and Backbuffer needs
        // no null check at a declaration site.
        _resources.Add(new Resource(ResourceKind.Backbuffer, GraphResourceScope.External, default, default, null, default));
    }

    /// <summary>The targets this graph owns, or null.</summary>
    public GraphTextureRegistry? Textures { get; }

    /// <summary>The cache declared bind groups resolve through, or null.</summary>
    public BindGroupCache? BindGroups { get; }

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
        _writes.Clear();
        _bindings.Clear();
        _owned.Clear();
        _resources.RemoveRange(1, _resources.Count - 1);
    }

    /// <summary>Route a color attachment to a texture view the caller owns.</summary>
    public GraphTexture ImportColor(TextureViewHandle view, GraphResourceScope scope = GraphResourceScope.External)
    {
        _resources.Add(new Resource(ResourceKind.ImportedColor, scope, view, default, null, default));
        return new GraphTexture(_resources.Count - 1);
    }

    /// <summary>Route a depth attachment to a texture the caller owns. <paramref name="view"/>
    /// selects one slice of an array (a shadow-map layer); default takes the texture's own view.</summary>
    public GraphTexture ImportDepth(TextureHandle texture, TextureViewHandle view = default,
        GraphResourceScope scope = GraphResourceScope.External)
    {
        _resources.Add(new Resource(ResourceKind.ImportedDepth, scope, view, texture, null, default));
        return new GraphTexture(_resources.Count - 1);
    }

    /// <summary>Track a buffer the caller owns. A compute pass that binds it for writing becomes
    /// its producer; a pass that binds it for reading depends on that producer, and a
    /// <see cref="GraphResourceScope.GraphOnly"/> buffer nobody reads culls whoever fills it —
    /// which is how a ray-trace pass whose only output is a hit buffer is switched off from the
    /// consumer's side, the way a texture producer already is.</summary>
    public GraphBuffer ImportBuffer(BufferHandle buffer, GraphResourceScope scope = GraphResourceScope.External)
    {
        if (!buffer.IsValid) throw new ArgumentException("Buffer handle is invalid.", nameof(buffer));
        _resources.Add(new Resource(ResourceKind.ImportedBuffer, scope, default, default, null, buffer));
        return new GraphBuffer(_resources.Count - 1);
    }

    /// <summary>A target the graph owns, by its <see cref="GraphTextureRegistry"/> name. One
    /// resource per name per frame, however many passes ask — which is what lets a read of the
    /// whole shadow array find the passes that each wrote one layer of it. Its scope is derived:
    /// private unless the registry exported it.</summary>
    public GraphTexture Texture(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_owned.TryGetValue(name, out var index)) return new GraphTexture(index);

        var textures = Textures ?? throw new InvalidOperationException(
            "This graph owns no textures; import the resource instead.");
        var scope = textures.IsExported(name) ? GraphResourceScope.External : GraphResourceScope.GraphOnly;
        _resources.Add(new Resource(ResourceKind.Owned, scope, textures.View(name), textures.Texture(name), name, default));
        index = _resources.Count - 1;
        _owned.Add(name, index);
        return new GraphTexture(index);
    }

    /// <summary>Declare a raster pass. <paramref name="offset"/> shifts it within the event's gap —
    /// <c>Opaque + 1</c> runs immediately after the built-in opaque pass and still before anything
    /// at <see cref="RenderPassEvent.AfterOpaque"/>.</summary>
    public PassBuilder AddRasterPass(string name, RenderPassEvent when, int offset = 0) =>
        AddPass(name, when, offset, PassKind.Raster);

    /// <summary>Declare a compute pass. It has no attachments, so what it produces is declared
    /// through its bind groups — a <see cref="GraphBinding.StorageTexture"/> or a
    /// <see cref="GraphBinding.Buffer(uint, GraphBuffer, ulong, ulong, bool)"/> bound for writing
    /// — or with <see cref="PassBuilder.Writes(GraphTexture)"/> when the write goes through a
    /// group the graph does not build. It is sorted, culled and ordered exactly like a raster
    /// pass; only the lowering differs, and it takes no slot in the pass table.</summary>
    public PassBuilder AddComputePass(string name, RenderPassEvent when, int offset = 0) =>
        AddPass(name, when, offset, PassKind.Compute);

    private PassBuilder AddPass(string name, RenderPassEvent when, int offset, PassKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        _passes.Add(new Pass
        {
            Name = name,
            Kind = kind,
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
        CheckReadsFollowWrites(passes, count);

        // Only raster passes occupy the pass table: a compute pass has no attachments, so the
        // table index a BeginPass carries counts raster passes in sorted order and skips compute.
        var rasterCount = 0;
        for (var slot = 0; slot < count; slot++)
        {
            ref var pass = ref passes[_order[slot]];
            if (pass.Kind != PassKind.Raster) continue;
            ref var desc = ref _descs[rasterCount++];
            desc = new RenderPassDesc(pass.ColorCount, ResolveDepth(passes, slot, count));
            for (var c = 0; c < pass.ColorCount; c++)
                desc[c] = ResolveColor(passes, slot, count, c);
        }

        // Bind groups resolve only for passes that survived culling: a culled feature's groups
        // are never requested, so the cache lets them go instead of holding views nobody samples.
        for (var slot = 0; slot < count; slot++)
            ResolveBindGroups(ref passes[_order[slot]]);

        var encoder = new RenderCommandEncoder(writer);
        var rasterSlot = 0;
        for (var slot = 0; slot < count; slot++)
        {
            ref var pass = ref passes[_order[slot]];
            var recording = new PassRecording(encoder, pass.Groups, pass.Name);
            if (pass.Kind == PassKind.Compute)
            {
                encoder.BeginComputePass();
                pass.Invoke!(pass.Recorder!, pass.Context!, ref recording, pass.Argument);
                encoder.EndComputePass();
            }
            else
            {
                encoder.BeginPass(rasterSlot++);
                pass.Invoke!(pass.Recorder!, pass.Context!, ref recording, pass.Argument);
                encoder.EndPass();
            }
        }

        return new RenderCommandStream(writer.WrittenMemory, _descs.AsMemory(0, rasterCount));
    }

    private void ResolveBindGroups(ref Pass pass)
    {
        Span<BindGroupEntryDesc> entries = stackalloc BindGroupEntryDesc[BindGroupCache.MaxEntries];
        for (var g = 0; g < MaxBindGroups; g++)
        {
            ref var group = ref pass.GroupDecls[g];
            if (group.Layout is null) continue;

            for (var i = 0; i < group.Count; i++)
                entries[i] = Resolve(_bindings[group.Start + i]);
            pass.Groups[g] = BindGroups!.Get(group.Name, group.Layout, entries[..group.Count]);
        }
    }

    private BindGroupEntryDesc Resolve(in GraphBinding binding)
    {
        switch (binding.Kind)
        {
            case GraphBindingKind.Raw:
                return binding.Raw;
            case GraphBindingKind.BufferRead:
            case GraphBindingKind.BufferWrite:
            {
                var buffer = _resources[binding.TargetBuffer.Index];
                // The declaration carried the window; the handle is the graph's to supply.
                return BindGroupEntryDesc.ForBuffer(binding.Binding, buffer.Buffer, binding.Raw.Offset, binding.Raw.Size);
            }
        }

        var resource = _resources[binding.Target.Index];
        if (resource.Kind != ResourceKind.Owned)
            throw new InvalidOperationException("A bind group can name only textures the graph owns; bind an imported view with GraphBinding.View.");
        var view = binding.Kind == GraphBindingKind.TextureArrayView
            ? Textures!.ArrayView(resource.Name!)
            : resource.View;
        return BindGroupEntryDesc.ForTextureView(binding.Binding, view);
    }

    private void Validate(Span<Pass> passes, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ref var pass = ref passes[i];
            if (pass.Kind == PassKind.Raster && pass.ColorCount == 0 && !pass.HasDepth)
                throw new InvalidOperationException(
                    $"Raster pass '{pass.Name}' declares no attachments; it would render nowhere.");
            if (pass.Recorder is null)
                throw new InvalidOperationException(
                    $"Pass '{pass.Name}' was declared but never given a recorder.");
        }

        // A pass that samples what it is rendering into is rejected by every backend at draw
        // time with a message about a bind group; name the pass and the resource here instead.
        // The same holds for a compute pass reading a buffer or texture it also writes.
        foreach (var edge in _reads)
        {
            if (edge.History) continue;
            if (WritesResource(passes, edge.Pass, edge.Resource))
                throw new InvalidOperationException(
                    $"Pass '{passes[edge.Pass].Name}' reads '{NameOf(edge.Resource)}' while writing it.");
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
            if (passes[i].NeverCull || WritesObservable(passes, i))
                _liveStack.Push(i);

        while (_liveStack.Count > 0)
        {
            var index = _liveStack.Pop();
            if (passes[index].Live) continue;
            passes[index].Live = true;

            foreach (var edge in _reads)
                if (edge.Pass == index)
                    PushProducers(passes, count, edge.Resource);

            // An attachment loaded rather than cleared is a read of whatever wrote it, and the
            // one kind the declaring code cannot forget to mention: it is in the attachment.
            ref var live = ref passes[index];
            for (var c = 0; c < live.ColorCount; c++)
                if (live.Colors[c].Load == LoadOp.Load)
                    PushProducers(passes, count, live.Colors[c].Target.Index);
            if (live.HasDepth && live.Depth.Load == LoadOp.Load)
                PushProducers(passes, count, live.Depth.Target.Index);
        }
    }

    private void PushProducers(Span<Pass> passes, int count, int resourceIndex)
    {
        for (var producer = 0; producer < count; producer++)
            if (!passes[producer].Live && WritesResource(passes, producer, resourceIndex))
                _liveStack.Push(producer);
    }

    private bool WritesObservable(Span<Pass> passes, int passIndex)
    {
        ref var pass = ref passes[passIndex];
        for (var c = 0; c < pass.ColorCount; c++)
            if (_resources[pass.Colors[c].Target.Index].Scope == GraphResourceScope.External)
                return true;
        if (pass.HasDepth && _resources[pass.Depth.Target.Index].Scope == GraphResourceScope.External)
            return true;
        foreach (var edge in _writes)
            if (edge.Pass == passIndex && _resources[edge.Resource].Scope == GraphResourceScope.External)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="passIndex"/> writes <paramref name="resourceIndex"/>, as
    /// an attachment or through a declared storage write.</summary>
    private bool WritesResource(Span<Pass> passes, int passIndex, int resourceIndex)
    {
        ref var pass = ref passes[passIndex];
        for (var c = 0; c < pass.ColorCount; c++)
            if (pass.Colors[c].Target.Index == resourceIndex)
                return true;
        if (pass.HasDepth && pass.Depth.Target.Index == resourceIndex)
            return true;
        foreach (var edge in _writes)
            if (edge.Pass == passIndex && edge.Resource == resourceIndex)
                return true;
        return false;
    }

    private ColorAttachmentDesc ResolveColor(Span<Pass> passes, int slot, int count, int color)
    {
        ref var a = ref passes[_order[slot]].Colors[color];
        var resource = _resources[a.Target.Index];
        var store = StoreOf(passes, slot, count, in a);
        return resource.Kind == ResourceKind.Backbuffer
            ? new ColorAttachmentDesc(RenderViewHandle.Invalid, a.Load, store, a.Clear)
            : new ColorAttachmentDesc(RenderViewHandle.Invalid, a.Load, store, a.Clear, resource.View);
    }

    private DepthAttachmentDesc? ResolveDepth(Span<Pass> passes, int slot, int count)
    {
        ref var pass = ref passes[_order[slot]];
        if (!pass.HasDepth) return null;
        ref var a = ref pass.Depth;
        var resource = _resources[a.Target.Index];
        var store = StoreOf(passes, slot, count, in a);
        // An owned depth target renders through the texture's own default view unless a layer was
        // chosen; an imported one renders through whatever view it was imported with.
        var view = resource.Kind switch
        {
            ResourceKind.Owned when a.Layer >= 0 => Textures!.SliceView(resource.Name!, (uint)a.Layer),
            ResourceKind.Owned => default,
            _ => resource.View,
        };
        return new DepthAttachmentDesc(resource.Texture, a.Load, store, a.ClearDepth, view);
    }

    /// <summary>The edges checking the events: a pass placed before the pass that writes what it
    /// reads gets last frame's contents, and the event key is the only thing that put it there.
    ///
    /// <para>Three reads are exempt. A resource nothing writes at all may be legitimately bound
    /// and never sampled — the shadow array in a frame with no shadowed light. An imported
    /// resource or the backbuffer may have been written by the host before the frame, so the
    /// first pass loading it is not reading ahead of anyone; an owned target is checked even when
    /// exported, because its writers are all in this graph. And a
    /// <see cref="PassBuilder.ReadsHistory"/> read wants last frame's contents by definition.</para></summary>
    private void CheckReadsFollowWrites(Span<Pass> passes, int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            var index = _order[slot];
            foreach (var edge in _reads)
                if (edge.Pass == index && !edge.History)
                    RequireNoLaterWriter(passes, slot, count, edge.Resource);

            ref var pass = ref passes[index];
            for (var c = 0; c < pass.ColorCount; c++)
                if (pass.Colors[c].Load == LoadOp.Load)
                    RequireNoLaterWriter(passes, slot, count, pass.Colors[c].Target.Index);
            if (pass.HasDepth && pass.Depth.Load == LoadOp.Load)
                RequireNoLaterWriter(passes, slot, count, pass.Depth.Target.Index);
        }
    }

    private void RequireNoLaterWriter(Span<Pass> passes, int slot, int count, int resourceIndex)
    {
        // Owned textures and tracked buffers have all their writers in this graph; an imported
        // texture or the backbuffer may have been written by the host before the frame.
        var kind = _resources[resourceIndex].Kind;
        if (kind != ResourceKind.Owned && kind != ResourceKind.ImportedBuffer) return;
        if (WrittenBefore(passes, slot, resourceIndex)) return;
        for (var later = slot + 1; later < count; later++)
        {
            if (!WritesResource(passes, _order[later], resourceIndex)) continue;
            ref var writer = ref passes[_order[later]];
            ref var reader = ref passes[_order[slot]];
            throw new InvalidOperationException(
                $"Pass '{reader.Name}' (event {reader.SortKey}) reads '{NameOf(resourceIndex)}' before " +
                $"'{writer.Name}' (event {writer.SortKey}) writes it this frame. Move the reader after the " +
                "writer's event, or the writer before the reader's.");
        }
    }

    /// <summary>The declared store op, or the inferred one: keep the contents if anything after
    /// this pass consumes them or something outside the graph might, else discard.</summary>
    private StoreOp StoreOf(Span<Pass> passes, int slot, int count, in Attachment a)
    {
        var resourceIndex = a.Target.Index;
        // Owned only: a host may well have written an imported target before the frame.
        if (a.Load == LoadOp.Load && _resources[resourceIndex].Kind == ResourceKind.Owned
            && !WrittenBefore(passes, slot, resourceIndex))
            LogLoadOfUnwritten(_log, passes[_order[slot]].Name, NameOf(resourceIndex));

        if (a.Store is { } declared) return declared;
        if (_resources[resourceIndex].Scope == GraphResourceScope.External) return StoreOp.Store;
        return ReadAfter(passes, slot, count, resourceIndex) || ReadAsHistory(resourceIndex)
            ? StoreOp.Store
            : StoreOp.Discard;
    }

    // A history reader consumes the write next frame, wherever it sits in this one.
    private bool ReadAsHistory(int resourceIndex)
    {
        foreach (var edge in _reads)
            if (edge.History && edge.Resource == resourceIndex) return true;
        return false;
    }

    private bool ReadAfter(Span<Pass> passes, int slot, int count, int resourceIndex)
    {
        for (var later = slot + 1; later < count; later++)
        {
            var index = _order[later];
            if (Loads(in passes[index], resourceIndex)) return true;
            foreach (var edge in _reads)
                if (edge.Pass == index && edge.Resource == resourceIndex) return true;
        }
        return false;
    }

    private bool WrittenBefore(Span<Pass> passes, int slot, int resourceIndex)
    {
        for (var earlier = 0; earlier < slot; earlier++)
            if (WritesResource(passes, _order[earlier], resourceIndex)) return true;
        return false;
    }

    private static bool Loads(in Pass pass, int resourceIndex)
    {
        for (var c = 0; c < pass.ColorCount; c++)
            if (pass.Colors[c].Target.Index == resourceIndex && pass.Colors[c].Load == LoadOp.Load)
                return true;
        return pass.HasDepth && pass.Depth.Target.Index == resourceIndex && pass.Depth.Load == LoadOp.Load;
    }

    private string NameOf(int resourceIndex)
    {
        var resource = _resources[resourceIndex];
        return resource.Name ?? resource.Kind switch
        {
            ResourceKind.Backbuffer => "backbuffer",
            ResourceKind.ImportedBuffer => $"buffer#{resourceIndex}",
            _ => $"imported#{resourceIndex}",
        };
    }

    /// <summary>Loading what nothing wrote reads last frame's contents — or, after a resize,
    /// whatever the new allocation holds. Sometimes intended (a history buffer), never silent.</summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Pass '{Pass}' loads '{Resource}', which no earlier pass wrote this frame.")]
    private static partial void LogLoadOfUnwritten(ILogger logger, string pass, string resource);

    private enum ResourceKind : byte { Backbuffer, ImportedColor, ImportedDepth, Owned, ImportedBuffer }

    private enum PassKind : byte { Raster, Compute }

    private readonly record struct Resource(
        ResourceKind Kind, GraphResourceScope Scope, TextureViewHandle View, TextureHandle Texture, string? Name, BufferHandle Buffer);

    private readonly record struct ReadEdge(int Pass, int Resource, bool History);

    private readonly record struct WriteEdge(int Pass, int Resource);

    private struct Attachment
    {
        public GraphTexture Target;
        public LoadOp Load;
        public StoreOp? Store;
        public ColorRgba Clear;
        public float ClearDepth;
        public int Layer;
    }

    [InlineArray(MaxColorAttachments)]
    private struct ColorSlots
    {
        private Attachment _slot0;
    }

    private struct GroupDecl
    {
        public string Name;
        public BindGroupLayoutDesc? Layout;
        public int Start;
        public int Count;
    }

    [InlineArray(MaxBindGroups)]
    private struct GroupDecls
    {
        private GroupDecl _slot0;
    }

    [InlineArray(MaxBindGroups)]
    private struct GroupHandles
    {
        private BindGroupHandle _slot0;
    }

    /// <summary>How a stored recorder is called. Typed and untyped recorders are both kept as a
    /// <see cref="Delegate"/> plus one of these, cached per feature type, so declaring a pass
    /// allocates nothing however it was declared.</summary>
    private delegate void PassInvoker(Delegate recorder, object context, ref PassRecording pass, int argument);

    private static readonly PassInvoker s_invokeUntyped =
        static (Delegate recorder, object context, ref PassRecording pass, int argument) =>
            ((PassRecorder)recorder)(context, ref pass, argument);

    private static class Typed<TFeature> where TFeature : class
    {
        public static readonly PassInvoker Invoke =
            static (Delegate recorder, object context, ref PassRecording pass, int argument) =>
                ((PassRecorder<TFeature>)recorder)((TFeature)context, ref pass, argument);
    }

    private struct Pass
    {
        public string Name;
        public PassKind Kind;
        public int SortKey;
        public object? Context;
        public Delegate? Recorder;
        public PassInvoker? Invoke;
        public int Argument;
        public int ColorCount;
        public ColorSlots Colors;
        public bool HasDepth;
        public Attachment Depth;
        public GroupDecls GroupDecls;
        public GroupHandles Groups;
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

        /// <summary>Bind a color attachment. Slots must be filled from 0 upward. A null
        /// <paramref name="store"/> lets the graph infer it.</summary>
        public PassBuilder Color(int slot, GraphTexture target, LoadOp load, StoreOp? store = null, ColorRgba clear = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(slot);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, MaxColorAttachments);
            if (!target.IsValid) throw new ArgumentException("Attachment target is not a graph resource.", nameof(target));

            ref var pass = ref _graph.PassAt(_index);
            RequireRaster(in pass, "a color attachment");
            if (slot > pass.ColorCount)
                throw new ArgumentOutOfRangeException(nameof(slot),
                    $"Color slot {slot} skips slot {pass.ColorCount}; attachments must be bound in order.");
            pass.Colors[slot] = new Attachment { Target = target, Load = load, Store = store, Clear = clear };
            if (slot == pass.ColorCount) pass.ColorCount++;
            return this;
        }

        /// <summary>Bind the depth attachment. A null <paramref name="store"/> lets the graph
        /// infer it.</summary>
        public PassBuilder Depth(GraphTexture target, LoadOp load, StoreOp? store = null, float clear = 1f) =>
            DepthAttachment(target, -1, load, store, clear);

        /// <summary>Bind one layer of an owned depth array as the depth attachment. The pass still
        /// counts as writing the whole array, so a reader of the array keeps every layer's pass.</summary>
        public PassBuilder DepthLayer(GraphTexture target, uint layer, LoadOp load, StoreOp? store = null, float clear = 1f)
        {
            if (_graph._resources[target.Index].Kind != ResourceKind.Owned)
                throw new ArgumentException("Only a texture the graph owns can be rendered by layer; import the layer's view instead.", nameof(target));
            return DepthAttachment(target, (int)layer, load, store, clear);
        }

        private PassBuilder DepthAttachment(GraphTexture target, int layer, LoadOp load, StoreOp? store, float clear)
        {
            if (!target.IsValid) throw new ArgumentException("Attachment target is not a graph resource.", nameof(target));

            ref var pass = ref _graph.PassAt(_index);
            RequireRaster(in pass, "a depth attachment");
            pass.HasDepth = true;
            pass.Depth = new Attachment { Target = target, Load = load, Store = store, ClearDepth = clear, Layer = layer };
            return this;
        }

        /// <summary>Declare a bind group the pass will bind at <paramref name="groupIndex"/>. Every
        /// owned texture among <paramref name="bindings"/> becomes a read of that texture, and the
        /// group is resolved after culling through the graph's <see cref="BindGroupCache"/>, so the
        /// recorder binds it by index with <see cref="PassRecording.SetBindGroup"/>.</summary>
        public PassBuilder BindGroup(uint groupIndex, string name, BindGroupLayoutDesc layout, ReadOnlySpan<GraphBinding> bindings)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(groupIndex, (uint)MaxBindGroups);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(layout);
            if (_graph.BindGroups is null)
                throw new InvalidOperationException("This graph has no bind group cache; bind groups cannot be declared on its passes.");
            if (bindings.Length > BindGroupCache.MaxEntries)
                throw new ArgumentOutOfRangeException(nameof(bindings),
                    $"Pass '{_graph.PassAt(_index).Name}' declares {bindings.Length} entries in group {groupIndex}; the cache holds at most {BindGroupCache.MaxEntries}.");
            // A second declaration would replace the group but leave the first one's read edges on
            // the pass — a target swapped for black would keep its producer alive.
            if (_graph.PassAt(_index).GroupDecls[(int)groupIndex].Layout is not null)
                throw new InvalidOperationException(
                    $"Pass '{_graph.PassAt(_index).Name}' already declared bind group {groupIndex}.");

            var start = _graph._bindings.Count;
            foreach (var binding in bindings)
            {
                _graph._bindings.Add(binding);
                switch (binding.Kind)
                {
                    case GraphBindingKind.TextureView:
                    case GraphBindingKind.TextureArrayView:
                        Reads(binding.Target);
                        break;
                    case GraphBindingKind.StorageTextureView:
                        Writes(binding.Target);
                        break;
                    case GraphBindingKind.BufferRead:
                        Reads(binding.TargetBuffer);
                        break;
                    case GraphBindingKind.BufferWrite:
                        Writes(binding.TargetBuffer);
                        break;
                }
            }
            _graph.PassAt(_index).GroupDecls[(int)groupIndex] = new GroupDecl
            {
                Name = name, Layout = layout, Start = start, Count = bindings.Length,
            };
            return this;
        }

        /// <summary>Declare that this pass samples <paramref name="source"/> through a bind group
        /// the graph does not build — a material's, a host's. The escape hatch: a read declared on
        /// the pass with <see cref="BindGroup"/> cannot be forgotten, and this one can, and
        /// forgetting it on a private target culls the producer while the pass samples stale
        /// contents.</summary>
        public PassBuilder Reads(GraphTexture source)
        {
            if (!source.IsValid) throw new ArgumentException("Read source is not a graph resource.", nameof(source));
            _graph._reads.Add(new ReadEdge(_index, source.Index, History: false));
            return this;
        }

        /// <summary>Declare that this pass reads <paramref name="source"/> through a group the graph
        /// does not build. The buffer twin of <see cref="Reads(GraphTexture)"/>.</summary>
        public PassBuilder Reads(GraphBuffer source)
        {
            if (!source.IsValid) throw new ArgumentException("Read source is not a graph resource.", nameof(source));
            _graph._reads.Add(new ReadEdge(_index, source.Index, History: false));
            return this;
        }

        /// <summary>Declare that this pass writes <paramref name="target"/> other than as an
        /// attachment — a storage texture bound through a group the graph does not build. The pass
        /// becomes the target's producer for culling and ordering, exactly as if it rendered into
        /// it. Bound through <see cref="BindGroup"/> with <see cref="GraphBinding.StorageTexture"/>
        /// the declaration is derived and this call is unnecessary.</summary>
        public PassBuilder Writes(GraphTexture target)
        {
            if (!target.IsValid) throw new ArgumentException("Write target is not a graph resource.", nameof(target));
            _graph._writes.Add(new WriteEdge(_index, target.Index));
            return this;
        }

        /// <summary>Declare that this pass writes <paramref name="target"/>. The buffer twin of
        /// <see cref="Writes(GraphTexture)"/>.</summary>
        public PassBuilder Writes(GraphBuffer target)
        {
            if (!target.IsValid) throw new ArgumentException("Write target is not a graph resource.", nameof(target));
            _graph._writes.Add(new WriteEdge(_index, target.Index));
            return this;
        }

        private static void RequireRaster(in Pass pass, string what)
        {
            if (pass.Kind != PassKind.Raster)
                throw new InvalidOperationException($"Compute pass '{pass.Name}' cannot declare {what}; it has no attachments.");
        }

        /// <summary>Declare that this pass samples what <paramref name="source"/> held at the END
        /// of the previous frame — a history buffer. The producer stays live and stored however
        /// the two are ordered, and reading it before the producer runs is not the mistake it
        /// would be for a plain read.</summary>
        public PassBuilder ReadsHistory(GraphTexture source)
        {
            if (!source.IsValid) throw new ArgumentException("Read source is not a graph resource.", nameof(source));
            _graph._reads.Add(new ReadEdge(_index, source.Index, History: true));
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
            return Record(context, recorder, s_invokeUntyped, argument);
        }

        /// <summary>Supply the recorder as a method of the declaring feature's own type.</summary>
        public PassBuilder Record<TFeature>(TFeature feature, PassRecorder<TFeature> recorder, int argument = 0)
            where TFeature : class
        {
            ArgumentNullException.ThrowIfNull(feature);
            ArgumentNullException.ThrowIfNull(recorder);
            return Record(feature, recorder, Typed<TFeature>.Invoke, argument);
        }

        private PassBuilder Record(object context, Delegate recorder, PassInvoker invoke, int argument)
        {
            ref var pass = ref _graph.PassAt(_index);
            pass.Context = context;
            pass.Recorder = recorder;
            pass.Invoke = invoke;
            pass.Argument = argument;
            return this;
        }
    }
}
