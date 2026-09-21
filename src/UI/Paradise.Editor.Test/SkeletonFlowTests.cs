using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Paradise.Diagnostics;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.History;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
using Zio.FileSystems;

namespace Paradise.Editor.Test;

/// <summary>The intended flow, end to end, on mocks: an extension registers an operator, a
/// panel dispatches it by id, the operator commits a new document version through the scene
/// provider, undo republishes the previous one, and removing the owner removes everything it
/// registered.</summary>
public class SkeletonFlowTests
{
    /// <summary>Stands in for the standalone host's file-backed provider. The in-game one applies
    /// each accepted version to the live world instead; nothing above this line can tell.</summary>
    private sealed class SceneProvider(SceneDocument document, bool canAccept = true) : ISceneProvider
    {
        public SceneDocument Current { get; private set; } = document;
        public bool CanAccept => canAccept;
        public event Action<SceneDocument, SceneDocument>? Changed;

        public void Accept(SceneDocument accepted)
        {
            var previous = Current;
            Current = accepted;
            Changed?.Invoke(previous, accepted);
        }
    }

    private sealed class RenameOperator : IOperator
    {
        public string Id => "mock.object.rename";
        public string Label => "Rename";
        public string Description => "Renames the primary selected object.";

        public bool IsAvailable(IOperatorContext context) =>
            context.Host.CanEditDocument && context.Selection.Primary is not null;

        public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
        {
            // Invoked from a menu there is no name to apply, and applying `default` would rename
            // the object to nothing — which is why OperatorArgs distinguishes absent.
            if (!args.TryGet<string>("name", out var name)) return OperatorResult.Cancelled;

            var target = context.Document.Find(context.Selection.Primary!.Value)!;
            context.Commit(context.Document.Replace(target.WithName(name)), $"Rename {target.Name}");
            return OperatorResult.Finished;
        }
    }

    private sealed class ThrowingOperator : IOperator
    {
        public string Id => "mock.broken";
        public string Label => "Broken";
        public string Description => "Stands in for the extension that has a bug.";

        public bool IsAvailable(IOperatorContext context) => true;

        public OperatorResult Execute(IOperatorContext context, OperatorArgs args) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class MockExtension : IEditorExtension
    {
        public string Id => "mock";

        public void Register(EditorRegistrar registrar) => registrar
            .AddOperator(new RenameOperator())
            .AddWindow(new WindowDescriptor("mock.window", "Mock", DockArea.Left, "Mock"))
            .AddMenuEntry(new MenuEntry("Edit", "Rename", "mock.object.rename"));
    }

    private sealed class Host : IHostCapabilities
    {
        public bool CanEditDocument => true;
        public bool CanBake => false;
        public bool CanPlayChildProcess => false;
    }

    private sealed class Context : IOperatorContext
    {
        private readonly ISceneProvider _scene;

        public Context(ISceneProvider scene)
        {
            _scene = scene;
            History = new History(scene, new MemoryFileSystem());
        }

        public EditorRegistries Registries { get; } = new();

        /// <summary>A host picks the sink; here that is the thing the assertions read back.</summary>
        public CollectingLogger Sink { get; } = new();

        public SceneDocument Document => _scene.Current;
        public Core.Selection.Selection Selection { get; private set; } = Core.Selection.Selection.Empty;
        public IHistory History { get; }
        public IHostCapabilities Host { get; } = new Host();
        public ILogger Log => Sink;

        public void Commit(SceneDocument document, string description) =>
            History.Commit(new DocumentVersion(document, description));

        public void Select(Core.Selection.Selection selection) => Selection = selection;

        public OperatorDispatcher Dispatcher() => new(this, Registries.Operators);
    }

    private static SceneDocument OneObject(NodeId id, string name) => new([SceneObject.WithMeta(id, name)]);

    private static Context Registered(SceneDocument document, bool canAccept = true)
    {
        var context = new Context(new SceneProvider(document, canAccept));
        new MockExtension().Register(new EditorRegistrar(context.Registries, new OwnerToken("mock")));
        return context;
    }

    private static OperatorArgs Named(string name) =>
        new(ImmutableDictionary<string, object?>.Empty.Add("name", name));

    [Test]
    public async Task dispatch_commits_a_version_and_undo_republishes_the_previous_one()
    {
        var id = NodeId.New();
        var context = Registered(OneObject(id, "crate"));
        context.Select(Core.Selection.Selection.Empty.Only(id));

        await Assert.That(context.Dispatcher().Dispatch("mock.object.rename", Named("barrel"))).IsEqualTo(OperatorResult.Finished);
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");

        context.History.Undo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("crate");
        context.History.Redo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");
    }

