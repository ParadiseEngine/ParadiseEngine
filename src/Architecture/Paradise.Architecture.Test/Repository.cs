using System.Xml.Linq;

namespace Paradise.Architecture.Test;

/// <summary>One project file under <c>src/</c>, read as XML.</summary>
internal sealed class ProjectFile
{
    private readonly XDocument _document;

    public ProjectFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        _document = XDocument.Load(path);
        ProjectReferences = _document.Descendants("ProjectReference")
            .Where(reference => (string?)reference.Attribute("ReferenceOutputAssembly") != "false")
            .Select(ReferencedName)
            .ToArray();
        BuildReferences = _document.Descendants("ProjectReference").Select(ReferencedName).ToArray();
        PackageReferences = _document.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .OfType<string>()
            .ToArray();
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>Referenced projects whose assemblies this project compiles against (analyzer-only references excluded).</summary>
    public IReadOnlyList<string> ProjectReferences { get; }

    /// <summary>Every referenced project, including analyzers and build-only fixtures.</summary>
    public IReadOnlyList<string> BuildReferences { get; }

    public IReadOnlyList<string> PackageReferences { get; }

    /// <summary>The last unconditional value this file gives a property, or null.</summary>
    public string? Property(string name) => _document.Descendants(name)
        .Where(element => element.Parent?.Name == "PropertyGroup" && element.Parent.Attribute("Condition") is null)
        .Select(element => element.Value.Trim())
        .LastOrDefault();

    private static string ReferencedName(XElement reference) =>
        System.IO.Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/'));

    /// <summary>Whether <c>dotnet pack</c> produces a package. Mirrors the SDK: tests and plain executables opt out.</summary>
    public bool IsPackable =>
        Property("IsPackable") != "false"
        && Property("IsTestProject") != "true"
        && (Property("OutputType") != "Exe" || Property("PackAsTool") == "true");
}

/// <summary>The repository checkout the tests run from.</summary>
internal static class Repository
{
    private static readonly Lazy<string> s_root = new(FindRoot);
    private static readonly Lazy<IReadOnlyDictionary<string, ProjectFile>> s_projects = new(LoadProjects);

    public static string Root => s_root.Value;

    public static IReadOnlyDictionary<string, ProjectFile> Projects => s_projects.Value;

    public static string ReadText(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Root, relativePath));

    public static string Relative(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>Every project <paramref name="name"/> reaches through project references, itself excluded.</summary>
    public static IReadOnlySet<string> Closure(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Projects[name].ProjectReferences);
        while (pending.TryPop(out var next))
        {
            if (!seen.Add(next) || !Projects.TryGetValue(next, out var project)) continue;
            foreach (var reference in project.ProjectReferences) pending.Push(reference);
        }

        return seen;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "ParadiseEngine.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("The tests must run from inside a ParadiseEngine checkout.");
    }

    private static IReadOnlyDictionary<string, ProjectFile> LoadProjects() =>
        Directory.EnumerateFiles(System.IO.Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj"))
            .Select(path => new ProjectFile(path))
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
}
