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
/// panel dispatches it by id, the operator commits a new document version, undo republishes the
/// previous one, and removing the owner removes everything it registered.</summary>
public class SkeletonFlowTests
{
    private sealed class RenameOperator : IOperator
    {
        public string Id => "mock.object.rename";
        public string Label => "Rename";
        public string Description => "Renames the primary selected object.";

        public bool IsAvailable(IOperatorContext context) =>
            context.Host.CanEditDocument && context.Selection.Primary is not null;

        public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
        {
            var target = context.Document.Find(context.Selection.Primary!.Value)!;
            context.Commit(context.Document.Replace(target with { Name = args.Get<string>("name") }), $"Rename {target.Name}");
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

    private sealed class Context(IHistory history) : IOperatorContext
    {
        public EditorRegistries Registries { get; } = new();

        /// <summary>A host picks the sink; here that is the thing the assertions read back.</summary>
        public CollectingLogger Sink { get; } = new();

        public SceneDocument Document => history.Current;
        public Core.Selection.Selection Selection { get; private set; } = Core.Selection.Selection.Empty;
        public IHistory History => history;
        public IHostCapabilities Host { get; } = new Host();
        public ILogger Log => Sink;

        public void Commit(SceneDocument document, string description) => history.Commit(new DocumentVersion(document, description));
        public void Select(Core.Selection.Selection selection) => Selection = selection;

        public OperatorDispatcher Dispatcher() => new(this, Registries.Operators, Sink);
    }

    private static SceneDocument OneObject(NodeId id, string name) =>
        new([new SceneObject(id, name, null, ImmutableList<SceneComponent>.Empty)]);

    private static Context Registered(SceneDocument document)
    {
        var context = new Context(new History(document, new MemoryFileSystem()));
        new MockExtension().Register(new EditorRegistrar(context.Registries, new OwnerToken("mock")));
        return context;
    }

    [Test]
    public async Task dispatch_commits_a_version_and_undo_republishes_the_previous_one()
    {
        var id = NodeId.New();
        var context = Registered(OneObject(id, "crate"));
        context.Select(Core.Selection.Selection.Empty.Only(id));

        var args = new OperatorArgs(ImmutableDictionary<string, object?>.Empty.Add("name", "barrel"));
        await Assert.That(context.Dispatcher().Dispatch("mock.object.rename", args)).IsEqualTo(OperatorResult.Finished);
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");

        context.History.Undo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("crate");
        context.History.Redo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");
    }

    [Test]
    public async Task an_unavailable_operator_does_not_run()
    {
        var context = Registered(OneObject(NodeId.New(), "crate"));

        await Assert.That(context.Dispatcher().Dispatch("mock.object.rename", OperatorArgs.None)).IsEqualTo(OperatorResult.Unavailable);
        await Assert.That(context.History.CanUndo).IsFalse();
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
