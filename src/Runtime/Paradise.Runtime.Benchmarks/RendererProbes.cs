using System.Reflection;
using Paradise.Features;
using Paradise.Rendering.Graph;
using Paradise.Rendering.Pbr;

namespace Paradise.Runtime.Benchmarks;

/// <summary>Splits <see cref="PbrRenderer.RenderFrame"/>'s allocations by stage and by built-in feature,
/// without changing the renderer.</summary>
/// <remarks>
/// <para>A probe feature sits just after every <see cref="PbrFeatureOrder"/> slot, plus one before all
/// features and one after all of them. Features run in order in each pipeline phase, so the bytes a probe
/// sees since the previous probe belong to the features between the two. What runs between pipeline
/// phases is attributed to the renderer stage it is:</para>
/// <list type="bullet">
/// <item>before the first <c>PrepareFrame</c> probe: frame begin (switch snapshot, context begin);</item>
/// <item>after the last <c>PrepareFrame</c> probe until the first <c>Setup</c> probe: extraction,
/// partition, trace build and graph reset;</item>
/// <item>after the last <c>Setup</c> probe until the first <c>BeforeSubmit</c> probe: graph compile,
/// which runs every pass recorder;</item>
/// <item>after the last <c>BeforeSubmit</c> probe: uploads and the backend's submit.</item>
/// </list>
/// <para>Probes declare no passes and require nothing, so the frame is otherwise unchanged.</para>
/// </remarks>
internal sealed class RendererProbes
{
    private readonly AllocationLedger.Phase _begin;
    private readonly AllocationLedger.Phase _extract;
    private readonly AllocationLedger.Phase _compile;
    private readonly AllocationLedger.Phase _submit;
    private long _mark;

    private RendererProbes(AllocationLedger ledger)
    {
        _begin = ledger.Get("render: frame begin");
        _extract = ledger.Get("render: extract, partition, trace build");
        _compile = ledger.Get("render: graph compile (pass recorders)");
        _submit = ledger.Get("render: upload + backend submit");
    }

    /// <summary>Adds the probes to <paramref name="renderer"/>'s pipeline.</summary>
    public static RendererProbes Install(PbrRenderer renderer, AllocationLedger ledger)
    {
        var probes = new RendererProbes(ledger);
        var pipeline = renderer.Pipeline;
        var index = 0;
        pipeline.Add(new Probe(probes, ledger, index++, "start", first: true), int.MinValue);
        foreach (var (order, label) in BuiltInSlots())
            pipeline.Add(new Probe(probes, ledger, index++, label, first: false), order + 1);
        pipeline.Add(new Probe(probes, ledger, index, "game features", first: false), RenderPipeline.DefaultOrder);
        return probes;
    }

    /// <summary>Call immediately before <see cref="PbrRenderer.RenderFrame"/>.</summary>
    public void BeginFrame() => _mark = AllocationLedger.Now();

    /// <summary>Call immediately after <see cref="PbrRenderer.RenderFrame"/>.</summary>
    public void EndFrame() => Cut(_submit);

    private void Cut(AllocationLedger.Phase phase)
    {
        var now = AllocationLedger.Now();
        phase.AddBytes(now - _mark);
        // Re-read: recording the sample must not be charged to the next segment.
        _mark = AllocationLedger.Now();
    }

    /// <summary>The built-in order slots, merged where two names share a value (<c>Fxaa</c> and
    /// <c>AntiAliasing</c>, say), in setup order.</summary>
    private static IEnumerable<(int Order, string Label)> BuiltInSlots() =>
        typeof(PbrFeatureOrder)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .GroupBy(field => (int)field.GetRawConstantValue()!)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, string.Join("/", group.Select(field => field.Name))));

    private sealed class Probe : IRenderFeature
    {
        private readonly RendererProbes _probes;
        private readonly AllocationLedger.Phase _prepare;
        private readonly AllocationLedger.Phase _setup;
        private readonly AllocationLedger.Phase _beforeSubmit;

        public Probe(RendererProbes probes, AllocationLedger ledger, int index, string label, bool first)
        {
            _probes = probes;
            Definition = new FeatureDefinition($"benchmark.allocationProbe{index}", true, "Allocation probe.");
            _prepare = first ? probes._begin : ledger.Get($"prepare: {label}");
            _setup = first ? probes._extract : ledger.Get($"setup: {label}");
            _beforeSubmit = first ? probes._compile : ledger.Get($"beforeSubmit: {label}");
        }

        public FeatureDefinition Definition { get; }
        public FrameRequirements Requires => FrameRequirements.None;

        public void Resize(uint width, uint height) { }
        public void PrepareFrame() => _probes.Cut(_prepare);
        public void Setup(in FrameContext frame) => _probes.Cut(_setup);
        public void BeforeSubmit() => _probes.Cut(_beforeSubmit);
        public void Dispose() { }
    }
}
