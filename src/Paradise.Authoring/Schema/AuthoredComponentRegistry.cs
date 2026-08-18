using System.Text.Json;

namespace Paradise.Authoring;

/// <summary>
/// Turns an exported payload back into the <c>[Authored]</c> record it was written from.
///
/// The other half of the mechanism. Authoring goes record → schema → editor → JSON; loading has to
/// come back the same way, or a component can be authored, exported, and then silently never read
/// — which is exactly what happened to Pingu's ice ledge, whose payload only a test ever looked at.
///
/// An implementation is GENERATED per assembly from its <c>[Authored]</c> types, so filling an
/// instance is no longer a hand-written accessor per component that someone has to remember.
/// </summary>
public interface IAuthoredComponentRegistry
{
    /// <summary>Component ids this registry can materialize.</summary>
    IReadOnlyCollection<string> ComponentIds { get; }

    /// <summary>
    /// Deserialize a payload into its record, or false when the id is not one of ours.
    ///
    /// <c>object</c> rather than a generic: the caller has an id at runtime, not a type at compile
    /// time, and every authored record is a class — so there is nothing to box.
    /// </summary>
    bool TryRead(string componentId, JsonElement data, out object? component);
}
