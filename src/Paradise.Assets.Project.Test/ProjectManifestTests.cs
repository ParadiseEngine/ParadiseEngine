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
            pack = true
            """, "project.toml");

        await Assert.That(manifest.Profiles.Count).IsEqualTo(2);

        var dev = manifest.Profiles["dev"];
        await Assert.That(dev.DocumentFormat).IsEqualTo(DocumentFormat.Toml);
        await Assert.That(dev.TextureQuality).IsEqualTo(TextureQuality.Fast);
        await Assert.That(dev.Pack).IsFalse();

        var release = manifest.Profiles["release"];
        await Assert.That(release.DocumentFormat).IsEqualTo(DocumentFormat.Json);
        await Assert.That(release.TextureQuality).IsEqualTo(TextureQuality.Full);
        await Assert.That(release.Pack).IsTrue();
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
        var manifest = ProjectManifest.Parse($"{Minimal}\n\n[build.profiles.demo-kiosk]\npack = true\n", "project.toml");

        await Assert.That(manifest.TryGetProfile("demo-kiosk", out var profile)).IsTrue();
        await Assert.That(profile!.Pack).IsTrue();
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
}
