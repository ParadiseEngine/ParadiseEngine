using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>
/// How the CLI renders a <see cref="UPath"/> for a person (issue #232).
///
/// The pipeline logs the mounted path it was handed and never translates it — a reader does not
/// know what its filesystem is mounted over. This is the layer that does, so the rule lives here
/// and is worth pinning: a path inside <c>assets/</c> reads project-relative, anything else reads
/// as a host path.
/// </summary>
public class PipelineLogTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    /// <summary>Memory has no host form: <c>ConvertPathToInternal</c> throws, so anything that is
    /// NOT project-relative falls back to the UPath. That makes the fallback observable.</summary>
    private static string Render(UPath path)
    {
        using var fileSystem = new MemoryFileSystem();
        return PipelineLog.Render(fileSystem, s_layout, path);
    }

    [Test]
    public async Task a_path_inside_assets_reads_project_relative()
    {
        await Assert.That(Render("/game/assets/props/lamp.glb")).IsEqualTo("props/lamp.glb");
        await Assert.That(Render("/game/assets/crate.png.meta")).IsEqualTo("crate.png.meta");
    }

    [Test]
    public async Task a_sibling_whose_name_starts_with_the_assets_root_is_not_inside_it()
    {
        // Without the separator check `/game/assets-backup/x` renders as `backup/x`: a path that
        // reads like it is inside the project, is not, and gives no hint that it is wrong. The
        // helper this replaced had exactly that defect.
        await Assert.That(Render("/game/assets-backup/x")).IsEqualTo("/game/assets-backup/x");
        await Assert.That(Render("/game/assets2/x")).IsEqualTo("/game/assets2/x");
    }

    [Test]
    public async Task the_assets_root_itself_is_not_rendered_relative()
    {
        // Nothing sensible to relativise against, and the version of this rule that lived on
        // SidecarMaintainer indexed past the end of the string here rather than declining.
        await Assert.That(Render("/game/assets")).IsEqualTo("/game/assets");
    }

    [Test]
    public async Task a_path_outside_the_project_falls_back()
    {
        await Assert.That(Render("/game/build/manifest.json")).IsEqualTo("/game/build/manifest.json");
        await Assert.That(Render("/elsewhere/x")).IsEqualTo("/elsewhere/x");
    }

    [Test]
    public async Task a_physical_mount_renders_a_path_a_person_can_paste()
    {
        // The case that raised the issue: inside the abstraction `/mnt/c/...` is correct and to a
        // person it is useless. Outside assets/, the host form is what gets printed.
        using var physical = new PhysicalFileSystem();
        var outside = physical.ConvertPathFromInternal(Path.GetTempPath());

        var rendered = PipelineLog.Render(physical, s_layout, outside);

        await Assert.That(rendered).IsEqualTo(physical.ConvertPathToInternal(outside));
        await Assert.That(rendered).IsNotEqualTo(outside.FullName);
    }
}
