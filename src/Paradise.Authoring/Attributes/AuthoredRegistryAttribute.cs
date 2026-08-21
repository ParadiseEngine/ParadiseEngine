using System;

namespace Paradise.Authoring;

/// <summary>
/// Asks the generator to emit <c>AuthoredComponents</c> — the registry that materializes this
/// assembly's <c>[Authored]</c> records from exported payloads — in this assembly:
///
/// <code>
/// [assembly: AuthoredRegistry]
/// </code>
///
/// It is an opt-in, not a default, because the registry is PUBLIC surface: an assembly that
/// only publishes a schema for editors declares [Authored] types with no business shipping a
/// loader for them.
///
/// Paradise.Export used to be the example of exactly that, and is no longer: schema v3 removed
/// the typed component slots, so the engine's own components arrive as payloads like everyone
/// else's and something has to read them. It opts in.
///
/// The generated readers parse the payloads directly, so there is no JsonSerializerContext to
/// declare, no [JsonSerializable] line per record, and nothing to forget.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AuthoredRegistryAttribute : Attribute;
