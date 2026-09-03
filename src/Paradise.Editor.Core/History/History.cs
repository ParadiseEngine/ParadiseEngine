using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.History;

/// <summary>The reference <see cref="IHistory"/>: versions and file operations in one list,
/// published through the scene provider.</summary>
/// <remarks>
/// <para>
/// Every document step goes out through <see cref="ISceneProvider.Accept"/>, which is what makes
/// one history serve both hosts: standalone the provider holds the file's document, in-game it
/// applies the version to the live world. Nothing here keeps its own copy to diverge from that.
/// </para>
/// <para>
/// Deliberately without a byte budget or grouping yet; those are the first things E1 adds, and
/// the shape here is what they extend.
/// </para>
/// </remarks>
public sealed class History(ISceneProvider scene, IFileSystem fileSystem, long budgetBytes = History.DefaultBudgetBytes) : IHistory
{
    /// <summary>How much of the undo stack to keep. Generous, because the whole point of
    /// structural sharing is that a property edit costs one object and its spine rather than a
    /// copy of the document.</summary>
    public const long DefaultBudgetBytes = 64L * 1024 * 1024;

    // Estimates, and deliberately over-estimates: a budget that runs slightly short trims one
    // extra step, while one that runs over is a memory leak with a number on it. Measuring the
    // real cost would mean walking every component table on every commit, on the frame a user is
    // dragging something.
    private const long ObjectBytes = 512;
    private const long ComponentBytes = 256;

    private readonly long _budgetBytes = budgetBytes > 0
        ? budgetBytes
        : throw new ArgumentOutOfRangeException(nameof(budgetBytes), budgetBytes, "The history budget must be positive.");

    private readonly List<long> _costs = [];
    private long _total;
    // Read through the field, never the parameter: a primary-constructor parameter used BOTH in a
    // field initializer and in a method body is CS9124.
    private readonly ISceneProvider _scene = scene;
    private readonly SceneDocument _initial = scene.Current;
    private readonly List<IHistoryEntry> _entries = [];
    private int _cursor;

    public bool CanUndo => _cursor > 0;

    public bool CanRedo => _cursor < _entries.Count;

    /// <summary>What the kept steps are estimated to cost.</summary>
    public long EstimatedBytes => _total;

    public void Commit(IHistoryEntry entry)
    {
        // Published BEFORE the entry is recorded: a provider that refuses must leave the history
        // exactly as it was, not holding a step that never happened.
        if (entry is DocumentVersion version) Publish(version.Document);

        var cost = Estimate(entry, LatestDocumentBefore(_cursor) ?? _initial);

        Drop(_cursor, _entries.Count - _cursor);
        _entries.Add(entry);
        _costs.Add(cost);
        _total += cost;
        _cursor = _entries.Count;

        Trim();
    }

    /// <summary>Forget the oldest steps until the budget is met.</summary>
    /// <remarks>From the FRONT, and never past the cursor: the steps between the cursor and the
    /// end are the redo stack, and dropping those to save memory would undo a user's redo while
    /// they were looking at it. A single step larger than the whole budget is kept anyway —
    /// refusing to remember an edit is worse than exceeding a number nobody chose exactly.</remarks>
    private void Trim()
    {
        while (_total > _budgetBytes && _cursor > 1)
        {
            _total -= _costs[0];
            _costs.RemoveAt(0);
            _entries.RemoveAt(0);
            _cursor--;
        }
    }

    private void Drop(int index, int count)
    {
        for (var i = index; i < index + count; i++) _total -= _costs[i];
        _entries.RemoveRange(index, count);
        _costs.RemoveRange(index, count);
    }

    /// <summary>What this step ADDS, not what the document weighs.</summary>
    /// <remarks>The difference is the whole reason snapshot undo is affordable: an edit to one
    /// object produces a document sharing every other object with the previous version, so only
    /// the objects that are not reference-equal are new memory. Charging each version for the
    /// whole document would make a hundred versions of a thousand-object scene look a hundred
    /// times more expensive than it is, and trim a history that costs almost nothing.</remarks>
    private static long Estimate(IHistoryEntry entry, SceneDocument previous)
    {
        if (entry is not DocumentVersion version) return ObjectBytes;

        var shared = new HashSet<SceneObject>(ReferenceEqualityComparer.Instance);
        foreach (var existing in previous.Objects) shared.Add(existing);

        var cost = 0L;
        foreach (var candidate in version.Document.Objects)
        {
            if (shared.Contains(candidate)) continue;
            cost += ObjectBytes + candidate.Components.Count * ComponentBytes;
        }
        return cost;
    }

    public IHistoryEntry? Undo()
    {
        if (!CanUndo) return null;
        var index = _cursor - 1;
        var entry = _entries[index];
        switch (entry)
        {
            case DocumentVersion:
                Publish(LatestDocumentBefore(index) ?? _initial);
                break;
            case IReversibleFileOperation operation:
                operation.Revert(fileSystem);
                break;
        }

        // Moved only once the side effect succeeded. A Revert that throws — the file deleted
        // outside the editor, a read-only mount — would otherwise leave the cursor past a step
        // that is still applied, and the next Redo would Reapply something never reverted.
        _cursor = index;
        return entry;
    }

    public IHistoryEntry? Redo()
    {
        if (!CanRedo) return null;
        var entry = _entries[_cursor];
        switch (entry)
        {
            case DocumentVersion version:
                Publish(version.Document);
                break;
            case IReversibleFileOperation operation:
                operation.Reapply(fileSystem);
                break;
        }

        _cursor++;
        return entry;
    }

    private void Publish(SceneDocument document)
    {
        if (!_scene.CanAccept)
        {
            throw new InvalidOperationException(
                "The scene provider is read-only. A host reporting CanAccept false must expose no "
                + "operators that commit a document version.");
        }
        _scene.Accept(document);
    }

    private SceneDocument? LatestDocumentBefore(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (_entries[i] is DocumentVersion version) return version.Document;
        }
        return null;
    }
}
