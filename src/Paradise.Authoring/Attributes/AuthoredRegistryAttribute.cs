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
/// It is an opt-in, not a default, because the registry is PUBLIC surface: an assembly that only
/// publishes a schema for editors (Paradise.Export itself is one) declares [Authored] types but
/// has no business shipping a loader for them.
///
/// The generated readers parse the payloads directly, so there is no JsonSerializerContext to
/// declare, no [JsonSerializable] line per record, and nothing to forget.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AuthoredRegistryAttribute : Attribute;
