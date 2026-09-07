using System.Buffers;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

/// <summary>The feature seam: features run in the order their slots put them in, see each other
/// only through the blackboard and the frame's requirements, and are skipped entirely when the
/// engine's feature configuration says they are off.</summary>
public class RenderPipelineTests
{
    private sealed class Probe(string name, FrameRequirements requires = FrameRequirements.None, bool enabledByDefault = true) : IRenderFeature
    {
        // Per test, not static: TUnit runs tests in parallel.
        public List<string> Log { get; init; } = [];

        public FeatureDefinition Definition { get; } = new($"test.{name}", enabledByDefault);
        public FrameRequirements Requires { get; } = requires;
        public FrameRequirements SeenRequirements { get; private set; }
        public bool SawTexture { get; private set; }
        public bool Disposed { get; private set; }
        public string? Publishes { get; init; }
        public string? Consumes { get; init; }
        public int Resized { get; private set; }
        public List<bool> EnabledChanges { get; } = [];

        public void Resize(uint width, uint height) => Resized++;

        public void OnEnabledChanged(bool enabled)
        {
            EnabledChanges.Add(enabled);
            Log.Add((enabled ? "on " : "off ") + Definition.Name);
        }

        public void Setup(in FrameContext frame)
        {
            Log.Add(Definition.Name);
            SeenRequirements = frame.Requirements;
            if (Publishes is not null)
            {
                frame.Textures.Ensure(Publishes, new TextureDesc(null, 4, 4, 1, 1, 1, TextureDimension.D2,
                    TextureFormat.Rgba16Float, TextureUsage.RenderAttachment | TextureUsage.TextureBinding));
                frame.Blackboard.Publish(Publishes, frame.Graph.Texture(Publishes));
            }
            if (Consumes is not null)
                SawTexture = frame.Blackboard.TryGet(Consumes, out _);
        }

        public void Dispose()
        {
            Log.Add("dispose " + Definition.Name);
            Disposed = true;
        }
    }

    private static FrameGraph GraphWithTextures() => new(new GraphTextureRegistry(new FakeTextureFactory()));

    [Test]
    public async Task features_set_up_in_list_order_and_skip_the_disabled()
    {
        var log = new List<string>();
        var a = new Probe("a") { Log = log };
        var b = new Probe("b") { Log = log };
        var c = new Probe("c") { Log = log };
        using var pipeline = new RenderPipeline(8, 8).Add(a).Add(b).Add(c);
        pipeline.Switches.Set(b.Definition.Id, false);
        log.Clear();

        pipeline.Setup(GraphWithTextures());

        await Assert.That(log).IsEquivalentTo(["test.a", "test.c"]);
    }

    /// <summary>The switch is read every frame, so a host, a debug panel or a hot-reloaded config
    /// changes the frame without rebuilding anything.</summary>
    [Test]
    public async Task a_switch_flipped_between_frames_changes_the_next_frame()
    {
        var log = new List<string>();
        var a = new Probe("a") { Log = log };
        using var pipeline = new RenderPipeline(8, 8).Add(a);

        pipeline.Setup(GraphWithTextures());
        pipeline.Switches.Set(a.Definition.Id, false);
        pipeline.Setup(GraphWithTextures());
        pipeline.Switches.Set(a.Definition.Id, true);
        pipeline.Setup(GraphWithTextures());

        await Assert.That(log).IsEquivalentTo(["test.a", "off test.a", "on test.a", "test.a"]);
    }

    /// <summary>The transition, not the state: a feature that must retract something it left
    /// behind hears about it exactly once, and hears nothing while the switch does not move.</summary>
    [Test]
    public async Task on_enabled_changed_fires_on_the_transition_only()
    {
        var a = new Probe("a");
        using var pipeline = new RenderPipeline(8, 8).Add(a);

        pipeline.Setup(GraphWithTextures());
        pipeline.Switches.Set(a.Definition.Id, false);
        pipeline.Switches.Set(a.Definition.Id, false);
        pipeline.Setup(GraphWithTextures());

        await Assert.That(a.EnabledChanges).IsEquivalentTo([false]);
    }

