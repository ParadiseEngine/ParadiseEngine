using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>The verbs' console rendering. Logic lives in the pipeline library; this prints.</summary>
internal static class Verbs
{
    public static int Verify(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        var findings = ProjectVerifier.Verify(fileSystem, layout);
        foreach (var finding in findings)
        {
            var severity = finding.Severity == VerifySeverity.Error ? "error" : "warning";
            Console.WriteLine($"{severity}: {Display(fileSystem, finding.Path)}: {finding.Message}");
        }

        var errors = findings.Count(finding => finding.Severity == VerifySeverity.Error);
        Console.WriteLine($"verify: {errors} error(s), {findings.Count - errors} warning(s)");
        return errors == 0 ? 0 : 1;
    }

    public static int PrefabCheck(IFileSystem fileSystem, AssetProjectLayout layout, bool fix)
    {
        var results = Paradise.Assets.Pipeline.PrefabCheck.Run(fileSystem, layout, fix);
        var failed = 0;
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case PrefabCheckOutcome.Invalid:
                    Console.WriteLine($"error: {Display(fileSystem, result.Path)}: {result.Message}");
                    failed++;
                    break;

                case PrefabCheckOutcome.NotCanonical:
                    Console.WriteLine($"error: {Display(fileSystem, result.Path)}: not in canonical form (run prefab-check --fix)");
                    failed++;
                    break;

                case PrefabCheckOutcome.Rewritten:
                    Console.WriteLine($"fixed: {Display(fileSystem, result.Path)}");
                    break;
            }
        }

        Console.WriteLine($"prefab-check: {results.Count} document(s), {failed} problem(s)");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Keeps sidecars in step with the assets while you work, and rebuilds after each settled
    /// change. Runs until interrupted.
    /// </summary>
    /// <remarks>
    /// Reconciles once at startup, because a project whose sidecars drifted before anyone was
    /// watching is the normal starting state — and it is what makes <c>--dry-run</c> useful as a
    /// "what is wrong right now" report.
    /// </remarks>
    public static int Watch(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        string? profile,
        bool editor,
        bool dryRun,
        bool build,
        bool tray = true)
    {
        var maintainer = new SidecarMaintainer(fileSystem, layout, Console.WriteLine, dryRun);
        var settled = maintainer.Reconcile();
        Console.WriteLine(dryRun
            ? $"watch: {settled} sidecar(s) would be brought up to date (dry run — nothing written)"
            : $"watch: {settled} sidecar(s) brought up to date");

        if (dryRun) return 0;

        KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder);
        var target = editor ? ProjectOutputTarget.Play : ProjectOutputTarget.Build;
        var output = fileSystem.ConvertPathToInternal(layout.OutputFor(target));

        using var signals = new WatchSignals();
        using var watcher = new AssetWatcher(fileSystem, layout, maintainer, Console.WriteLine);
        using var watchTray = WatchTray.Create(
            new WatchTrayHooks(
                Stop: signals.RequestStop,
                Rebuild: build ? signals.RequestRebuild : null,
                OpenOutput: () => ShellFolders.Open(output)),
            enabled: tray);
        Console.CancelKeyPress += (_, e) =>
        {
            // Handled, so the loop can put the watcher down rather than the process being shot
            // mid-write. A second Ctrl+C is the OS's to act on. The tray's Stop is the same
            // signal, so both paths leave through the loop rather than through the OS.
            e.Cancel = true;
            signals.RequestStop();
        };

        watcher.Start();
        Console.WriteLine($"watch: watching {Display(fileSystem, layout.Assets)} — Ctrl+C to stop");
        if (watchTray.IsAvailable)
        {
            Console.WriteLine("watch: tray icon is up (right-click to stop, rebuild, or open the build folder)");
        }

        new WatchSession(
            signals,
            watchTray,
            drain: watcher.Drain,
            rebuild: build ? () => watcher.Rebuild(profile, target, encoder) : null,
            log: Console.WriteLine,
            error: message => Console.Error.WriteLine(message),
            outputDisplay: Display(fileSystem, layout.OutputFor(target)),
            quiet: AssetWatcher.Debounce).Run();

        Console.WriteLine("watch: stopped");
        return 0;
    }

    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string? profile, bool editor)
    {
        // A vendored third_party/tools/KTX-Software under the project root wins; PATH and
        // PARADISE_KTX_PATH are the fallbacks — the same probe order as KtxCreate itself.
        KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder);

        var runner = new BuildRunner(
            fileSystem, layout, encoder,
            log: Console.WriteLine,
            warn: message => Console.Error.WriteLine($"warning: {message}"));
        var result = runner.Run(profile, editor ? Paradise.Assets.Project.ProjectOutputTarget.Play : Paradise.Assets.Project.ProjectOutputTarget.Build);

        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }

        Console.WriteLine(result.Succeeded
            ? $"build: {result.AssetCount} asset(s) into {Display(fileSystem, result.Output)}"
            : $"build: FAILED with {result.Errors.Count} error(s)");
        return result.Succeeded ? 0 : 1;
    }

    public static int Clean(IFileSystem fileSystem, AssetProjectLayout layout, bool keepEditor)
    {
        foreach (var removed in ProjectCleaner.Clean(fileSystem, layout, keepEditor))
        {
            Console.WriteLine($"removed: {Display(fileSystem, removed)}");
        }

        return 0;
    }

    /// <summary>Creates a project and tells the user what to run next.</summary>
    public static int New(IFileSystem fileSystem, UPath root, string name)
    {
        IReadOnlyList<ProjectScaffold.ScaffoldedFile> written;
        try
        {
            written = ProjectScaffold.Create(fileSystem, root, name);
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"paradise: {error.Message}");
            return 1;
        }

        foreach (var file in written)
        {
            Console.WriteLine($"  {Display(fileSystem, file.Path)}  — {file.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"created '{name}' ({written.Count} files). Next:");
        Console.WriteLine($"  cd {Display(fileSystem, root)}");
        Console.WriteLine("  paradise assets verify");
        // Named explicitly: an absent --profile means the built-in defaults (TOML), not the
        // scaffolded dev profile, and the first-run path should build what the scaffold wrote.
        Console.WriteLine("  paradise assets build --profile dev");
        return 0;
    }

    /// <summary>
    /// Regenerates the Asset Browser catalogue for a project.
    /// </summary>
    /// <remarks>
    /// <b>This launches Blender, because only Blender can write a <c>.blend</c>.</b> The generator
    /// itself is Python and lives in the addon, where it has to live anyway — it needs <c>bpy</c>
    /// to mark a datablock as an asset. So this verb is a launcher, and it fails with the reason
    /// rather than a stack trace when Blender or the addon is missing.
    /// </remarks>
    public static int Catalogue(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        var blender = ProcessTools.FindExecutable(
            Environment.GetEnvironmentVariable("PARADISE_BLENDER_PATH"), [], "blender");

        if (blender is null)
        {
            Console.Error.WriteLine(
                "paradise: no blender on PATH — set PARADISE_BLENDER_PATH to its executable. " +
                "Only Blender can write a .blend, so the catalogue needs it.");
            return 1;
        }

        var root = fileSystem.ConvertPathToInternal(layout.Root).Replace("\\", "/");

        // The addon is installed as an EXTENSION, so its module is bl_ext.<repo>.paradise_assets
        // and the repo name is whatever the user called it. Finding it through addon_utils rather
        // than hard-coding "user_default" is what keeps this working on someone else's machine.
        var script =
            "import addon_utils,importlib;" +
            "m=next(x.__name__ for x in addon_utils.modules() if x.__name__.endswith('paradise_assets'));" +
            "c=importlib.import_module(m+'.catalogue');" +
            $"print('CATALOGUE', *c.build(r'{root}'))";

        Console.WriteLine($"building the prefab catalogue with {blender}");
        var result = ProcessTools.Run(
            blender, $"--background --python-expr \"{script}\"", timeoutMilliseconds: 10 * 60 * 1000);

        var reported = result.Stdout
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("CATALOGUE", StringComparison.Ordinal));

        if (!result.Succeeded || reported is null)
        {
            Console.Error.WriteLine(
                "paradise: the catalogue build failed. The most likely cause is that the " +
                "paradise_assets addon is not installed in that Blender — it owns the generator.");
            if (!string.IsNullOrWhiteSpace(result.Stderr)) Console.Error.WriteLine(result.Stderr.TrimEnd());
            return 1;
        }

        Console.WriteLine(reported.Trim());
        Console.WriteLine(
            $"catalogue: register '{Path.Combine(root, ".editor", "asset-library")}' " +
            "as an asset library in Blender's preferences to see it.");
        return 0;
    }

    /// <summary>Reports every build tool: what was found, and what to do about what was not.</summary>
    public static int ToolsDoctor(string repoRoot)
    {
        var findings = ToolReport.Collect(repoRoot);
        var missing = 0;

        foreach (var finding in findings)
        {
            if (finding.Status == ToolStatus.Ok)
            {
                Console.WriteLine($"  {finding.Name,-8} {finding.Version,-14} ok    {finding.Path}");
                continue;
            }

            missing++;
            Console.WriteLine($"  {finding.Name,-8} {"—",-14} MISSING");
            if (finding.Fix is { } fix) Console.WriteLine($"           -> {fix}");
        }

        Console.WriteLine();
        Console.WriteLine(missing == 0
            ? $"tools: {findings.Count} tool(s), all present"
            : $"tools: {missing} of {findings.Count} missing");

        // Zero even when something is missing: `doctor` REPORTS, and a report that fails the shell
        // cannot be run casually or in a prompt. `build` is what refuses to proceed.
        return 0;
    }

    /// <summary>Installs one tool through its vendored bootstrap.</summary>
    /// <remarks>
    /// Elevation is passed through here and NOWHERE else. Khronos ships Windows KTX as an NSIS
    /// installer that requires admin, so the bootstrap refuses that format unless asked — a build
    /// must never raise a UAC prompt, while a person typing <c>tools install</c> has asked for
    /// exactly this.
    /// </remarks>
    public static int ToolsInstall(string repoRoot, string tool)
    {
        var name = tool.ToLowerInvariant();
        if (name is not ("ktx" or "slang"))
        {
            Console.Error.WriteLine($"paradise: unknown tool '{tool}' (known: ktx, slang)");
            return 2;
        }

        var project = Path.Combine(repoRoot, "tools", name, name == "ktx" ? "KtxBootstrap.csproj" : "SlangBootstrap.csproj");
        if (!File.Exists(project))
        {
            Console.Error.WriteLine(
                $"paradise: no bootstrap at '{project}' — 'tools install' needs an engine checkout; " +
                "from a game repo, install the tool yourself (see 'paradise tools doctor').");
            return 1;
        }

        var manifest = Path.Combine(repoRoot, "tools", name, $"{name}.manifest.json");
        var version = ManifestVersion(manifest);
        if (version is null)
        {
            Console.Error.WriteLine($"paradise: could not read a version from '{manifest}'");
            return 1;
        }

        var rid = HostRid();
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var output = Path.Combine(packages, $"_{name}", version, rid);

        Console.WriteLine($"installing {name} {version} ({rid}) into {output}");

        var arguments = $"run --project \"{project}\" -- --manifest \"{manifest}\" --rid {rid} --out \"{output}\"";
        if (name == "ktx") arguments += " --elevate";

        var result = ProcessTools.Run("dotnet", arguments, timeoutMilliseconds: 20 * 60 * 1000);
        if (!string.IsNullOrWhiteSpace(result.Stdout)) Console.WriteLine(result.Stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.Stderr)) Console.Error.WriteLine(result.Stderr.TrimEnd());

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"paradise: installing {name} failed");
            return 1;
        }

        Console.WriteLine($"{name} installed. Run 'paradise tools doctor' to confirm.");
        return 0;
    }

    private static string? ManifestVersion(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.GetProperty("version").GetString();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    private static string HostRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        return $"{os}-{architecture}";
    }

    /// <summary>
    /// A path as the user's shell knows it. Findings print OS paths (<c>C:\…</c>, not Zio's
    /// internal form) because they exist to be opened, copied, and pasted.
    /// </summary>
    private static string Display(IFileSystem fileSystem, UPath path) => fileSystem.ConvertPathToInternal(path);
}
