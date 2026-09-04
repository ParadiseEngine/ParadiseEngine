using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>The verbs' console rendering. Logic lives in the pipeline library; this prints.</summary>
internal static class Verbs
{
    /// <param name="fix">
    /// Catches stale reference paths up to where their guids now live. Only over a tree with no
    /// errors: a duplicate guid resolves to the ordinal-first asset, and fixing before that error
    /// was shown would rewrite paths toward an arbitrary winner the author never saw named.
    /// </param>
    public static int Verify(IFileSystem fileSystem, AssetProjectLayout layout, bool fix, IReadOnlyList<IAssetImporter>? importers = null)
    {
        // One scan for both passes: the fix rewrites document bodies only, never files or
        // identities, so the index it was taken over still describes the tree after it.
        var index = AssetIndex.Scan(fileSystem, layout.Assets, IgnoreRules(fileSystem, layout));
        var findings = ProjectVerifier.Verify(fileSystem, layout, index, importers);

        if (fix && findings.All(finding => finding.Severity != VerifySeverity.Error))
        {
            foreach (var repaired in ReferenceRepair.Fix(fileSystem, layout, index, importers))
            {
                Console.WriteLine($"fixed: {Display(fileSystem, repaired.Path)}");
                foreach (var repointed in repaired.Repointed) Console.WriteLine($"       {repointed}");
            }

            findings = ProjectVerifier.Verify(fileSystem, layout, index, importers);
        }
        else if (fix)
        {
            Console.WriteLine("verify: not fixing paths while the tree has errors — resolve those first");
        }

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
        bool tray,
        IReadOnlyList<IAssetImporter> importers)
    {
        var log = PipelineLog.For(fileSystem, layout);
        var maintainer = new SidecarMaintainer(fileSystem, layout, log, dryRun, IgnoreRules(fileSystem, layout), importers);
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
        using var watcher = new AssetWatcher(fileSystem, layout, maintainer, log, importers: importers);
        var minted = watcher.MintReferences();
        if (minted > 0) Console.WriteLine($"watch: {minted} mesh, skeleton and clip document(s) minted");
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

    /// <summary>A manifest the watch cannot read is reported by the first rebuild; until then nothing is ignored, which mints nothing wrong because verify refuses the tree anyway.</summary>
    private static AssetIgnoreRules IgnoreRules(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        try
        {
            return ProjectManifest.Load(fileSystem, layout.Manifest).Ignore;
        }
        catch (ProjectManifestException error)
        {
            Console.Error.WriteLine($"warning: {error.Message}");
            return AssetIgnoreRules.None;
        }
    }

    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string? profile, bool editor, IReadOnlyList<IAssetImporter> importers)
    {
        // Same probe as `tools doctor`, in its order: PARADISE_KTX_PATH, a vendored
        // third_party/tools/KTX-Software under the project root, the tools-install cache, PATH.
        if (!KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder, out var ktxProblem) && ktxProblem is not null)
        {
            Console.Error.WriteLine($"warning: {ktxProblem}");
        }

        var runner = new BuildRunner(
            fileSystem, layout, encoder,
            logger: PipelineLog.For(fileSystem, layout),
            importers: importers);
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