    /// <summary>A config file is read before the renderer is built, so the override lands on a
    /// name nothing has declared yet — and must still be in force when the feature turns up.</summary>
    [Test]
    public async Task an_override_applied_before_the_feature_exists_still_takes_effect()
    {
        var switches = new FeatureSwitches(FeatureOverrides.Parse("-test.a"));
        var a = new Probe("a");
        using var pipeline = new RenderPipeline(8, 8, switches).Add(a);

        pipeline.Setup(GraphWithTextures());

        await Assert.That(pipeline.IsEnabled(a)).IsFalse();
        await Assert.That(a.Log).IsEquivalentTo(["off test.a"]);
        await Assert.That(a.SeenRequirements).IsEqualTo(FrameRequirements.None);
    }

    /// <summary>A feature that ships off is off without anybody configuring anything, and is not
    /// told about a state it was born in.</summary>
    [Test]
    public async Task a_feature_that_ships_disabled_stays_off_and_is_not_notified()
    {
        var a = new Probe("a", enabledByDefault: false);
        using var pipeline = new RenderPipeline(8, 8).Add(a);

        pipeline.Setup(GraphWithTextures());

        await Assert.That(pipeline.IsEnabled(a)).IsFalse();
        await Assert.That(a.EnabledChanges).IsEmpty();
    }

    /// <summary>The slot, not the call order: a feature added last can still set up first, which
    /// is what lets a game publish something an engine feature reads.</summary>
    [Test]
    public async Task order_places_a_late_addition_between_two_earlier_ones()
    {
        var log = new List<string>();
        var first = new Probe("first") { Log = log };
        var last = new Probe("last") { Log = log };
        var between = new Probe("between") { Log = log };
        using var pipeline = new RenderPipeline(8, 8).Add(first, 100).Add(last, 300).Add(between, 200);

        pipeline.Setup(GraphWithTextures());

        await Assert.That(log).IsEquivalentTo(["test.first", "test.between", "test.last"]);
        await Assert.That(pipeline.Features.Select(f => f.Definition.Name))
            .IsEquivalentTo(["test.first", "test.between", "test.last"]);
    }

    /// <summary>Two features in the same slot keep the order they were added in — the tie-break
    /// a composer relies on when it adds a group of features that belong together.</summary>
    [Test]
    public async Task an_equal_order_keeps_insertion_order()
    {
        var log = new List<string>();
        using var pipeline = new RenderPipeline(8, 8)
            .Add(new Probe("a") { Log = log }, 100)
            .Add(new Probe("b") { Log = log }, 100)
            .Add(new Probe("c") { Log = log }, 100);

        pipeline.Setup(GraphWithTextures());

        await Assert.That(log).IsEquivalentTo(["test.a", "test.b", "test.c"]);
    }

    /// <summary>The mechanism by which one feature reshapes a pass another owns: the requirement
    /// is visible to every feature, before any of them sets up.</summary>
    [Test]
    public async Task requirements_are_the_union_over_enabled_features_and_every_feature_sees_them()
    {
        var scene = new Probe("scene");
        var capture = new Probe("capture", FrameRequirements.SceneColorCapture);
        using var pipeline = new RenderPipeline(8, 8).Add(scene).Add(capture);

        pipeline.Setup(GraphWithTextures());
        var seenBySceneFirst = scene.SeenRequirements;

        pipeline.Switches.Set(capture.Definition.Id, false);
        pipeline.Setup(GraphWithTextures());
        var seenAfterDisable = scene.SeenRequirements;

        await Assert.That(seenBySceneFirst).IsEqualTo(FrameRequirements.SceneColorCapture);
        await Assert.That(seenAfterDisable).IsEqualTo(FrameRequirements.None);
    }

