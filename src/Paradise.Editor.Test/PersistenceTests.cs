using System.Collections.Immutable;
using Paradise.Assets.Project;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Persistence;
using Zio;
using Zio.FileSystems;

namespace Paradise.Editor.Test;

/// <summary>The two files the editor remembers itself in, both disposable, both on a mount.</summary>
/// <remarks>Memory rather than a temp directory: nothing survives a test that throws before its
/// cleanup, because there is nothing to clean up.</remarks>
public class PersistenceTests
{
    [Test]
    public async Task user_settings_round_trip()
    {
        var fileSystem = new MemoryFileSystem();
        var written = EditorUserSettings.Default with { Theme = "light" };
        written = written.WithRecentProject("/work/game").WithRecentProject("/work/other");

        written.Write(fileSystem, EditorUserLayout.Settings);
        var read = EditorUserSettings.Read(fileSystem, EditorUserLayout.Settings);

        await Assert.That(read.Theme).IsEqualTo("light");
        await Assert.That(read.RecentProjects).IsEquivalentTo(new[] { "/work/other", "/work/game" });
    }

    // Most-recent-first, no duplicates: reopening a project moves it to the top rather than
    // adding a second entry, which is the behaviour anyone expects from a recent list.
    [Test]
    public async Task a_reopened_project_moves_to_the_front_instead_of_repeating()
    {
        var settings = EditorUserSettings.Default
            .WithRecentProject("/a")
            .WithRecentProject("/b")
            .WithRecentProject("/a");

        await Assert.That(settings.RecentProjects).IsEquivalentTo(new[] { "/a", "/b" });
    }

    [Test]
    public async Task the_recent_list_is_bounded()
    {
        var settings = EditorUserSettings.Default;
        for (var i = 0; i < EditorUserSettings.MaxRecentProjects + 5; i++)
        {
            settings = settings.WithRecentProject($"/project{i}");
        }

        await Assert.That(settings.RecentProjects.Count).IsEqualTo(EditorUserSettings.MaxRecentProjects);
        await Assert.That(settings.RecentProjects[0]).IsEqualTo($"/project{EditorUserSettings.MaxRecentProjects + 4}");
    }

    [Test]
    public async Task settings_a_user_broke_by_hand_cost_them_settings_not_the_editor()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText(EditorUserLayout.Settings, "this = = not toml");

        await Assert.That(EditorUserSettings.Read(fileSystem, EditorUserLayout.Settings))
            .IsEqualTo(EditorUserSettings.Default);
    }

    [Test]
    public async Task project_state_round_trips_through_the_editor_directory()
    {
        var fileSystem = new MemoryFileSystem();
        var layout = new AssetProjectLayout("/work/game");
        var path = EditorProjectState.PathFor(layout);
        var ids = ImmutableList.Create(NodeId.New(), NodeId.New());
        var written = new EditorProjectState("/assets/scenes/level.scene.toml", ids);

        written.Write(fileSystem, path);
        var read = EditorProjectState.Read(fileSystem, path);

        await Assert.That(read.LastScene).IsEqualTo(written.LastScene);
        await Assert.That(read.Selection).IsEquivalentTo(ids);
    }

    // The editor's file, not the pipeline's. Two writers on one file is a corruption waiting for a
    // build to run while the editor is open.
    [Test]
    public async Task project_state_does_not_share_the_pipeline_state_file()
    {
        var layout = new AssetProjectLayout("/work/game");
        await Assert.That(EditorProjectState.PathFor(layout)).IsNotEqualTo(layout.EditorState);
        await Assert.That(EditorProjectState.PathFor(layout).GetDirectory()).IsEqualTo(layout.Editor);
    }

    [Test]
    public async Task state_below_the_floor_is_discarded_rather_than_shimmed()
    {
        var fileSystem = new MemoryFileSystem();
        var path = EditorProjectState.PathFor(new AssetProjectLayout("/work/game"));
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, $"Version = {EditorProjectState.MinimumReadableVersion - 1}\nLastScene = \"/x\"\n");

        await Assert.That(EditorProjectState.Read(fileSystem, path)).IsEqualTo(EditorProjectState.Empty);
    }

    [Test]
    public async Task a_missing_state_file_reads_as_empty()
    {
        var path = EditorProjectState.PathFor(new AssetProjectLayout("/work/game"));
        await Assert.That(EditorProjectState.Read(new MemoryFileSystem(), path)).IsEqualTo(EditorProjectState.Empty);
    }

    // One ini per workspace, or switching workspaces overwrites the layout of the one being left.
    [Test]
    public async Task each_workspace_gets_its_own_layout_file()
    {
        await Assert.That(EditorUserLayout.LayoutFor("level")).IsNotEqualTo(EditorUserLayout.LayoutFor("shading"));
        await Assert.That(EditorUserLayout.LayoutFor("level").GetName()).IsEqualTo("level.ini");
    }
}
