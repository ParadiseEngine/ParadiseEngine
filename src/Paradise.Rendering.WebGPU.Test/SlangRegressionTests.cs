using System.Text.Json;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Slang version regression suite (added in M4 / issue #45).
///
/// <para>Covers every checked-in <c>.slang</c> file compiled into this test assembly via
/// <c>Slang.targets</c>. Currently: <c>triangle.slang</c> (M1 triangle draw path).</para>
///
/// <para>Two snapshot types per shader:</para>
/// <list type="number">
///   <item><b>Snapshot A (loader output)</b>: runs <see cref="ShaderProgramLoader.Load"/> and
///     asserts that vertex attribute locations, formats, entry point names, stages, and pipeline
///     layout match the checked-in <c>fixtures/&lt;shader&gt;.expected.json</c>. Catches loader
///     regressions (e.g. a mapping change in <see cref="ShaderProgramLoader"/> that silently
///     produces wrong attribute formats).</item>
///   <item><b>Snapshot B (raw slangc)</b>: loads the embedded
///     <c>Shaders.&lt;shader&gt;.raw-reflection.json</c>, parses it through the internal
///     <see cref="SlangReflectionJsonContext"/> schema, re-serializes (normalizes, dropping
///     unknown fields), and compares against <c>fixtures/&lt;shader&gt;.raw.expected.json</c>.
///     Catches Slang version bumps that silently change the reflection JSON schema BEFORE the
///     loader notices — the raw snapshot pins the slangc input contract independently of the
///     loader's output contract.</item>
/// </list>
///
/// <para>Bumping Slang in <c>tools/slang/slang.manifest.json</c>:</para>
/// <code>
///   # 1. Bump SlangVersion in slang.manifest.json
///   # 2. Run dotnet build to regenerate .wgsl + .reflection.json + .raw-reflection.json
///   # 3. Run tests with SLANG_UPDATE_SNAPSHOTS=1 to regenerate fixture goldens
///   # 4. Code-review the diff in fixtures/ — accept if the change is expected
///   # 5. Commit the updated fixtures alongside the version bump
/// </code>
///
/// <para>GPU-touching test (<see cref="wgsl_compiles_clean_on_dawn_headless_adapter"/>) skips
/// via <c>DllNotFoundException</c>/<c>AdapterUnavailableException</c> when Dawn is not
/// available. The two snapshot tests run without a GPU.</para>
/// </summary>
public class SlangRegressionTests
{
    private const string UpdateEnvVar = "SLANG_UPDATE_SNAPSHOTS";

    private static System.Reflection.Assembly Assembly => typeof(SlangRegressionTests).Assembly;

    // -------- Per-shader fixture names --------

    /// <summary>All shader logical-name prefixes compiled into this assembly by Slang.targets.
    /// Public and named as a method so TUnit's source generator can discover it via
    /// [MethodDataSource(nameof(ShaderPrefixes))].</summary>
    public static IEnumerable<string> ShaderPrefixes() => new[] { "Shaders.triangle" };

    // -------- Helpers --------

    private static string ReadEmbeddedResource(string logicalName)
    {
        using var stream = Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found. " +
                $"Available: {string.Join(", ", Assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LoadFixtureExpected(string fixtureFileName)
    {
        // Fixtures are embedded via <EmbeddedResource> in the csproj.
        var logicalName = $"Fixtures.{fixtureFileName}";
        using var stream = Assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
            return string.Empty; // No fixture yet — caller decides whether to seed or fail.
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static bool ShouldUpdateSnapshots =>
        string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal);

    /// <summary>When <c>SLANG_UPDATE_SNAPSHOTS=1</c>, write the actual content to
    /// <c>src/Paradise.Rendering.WebGPU.Test/fixtures/{fixtureFileName}</c> alongside the
    /// source tree so the developer can review and commit the diff. The path is resolved
    /// relative to the assembly location, then walked up to the <c>fixtures/</c> directory.</summary>
    private static void MaybeWriteFixture(string fixtureFileName, string actual)
    {
        if (!ShouldUpdateSnapshots) return;

        // Walk up from the assembly output dir to the project root, then down to fixtures/.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "fixtures")))
            dir = Directory.GetParent(dir)?.FullName;

        if (dir is null) return;

        var path = Path.Combine(dir, "fixtures", fixtureFileName);
        File.WriteAllText(path, actual);
        Console.WriteLine($"[SlangRegressionTests] Updated fixture: {path}");
    }

    private static string BuildSnapshotA(string shaderPrefix)
    {
        var program = ShaderProgramLoader.Load(Assembly, shaderPrefix);

        // Serialize only the structural parts — NOT the WGSL text (which is a build artifact
        // that changes with every slangc run even if the source is unchanged). The snapshot
        // pins entry point names, stages, vertex attribute layout, and pipeline layout shape.
        var obj = new
        {
            modules = Array.ConvertAll(program.Modules, m => new { entry_point = m.EntryPoint, stage = m.Stage.ToString() }),
            layout = new
            {
                groups = program.Layout.Groups.Length,
                push_constants = program.Layout.PushConstants.Length,
            },
            vertex_buffers = Array.ConvertAll(program.VertexBuffers, vb => new
            {
                stride = vb.Stride,
                step_mode = vb.StepMode.ToString(),
                attributes = Array.ConvertAll(vb.Attributes, a => new
                {
                    shader_location = a.ShaderLocation,
                    format = a.Format.ToString(),
                    offset = a.Offset,
                }),
            }),
        };

        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
        });
    }

    private static string BuildSnapshotB(string shaderPrefix)
    {
        var rawJson = ReadEmbeddedResource(shaderPrefix + ".raw-reflection.json");

        // Parse through the schema (drops unknown slangc fields), then re-serialize to
        // normalize whitespace and field order. This makes the comparison robust to slangc
        // adding extra metadata fields that we don't model.
        var parsed = JsonSerializer.Deserialize(rawJson, SlangReflectionJsonContext.Default.SlangReflection)
            ?? throw new InvalidOperationException("Failed to parse raw reflection JSON.");

        return JsonSerializer.Serialize(parsed, SlangReflectionJsonContext.Default.SlangReflection);
    }

    // -------- WGSL Dawn-clean test (GPU-needed) --------

    [Test]
    [MethodDataSource(nameof(ShaderPrefixes))]
    public async Task wgsl_compiles_clean_on_dawn_headless_adapter(string shaderPrefix)
    {
        WebGpuRenderer renderer;
        try
        {
            renderer = WebGpuRenderer.CreateHeadless(1, 1);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter: {ex.Message}");
            return;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native not loadable: {ex.Message}");
            return;
        }

        using (renderer)
        {
            var program = ShaderProgramLoader.Load(Assembly, shaderPrefix);

            // Create shader modules for each entry point. If Dawn rejects the WGSL (which it
            // validates on module creation), CreateShader throws InvalidOperationException with
            // "ShaderModule creation returned null." — surfaced as a hard test failure, not a skip.
            // Any Dawn-logged uncaptured-error callback output also appears in the test stderr.
            foreach (var module in program.Modules)
            {
                var handle = renderer.CreateShader(in module);
                await Assert.That(handle.IsValid).IsTrue();
                renderer.DestroyShader(handle);
            }
        }
    }

    // -------- Snapshot A: loader output --------

    [Test]
    [MethodDataSource(nameof(ShaderPrefixes))]
    public async Task loader_output_snapshot_matches_golden_fixture(string shaderPrefix)
    {
        var shaderName = shaderPrefix.Replace("Shaders.", string.Empty);
        var fixtureFile = $"{shaderName}.expected.json";

        var actual = BuildSnapshotA(shaderPrefix);
        MaybeWriteFixture(fixtureFile, actual);

        var golden = LoadFixtureExpected(fixtureFile).Trim();

        if (string.IsNullOrEmpty(golden))
        {
            // No fixture embedded — seed mode only. Skip with informative message.
            Skip.Test(
                $"No golden fixture embedded for '{fixtureFile}'. " +
                $"Run with {UpdateEnvVar}=1 to seed the fixture, then add it to the csproj <EmbeddedResource>.");
            return;
        }

        // Normalize actual JSON for comparison (re-parse both to ensure consistent formatting).
        var actualNorm = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(actual),
            new JsonSerializerOptions { WriteIndented = false });
        var goldenNorm = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(golden),
            new JsonSerializerOptions { WriteIndented = false });

        await Assert.That(actualNorm).IsEqualTo(goldenNorm);
    }

    // -------- Snapshot B: raw slangc JSON --------

    [Test]
    [MethodDataSource(nameof(ShaderPrefixes))]
    public async Task raw_slangc_reflection_snapshot_matches_golden_fixture(string shaderPrefix)
    {
        var shaderName = shaderPrefix.Replace("Shaders.", string.Empty);
        var fixtureFile = $"{shaderName}.raw.expected.json";

        var actual = BuildSnapshotB(shaderPrefix);
        MaybeWriteFixture(fixtureFile, actual);

        var golden = LoadFixtureExpected(fixtureFile).Trim();

        if (string.IsNullOrEmpty(golden))
        {
            Skip.Test(
                $"No golden fixture embedded for '{fixtureFile}'. " +
                $"Run with {UpdateEnvVar}=1 to seed the fixture, then add it to the csproj <EmbeddedResource>.");
            return;
        }

        var actualNorm = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(actual),
            new JsonSerializerOptions { WriteIndented = false });
        var goldenNorm = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(golden),
            new JsonSerializerOptions { WriteIndented = false });

        await Assert.That(actualNorm).IsEqualTo(goldenNorm);
    }
}
