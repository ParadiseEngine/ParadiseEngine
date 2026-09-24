using System.Text.Json.Nodes;

namespace Paradise.Export.Tests;

// TUnit globals ([Test], Assert) come from the package's implicit global usings.
public class ParadiseExportInfoTests
{
    [Test]
    public async Task describe_returns_non_empty_json_object()
    {
        // JsonNode.Parse throws on invalid/empty input, so reaching the assertion already proves
        // the output parses; assert on property count so an empty "{}" would still fail.
        JsonNode parsed = JsonNode.Parse(ParadiseExportInfo.Describe())!;
        await Assert.That(parsed.AsObject().Count).IsGreaterThan(0);
    }

    [Test]
    public async Task describe_reports_tool_and_version()
    {
        JsonNode info = JsonNode.Parse(ParadiseExportInfo.Describe())!;
        await Assert.That((string?)info["tool"]).IsEqualTo("Paradise.Export");
        await Assert.That((string?)info["version"]).IsEqualTo(ParadiseExportInfo.Version);
    }

    [Test]
    public async Task describe_resolves_dependency_assemblies_at_runtime()
    {
        // Non-null version strings prove System.Text.Json and DotRecast actually load at runtime,
        // not merely that they compiled.
        JsonNode info = JsonNode.Parse(ParadiseExportInfo.Describe())!;
        await Assert.That(string.IsNullOrWhiteSpace((string?)info["systemTextJson"])).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace((string?)info["dotRecast"])).IsFalse();
    }
}
