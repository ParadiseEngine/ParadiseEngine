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

    /// <summary>Reconciles once at startup (drifted sidecars are the normal starting state, and that is what <c>--dry-run</c> reports), then watches until interrupted.</summary>
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

        if (!KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder, out var ktxProblem) && ktxProblem is not null)
        {
            Console.Error.WriteLine($"warning: {ktxProblem}");
        }

        var editorMode = new WatchEditorMode(editor);
        ProjectOutputTarget Target() => editorMode.IsOn ? ProjectOutputTarget.Play : ProjectOutputTarget.Build;
        string OutputPath() => fileSystem.ConvertPathToInternal(layout.OutputFor(Target()));

        using var signals = new WatchSignals();
        using var watcher = new AssetWatcher(fileSystem, layout, maintainer, Console.WriteLine);
        using var watchTray = WatchTray.Create(
            new WatchTrayHooks(
                Stop: signals.RequestStop,
                Rebuild: build ? signals.RequestRebuild : null,
                OpenOutput: () => ShellFolders.Open(OutputPath()),
                Editor: editorMode,
                ToggleEditor: () =>
                {
                    var on = editorMode.Toggle();
                    Console.WriteLine(on
                        ? "watch: play mode on — asset changes rebuild .editor/play"
                        : "watch: play mode off — asset changes rebuild build/");
                }),
            enabled: tray);
        Console.CancelKeyPress += (_, e) =>
        {
            // Handled so the watcher is put down rather than shot mid-write; a second Ctrl+C is
            // the OS's.
            e.Cancel = true;
            signals.RequestStop();
        };

        watcher.Start();
        Console.WriteLine($"watch: watching {Display(fileSystem, layout.Assets)} — Ctrl+C to stop");
        Console.WriteLine(editorMode.IsOn
            ? "watch: play mode on — asset changes rebuild .editor/play"
            : "watch: play mode off — asset changes rebuild build/");

        var session = new WatchSession(
            signals,
            watchTray,
            drain: () => watcher.Drain().Changes,
            rebuild: build ? () => watcher.Rebuild(profile, Target(), encoder) : null,
            log: Console.WriteLine,
            error: message => Console.Error.WriteLine(message),
            outputDisplay: () => Display(fileSystem, layout.OutputFor(Target())),
            quiet: AssetWatcher.Debounce);

        watchTray.Run(session.Run, Console.WriteLine);

        Console.WriteLine("watch: stopped");
        return 0;
    }

    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string? profile, bool editor)
    {
        // Same probe as `tools doctor`, in its order: PARADISE_KTX_PATH, a vendored
        // third_party/tools/KTX-Software under the project root, the tools-install cache, PATH.
        if (!KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder, out var ktxProblem) && ktxProblem is not null)
        {
            Console.Error.WriteLine($"warning: {ktxProblem}");
        }

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
        // An absent --profile means the built-in defaults (TOML), not the scaffolded dev profile.
        Console.WriteLine("  paradise assets build --profile dev");
        return 0;
    }

    /// <summary>Launches Blender to regenerate the Asset Browser catalogue; only <c>bpy</c> can write a <c>.blend</c>, so the generator lives in the addon.</summary>
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

        // The extension module is bl_ext.<repo>.paradise_assets and <repo> is the user's choice;
        // hard-coding "user_default" breaks on another machine.
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

        // Zero even when something is missing: a report that fails the shell cannot be run
        // casually or in a prompt. `build` is what refuses.
        return 0;
    }

    /// <summary>Elevation is passed through here and NOWHERE else: a build must never raise a UAC prompt, while a person typing <c>tools install</c> has asked for exactly that.</summary>
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

        var rid = ToolLocations.HostRid();
        var output = ToolLocations.InstallRoot(name, version);

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

    private static string Display(IFileSystem fileSystem, UPath path) => fileSystem.ConvertPathToInternal(path);
}
