using System.Buffers;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Test;

/// <summary>The feature seam: features run in list order, see each other only through the
/// blackboard and the frame's requirements, and are skipped entirely when disabled.</summary>
public class RenderPipelineTests
{
    private sealed class Probe(string name, FrameRequirements requires = FrameRequirements.None) : IRenderFeature
    {
        // Per test, not static: TUnit runs tests in parallel.
        public List<string> Log { get; init; } = [];

        public string Name { get; } = name;
        public bool Enabled { get; set; } = true;
        public FrameRequirements Requires { get; } = requires;
        public FrameRequirements SeenRequirements { get; private set; }
        public bool SawTexture { get; private set; }
        public bool Disposed { get; private set; }
        public string? Publishes { get; init; }
        public string? Consumes { get; init; }
        public int Resized { get; private set; }

        public void Resize(uint width, uint height) => Resized++;

        public void Setup(in FrameContext frame)
        {
            Log.Add(Name);
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
            Log.Add("dispose " + Name);
            Disposed = true;
        }
    }

    private static FrameGraph GraphWithTextures() => new(new GraphTextureRegistry(new FakeTextureFactory()));

    [Test]
    public async Task features_set_up_in_list_order_and_skip_the_disabled()
    {
        var log = new List<string>();
        var a = new Probe("a") { Log = log };
        var b = new Probe("b") { Enabled = false, Log = log };
        var c = new Probe("c") { Log = log };
        using var pipeline = new RenderPipeline(8, 8).Add(a).Add(b).Add(c);

        pipeline.Setup(GraphWithTextures());

        await Assert.That(log).IsEquivalentTo(["a", "c"]);
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

        capture.Enabled = false;
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

        producer.Enabled = false;
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

    [Test]
    public async Task resize_reaches_every_feature_and_dispose_runs_in_reverse()
    {
        var log = new List<string>();
        var a = new Probe("a") { Log = log };
        var b = new Probe("b") { Log = log };
        var pipeline = new RenderPipeline(8, 8).Add(a).Add(b);

        pipeline.Resize(16, 16);
        pipeline.Dispose();

        // Once from Add, once from Resize: a feature declares its targets through one call.
        await Assert.That(a.Resized).IsEqualTo(2);
        await Assert.That(b.Resized).IsEqualTo(2);
        await Assert.That(log).IsEquivalentTo(["dispose b", "dispose a"]);
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
