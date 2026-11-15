using System.Text.RegularExpressions;

namespace Paradise.Architecture.Test;

/// <summary>Rules about what ships, how it is built and how CI covers it.</summary>
public class PackagingTests
{
    /// <summary>The CLI loads importer and tray extensions at run time, so it cannot be trimmed.</summary>
    private static readonly string[] s_dynamicLoaders = ["Paradise.Cli", "Paradise.Cli.Host"];

    [Test]
    public async Task every_package_has_a_readme_row()
    {
        var readme = Repository.ReadText("README.md");
        var missing = Packages().Where(name => !readme.Contains($"[{name}](", StringComparison.Ordinal)).ToArray();
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task readme_lists_no_package_that_does_not_exist()
    {
        var readme = Repository.ReadText("README.md");
        // Package rows link into src/; third-party rows (the Paradise.ImGui fork) link to nuget.org.
        var listed = Regex.Matches(readme, @"^\| \[(Paradise\.[A-Za-z.]+)\]\(src/", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value);
        var unknown = listed.Where(name => !Packages().Contains(name)).ToArray();
        await Assert.That(unknown).IsEmpty();
    }

    /// <summary>The README promises NativeAOT and trimming; the analyzers are what keep that true.</summary>
    [Test]
    public async Task packages_enable_aot_and_trim_analysis()
    {
        var offenders = Packages()
            .Where(name => !s_dynamicLoaders.Contains(name))
            .Where(name => Repository.Projects[name].Property("IsAotCompatible") != "true"
                || Repository.Projects[name].Property("IsTrimmable") != "true")
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>The language version is set once, in src/Directory.Build.props.</summary>
    [Test]
    public async Task projects_do_not_override_the_language_version()
    {
        var offenders = Repository.Projects.Values.Where(project => project.Property("LangVersion") is not null).Select(project => project.Name).ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task every_coyote_runner_is_explored_in_ci()
    {
        var workflow = Repository.ReadText(".github/workflows/dotnet-test.yml");
        var missing = Repository.Projects.Values
            .Where(project => project.Name.EndsWith(".CoyoteTest", StringComparison.Ordinal))
            .Select(project => Repository.Relative(Path.GetDirectoryName(project.Path)!))
            .Where(directory => !workflow.Contains($"project: {directory} ", StringComparison.Ordinal)
                && !workflow.Contains($"project: {directory}}}", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task the_local_version_is_a_development_build()
    {
        var props = Repository.ReadText("src/Directory.Build.props");
        var version = Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        // Locally packed packages must sort below the release they lead up to.
        await Assert.That(version).EndsWith("-dev", StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<string> Packages() =>
        Repository.Projects.Values.Where(project => project.IsPackable).Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
}
