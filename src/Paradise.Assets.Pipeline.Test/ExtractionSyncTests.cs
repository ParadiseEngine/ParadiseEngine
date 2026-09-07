using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The keep-in-step rule on its own. It is the part of extraction that is hard to get right and the
/// part a game's extractor should never have to rewrite, so it is covered here directly rather than
/// only through the GLB path that happens to call it.
/// </summary>
public class ExtractionSyncTests
{
    private const string Source = "aaaa";
    private const string Document = "bbbb";

    private static ExtractedPart Recorded(string source, string document)
        => new("materials", PartOwnership.TwoSided, 0, "wood", new AssetReference(Guid.NewGuid(), "materials/wood.material"), source, document);

    [Test]
    public async Task nothing_on_disk_is_a_first_write()
    {
        var outcome = ExtractionSync.Decide(Source, documentSide: null, recorded: null, ConflictResolution.Refuse);
        await Assert.That(outcome.Action).IsEqualTo(SyncAction.Create);
    }

    [Test]
    public async Task the_four_branches_tell_a_re_export_from_an_edit()
    {
        var recorded = Recorded(Source, Document);

        await Assert.That(ExtractionSync.Decide(Source, Document, recorded, ConflictResolution.Refuse).Action)
            .IsEqualTo(SyncAction.Unchanged);

        // Only the container moved: the file is stale and is re-extracted.
        await Assert.That(ExtractionSync.Decide("changed", Document, recorded, ConflictResolution.Refuse).Action)
            .IsEqualTo(SyncAction.TakeSource);

        // Only the document moved: the edit is the author's and goes back into the container.
        await Assert.That(ExtractionSync.Decide(Source, "changed", recorded, ConflictResolution.Refuse).Action)
            .IsEqualTo(SyncAction.TakeDocument);

        // Both moved: refused rather than guessed, and the message names the way out.
        var conflict = ExtractionSync.Decide("changed", "alsochanged", recorded, ConflictResolution.Refuse, "material");
        await Assert.That(conflict.Action).IsEqualTo(SyncAction.Refuse);
        await Assert.That(conflict.Problem).Contains("material");
        await Assert.That(conflict.Problem).Contains("--take-glb");
    }

    [Test]
    public async Task a_conflict_resolves_to_whichever_side_the_flag_names()
    {
        var recorded = Recorded(Source, Document);

        await Assert.That(ExtractionSync.Decide("changed", "alsochanged", recorded, ConflictResolution.TakeGlb).Action)
            .IsEqualTo(SyncAction.TakeSource);
        // ResolveToDocument, NOT TakeDocument: the two differ for a format that cannot write back.
        // A passive divergence there holds the recorded pair so a re-export becomes the conflict it
        // is; a RESOLVED conflict records both sides as they stand, or `--take-document` could
        // never settle anything and the same conflict would return on every run.
        await Assert.That(ExtractionSync.Decide("changed", "alsochanged", recorded, ConflictResolution.TakeDocument).Action)
            .IsEqualTo(SyncAction.ResolveToDocument);
    }

    [Test]
    public async Task a_resolved_conflict_is_a_different_action_from_a_passive_divergence()
    {
        var recorded = Recorded(Source, Document);

        // Only the document moved — nobody asked for anything.
        await Assert.That(ExtractionSync.Decide(Source, "edited", recorded, ConflictResolution.Refuse).Action)
            .IsEqualTo(SyncAction.TakeDocument);

        // Both moved and the author named a side. Same "keep the document" intent, but the caller
        // has to be able to tell them apart, so it is not carried in the note.
        await Assert.That(ExtractionSync.Decide("changed", "edited", recorded, ConflictResolution.TakeDocument).Action)
            .IsEqualTo(SyncAction.ResolveToDocument);
    }

    [Test]
    public async Task a_file_no_sync_recorded_is_adopted_only_when_it_already_matches()
    {
        // Adopting a file that matches is safe: it IS what the container extracts to.
        var matching = ExtractionSync.Decide(Source, Source, recorded: null, ConflictResolution.Refuse);
        await Assert.That(matching.Action).IsEqualTo(SyncAction.Adopt);

        // One that does not match is refused, because recording it would make it this container's
        // on the next re-export — for an image, binding the container to someone else's pixels.
        var foreign = ExtractionSync.Decide(Source, Document, recorded: null, ConflictResolution.Refuse);
        await Assert.That(foreign.Action).IsEqualTo(SyncAction.Refuse);
        await Assert.That(foreign.Problem).Contains("was not extracted by this tool");

        await Assert.That(ExtractionSync.Decide(Source, Document, recorded: null, ConflictResolution.TakeGlb).Action)
            .IsEqualTo(SyncAction.TakeSource);

        // Adopted as it stands: the two sides stay different on purpose, so the difference is still
        // visible in the record rather than being flattened into agreement.
        await Assert.That(ExtractionSync.Decide(Source, Document, recorded: null, ConflictResolution.TakeDocument).Action)
            .IsEqualTo(SyncAction.AdoptAsIs);
    }

    [Test]
    public async Task the_decision_carries_no_fingerprints_because_write_back_is_the_callers_to_know()
    {
        // TakeDocument means "keep the file and put it back into the container". A format that can
        // do that ends with both sides reading as the document; one that cannot still has two. The
        // rule cannot say which, so it says neither — see the GLB material and image paths, which
        // record differently from the same action.
        var outcome = ExtractionSync.Decide(Source, "changed", Recorded(Source, Document), ConflictResolution.Refuse);

        await Assert.That(outcome.Action).IsEqualTo(SyncAction.TakeDocument);
        await Assert.That(outcome.Note).IsNotNull();
        await Assert.That(outcome.Problem).IsNull();
    }
}
