using System.Text.Json;

namespace Paradise.Authoring;

/// <summary>Materializes exported payloads into the assembly's authored records.</summary>
/// <remarks>Implementations are generated from <c>[Authored]</c> types when the assembly opts in.</remarks>
public interface IAuthoredComponentRegistry
{
    /// <summary>Component ids this registry can materialize.</summary>
    IReadOnlyCollection<Guid> ComponentIds { get; }

    /// <summary>
    /// Deserialize a payload into its record, or false when the id is not one of ours.
    ///
    /// <c>object</c> rather than a generic: the caller has an id at runtime, not a type at compile
    /// time, and every authored record is a class — so there is nothing to box.
    /// </summary>
    bool TryRead(Guid id, JsonElement data, out object? component);

    /// <summary>
    /// The same, resolved by fully qualified CLR type name instead — the FALLBACK for a payload
    /// whose <see cref="Guid"/> this registry does not recognize.
    ///
    /// It exists because a GUID is unreadable and therefore unrecoverable by hand. A document
    /// written before a component was given its id, or by a host that got the id wrong, is
    /// otherwise a dead payload nobody can diagnose: the JSON names a number, and nothing in the
    /// build knows what that number was supposed to mean. Carrying the type name alongside costs
    /// one string per component and turns that into a warning plus a correct load.
    ///
    /// Deliberately second, never first. The name is a repair path, not an identity — resolving by
    /// it preferentially would reintroduce exactly the rename fragility the GUID replaced.
    /// </summary>
    bool TryReadByType(string fullTypeName, JsonElement data, out object? component);
}