    // Name lives in meta and nowhere else, so a rename that reached only a field beside it would
    // save the old name back out. This is the assertion that catches that.
    [Test]
    public async Task a_rename_rewrites_the_meta_component_the_file_is_written_from()
    {
        var id = NodeId.New();
        var context = Registered(OneObject(id, "crate"));
        context.Select(Core.Selection.Selection.Empty.Only(id));

        context.Dispatcher().Dispatch("mock.object.rename", Named("barrel"));

        var meta = context.Document.Find(id)!.Meta!;
        await Assert.That(meta.Data.Value("Name")).IsEqualTo("barrel");
        await Assert.That(meta.Data.Value("Guid")).IsEqualTo(id.Value.ToString("D"));
    }

    [Test]
    public async Task an_unavailable_operator_does_not_run()
    {
        var context = Registered(OneObject(NodeId.New(), "crate"));

        await Assert.That(context.Dispatcher().Dispatch("mock.object.rename", OperatorArgs.None)).IsEqualTo(OperatorResult.Unavailable);
        await Assert.That(context.History.CanUndo).IsFalse();
    }

    // CanAccept false is the read-only in-game projection. It has to gate the COMMIT itself:
    // gating only the menu leaves a keybind or the palette able to reach the same operator.
    //
    // The two mechanisms compose here, which is the point of the test. History refuses by
    // throwing, because a host that exposed the operator anyway has a bug worth naming; the
    // dispatcher contains that throw, so the editor reports it and keeps drawing instead of
    // dying inside somebody's panel.
    [Test]
    public async Task a_read_only_provider_refuses_a_version_and_the_history_stays_empty()
    {
        var id = NodeId.New();
        var context = Registered(OneObject(id, "crate"), canAccept: false);
        context.Select(Core.Selection.Selection.Empty.Only(id));

        await Assert.That(context.Dispatcher().Dispatch("mock.object.rename", Named("barrel")))
            .IsEqualTo(OperatorResult.Failed);
        await Assert.That(context.History.CanUndo).IsFalse();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("crate");
        await Assert.That(context.Sink.Records.Single(record => record.Level == LogLevel.Error).Exception)
            .IsTypeOf<InvalidOperationException>();
    }

    // An exception escaping the dispatcher would unwind through the host's in-progress ImGui
    // frame, so the frame AFTER the bad one is the one that breaks. Containing it here is what
    // keeps a third-party panel from taking the editor down, and the report is the only trace.
    [Test]
    public async Task an_operator_that_throws_is_contained_and_reported()
    {
        var context = Registered(OneObject(NodeId.New(), "crate"));
        context.Registries.Operators.Add(new OwnerToken("mock"), new ThrowingOperator());

        await Assert.That(context.Dispatcher().Dispatch("mock.broken", OperatorArgs.None)).IsEqualTo(OperatorResult.Failed);

        var reported = context.Sink.Records.Single(record => record.Level == LogLevel.Error);
        await Assert.That(reported.Message).Contains("mock.broken");
        await Assert.That(reported.Exception).IsTypeOf<InvalidOperationException>();
    }

    // Constructed WITHOUT an explicit logger, which is how a host that already has one would do
    // it: the dispatcher must still report, through the context's.
    [Test]
    public async Task an_unknown_id_is_reported_rather_than_ignored()
    {
        var context = Registered(OneObject(NodeId.New(), "crate"));

        await Assert.That(context.Dispatcher().Dispatch("mock.nothing.registered", OperatorArgs.None)).IsEqualTo(OperatorResult.Unavailable);
        await Assert.That(context.Sink.MessagesAtLeast(LogLevel.Warning).Single()).Contains("mock.nothing.registered");
    }

    [Test]
    public async Task removing_an_owner_removes_everything_it_registered()
    {
        var registries = new EditorRegistries();
        var owner = new OwnerToken("mock");
        new MockExtension().Register(new EditorRegistrar(registries, owner));
        await Assert.That(registries.Operators.Entries).Count().IsEqualTo(1);
        await Assert.That(registries.Windows.Entries).Count().IsEqualTo(1);
        await Assert.That(registries.Menus.Entries).Count().IsEqualTo(1);

        registries.RemoveOwner(owner);
        await Assert.That(registries.Operators.Entries).IsEmpty();
        await Assert.That(registries.Windows.Entries).IsEmpty();
        await Assert.That(registries.Menus.Entries).IsEmpty();
    }
}
