// Workaround for Coyote 1.7.11 (fixed upstream in microsoft/coyote#540):
// .NET 11's System.Text.Json source generator emits [UnsafeAccessor] extern methods with no IL body,
// which crashes Coyote 1.7.11's CallSiteExtractionRewritingPass with ArgumentNullException ("target").
// Coyote's rewriter matches SkipRewritingAttribute by full type name, so declaring it internally here
// skips pure JSON-context types without adding a Microsoft.Coyote package dependency.
// Remove once ParadiseEngine upgrades to a Coyote release containing microsoft/coyote#540.
namespace Microsoft.Coyote.Rewriting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class SkipRewritingAttribute : Attribute;
