using System.Collections.Immutable;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.History;
using Zio.FileSystems;

namespace Paradise.Editor.Test;

/// <summary>The undo stack's memory bound.</summary>
/// <remarks>The interesting property is not that it trims, but WHAT it charges: a version is
/// charged for the objects it does not share with the previous one, because that is what it
/// actually costs. Charging the whole document would make a hundred edits to a large scene look a
/// hundred times more expensive than they are and trim a history that costs almost nothing.</remarks>
public class HistoryBudgetTests
{
    private static SceneDocument Scene(int objects) =>
        new(Enumerable.Range(0, objects)
            .Select(i => SceneObject.WithMeta(NodeId.New(), $"object{i}"))
            .ToImmutableList());

    private static History NewHistory(SceneDocument initial, long budget) =>
        new(new InMemorySceneProvider(initial), new MemoryFileSystem(), budget);

    [Test]
    public async Task editing_one_object_costs_one_object_not_the_document()
    {
        var initial = Scene(200);
        var history = NewHistory(initial, budget: 1024 * 1024);
        var target = initial.Objects[0];

        history.Commit(new DocumentVersion(initial.Replace(target.WithName("renamed")), "rename"));

        // One changed object out of 200. A whole-document charge would be two orders larger.
        await Assert.That(history.EstimatedBytes).IsLessThan(4096L);
    }

    [Test]
    public async Task the_oldest_steps_are_dropped_once_the_budget_is_passed()
    {
        var initial = Scene(1);
        var history = NewHistory(initial, budget: 4096);
        var document = initial;

        for (var i = 0; i < 200; i++)
        {
            document = document.Replace(document.Objects[0].WithName($"name{i}"));
            history.Commit(new DocumentVersion(document, $"rename {i}"));
        }

        await Assert.That(history.EstimatedBytes).IsLessThanOrEqualTo(4096L);
        await Assert.That(history.CanUndo).IsTrue();
    }

    // Trimming must never eat the redo stack: those steps are ahead of the cursor, and dropping
    // them to save memory would undo a user's redo while they were looking at it.
    [Test]
    public async Task trimming_never_reaches_past_the_cursor()
    {
        var initial = Scene(1);
        var history = NewHistory(initial, budget: 1024 * 1024);
        var document = initial;
        for (var i = 0; i < 5; i++)
        {
            document = document.Replace(document.Objects[0].WithName($"name{i}"));
            history.Commit(new DocumentVersion(document, $"rename {i}"));
        }

        for (var i = 0; i < 4; i++) history.Undo();

        await Assert.That(history.CanRedo).IsTrue();
        await Assert.That(history.CanUndo).IsTrue();
    }

    [Test]
    public async Task a_budget_of_nothing_is_refused_rather_than_silently_keeping_nothing() =>
        await Assert.That(() => NewHistory(Scene(1), budget: 0)).Throws<ArgumentOutOfRangeException>();
}
