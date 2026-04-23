// Polyfills for attributes / types not present on netstandard2.1.
// Compiled only when the target framework lacks them.
#if NETSTANDARD2_1
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class UnscopedRefAttribute : Attribute { }
}

namespace System.Runtime.CompilerServices
{
    // Required by C# records' init-only setters when targeting netstandard2.1.
    internal static class IsExternalInit { }
}
#endif
