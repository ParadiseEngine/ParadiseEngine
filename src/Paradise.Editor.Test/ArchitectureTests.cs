using System.Reflection;
using System.Text.RegularExpressions;
using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Test;

/// <summary>The two boundaries that keep the editor model clean, enforced by the build rather
/// than by review: Core knows no ImGui, and Core touches no file except through a Zio mount.</summary>
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

    // Source is scanned rather than IL because System.IO is in the core library every assembly
    // references; the offence is a CALL, and a call is most cheaply found where it is written.
    [Test]
    public async Task core_uses_no_system_io_file_api()
    {
        var sources = CoreSourceFiles();
        await Assert.That(sources).IsNotEmpty();
        var offenders = sources
            .Where(path => FileApiCall().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToArray();
        await Assert.That(offenders).IsEmpty();
    }

    [GeneratedRegex(@"\b(File|Directory|FileStream|StreamReader|StreamWriter|Path)\.")]
    private static partial Regex FileApiCall();

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
