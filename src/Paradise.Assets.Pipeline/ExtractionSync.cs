namespace Paradise.Assets.Pipeline;

/// <summary>What to do with a part whose container side and document side may both have moved.</summary>
public enum SyncAction
{
    /// <summary>Both sides are what was recorded. Nothing to write.</summary>
    Unchanged,

    /// <summary>Nothing is there yet: write what the container extracts to, and record both sides as it.</summary>
    Create,

    /// <summary>Write what the container extracts to over the file, and record both sides as it.</summary>
    TakeSource,

    /// <summary>
    /// The document moved and the container did not. Keep the file and write its expressible half
    /// back into the container: a format that CAN do that records both sides as the document, since
    /// the container then reads as it. One that cannot keeps the recorded pair, leaving the
    /// divergence visible for the re-export that will make it a conflict.
    /// </summary>
    TakeDocument,

    /// <summary>
    /// Both sides moved and the author said to keep the document. Distinct from
    /// <see cref="TakeDocument"/> because the answer for a format that cannot write back is the
    /// opposite: there the divergence is deliberate and now RESOLVED, so both current fingerprints
    /// are recorded. Keeping the old pair would re-raise the same conflict on every later run and
    /// leave `--take-document` unable to settle it at all.
    /// </summary>
    ResolveToDocument,

    /// <summary>A file no sync recorded that already holds what the container extracts to: record it as this container's, writing nothing.</summary>
    Adopt,

    /// <summary>A file no sync recorded that does NOT match, taken as it stands: recorded with the two sides as they are, so the difference stays visible.</summary>
    AdoptAsIs,

    /// <summary>Nothing decided — <see cref="SyncOutcome.Problem"/> says why, and the part must not be recorded.</summary>
    Refuse,
}

/// <param name="Note">Why, for the line <c>extract</c> prints; null when there is nothing to say.</param>
/// <param name="Problem">Set only for <see cref="SyncAction.Refuse"/>.</param>
public sealed record SyncOutcome(SyncAction Action, string? Note = null, string? Problem = null);

/// <summary>
/// The rule for keeping an extracted file and the container it came from in step. Pure: it decides,
/// the caller writes.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of extraction that is genuinely hard to get right, and it is not any format's:
/// given each side's fingerprint now and the pair recorded at the last sync, a re-export is told
/// from an edit, and both at once is a conflict a flag resolves. A game's extractor gets it by
/// calling this instead of reimplementing six branches and their edge cases.
/// </para>
/// <para>
/// It returns the decision and not the fingerprints to record, because those do not follow from the
/// decision alone: after <see cref="SyncAction.TakeDocument"/> a format that can write back has both
/// sides reading as the document, and one that cannot still has two. What the caller can do is the
/// caller's to know.
/// </para>
/// </remarks>
public static class ExtractionSync
{
    /// <param name="sourceSide">Fingerprint of what the container extracts to NOW.</param>
    /// <param name="documentSide">Fingerprint of the file on disk now, or null when there is no file.</param>
    /// <param name="recorded">The part as the sidecar records it, or null when no sync ever recorded one.</param>
    /// <param name="noun">What the file is, for the conflict message: "material", "image".</param>
    public static SyncOutcome Decide(string sourceSide, string? documentSide, ExtractedPart? recorded, ConflictResolution resolution, string noun = "file")
        => Decide(sourceSide, documentSide, recorded?.SourceFingerprint, recorded?.DocumentFingerprint, resolution, noun);

    /// <summary>As above, against a recorded fingerprint pair directly; both null means no sync recorded this file.</summary>
    public static SyncOutcome Decide(
        string sourceSide, string? documentSide, string? recordedSource, string? recordedDocument, ConflictResolution resolution, string noun = "file")
    {
        ArgumentNullException.ThrowIfNull(sourceSide);
        ArgumentNullException.ThrowIfNull(noun);

        if (documentSide is null) return new SyncOutcome(SyncAction.Create);

        // A file at the extraction path that no sync recorded: another container's output, or the
        // author's. Adopted when it already holds what this container extracts to; otherwise a flag
        // has to name a side, because recording it would make it this container's on the next
        // re-export — and for an image, bind the container to pixels that are not its own.
        if (recordedSource is null && recordedDocument is null)
        {
            if (sourceSide == documentSide) return new SyncOutcome(SyncAction.Adopt, "exists with what the container extracts to; adopted");

            return resolution switch
            {
                ConflictResolution.TakeGlb => new SyncOutcome(SyncAction.TakeSource, "existed and was not extracted by this tool: took the container's"),
                ConflictResolution.TakeDocument => new SyncOutcome(SyncAction.AdoptAsIs, "existed and was not extracted by this tool: adopted as is"),
                _ => new SyncOutcome(SyncAction.Refuse, Problem:
                    "exists and was not extracted by this tool, and differs from what the container extracts to; delete it, or re-run with `--take-glb` to overwrite it or `--take-document` to adopt it"),
            };
        }

        return (recordedSource != sourceSide, recordedDocument != documentSide) switch
        {
            (false, false) => new SyncOutcome(SyncAction.Unchanged),
            (true, false) => new SyncOutcome(SyncAction.TakeSource, "re-extracted: the container changed"),
            (false, true) => new SyncOutcome(SyncAction.TakeDocument, "written back into the container"),
            _ => resolution switch
            {
                ConflictResolution.TakeGlb => new SyncOutcome(SyncAction.TakeSource, "conflict: took the container's"),
                ConflictResolution.TakeDocument => new SyncOutcome(SyncAction.ResolveToDocument, "conflict: kept the document's"),
                _ => new SyncOutcome(SyncAction.Refuse, Problem:
                    $"both the container and the extracted {noun} changed since they were last in step; re-run with `--take-glb` or `--take-document`"),
            },
        };
    }
}
