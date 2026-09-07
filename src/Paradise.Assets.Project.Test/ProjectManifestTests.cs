using TUnit.Assertions.Enums;

namespace Paradise.Assets.Project.Test;

public class ProjectManifestTests
{
    private const string Minimal = """
        name = "shiningpie"
        schema_version = 1
        """;

    [Test]
    public async Task minimal_manifest_declares_a_name_and_no_profiles()
    {
        var manifest = ProjectManifest.Parse(Minimal, "project.toml");

        await Assert.That(manifest.Name).IsEqualTo("shiningpie");
        await Assert.That(manifest.SchemaVersion).IsEqualTo(ProjectManifest.SupportedSchemaVersion);
        await Assert.That(manifest.Profiles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task profiles_read_their_declared_values()
    {
        var manifest = ProjectManifest.Parse("""
            name = "shiningpie"
            schema_version = 1

            [build.profiles.dev]
            document_format = "toml"
            texture_quality = "fast"

            [build.profiles.release]
            document_format = "json"
            """, "project.toml");

        await Assert.That(manifest.Profiles.Count).IsEqualTo(2);

        var dev = manifest.Profiles["dev"];
        await Assert.That(dev.DocumentFormat).IsEqualTo(DocumentFormat.Toml);
        await Assert.That(dev.TextureQuality).IsEqualTo(TextureQuality.Fast);
        await Assert.That(dev.Pack).IsFalse();

        var release = manifest.Profiles["release"];
        await Assert.That(release.DocumentFormat).IsEqualTo(DocumentFormat.Json);
        await Assert.That(release.TextureQuality).IsEqualTo(TextureQuality.Full);
        await Assert.That(release.Pack).IsFalse();
    }

    [Test]
    public async Task an_empty_profile_table_means_every_default()
    {
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[build.profiles.debug]\n", "project.toml");

        var debug = manifest.Profiles["debug"];
        await Assert.That(debug.DocumentFormat).IsEqualTo(DocumentFormat.Toml);
        await Assert.That(debug.TextureQuality).IsEqualTo(TextureQuality.Full);
        await Assert.That(debug.Pack).IsFalse();
    }

    [Test]
    public async Task profile_names_are_the_games_to_invent()
    {
        // Deliberately not one of dev/debug/release: the manifest does not enumerate names, so a
        // game adding a profile never has to patch this package.
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[build.profiles.demo-kiosk]\ntexture_quality = \"fast\"\n", "project.toml");

        await Assert.That(manifest.TryGetProfile("demo-kiosk", out var profile)).IsTrue();
        await Assert.That(profile!.TextureQuality).IsEqualTo(TextureQuality.Fast);
        await Assert.That(manifest.TryGetProfile("nope", out _)).IsFalse();
    }

    [Test]
    public async Task an_unknown_document_format_is_refused_and_names_the_profile()
    {
        // The point of refusing rather than defaulting: a typo that quietly shipped TOML into a
        // release tree is not discovered until someone reads the pak.
        var error = Rejects($"{Minimal}\n\n[build.profiles.release]\ndocument_format = \"yaml\"\n");

        await Assert.That(error.Message).Contains("release");
        await Assert.That(error.Message).Contains("yaml");
        await Assert.That(error.SourceName).IsEqualTo("project.toml");
    }

    /// <summary>The failure the strict loader exists to prevent: a typo'd key that a lenient read ignored is a setting that never applied.</summary>
    [Test]
    public async Task a_typoed_profile_key_is_refused_rather_than_ignored()
    {
        var error = Rejects($"{Minimal}\n\n[build.profiles.release]\ndocument_fromat = \"json\"\n");

        await Assert.That(error.Message).Contains("document_fromat");
        await Assert.That(error.Message).Contains("release");
    }

    [Test]
    public async Task extract_routes_each_kind_and_falls_back_to_directory()
    {
        var manifest = ProjectManifest.Parse("""
            name = "shiningpie"
            schema_version = 1

            [extract]
            directory = "cooked"
            animations = "animations/"
            materials = "materials"
            textures = "textures"
            prefabs = "prefabs/models"
            """, "project.toml");

        var kinds = GlbKinds;
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Animations, kinds)).IsEqualTo("animations");
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Materials, kinds)).IsEqualTo("materials");
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Textures, kinds)).IsEqualTo("textures");
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Prefabs, kinds)).IsEqualTo("prefabs/models");

        // `meshes` names nothing, so the geometry documents take the section's fallback, and so
        // does the skeleton — through `meshes`, which is also unset.
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Meshes, kinds)).IsEqualTo("cooked");
        await Assert.That(manifest.Extract.DirectoryFor(ExtractKind.Skeletons, kinds)).IsEqualTo("cooked");
    }

    [Test]
    public async Task a_skeleton_follows_the_meshes_directory_until_it_names_its_own()
    {
        var withMeshes = ProjectManifest.Parse($"{Minimal}\n\n[extract]\ndirectory = \"cooked\"\nmeshes = \"meshes\"\n", "project.toml");
        await Assert.That(withMeshes.Extract.DirectoryFor(ExtractKind.Skeletons, GlbKinds)).IsEqualTo("meshes");

        var withOwn = ProjectManifest.Parse($"{Minimal}\n\n[extract]\nmeshes = \"meshes\"\nskeletons = \"animations\"\n", "project.toml");
        await Assert.That(withOwn.Extract.DirectoryFor(ExtractKind.Skeletons, GlbKinds)).IsEqualTo("animations");
        await Assert.That(withOwn.Extract.DirectoryFor(ExtractKind.Meshes, GlbKinds)).IsEqualTo("meshes");
    }

    [Test]
    public async Task an_extract_section_that_names_no_directory_leaves_every_kind_beside_the_source()
    {
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[extract]\nstatic_mesh_component = \"Game.StaticMesh\"\n", "project.toml");

        foreach (var kind in GlbKinds)
        {
            await Assert.That(manifest.Extract.DirectoryFor(kind.Id, GlbKinds)).IsNull();
        }

        await Assert.That(manifest.Extract.StaticMeshComponent).IsEqualTo("Game.StaticMesh");
        await Assert.That(manifest.Extract.Kinds).IsEmpty();
    }

    [Test]
    public async Task a_kind_the_engine_never_heard_of_routes_like_any_other()
    {
        // The whole point of the open key set: a game's extractor declares `tilesets`, and the
        // manifest routes it with no engine change. The manifest does not judge the name — whether
        // a kind exists depends on the build's extractor chain, so `verify` is what reports one
        // nothing declares.
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[extract]\ndirectory = \"src\"\ntilesets = \"tilesets\"\n", "project.toml");

        IReadOnlyList<ExtractKindDeclaration> game = [new("tilesets"), new("tilemaps", FallsBackTo: "tilesets")];
        await Assert.That(manifest.Extract.DirectoryFor("tilesets", game)).IsEqualTo("tilesets");
        await Assert.That(manifest.Extract.DirectoryFor("tilemaps", game)).IsEqualTo("tilesets");
        await Assert.That(manifest.Extract.DirectoryFor("lods", game)).IsEqualTo("src");
        await Assert.That(manifest.Extract.Kinds).IsEquivalentTo(new[] { "tilesets" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_fallback_cycle_degrades_to_directory_rather_than_hanging()
    {
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[extract]\ndirectory = \"src\"\n", "project.toml");

        IReadOnlyList<ExtractKindDeclaration> looped = [new("a", FallsBackTo: "b"), new("b", FallsBackTo: "a")];
        await Assert.That(manifest.Extract.DirectoryFor("a", looped)).IsEqualTo("src");
    }

    [Test]
    public async Task a_kind_whose_value_is_not_a_directory_is_refused()
    {
        var error = Assert.Throws<ProjectManifestException>(
            () => ProjectManifest.Parse($"{Minimal}\n\n[extract]\nmaterials = 3\n", "project.toml"));

        await Assert.That(error!.Message).Contains("materials");
        await Assert.That(error.Message).Contains("[extract]");
    }

    private static IReadOnlyList<ExtractKindDeclaration> GlbKinds =>
    [
        new(ExtractKind.Meshes),
        new(ExtractKind.Skeletons, FallsBackTo: ExtractKind.Meshes),
        new(ExtractKind.Animations),
        new(ExtractKind.Materials),
        new(ExtractKind.Textures),
        new(ExtractKind.Prefabs),
    ];

    [Test]
    public async Task extensions_name_assemblies_relative_to_the_project_root()
    {
        var manifest = ProjectManifest.Parse("""
            name = "shiningpie"
            schema_version = 1

            [extensions]
            assemblies = ["tools/assets/bin/Debug/net10.0/ShiningPie.Assets.dll", " spaced.dll "]
            """, "project.toml");

        await Assert.That(manifest.Extensions).IsEquivalentTo(new[]
        {
            "tools/assets/bin/Debug/net10.0/ShiningPie.Assets.dll",
            "spaced.dll",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_project_that_extends_nothing_names_no_assemblies()
    {
        await Assert.That(ProjectManifest.Parse(Minimal, "project.toml").Extensions).IsEmpty();
    }

    [Test]
    public async Task an_empty_extension_path_is_refused()
    {
        var error = Assert.Throws<ProjectManifestException>(
            () => ProjectManifest.Parse($"{Minimal}\n\n[extensions]\nassemblies = [\"a.dll\", \"\"]\n", "project.toml"));

        await Assert.That(error!.Message).Contains("[extensions]");
    }

    [Test]
    public async Task an_unknown_extensions_key_is_refused()
    {
        var error = Assert.Throws<ProjectManifestException>(
            () => ProjectManifest.Parse($"{Minimal}\n\n[extensions]\nplugins = [\"a.dll\"]\n", "project.toml"));

        await Assert.That(error!.Message).Contains("plugins");
    }

    [Test]
    public async Task an_extract_directory_outside_the_asset_tree_is_refused_at_the_key()
    {
        // Diagnosed here rather than as an unresolved entry per extracted file: the value is what
        // is wrong, and `(layout.Assets / relative)` would happily put output where nothing indexes it.
        foreach (var bad in new[] { "/etc", "../outside", "materials/../..", "C:/temp" })
        {
            var error = Assert.Throws<ProjectManifestException>(
                () => ProjectManifest.Parse($"{Minimal}\n\n[extract]\nmaterials = \"{bad}\"\n", "project.toml"));

            await Assert.That(error!.Message).Contains("materials");
            await Assert.That(error.Message).Contains("assets/");
        }

        // A nested relative directory is fine, and so is a trailing slash.
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[extract]\nmaterials = \"a/b/\"\n", "project.toml");
        await Assert.That(manifest.Extract.DirectoryFor("materials")).IsEqualTo("a/b");
    }

    [Test]
    public async Task an_unknown_root_key_is_refused()
    {
        var error = Rejects($"{Minimal}\nnmae = \"y\"\n");

        await Assert.That(error.Message).Contains("nmae");
    }

    [Test]
    public async Task an_unknown_build_key_is_refused()
    {
        var error = Rejects($"{Minimal}\n\n[build]\nprofile = \"dev\"\n");

        await Assert.That(error.Message).Contains("profile");
        await Assert.That(error.Message).Contains("[build]");
    }

    [Test]
    public async Task a_duplicate_key_is_refused_rather_than_last_wins()
    {
        var error = Rejects("name = \"a\"\nname = \"b\"\nschema_version = 1\n");

        await Assert.That(error.Message).Contains("name");
    }

    /// <summary>Reserved values are refused at load, not at the first asset: a strict loader that accepts a value nothing implements only moves the failure.</summary>
    [Test]
    public async Task blob_and_pack_are_refused_until_a_writer_exists()
    {
        var blob = Rejects($"{Minimal}\n\n[build.profiles.release]\ndocument_format = \"blob\"\n");
        await Assert.That(blob.Message).Contains("blob");
        await Assert.That(blob.Message).Contains("release");

        var pack = Rejects($"{Minimal}\n\n[build.profiles.release]\npack = true\n");
        await Assert.That(pack.Message).Contains("pack");
        await Assert.That(pack.Message).Contains("release");
    }

    [Test]
    public async Task an_unknown_texture_quality_is_refused_and_names_the_profile()
    {
        var error = Rejects($"{Minimal}\n\n[build.profiles.dev]\ntexture_quality = \"potato\"\n");

        await Assert.That(error.Message).Contains("dev");
        await Assert.That(error.Message).Contains("potato");
    }

    [Test]
    public async Task name_is_required()
    {
        await Assert.That(Rejects("schema_version = 1").Message).Contains("name");
        await Assert.That(Rejects("name = \"\"\nschema_version = 1").Message).Contains("name");
    }

    [Test]
    public async Task schema_version_is_required()
    {
        await Assert.That(Rejects("name = \"shiningpie\"").Message).Contains("schema_version");
    }

    [Test]
    public async Task a_future_schema_version_is_refused_rather_than_guessed_at()
    {
        var error = Rejects("name = \"shiningpie\"\nschema_version = 2");

        await Assert.That(error.Message).Contains("2");
        await Assert.That(error.Message).Contains("1");
    }

    [Test]
    public async Task malformed_toml_is_reported_as_a_manifest_problem()
    {
        // Callers handle one exception type: whatever went wrong, the response is to tell the
        // author and stop.
        var error = Rejects("name = = \"broken\"");

        await Assert.That(error.SourceName).IsEqualTo("project.toml");
        await Assert.That(error.InnerException).IsNotNull();
    }

    [Test]
    public async Task load_reads_through_the_filesystem_abstraction()
    {
        using var fileSystem = new MemoryFileSystem();
        var layout = new AssetProjectLayout("/game");
        fileSystem.CreateDirectory(layout.Assets);
        fileSystem.WriteAllText(layout.Manifest, Minimal);

        var manifest = ProjectManifest.Load(fileSystem, layout.Manifest);

        await Assert.That(manifest.Name).IsEqualTo("shiningpie");
    }

    [Test]
    public async Task loading_a_missing_manifest_reports_the_path()
    {
        using var fileSystem = new MemoryFileSystem();

        await Assert.That(() => ProjectManifest.Load(fileSystem, "/game/assets/project.toml"))
            .Throws<ProjectManifestException>();
    }

    [Test]
    public async Task a_manifest_without_a_host_section_launches_nothing()
    {
        var manifest = ProjectManifest.Parse(Minimal, "project.toml");

        await Assert.That(manifest.Host.Project).IsNull();
        await Assert.That(manifest.Host.Arguments).IsEmpty();
    }

    [Test]
    public async Task the_host_section_names_the_launcher_and_its_arguments()
    {
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[host]\nproject = \"Game.Launcher/Game.Launcher.csproj\"\narguments = [\"--ui\", \"ui/Shell.xaml\"]\n", "project.toml");

        await Assert.That(manifest.Host.Project).IsEqualTo("Game.Launcher/Game.Launcher.csproj");
        await Assert.That(manifest.Host.Arguments).IsEquivalentTo(new[] { "--ui", "ui/Shell.xaml" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task the_host_scene_is_assets_relative_and_optional()
    {
        var bare = ProjectManifest.Parse($"{Minimal}\n\n[host]\nproject = \"G/G.csproj\"\n", "project.toml");
        await Assert.That(bare.Host.Scene).IsNull();

        var declared = ProjectManifest.Parse($"{Minimal}\n\n[host]\nproject = \"G/G.csproj\"\nscene = \"levels/arena.prefab\"\n", "project.toml");
        await Assert.That(declared.Host.Scene).IsEqualTo("levels/arena.prefab");
    }

    [Test]
    public async Task a_host_project_that_is_not_a_csproj_is_refused()
    {
        // A prebuilt executable has no build to run and no reference closure to check, so the
        // freshness gate and `dotnet watch run` would both be lies about it.
        var error = Rejects($"{Minimal}\n\n[host]\nproject = \"bin/Game\"\n");

        await Assert.That(error.Message).Contains("bin/Game");
        await Assert.That(error.Message).Contains(".csproj");
    }

    [Test]
    public async Task an_unknown_host_key_is_refused()
    {
        var error = Rejects($"{Minimal}\n\n[host]\nargs = [\"--seed\"]\n");

        await Assert.That(error.Message).Contains("args");
        await Assert.That(error.Message).Contains("[host]");
    }

    private static ProjectManifestException Rejects(string toml)
    {
        try
        {
            ProjectManifest.Parse(toml, "project.toml");
        }
        catch (ProjectManifestException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected the manifest to be rejected, but it loaded.");
    }

    [Test]
    public async Task the_ignore_list_is_the_projects_and_defaults_to_nothing()
    {
        var bare = ProjectManifest.Parse(Minimal, "project.toml");
        await Assert.That(bare.Ignore.Patterns).IsEmpty();
        await Assert.That(bare.Ignore.Matches("/game/assets", "/game/assets/.DS_Store")).IsFalse();

        var manifest = ProjectManifest.Parse("""
            name = "shiningpie"
            schema_version = 1

            [assets]
            ignore = [".DS_Store", "*.tmp", "scratch/**"]
            """, "project.toml");

        await Assert.That(manifest.Ignore.Patterns).IsEquivalentTo(new[] { ".DS_Store", "*.tmp", "scratch/**" }, CollectionOrdering.Matching);
        await Assert.That(manifest.Ignore.Matches("/game/assets", "/game/assets/models/.DS_Store")).IsTrue();
        await Assert.That(manifest.Ignore.Matches("/game/assets", "/game/assets/scratch/a/b.prefab")).IsTrue();
        await Assert.That(manifest.Ignore.Matches("/game/assets", "/game/assets/models/crate.glb")).IsFalse();
    }

    [Test]
    public async Task an_unknown_key_in_assets_is_refused()
    {
        var error = await Assert.That(() => ProjectManifest.Parse("""
            name = "x"
            schema_version = 1

            [assets]
            ignored = [".DS_Store"]
            """, "project.toml")).Throws<ProjectManifestException>();

        await Assert.That(error!.Message).Contains("'ignored'");
        await Assert.That(error.Message).Contains("in [assets]");
    }

    [Test]
    public async Task an_empty_or_rooted_ignore_pattern_is_refused()
    {
        var error = await Assert.That(() => ProjectManifest.Parse("""
            name = "x"
            schema_version = 1

            [assets]
            ignore = ["/models/*.tmp"]
            """, "project.toml")).Throws<ProjectManifestException>();

        await Assert.That(error!.Message).Contains("relative to assets/");
    }
}