    public static int Move(IFileSystem fileSystem, AssetProjectLayout layout, UPath from, UPath to, IReadOnlyList<IAssetImporter>? importers = null)
    {
        var result = AssetMover.Move(fileSystem, layout, from, to, PipelineLog.For(fileSystem, layout), importers);

        foreach (var error in result.Errors) Console.Error.WriteLine($"error: {error}");
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");

        // The counts print either way: a failure after the files moved is exactly when the
        // author must know the tree changed.
        var summary = $"{result.Moved.Count} file(s) moved, {result.Rewritten.Count} document(s) rewritten, {result.Warnings.Count} warning(s)";
        Console.WriteLine(result.Succeeded ? $"mv: {summary}" : $"mv: FAILED with {result.Errors.Count} error(s) — {summary}");
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>A query, so it exits 0: who references the asset, then what it references.</summary>
    public static int Refs(IFileSystem fileSystem, AssetProjectLayout layout, UPath target, bool transitive, IReadOnlyList<IAssetImporter>? importers = null)
    {
        var ignore = IgnoreRules(fileSystem, layout);
        var index = AssetIndex.Scan(fileSystem, layout.Assets, ignore);
        if (!index.Contains(target))
        {
            Console.Error.WriteLine($"refs: '{Display(fileSystem, target)}' is not a file under assets/");
            return 1;
        }

        var graph = ReferenceGraph.Build(fileSystem, layout, index, ignore, importers);
        if (index.IdentityOf(target) is not { } guid)
        {
            Console.Error.WriteLine($"refs: '{index.Relative(target)}' has no identity (no readable sidecar), so nothing can reference it");
            return 1;
        }

        Console.WriteLine($"{index.Relative(target)} ({DocumentGuid.Format(guid)})");
        Console.WriteLine("referenced by:");
        var dependents = graph.DependentsOf(guid);
        if (dependents.Count == 0) Console.WriteLine("  (nothing)");
        foreach (var edge in dependents.OrderBy(edge => edge.ReferrerPath.FullName, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {index.Relative(edge.ReferrerPath)}: in {edge.Where} -> {edge.Path}");
        }

        if (transitive)
        {
            var direct = dependents.Select(edge => edge.Referrer).ToHashSet();
            var beyond = graph.TransitiveDependentsOf(guid).Where(referrer => !direct.Contains(referrer))
                .Select(referrer => index.Find(referrer) is { } file ? index.Relative(file) : DocumentGuid.Format(referrer))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            Console.WriteLine("and, through those:");
            if (beyond.Count == 0) Console.WriteLine("  (nothing)");
            foreach (var name in beyond) Console.WriteLine($"  {name}");
        }

        Console.WriteLine("references:");
        var dependencies = graph.DependenciesOf(guid);
        if (dependencies.Count == 0) Console.WriteLine("  (nothing)");
        foreach (var edge in dependencies)
        {
            var where = index.Find(edge.Target) is { } asset ? index.Relative(asset) : $"{edge.Path} (MISSING)";
            Console.WriteLine($"  in {edge.Where} -> {where} ({DocumentGuid.Format(edge.Target)})");
        }

        if (graph.Unreadable.Count > 0)
        {
            Console.WriteLine($"{graph.Unreadable.Count} file(s) could not be checked: {string.Join(", ", graph.Unreadable.Select(index.Relative))}");
        }

        return 0;
    }

    public static int Remove(IFileSystem fileSystem, AssetProjectLayout layout, UPath target, bool force, bool dryRun, IReadOnlyList<IAssetImporter>? importers = null)
    {
        var result = AssetRemover.Remove(fileSystem, layout, target, force, dryRun, PipelineLog.For(fileSystem, layout), importers);

        foreach (var error in result.Errors) Console.Error.WriteLine($"error: {error}");
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
        foreach (var edge in result.Dangling)
        {
            Console.WriteLine($"  {Display(fileSystem, edge.ReferrerPath)}: in {edge.Where} -> {edge.Path}");
        }

        foreach (var removed in result.Removed) Console.WriteLine(dryRun ? $"would remove: {removed}" : $"removed: {removed}");

        var summary = $"{result.Removed.Count} file(s) removed, {result.Dangling.Count} reference(s) left dangling";
        Console.WriteLine(result.Succeeded ? $"rm: {summary}" : $"rm: FAILED — {summary}");
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>One GLB, or every GLB under a directory with <paramref name="all"/>.</summary>
    public static int Extract(IFileSystem fileSystem, AssetProjectLayout layout, UPath target, bool all, ConflictResolution resolution, IReadOnlyList<IAssetImporter>? importers = null)
    {
        var targets = new List<UPath>();
        if (fileSystem.DirectoryExists(target))
        {
            if (!all)
            {
                Console.Error.WriteLine($"extract: '{Display(fileSystem, target)}' is a directory; pass --all to extract every GLB under it");
                return 1;
            }

            var ignore = IgnoreRules(fileSystem, layout);
            targets.AddRange(fileSystem.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Where(path => MeshContainer.IsMesh(path) && !ignore.Matches(layout.Assets, path))
                .OrderBy(p => p.FullName, StringComparer.Ordinal));
        }
        else
        {
            targets.Add(target);
        }

        var failed = 0;
        var log = PipelineLog.For(fileSystem, layout);
        // The same minting authority `watch` runs, started for this command; no watcher is alive
        // to hold a quarantined identity, so there is none to lose.
        var maintainer = new SidecarMaintainer(fileSystem, layout, log, ignore: IgnoreRules(fileSystem, layout), importers: importers);
        foreach (var glb in targets)
        {
            var result = AssetExtractor.Extract(fileSystem, layout, glb, importers, resolution, log, maintainer: maintainer);
            foreach (var error in result.Errors) Console.Error.WriteLine($"error: {error}");
            foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
            foreach (var written in result.Written) Console.WriteLine($"wrote: {written}");
            foreach (var kept in result.Kept) Console.WriteLine($"kept: {kept}");
            if (!result.Succeeded) failed++;
        }

        Console.WriteLine($"extract: {targets.Count} glb(s), {failed} failed");
        return failed == 0 ? 0 : 1;
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
