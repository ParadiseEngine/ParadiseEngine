using System.Collections.Immutable;
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

    private sealed class Context(IHistory history) : IOperatorContext, IOperatorDispatcher
    {
        public EditorRegistries Registries { get; } = new();
        public SceneDocument Document => history.Current;
        public Core.Selection.Selection Selection { get; private set; } = Core.Selection.Selection.Empty;
        public IHistory History => history;
        public IHostCapabilities Host { get; } = new Host();

        public void Commit(SceneDocument document, string description) => history.Commit(new DocumentVersion(document, description));
        public void Select(Core.Selection.Selection selection) => Selection = selection;

        public IOperator? Find(string id) => Registries.Operators.Entries.FirstOrDefault(op => op.Id == id);

        public OperatorResult Dispatch(string id, OperatorArgs args) =>
            Find(id) is { } op && op.IsAvailable(this) ? op.Execute(this, args) : OperatorResult.Unavailable;
    }

    private static SceneDocument OneObject(NodeId id, string name) =>
        new([new SceneObject(id, name, null, ImmutableList<SceneComponent>.Empty)]);

    [Test]
    public async Task dispatch_commits_a_version_and_undo_republishes_the_previous_one()
    {
        var id = NodeId.New();
        var context = new Context(new History(OneObject(id, "crate"), new MemoryFileSystem()));
        new MockExtension().Register(new EditorRegistrar(context.Registries, new OwnerToken("mock")));
        context.Select(Core.Selection.Selection.Empty.Only(id));

        var args = new OperatorArgs(ImmutableDictionary<string, object?>.Empty.Add("name", "barrel"));
        await Assert.That(context.Dispatch("mock.object.rename", args)).IsEqualTo(OperatorResult.Finished);
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");

        context.History.Undo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("crate");
        context.History.Redo();
        await Assert.That(context.Document.Find(id)!.Name).IsEqualTo("barrel");
    }

    [Test]
    public async Task an_unavailable_operator_does_not_run()
    {
        var context = new Context(new History(OneObject(NodeId.New(), "crate"), new MemoryFileSystem()));
        new MockExtension().Register(new EditorRegistrar(context.Registries, new OwnerToken("mock")));

        await Assert.That(context.Dispatch("mock.object.rename", OperatorArgs.None)).IsEqualTo(OperatorResult.Unavailable);
        await Assert.That(context.History.CanUndo).IsFalse();
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
