using Microsoft.Extensions.Logging;
using Paradise.Diagnostics;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class KtxToolTests
{
    [Test]
    [Arguments("ktx version: v5.0.0-rc1~5", "5.0.0")]
    [Arguments("v4.3.2", "4.3.2")]
    [Arguments("KTX-Software 4.4.0", "4.4.0")]
    [Arguments("5", "5.0")]
    public async Task a_version_line_parses_to_its_numeric_core(string line, string expected)
    {
        await Assert.That(KtxTool.TryParseVersion(line, out var version)).IsTrue();
        await Assert.That(version!.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task a_version_line_without_digits_does_not_parse()
    {
        await Assert.That(KtxTool.TryParseVersion("unknown option --version", out _)).IsFalse();
    }

    [Test]
    public async Task the_probe_refuses_a_ktx_older_than_the_minimum()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("the fake tool is a shell script"); return; }

        var fake = FakeTool("#!/bin/sh\necho 'ktx version: v4.3.0'\n");
        try
        {
            var probe = KtxTool.Probe(fake);
            await Assert.That(probe.Usable).IsFalse();
            await Assert.That(probe.Version).IsEqualTo(new Version(4, 3, 0));
            await Assert.That(probe.Problem!).Contains("v4.4");
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Test]
    public async Task the_probe_accepts_a_current_ktx_and_reports_its_version()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("the fake tool is a shell script"); return; }

        var fake = FakeTool("#!/bin/sh\necho 'ktx version: v5.0.0-rc2'\n");
        try
        {
            var probe = KtxTool.Probe(fake);
            await Assert.That(probe.Usable).IsTrue();
            await Assert.That(probe.Version).IsEqualTo(new Version(5, 0, 0));
            await Assert.That(probe.VersionText).IsEqualTo("ktx version: v5.0.0-rc2");
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Test]
    public async Task a_present_but_unrunnable_tool_is_reported_not_thrown()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("execute bits are a Unix notion");

        var notExecutable = Path.Combine(Path.GetTempPath(), $"paradise_noexec_{Guid.NewGuid():N}");
        File.WriteAllText(notExecutable, "not a program");
        try
        {
            await Assert.That(ProcessTools.IsRunnable(notExecutable)).IsFalse();
            await Assert.That(ProcessTools.FindExecutable(notExecutable, [], "definitely-not-a-real-binary-xyz")).IsNull();

            var run = ProcessTools.Run(notExecutable, "", timeoutMilliseconds: 5_000);
            await Assert.That(run.Started).IsFalse();
            await Assert.That(run.Succeeded).IsFalse();
            await Assert.That(run.Stderr).Contains(notExecutable);
            await Assert.That(run.Describe("tool", 5_000)).Contains("could not be started");

            await Assert.That(KtxTool.Probe(notExecutable).Usable).IsFalse();
        }
        finally
        {
            File.Delete(notExecutable);
        }
    }

    [Test]
    public async Task find_executable_prefers_the_environment_path_when_present()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"paradise_tool_{Guid.NewGuid():N}");
        File.WriteAllText(fake, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.That(ProcessTools.FindExecutable(fake, [], "does-not-exist-xyz")).IsEqualTo(fake);
            await Assert.That(ProcessTools.FindExecutable(null, [], "definitely-not-a-real-binary-xyz")).IsNull();
        }
        finally
        {
            if (File.Exists(fake)) File.Delete(fake);
        }
    }

    [Test]
    public async Task quote_argument_handles_plain_and_trailing_backslash()
    {
        await Assert.That(ProcessTools.QuoteArgument("plain")).IsEqualTo("\"plain\"");
        // A trailing backslash must be doubled so it can't escape the closing quote on Windows.
        await Assert.That(ProcessTools.QuoteArgument(@"C:\dir\").EndsWith("\\\\\"")).IsTrue();
    }

    // 8x8 transparent RGBA PNG (stdlib-generated once); enough for a real encode.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAADUlEQVR4nGNgGAUgAAABCAABgukLHQAAAABJRU5ErkJggg==";

    [Test]
    public async Task the_embed_workflow_encodes_end_to_end_with_the_vendored_ktx()
    {
        var repoRoot = RepoRootWithKtx();
        if (repoRoot is null) return;

        var png = Convert.FromBase64String(TinyPngBase64);
        var path = Path.Combine(Path.GetTempPath(), $"paradise_ktx5_{Guid.NewGuid():N}.glb");
        try
        {
            File.WriteAllBytes(path, BuildRunnerTests.EmbeddedImageGlb(png, "Wall_Albedo"));
            var log = new CollectingLogger();

            var result = GlbTextureWorkflows.ConvertEmbeddedTextures(path, repoRoot, log);

            // Filtered to Error: the delegate this replaced was an `error:` callback, so the
            // assertion has always meant "nothing went wrong". A CollectingLogger keeps every
            // level, and these workflows narrate their progress at Information ("KTX2 image: …"),
            // which would fail an unfiltered comparison for no reason.
            await Assert.That(string.Join("\n", log.MessagesAtLeast(LogLevel.Error))).IsEqualTo("");
            await Assert.That(result).IsEqualTo(ConversionResult.ConvertedAllTextures);

            await Assert.That(GlbBinary.TryRead(path, out var converted, out var bin)).IsTrue();
            var image = (System.Text.Json.Nodes.JsonObject)converted["images"]![0]!;
            await Assert.That((string?)image["mimeType"]).IsEqualTo("image/ktx2");
            await Assert.That(converted["textures"]![0]!["extensions"]?["KHR_texture_basisu"]?["source"]).IsNotNull();

            var view = (System.Text.Json.Nodes.JsonObject)converted["bufferViews"]![image["bufferView"]!.GetValue<int>()]!;
            var offset = (int?)view["byteOffset"] ?? 0;
            var length = view["byteLength"]!.GetValue<int>();
            await Assert.That(Ktx2Header.IsValid(bin.AsSpan(offset, length).ToArray(), out var validationError)).IsTrue();
            await Assert.That(validationError).IsEqualTo("");

            // No partial file left beside the rewritten GLB: outputs land by temp-then-rename.
            await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.partial")).IsEmpty();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task the_externalize_workflow_writes_sidecars_and_a_geometry_only_glb()
    {
        var repoRoot = RepoRootWithKtx();
        if (repoRoot is null) return;

        var png = Convert.FromBase64String(TinyPngBase64);
        var directory = Path.Combine(Path.GetTempPath(), $"paradise_ext_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wall.glb");
        try
        {
            File.WriteAllBytes(path, BuildRunnerTests.EmbeddedImageGlb(png, "Wall_Albedo"));
            var log = new CollectingLogger();

            var result = GlbTextureWorkflows.ExternalizeTextures(path, repoRoot, log);

            // Filtered to Error: the delegate this replaced was an `error:` callback, so the
            // assertion has always meant "nothing went wrong". A CollectingLogger keeps every
            // level, and these workflows narrate their progress at Information ("KTX2 image: …"),
            // which would fail an unfiltered comparison for no reason.
            await Assert.That(string.Join("\n", log.MessagesAtLeast(LogLevel.Error))).IsEqualTo("");
            await Assert.That(result).IsEqualTo(ConversionResult.ConvertedAllTextures);
            var sidecar = File.ReadAllBytes(Path.Combine(directory, "wall_0.ktx2"));
            await Assert.That(Ktx2Header.IsValid(sidecar, out _)).IsTrue();
            await Assert.That(GlbBinary.TryRead(path, out var gltf, out var bin)).IsTrue();
            await Assert.That((string?)gltf["images"]![0]!["uri"]).IsEqualTo("wall_0.ktx2");
            await Assert.That(bin.Length).IsEqualTo(0);

            // Idempotent: the second run has nothing embedded left to do.
            await Assert.That(GlbTextureWorkflows.ExternalizeTextures(path, repoRoot, log)).IsEqualTo(ConversionResult.NoConvertibleTextures);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? RepoRootWithKtx()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "third_party"))) dir = dir.Parent;

        if (dir is not null && KtxTool.Find(dir.FullName) is not null) return dir.FullName;

        // CI installs ktx on purpose; there a skip would hide a broken install step.
        if (Environment.GetEnvironmentVariable("PARADISE_REQUIRE_KTX") is not null)
        {
            Assert.Fail("PARADISE_REQUIRE_KTX is set but KtxTool.Find found no ktx.");
        }

        Skip.Test("ktx (KTX-Software v5) not available — vendored tool missing on this platform.");
        return null;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string FakeTool(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"paradise_fake_ktx_{Guid.NewGuid():N}");
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
