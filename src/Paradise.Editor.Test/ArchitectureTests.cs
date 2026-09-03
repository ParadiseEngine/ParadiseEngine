using System.Reflection;
using System.Text.RegularExpressions;
using Paradise.Editor.Core.Document;
using Paradise.Editor.ImGui;

namespace Paradise.Editor.Test;

/// <summary>The boundaries that keep the editor layers clean, enforced by the build rather than
/// by review: Core knows no ImGui, touches no file except through a Zio mount and names no logging
/// sink, and the ImGui layer draws without owning a frame, a window or a device.</summary>
public partial class ArchitectureTests
{
    private static readonly Assembly s_core = typeof(SceneDocument).Assembly;

    [Test]
    public async Task core_references_no_imgui_assembly()
    {
        var referenced = s_core.GetReferencedAssemblies().Select(name => name.Name ?? "").ToArray();
        await Assert.That(referenced).DoesNotContain("ImGui.NET");
        await Assert.That(referenced).DoesNotContain("Hexa.NET.ImGui");
        await Assert.That(referenced).DoesNotContain("Paradise.Ui.ImGui");
    }

    // The rule is the engine's (AGENTS.md, docs/logging.md) and the editor is held to it for a
    // reason of its own: in-game, the editor's diagnostics have to land in the GAME's stack, and a
    // provider referenced here would decide that for every host at once.
    [Test]
    public async Task core_references_the_logging_abstraction_and_no_provider()
    {
        var referenced = s_core.GetReferencedAssemblies().Select(name => name.Name ?? "").ToArray();
        await Assert.That(referenced).Contains("Microsoft.Extensions.Logging.Abstractions");
        await Assert.That(referenced).DoesNotContain("Microsoft.Extensions.Logging");
        await Assert.That(referenced).DoesNotContain("Paradise.Diagnostics");
    }

    // Source is scanned rather than IL because System.IO is in the core library every assembly
    // references; the offence is a CALL, and a call is most cheaply found where it is written.
    [Test]
    public async Task core_uses_no_system_io_file_api()
    {
        var sources = CoreSourceFiles();
        await Assert.That(sources).IsNotEmpty();
        var offenders = sources
            .Where(path => FileApiCall().IsMatch(Comment().Replace(File.ReadAllText(path), "")))
            .Select(Path.GetFileName)
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    // The csproj comment claims this library never owns a window, an input pump or a renderer.
    // Referencing Paradise.Ui.ImGui to obtain the binding would quietly make that false — it
    // carries WebGPUSharp and the frame-owning core — and nothing else would notice.
    [Test]
    public async Task the_imgui_layer_takes_the_binding_and_not_the_runtime()
    {
        var referenced = typeof(EditorWindow).Assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? "").ToArray();
        await Assert.That(referenced).Contains("Hexa.NET.ImGui");
        await Assert.That(referenced).DoesNotContain("Paradise.Ui.ImGui");
        await Assert.That(referenced).DoesNotContain("WebGPUSharp");
    }

    [GeneratedRegex(@"\b(File|Directory|FileStream|StreamReader|StreamWriter|Path)\.")]
    private static partial Regex FileApiCall();

    // Comments are stripped before matching so that documenting the rule does not break it: the
    // remarks on ISceneDocumentStore have every reason to name File and Path. Both forms, since a
    // csproj-style block comment explaining the mount would otherwise redden the build and name
    // the wrong culprit. `[^\n]` rather than `.` with Singleline, which would let a line comment
    // swallow the rest of the file.
    [GeneratedRegex(@"^[ \t]*//[^\n]*|/\*[\s\S]*?\*/", RegexOptions.Multiline)]
    private static partial Regex Comment();

    private static string[] CoreSourceFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paradise.Editor.Test.csproj")))
        {
            directory = directory.Parent;
        }
        var core = Path.Combine(directory?.Parent?.FullName ?? "", "Paradise.Editor.Core");
        return Directory.Exists(core) ? Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories) : [];
    }
}
