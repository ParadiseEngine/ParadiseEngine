using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>The freshness gate: a Play with nothing changed must not pay for MSBuild, and a changed source anywhere in the restore closure must.</summary>
public class HostFreshnessTests
{
    private static readonly UPath s_csproj = "/repo/Game.Launcher/Game.Launcher.csproj";
    private static readonly UPath s_output = "/repo/Game.Launcher/bin/Debug/net10.0/Game.Launcher.dll";
    private static readonly UPath s_assets = "/repo/Game.Launcher/obj/project.assets.json";
    private static readonly UPath s_stamp = "/repo/Game.Launcher/obj/paradise-host.stamp";

    private static readonly DateTime s_restored = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime s_built = s_restored.AddMinutes(10);

    private static MemoryFileSystem Built()
    {
        var fileSystem = new MemoryFileSystem();
        Write(fileSystem, s_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>", s_restored.AddMinutes(-5));
        Write(fileSystem, "/repo/Game.Launcher/Program.cs", "class P { static void Main() {} }", s_restored);
        Write(fileSystem, "/repo/Game.Core/Game.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />", s_restored.AddMinutes(-5));
        Write(fileSystem, "/repo/Game.Core/Sim.cs", "class Sim {}", s_restored);
        Write(fileSystem, "/engine/src/Paradise.ECS/Paradise.ECS.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />", s_restored.AddMinutes(-5));
        Write(fileSystem, "/engine/src/Paradise.ECS/World.cs", "class World {}", s_restored);
        Write(fileSystem, "/repo/Directory.Build.props", "<Project />", s_restored.AddMinutes(-5));
        Write(fileSystem, s_assets, """
            {
              "version": 3,
              "libraries": {
                "Game.Core/1.0.0": { "type": "project", "path": "../Game.Core/Game.Core.csproj", "msbuildProject": "../Game.Core/Game.Core.csproj" },
                "Paradise.ECS/0.39.0": { "type": "project", "path": "../../engine/src/Paradise.ECS/Paradise.ECS.csproj", "msbuildProject": "../../engine/src/Paradise.ECS/Paradise.ECS.csproj" },
                "Zio/0.24.0": { "type": "package", "path": "zio/0.24.0" }
              },
              "project": { "frameworks": { "net10.0": { } } }
            }
            """, s_restored);
        // The dll is OLDER than the stamp on purpose: an incremental build that recompiled only
        // a library leaves the launcher's dll alone, and that must still read as fresh.
        Write(fileSystem, s_output, "MZ", s_built.AddMinutes(-30));
        Write(fileSystem, s_stamp, "2026-09-01T12:10:00Z", s_built);
        return fileSystem;
    }

    private static void Write(IFileSystem fileSystem, UPath path, string text, DateTime stamp)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, text);
        fileSystem.SetLastWriteTime(path, stamp);
    }

    [Test]
    public async Task an_untouched_tree_is_fresh_and_owes_no_restore()
    {
        using var fileSystem = Built();

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsTrue();
        await Assert.That(freshness.NeedsRestore).IsFalse();
        await Assert.That(freshness.Output).IsEqualTo(s_output);
        await Assert.That(freshness.ProjectDirectories).IsEquivalentTo(new UPath[]
        {
            "/repo/Game.Launcher", "/repo/Game.Core", "/engine/src/Paradise.ECS",
        });
    }

    [Test]
    public async Task a_source_edit_in_a_project_the_csproj_never_names_is_stale_without_a_restore()
    {
        // The engine project reaches the launcher through a Directory.Build.targets injection;
        // it is in project.assets.json and nowhere in the csproj, and it is the whole reason the
        // closure is read from restore's output rather than parsed from the project file.
        using var fileSystem = Built();
        fileSystem.SetLastWriteTime("/engine/src/Paradise.ECS/World.cs", s_built.AddMinutes(1));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsFalse();
        await Assert.That(freshness.SourcesChanged).IsTrue();
        await Assert.That(freshness.NeedsRestore).IsFalse();
    }

    [Test]
    public async Task a_project_file_edit_is_stale_and_owes_a_restore()
    {
        using var fileSystem = Built();
        fileSystem.SetLastWriteTime("/repo/Game.Core/Game.Core.csproj", s_built.AddMinutes(1));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsFalse();
        await Assert.That(freshness.NeedsRestore).IsTrue();
    }

    [Test]
    public async Task an_ancestor_directory_build_file_counts()
    {
        using var fileSystem = Built();
        fileSystem.SetLastWriteTime("/repo/Directory.Build.props", s_built.AddMinutes(1));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsFalse();
        await Assert.That(freshness.NeedsRestore).IsTrue();
    }

    [Test]
    public async Task outputs_under_bin_and_obj_never_make_a_tree_stale()
    {
        // A build writes generated .cs under obj/ AFTER the dll's stamp would otherwise be taken;
        // counting them would make every build report itself stale.
        using var fileSystem = Built();
        Write(fileSystem, "/repo/Game.Core/obj/Debug/net10.0/Game.Core.AssemblyInfo.cs", "// generated", s_built.AddMinutes(1));
        Write(fileSystem, "/repo/Game.Launcher/bin/Debug/net10.0/Game.Core.dll", "MZ", s_built.AddMinutes(1));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsTrue();
    }

    [Test]
    public async Task a_build_made_elsewhere_leaves_no_stamp_and_costs_one_build()
    {
        using var fileSystem = Built();
        fileSystem.DeleteFile(s_stamp);

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsFalse();
        await Assert.That(freshness.HasStamp).IsFalse();
        await Assert.That(freshness.OutputExists).IsTrue();
        await Assert.That(freshness.NeedsRestore).IsFalse();
    }

    [Test]
    public async Task stamping_after_a_build_makes_the_edited_tree_fresh_again()
    {
        using var fileSystem = Built();
        fileSystem.SetLastWriteTime("/repo/Game.Core/Sim.cs", s_built.AddMinutes(1));
        await Assert.That(HostFreshness.Inspect(fileSystem, s_csproj, "Debug").IsFresh).IsFalse();

        HostFreshness.Stamp(fileSystem, s_csproj);

        await Assert.That(HostFreshness.Inspect(fileSystem, s_csproj, "Debug").IsFresh).IsTrue();
    }

    [Test]
    public async Task a_never_built_launcher_is_stale_and_owes_a_restore()
    {
        using var fileSystem = Built();
        fileSystem.DeleteFile(s_output);
        fileSystem.DeleteFile(s_assets);
        fileSystem.DeleteFile(s_stamp);

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.IsFresh).IsFalse();
        await Assert.That(freshness.OutputExists).IsFalse();
        await Assert.That(freshness.NeedsRestore).IsTrue();
        await Assert.That(freshness.Output).IsNull();
    }

    [Test]
    public async Task a_declared_assembly_name_names_the_output()
    {
        using var fileSystem = Built();
        fileSystem.WriteAllText(s_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>game</AssemblyName></PropertyGroup></Project>");
        fileSystem.SetLastWriteTime(s_csproj, s_restored.AddMinutes(-5));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.Output).IsEqualTo((UPath)"/repo/Game.Launcher/bin/Debug/net10.0/game.dll");
        await Assert.That(freshness.OutputExists).IsFalse();
    }

    [Test]
    public async Task a_restored_runtime_identifier_moves_the_output_one_directory_down()
    {
        // The SDK appends <rid>/ to the output path when a RuntimeIdentifier is set; a flat
        // probe would report the launcher never built and rebuild on every Play.
        using var fileSystem = Built();
        fileSystem.WriteAllText(s_assets, """
            {
              "libraries": {},
              "project": { "frameworks": { "net10.0": { } }, "runtimes": { "osx-arm64": { "#import": [] } } }
            }
            """);
        fileSystem.SetLastWriteTime(s_assets, s_restored);
        fileSystem.DeleteFile(s_output);
        Write(fileSystem, "/repo/Game.Launcher/bin/Debug/net10.0/osx-arm64/Game.Launcher.dll", "MZ", s_built.AddMinutes(-30));

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Debug");

        await Assert.That(freshness.Output).IsEqualTo((UPath)"/repo/Game.Launcher/bin/Debug/net10.0/osx-arm64/Game.Launcher.dll");
        await Assert.That(freshness.IsFresh).IsTrue();
    }

    [Test]
    public async Task the_configuration_picks_the_output_directory()
    {
        using var fileSystem = Built();

        var freshness = HostFreshness.Inspect(fileSystem, s_csproj, "Release");

        await Assert.That(freshness.Output).IsEqualTo((UPath)"/repo/Game.Launcher/bin/Release/net10.0/Game.Launcher.dll");
        await Assert.That(freshness.IsFresh).IsFalse();
    }
}
