using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Features;

/// <summary>The read half of the engine's feature configuration: which features this build has,
/// and whether each is on RIGHT NOW.
///
/// <para>This is the abstraction a subsystem depends on. A renderer, an ECS schedule or a debug
/// UI takes an <see cref="IFeatureSwitches"/> and asks; it never learns where the answer came
/// from — a declaration's default, a config file, an environment variable, a command-line flag,
/// or a switch a debug panel flipped a frame ago. The same reason every engine library takes an
/// <c>ILogger</c> and not a console.</para>
///
/// <para>Ask every time you would act on the answer. <see cref="IsEnabled"/> is a dictionary
/// lookup and is meant to be called per frame; caching it in a field is how a runtime toggle
/// stops working.</para></summary>
public interface IFeatureSwitches
{
    /// <summary>Whether <paramref name="id"/> is on. A feature nothing declared and nothing
    /// overrode is off — a build without a feature answers the same way as a build that turned
    /// it off, which is what a caller can actually act on.</summary>
    bool IsEnabled(FeatureId id);

    /// <summary>What the build declared about <paramref name="id"/>, if anything.</summary>
    bool TryGetDefinition(FeatureId id, [MaybeNullWhen(false)] out FeatureDefinition definition);

    /// <summary>Every feature this build declared, for a listing, a debug UI, or a config file
    /// written from what actually exists rather than from memory.</summary>
    IReadOnlyCollection<FeatureDefinition> Definitions { get; }

    /// <summary>Raised when a feature's effective state changes, with its new state.
    ///
    /// <para>Handlers run on the thread that made the change, and while writes are held off — so
    /// the last thing a handler was told about a feature is what <see cref="IsEnabled"/> now
    /// answers for it. That is what lets a handler ACT on the announcement: release a target,
    /// retract a plan, zero a buffer somebody else binds every frame.</para></summary>
    event Action<FeatureId, bool>? Changed;
}
