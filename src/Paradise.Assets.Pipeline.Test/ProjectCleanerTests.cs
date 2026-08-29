using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class ProjectCleanerTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task clean_removes_both_derived_trees_and_nothing_else()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/build/scenes");
        fileSystem.WriteAllText("/game/build/scenes/a.toml", "x");
        fileSystem.CreateDirectory("/game/.editor/cache");
        fileSystem.WriteAllText("/game/.editor/state.toml", "x");

        var removed = ProjectCleaner.Clean(fileSystem, s_layout);

        await Assert.That(removed.Count).IsEqualTo(2);
        await Assert.That(fileSystem.DirectoryExists("/game/build")).IsFalse();
        await Assert.That(fileSystem.DirectoryExists("/game/.editor")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/assets/project.toml")).IsTrue();
    }

    [Test]
    public async Task keep_editor_preserves_the_cache()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/build");
        fileSystem.CreateDirectory("/game/.editor/cache");

        var removed = ProjectCleaner.Clean(fileSystem, s_layout, keepEditor: true);

        await Assert.That(removed.Count).IsEqualTo(1);
        await Assert.That(fileSystem.DirectoryExists("/game/.editor/cache")).IsTrue();
    }

    [Test]
    public async Task cleaning_an_already_clean_project_removes_nothing()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();

        var removed = ProjectCleaner.Clean(fileSystem, s_layout);

        await Assert.That(removed.Count).IsEqualTo(0);
    }
}
