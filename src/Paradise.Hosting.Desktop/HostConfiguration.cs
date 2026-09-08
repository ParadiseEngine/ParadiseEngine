using Paradise.Features;
using Zio;

namespace Paradise.Hosting;

/// <summary>Loads one feature switchboard with file, environment and command-line overrides.</summary>
public static class HostConfiguration
{
    public static FeatureSwitches Load(IFileSystem fileSystem, UPath path, string? environment, string? commandLine)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var configuration = fileSystem.FileExists(path)
            ? TomlEngineConfiguration.Read(fileSystem.ReadAllText(path))
            : EngineConfiguration.Empty;
        configuration = configuration.Merge(new EngineConfiguration { Features = FeatureOverrides.Parse(environment) });
        configuration = configuration.Merge(new EngineConfiguration { Features = FeatureOverrides.Parse(commandLine) });
        var switches = new FeatureSwitches();
        switches.Apply(configuration);
        return switches;
    }
}
