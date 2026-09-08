using System;

namespace Paradise.Authoring;

/// <summary>Opts an assembly into generated <c>AuthoredComponents</c> readers.</summary>
/// <remarks>Use <c>[assembly: AuthoredRegistry]</c>; schema-only assemblies need no public loader or serializer context.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AuthoredRegistryAttribute : Attribute;