    [Test]
    public async Task a_result_published_earlier_in_the_list_is_visible_later_and_gone_next_frame()
    {
        var producer = new Probe("producer") { Publishes = "result" };
        var consumer = new Probe("consumer") { Consumes = "result" };
        using var pipeline = new RenderPipeline(8, 8).Add(producer).Add(consumer);
        var graph = GraphWithTextures();

        pipeline.Setup(graph);
        var seenWhenOn = consumer.SawTexture;

        pipeline.Switches.Set(producer.Definition.Id, false);
        graph.Reset();
        pipeline.Setup(graph);
        var seenWhenOff = consumer.SawTexture;

        await Assert.That(seenWhenOn).IsTrue();
        await Assert.That(seenWhenOff).IsFalse();
    }

    [Test]
    public async Task publishing_a_name_twice_in_one_frame_is_refused()
    {
        var blackboard = new FrameBlackboard();
        blackboard.Publish("hdr", new GraphTexture(1));

        await Assert.That(() => blackboard.Publish("hdr", new GraphTexture(2))).Throws<InvalidOperationException>()
            .WithMessageContaining("hdr");
    }

    /// <summary>Including the features that are off: one switched back on after a resize would
    /// otherwise hand out a target sized for the old frame.</summary>
    [Test]
    public async Task resize_reaches_every_feature_and_dispose_runs_in_reverse()
    {
        var log = new List<string>();
        var a = new Probe("a") { Log = log };
        var b = new Probe("b") { Log = log };
        var pipeline = new RenderPipeline(8, 8).Add(a).Add(b);
        pipeline.Switches.Set(b.Definition.Id, false);
        log.Clear();

        pipeline.Resize(16, 16);
        pipeline.Dispose();

        // Once from Add, once from Resize: a feature declares its targets through one call.
        await Assert.That(a.Resized).IsEqualTo(2);
        await Assert.That(b.Resized).IsEqualTo(2);
        await Assert.That(log).IsEquivalentTo(["dispose test.b", "dispose test.a"]);
    }

    /// <summary>A disposed pipeline stops hearing the switchboard it did not own — a shared
    /// configuration outlives one renderer, and a dead feature must not be told anything.</summary>
    [Test]
    public async Task dispose_stops_the_pipeline_listening_to_a_shared_switchboard()
    {
        var switches = new FeatureSwitches();
        var a = new Probe("a");
        var pipeline = new RenderPipeline(8, 8, switches).Add(a);

        pipeline.Dispose();
        switches.Set(a.Definition.Id, false);

        await Assert.That(a.EnabledChanges).IsEmpty();
    }

    [Test]
    public async Task find_returns_the_first_feature_of_a_type()
    {
        var a = new Probe("a");
        using var pipeline = new RenderPipeline(8, 8).Add(a).Add(new Probe("b"));

        await Assert.That(pipeline.Find<Probe>()).IsSameReferenceAs(a);
    }

    private sealed class TypedFeature
    {
        public int Argument = -1;

        public static void Record(TypedFeature self, ref PassRecording pass, int argument)
        {
            self.Argument = argument;
            pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
        }
    }

    /// <summary>The typed recorder arrives as itself, with the argument, and costs no closure.</summary>
    [Test]
    public async Task a_typed_recorder_receives_its_own_feature()
    {
        var graph = new FrameGraph();
        var target = graph.ImportColor(new TextureViewHandle(3, 1));
        var feature = new TypedFeature();
        var writer = new ArrayBufferWriter<RenderCommand>(16);

        graph.AddRasterPass("p", RenderPassEvent.Opaque)
            .Color(0, target, LoadOp.Clear).Record(feature, TypedFeature.Record, 7);
        var commands = graph.Compile(writer).Commands.Length;

        await Assert.That(feature.Argument).IsEqualTo(7);
        await Assert.That(commands).IsEqualTo(3);
    }

    /// <summary>A feature added after construction is told the size it was added at, so it
    /// need not create targets in its constructor to survive frame one.</summary>
    [Test]
    public async Task add_tells_the_feature_the_current_size()
    {
        var a = new Probe("a");
        using var pipeline = new RenderPipeline(320, 200);

        pipeline.Add(a);

        await Assert.That(a.Resized).IsEqualTo(1);
        await Assert.That(pipeline.Width).IsEqualTo(320u);
        await Assert.That(pipeline.Height).IsEqualTo(200u);
    }
}
