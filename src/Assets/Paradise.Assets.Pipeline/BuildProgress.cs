namespace Paradise.Assets.Pipeline;

/// <summary>The phases of a rebuild, in the order they run.</summary>
public enum BuildStage
{
    /// <summary>Sidecars and mesh references brought up to date before a watch rebuild.</summary>
    Sidecars,

    /// <summary>The tree checked the way <c>assets verify</c> checks it.</summary>
    Verify,

    /// <summary>Each source reused from the index or offered to its importer.</summary>
    Assets,

    /// <summary>Stale outputs swept, the index and the manifest written.</summary>
    Finish,
}

/// <summary>Where a build is: <paramref name="Done"/> of <paramref name="Total"/> steps of
/// <paramref name="Stage"/> are finished and <paramref name="Current"/> (assets-relative) is
/// being worked on. A stage without countable steps reports a <paramref name="Total"/> of 0.</summary>
public readonly record struct BuildProgress(BuildStage Stage, int Done, int Total, string? Current);
