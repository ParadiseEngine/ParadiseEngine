using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One GLB <c>to-blend</c> replaced, or with a dry run would replace, and the model it became.</summary>
/// <param name="Glb">The GLB, relative to <c>assets/</c>.</param>
/// <param name="Asset">The asset collection it became in a family's <c>.blend</c>, whose GUID is the GLB's own; null for a <c>.blend</c> of its own.</param>
public sealed record BlendMember(string Glb, ModelAsset? Asset);

/// <summary>A <c>.blend</c> that replaced its GLBs, or with a dry run would, every model verified against the GLB it replaces.</summary>
/// <param name="Blend">The <c>.blend</c>, relative to <c>assets/</c>.</param>
public sealed record BlendTarget(string Blend, IReadOnlyList<BlendMember> Members);

/// <summary>A GLB left as it is, and why.</summary>
public sealed record KeptGlb(string Glb, string Reason);

/// <summary>What <c>to-blend</c> did or, with a dry run, would do.</summary>
/// <param name="Rewritten">The documents repointed at a <c>.blend</c> or brought up to date with its conversion, relative to <c>assets/</c>.</param>
/// <param name="Errors">What stopped the verb as a whole: no Blender, a GLB that is not one.</param>
public sealed record BlendMigrationResult(
    IReadOnlyList<BlendTarget> Targets,
    IReadOnlyList<KeptGlb> Kept,
    IReadOnlyList<string> Rewritten,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// The <c>to-blend</c> verb: GLB model sources replaced by <c>.blend</c> sources that build the same
/// models, every document identity kept. Blender imports each GLB and saves a <c>.blend</c> that
/// references its textures where they are; the <c>.blend</c> is converted as any would be, and a
/// model is replaced only when its conversion matches the GLB (<see cref="ModelSignature"/>).
/// </summary>
/// <remarks>
/// <para>
/// A GLB alone becomes <c>&lt;stem&gt;.blend</c>, one model under the GLB's own sidecar identity:
/// its extraction record, settings and every document stay as they were, only the path they name
/// changes. With families, GLBs of one directory whose stems differ only in a trailing
/// <c>_&lt;8-12 hex digits&gt;</c> become one <c>&lt;family&gt;.blend</c> holding an asset collection per
/// member, named by its stem and laid side by side with its origin at the collection's instance
/// offset. Each collection's <c>paradise_guid</c> is its GLB's sidecar GUID, so the identity a
/// member had stays traceable as its asset's. The <c>.blend</c> mints one identity; each member's
/// record moves under its asset, and its documents are repointed at the <c>.blend</c> and that asset.
/// </para>
/// <para>
/// Blender merges datablocks by name across members, so a material two members define alike is
/// shared; one they define differently keeps a renamed copy, which the comparison refuses, and
/// the family is rebuilt without the members that do not match. Re-running finds no GLB to
/// replace; a <c>.blend</c> already at a target's path is never overwritten.
/// </para>
/// </remarks>
public static partial class BlendMigration
{
    private const int BlenderTimeoutMilliseconds = 60 * 60 * 1000;

    /// <summary>Space left between two members of a family laid out side by side, in meters.</summary>
    private const float Gap = 2f;

    [GeneratedRegex("^(?<family>.+)_[0-9a-fA-F]{8,12}$")]
    private static partial Regex FamilyPattern();

    private sealed class Member(UPath glb, string stem, SidecarMeta meta, ModelSignature before)
    {
        public UPath Glb { get; } = glb;

        public string Stem { get; } = stem;

        public SidecarMeta Meta { get; } = meta;

        public ModelSignature Before { get; } = before;
    }

    private sealed class Plan(UPath blend, List<Member> members, bool family)
    {
        public UPath Blend { get; } = blend;

        public List<Member> Members { get; } = members;

        /// <summary>Whether the <c>.blend</c> holds one asset collection per member rather than being one model.</summary>
        public bool Family { get; } = family;

        public UPath Staged => Blend.GetDirectory() / $".{Blend.GetName()}.paradise-staging";

        public BlenderModelConverter.Export Export { get; set; }

        /// <summary>The asset collection a family member becomes: named by its stem, identified by its GLB's GUID.</summary>
        public ModelAsset? Asset(Member member) => Family ? new ModelAsset(member.Meta.Guid, member.Stem) : null;
    }

    /// <summary>Replaces the GLBs at or under <paramref name="paths"/>; with <paramref name="dryRun"/>, builds and verifies each <c>.blend</c> beside its GLBs and removes it again, writing nothing else.</summary>
    public static BlendMigrationResult Run(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        IReadOnlyList<UPath> paths,
        bool families,
        bool dryRun,
        IReadOnlyList<IAssetImporter>? importers = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(paths);

        var chain = importers ?? AssetImporters.All;
        var log = logger ?? NullLogger.Instance;
        var errors = new List<string>();
        var kept = new List<KeptGlb>();

        AssetIgnoreRules ignore;
        try
        {
            ignore = ProjectManifest.Load(fileSystem, layout.Manifest).Ignore;
        }
        catch (ProjectManifestException error)
        {
            return new BlendMigrationResult([], [], [], [error.Message]);
        }

        var index = AssetIndex.Scan(fileSystem, layout.Assets, ignore);
        var glbs = Collect(fileSystem, layout, index, paths, errors);
        if (errors.Count > 0) return new BlendMigrationResult([], [], [], errors);

        var members = new List<Member>();
        foreach (var glb in glbs)
        {
            if (Read(fileSystem, index, glb, out var reason) is { } member) members.Add(member);
            else kept.Add(new KeptGlb(index.Relative(glb), reason!));
        }

        var graph = ReferenceGraph.Build(fileSystem, layout, index, ignore, chain);
        var plans = new List<Plan>();
        foreach (var group in members.GroupBy(member => (Directory: member.Glb.GetDirectory(), Name: families ? Family(member.Stem) : member.Stem)).OrderBy(group => group.Key.Directory.FullName + "/" + group.Key.Name, StringComparer.Ordinal))
        {
            var grouped = group.OrderBy(member => member.Stem, StringComparer.Ordinal).ToList();
            var plan = grouped.Count == 1
                ? new Plan(group.Key.Directory / $"{grouped[0].Stem}.blend", grouped, family: false)
                : new Plan(group.Key.Directory / $"{group.Key.Name}.blend", grouped, family: true);
            if (Refusal(fileSystem, index, graph, plan) is { } refusal)
            {
                kept.AddRange(plan.Members.Select(member => new KeptGlb(index.Relative(member.Glb), refusal)));
                continue;
            }

            plans.Add(plan);
        }

        if (plans.Count == 0) return new BlendMigrationResult([], kept, [], errors);

        if (BlenderModelConverter.FindBlender() is not { } blender || BlenderModelConverter.BlenderVersion(blender) is not { } version)
        {
            return new BlendMigrationResult([], kept, [], [$"to-blend needs Blender to build and convert each .blend; install it or set {BlenderModelConverter.BlenderPathEnvironmentVariable} to its executable"]);
        }

        var verified = new List<Plan>();
        try
        {
            // A family rebuilt without the members that did not match is built and checked again:
            // what is shared between the members that remain may differ.
            while (plans.Count > 0)
            {
                Build(fileSystem, index, plans, blender, kept);
                var retry = new List<Plan>();
                foreach (var plan in plans)
                {
                    if (!fileSystem.FileExists(plan.Staged)) continue;
                    var mismatched = Verify(fileSystem, index, plan, blender, kept);
                    if (mismatched.Count == 0)
                    {
                        verified.Add(plan);
                        continue;
                    }

                    Delete(fileSystem, plan.Staged);
                    var remaining = plan.Members.Except(mismatched).ToList();
                    if (remaining.Count == 0) continue;

                    var rebuilt = remaining.Count == 1
                        ? new Plan(remaining[0].Glb.GetDirectory() / $"{remaining[0].Stem}.blend", remaining, family: false)
                        : new Plan(plan.Blend, remaining, family: true);
                    if (Refusal(fileSystem, index, graph, rebuilt) is { } refusal) kept.AddRange(remaining.Select(member => new KeptGlb(index.Relative(member.Glb), refusal)));
                    else retry.Add(rebuilt);
                }

                plans = retry;
            }

            if (dryRun) return new BlendMigrationResult([.. verified.Select(plan => Target(index, plan))], kept, [], errors);

            var replaced = new List<Plan>();
            var rewritten = Replace(fileSystem, layout, index, ignore, chain, verified, replaced, version, log, errors);
            return new BlendMigrationResult([.. replaced.Select(plan => Target(index, plan))], kept, rewritten, errors);
        }
        finally
        {
            foreach (var plan in verified.Concat(plans)) Delete(fileSystem, plan.Staged);
        }
    }

    /// <summary>A family stem: the name without its trailing <c>_&lt;8-12 hex digits&gt;</c>, or the name itself.</summary>
    public static string Family(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var match = FamilyPattern().Match(stem);
        return match.Success ? match.Groups["family"].Value : stem;
    }

    private static BlendTarget Target(AssetIndex index, Plan plan)
        => new(index.Relative(plan.Blend), [.. plan.Members.Select(member => new BlendMember(index.Relative(member.Glb), plan.Asset(member)))]);

    /// <summary>Every <c>.glb</c> a path names and the manifest does not ignore: itself, or each one under a directory.</summary>
    private static List<UPath> Collect(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, IReadOnlyList<UPath> paths, List<string> errors)
    {
        var found = new SortedSet<UPath>(Comparer<UPath>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName)));
        foreach (var path in paths)
        {
            if (!path.IsInDirectory(layout.Assets, recursive: true))
            {
                errors.Add($"'{path}' is not under {layout.Assets}; to-blend replaces assets only");
            }
            else if (fileSystem.DirectoryExists(path))
            {
                found.UnionWith(index.Files.Where(file => file.IsInDirectory(path, recursive: true) && IsGlb(file) && !index.IsIgnored(file)));
            }
            else if (!fileSystem.FileExists(path))
            {
                errors.Add($"'{index.Relative(path)}' does not exist");
            }
            else if (!IsGlb(path))
            {
                errors.Add($"'{index.Relative(path)}' is not a .glb; to-blend replaces GLB model sources");
            }
            else if (!index.IsIgnored(path))
            {
                found.Add(path);
            }
        }

        return [.. found];
    }

    private static bool IsGlb(UPath path) => string.Equals(path.GetExtensionWithDot(), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>A GLB with its identity and signature; null with the reason it cannot be replaced.</summary>
    private static Member? Read(IFileSystem fileSystem, AssetIndex index, UPath glb, out string? reason)
    {
        reason = null;
        var sidecar = SidecarMeta.PathFor(glb);
        if (!fileSystem.FileExists(sidecar))
        {
            reason = "has no sidecar, so no identity to carry; run `paradise assets watch` first";
            return null;
        }

        try
        {
            var meta = SidecarMeta.Load(fileSystem, sidecar);
            var directory = glb.GetDirectory();
            var signature = ModelSignature.Read(fileSystem.ReadAllBytes(glb), uri => ReadRelative(fileSystem, directory, uri));
            return new Member(glb, glb.GetNameWithoutExtension()!, meta, signature);
        }
        catch (Exception error) when (error is SidecarMetaException or InvalidDataException)
        {
            reason = error.Message;
            return null;
        }
    }

    private static byte[]? ReadRelative(IFileSystem fileSystem, UPath directory, string uri)
    {
        try
        {
            var path = (directory / Uri.UnescapeDataString(uri)).ToAbsolute();
            return fileSystem.FileExists(path) ? fileSystem.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Why a plan cannot go ahead, or null: its <c>.blend</c> exists, or its members disagree on what a file shares, or something other than their documents names a member that loses its identity.</summary>
    private static string? Refusal(IFileSystem fileSystem, AssetIndex index, ReferenceGraph graph, Plan plan)
    {
        if (fileSystem.FileExists(plan.Blend) || fileSystem.FileExists(SidecarMeta.PathFor(plan.Blend)) || fileSystem.DirectoryExists(plan.Blend))
        {
            return $"'{index.Relative(plan.Blend)}' already exists; to-blend never overwrites";
        }

        if (!plan.Family) return null;

        var directories = plan.Members.Select(member => GlbImportSettings.ReadExtraction(member.Meta).Directory).Distinct().ToList();
        if (directories.Count > 1)
        {
            return $"its family '{index.Relative(plan.Blend)}' has members extracting to different [extract] directories ({string.Join(", ", directories.Select(directory => directory ?? "(project default)"))}), and one sidecar holds one";
        }

        var optimizations = plan.Members.Select(member => GlbImportSettings.ReadOptimization(member.Meta)).Distinct().ToList();
        if (optimizations.Count > 1)
        {
            return $"its family '{index.Relative(plan.Blend)}' has members with different [glb] optimize settings, and one sidecar holds one";
        }

        // A member's own identity is gone once it is an asset of the family's file: only a model
        // document can say which asset it means, so anything else naming the GLB would dangle.
        foreach (var member in plan.Members)
        {
            var foreign = graph.DependentsOf(member.Meta.Guid)
                .Where(edge => AssetClassifier.Classify(index.Root, edge.ReferrerPath, AssetIgnoreRules.None) != AssetClass.MeshReference)
                .Select(edge => index.Relative(edge.ReferrerPath))
                .Distinct()
                .ToList();
            if (foreign.Count > 0)
            {
                return $"'{index.Relative(member.Glb)}' is referenced by {string.Join(", ", foreign)}, which cannot name one asset of '{index.Relative(plan.Blend)}'; point them at its mesh documents first";
            }
        }

        return null;
    }

    /// <summary>Builds every plan's staged <c>.blend</c> in one Blender run; a plan Blender could not build keeps its GLBs.</summary>
    private static void Build(IFileSystem fileSystem, AssetIndex index, IReadOnlyList<Plan> plans, string blender, List<KeptGlb> kept)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ParadiseToBlend", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var script = Path.Combine(temporary, "to_blend.py");
            var jobs = Path.Combine(temporary, "jobs.json");
            var results = Path.Combine(temporary, "results.json");
            File.WriteAllText(script, BuilderScript);
            File.WriteAllText(jobs, new JsonArray([.. plans.Select(plan => (JsonNode)Job(fileSystem, plan))]).ToJsonString());

            string[] arguments =
            [
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", ProcessTools.QuoteArgument(script), "--", ProcessTools.QuoteArgument(jobs), ProcessTools.QuoteArgument(results),
            ];
            var run = ProcessTools.Run(blender, string.Join(' ', arguments), BlenderTimeoutMilliseconds);

            var failures = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (run.Succeeded && File.Exists(results) && JsonNode.Parse(File.ReadAllText(results)) is JsonObject answered)
            {
                foreach (var (staged, failure) in answered) failures[staged] = failure?.GetValue<string>();
            }

            foreach (var plan in plans)
            {
                var staged = fileSystem.ConvertPathToInternal(plan.Staged);
                var reason = !run.Succeeded ? run.Describe("Blender building the .blend files", BlenderTimeoutMilliseconds)
                    : !failures.TryGetValue(staged, out var failure) ? $"Blender did not build '{index.Relative(plan.Blend)}'.\n{run.Stdout}{run.Stderr}"
                    : failure is not null ? $"Blender could not build '{index.Relative(plan.Blend)}': {failure}"
                    : !File.Exists(staged) ? $"Blender reported '{index.Relative(plan.Blend)}' built but saved nothing"
                    : null;
                if (reason is null) continue;

                Delete(fileSystem, plan.Staged);
                kept.AddRange(plan.Members.Select(member => new KeptGlb(index.Relative(member.Glb), reason)));
            }
        }
        catch (JsonException error)
        {
            foreach (var plan in plans)
            {
                Delete(fileSystem, plan.Staged);
                kept.AddRange(plan.Members.Select(member => new KeptGlb(index.Relative(member.Glb), $"Blender's report of what it built could not be read: {error.Message}")));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// One plan as the builder script reads it. Members sit side by side along X, which the glTF
    /// importer keeps as Blender's X: each with its lowest X a gap past the one before, the
    /// offset it moved by being its collection's instance offset, which the conversion subtracts.
    /// </summary>
    private static JsonObject Job(IFileSystem fileSystem, Plan plan)
    {
        var members = new JsonArray();
        var cursor = 0f;
        foreach (var member in plan.Members)
        {
            var offset = 0f;
            if (plan.Family)
            {
                var (min, max) = XExtent(member.Before);
                offset = cursor - min;
                cursor += max - min + Gap;
            }

            members.Add((JsonNode)new JsonObject
            {
                ["glb"] = fileSystem.ConvertPathToInternal(member.Glb),
                ["asset"] = plan.Asset(member)?.Name,
                ["guid"] = plan.Asset(member) is { } asset ? DocumentGuid.Format(asset.Guid) : null,
                ["offset"] = new JsonArray(JsonValue.Create(offset), JsonValue.Create(0f), JsonValue.Create(0f)),
            });
        }

        var job = new JsonObject
        {
            ["staged"] = fileSystem.ConvertPathToInternal(plan.Staged),
            ["members"] = members,
            ["shared_materials"] = new JsonArray([.. SharedMaterials(plan).Select(name => (JsonNode)JsonValue.Create(name))]),
        };

        // Blender keys an imported clip at scene frames and exports it sampled at whole frames, so
        // the scene's rate has to be the clips' own or every clip is resampled at 24 per second.
        // The glTF add-on converts with fps × fps_base both ways, which carries a rate that is no
        // whole number (60.75) as a whole fps and a base.
        if (plan.Members.Select(member => member.Before.KeyRate).FirstOrDefault(rate => rate is not null) is { } keyRate)
        {
            var whole = MathF.Round(keyRate);
            var fps = MathF.Abs(keyRate - whole) < 1e-3f ? (int)whole : (int)MathF.Ceiling(keyRate);
            job["fps"] = new JsonArray(JsonValue.Create(fps), JsonValue.Create(fps == (int)whole && MathF.Abs(keyRate - whole) < 1e-3f ? 1.0 : (double)keyRate / fps));
        }

        return job;
    }

    private static (float Min, float Max) XExtent(ModelSignature signature)
    {
        var draws = signature.Draws.Where(draw => float.IsFinite(draw.Min.X) && float.IsFinite(draw.Max.X)).ToList();
        return draws.Count == 0 ? (0f, 0f) : (draws.Min(draw => draw.Min.X), draws.Max(draw => draw.Max.X));
    }

    /// <summary>The material names every member that has one defines alike — name, factors to within the comparison's tolerance, and texture bytes — so Blender keeps one of each.</summary>
    private static IEnumerable<string> SharedMaterials(Plan plan)
    {
        if (!plan.Family) return [];
        return plan.Members
            .SelectMany(member => member.Before.Draws.Select(draw => draw.Material).OfType<ModelSignature.MaterialData>())
            .Where(material => material.Name is not null)
            .GroupBy(material => material.Name!, StringComparer.Ordinal)
            .Where(group => group.Skip(1).All(material => Same(material, group.First())))
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal);
    }

    /// <summary>Whether <paramref name="member"/>'s material may be replaced by <paramref name="kept"/>, the one Blender keeps: the first member's, imported first.</summary>
    private static bool Same(ModelSignature.MaterialData member, ModelSignature.MaterialData kept)
        => member.AlphaMode == kept.AlphaMode && member.DoubleSided == kept.DoubleSided
            && ModelSignature.FactorsMatch(member.Factors, kept.Factors) && member.Textures.SequenceEqual(kept.Textures, StringComparer.Ordinal);

    /// <summary>Converts the staged <c>.blend</c> as any would be and compares each model with its GLB; the members that do not match, each kept with why.</summary>
    private static List<Member> Verify(IFileSystem fileSystem, AssetIndex index, Plan plan, string blender, List<KeptGlb> kept)
    {
        BlenderModelConverter.Export export;
        try
        {
            export = BlenderModelConverter.Convert(blender, fileSystem.ConvertPathToInternal(plan.Staged), ".blend");
        }
        catch (InvalidDataException error)
        {
            export = new BlenderModelConverter.Export(null, [], error.Message);
        }

        plan.Export = export;
        var mismatched = new List<Member>();
        foreach (var member in plan.Members)
        {
            var asset = plan.Asset(member);
            var reasons = new List<string>();
            if (export.Models is not { } models)
            {
                reasons.Add($"'{index.Relative(plan.Blend)}' did not convert: {export.Failure}");
            }
            else if (models.FirstOrDefault(model => model.Asset?.Guid == asset?.Guid) is not { Glb: { } glb })
            {
                reasons.Add(asset is null ? $"'{index.Relative(plan.Blend)}' converted to asset collections, not one model" : $"'{index.Relative(plan.Blend)}' converted without asset {asset}");
            }
            else
            {
                try
                {
                    var after = ModelSignature.Read(glb, _ => null);
                    reasons.AddRange(ModelSignature.Differences(member.Before, after));
                    reasons.AddRange(Unmappable(member, after));
                }
                catch (InvalidDataException error)
                {
                    reasons.Add($"its conversion is not a GLB the pipeline reads: {error.Message}");
                }
            }

            if (reasons.Count == 0) continue;
            mismatched.Add(member);
            kept.Add(new KeptGlb(index.Relative(member.Glb), $"'{index.Relative(plan.Blend)}'{(asset is null ? "" : $" [{asset.Name}]")} does not build the same: {string.Join("; ", reasons)}"));
        }

        return mismatched;
    }

    /// <summary>Recorded materials the converted GLB does not have under exactly one index of the same name, so their documents could not follow.</summary>
    private static IEnumerable<string> Unmappable(Member member, ModelSignature after)
    {
        foreach (var material in GlbImportSettings.ReadExtraction(member.Meta).Materials)
        {
            if (MaterialIndex(member.Before, after, material.Index) is null)
            {
                yield return $"recorded material #{material.Index} has no one counterpart by name in the conversion";
            }
        }
    }

    /// <summary>Where the material at <paramref name="index"/> before is after: the one index with its name.</summary>
    private static int? MaterialIndex(ModelSignature before, ModelSignature after, int index)
    {
        if (index < 0 || index >= before.MaterialNames.Count || before.MaterialNames[index] is not { } name) return null;
        var matches = after.MaterialNames.Select((candidate, at) => (candidate, at)).Where(pair => pair.candidate == name).ToList();
        return matches.Count == 1 ? matches[0].at : null;
    }

    /// <summary>Puts every verified plan in place: the <c>.blend</c>, its sidecar, its conversion, its documents repointed and minted and its GLBs gone. Answers the documents it rewrote.</summary>
    private static List<string> Replace(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, AssetIgnoreRules ignore, IReadOnlyList<IAssetImporter> chain,
        IReadOnlyList<Plan> plans, List<Plan> replaced, string blenderVersion, ILogger log, List<string> errors)
    {
        var rewritten = new List<string>();
        var repoint = new Dictionary<Guid, (AssetReference Source, ModelAsset? Asset)>();
        var carried = new List<Guid>();
        var placed = new List<UPath>();

        foreach (var plan in plans)
        {
            var converted = plan.Export.Models!.ToDictionary(model => model.Asset?.Guid ?? Guid.Empty, model => ModelSignature.Read(model.Glb, _ => null));
            var meta = plan.Family ? SidecarMeta.Mint() : Copy(plan.Members[0].Meta);
            meta.Importer = GlbImportSettings.GlbImporterName;

            var extractions = new List<GlbExtraction>();
            var clips = new List<CanonicalInlineTable>();
            foreach (var member in plan.Members)
            {
                var asset = plan.Asset(member)?.Guid;
                var signature = converted[asset ?? Guid.Empty];
                extractions.Add(Carried(member, signature, asset));
                clips.AddRange(GlbImportSettings.ReadClipSettings(member.Meta).Select(setting => Clip(setting, member.Before, signature, asset)));
            }

            // A GLB's container references are its own uris; a converted source names none.
            meta.RemoveSetting(GlbImportSettings.Domain);
            GlbImportSettings.WriteOptimization(meta, GlbImportSettings.ReadOptimization(plan.Members[0].Meta));
            GlbImportSettings.WriteClipSettings(meta, clips);
            GlbImportSettings.WriteExtractions(meta, GlbImportSettings.ReadExtraction(plan.Members[0].Meta).Directory, extractions);

            var moved = false;
            try
            {
                File.Move(fileSystem.ConvertPathToInternal(plan.Staged), fileSystem.ConvertPathToInternal(plan.Blend));
                moved = true;
                meta.Save(fileSystem, SidecarMeta.PathFor(plan.Blend));
                ModelSource.Store(fileSystem, plan.Blend, plan.Export, blenderVersion);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (moved)
                {
                    Delete(fileSystem, plan.Blend);
                    Delete(fileSystem, SidecarMeta.PathFor(plan.Blend));
                }

                errors.Add($"{index.Relative(plan.Blend)}: could not be put in place ({error.Message}); its GLBs are left as they are");
                continue;
            }

            placed.Add(plan.Blend);
            replaced.Add(plan);

            var source = new AssetReference(meta.Guid, index.Relative(plan.Blend));
            foreach (var member in plan.Members)
            {
                repoint[member.Meta.Guid] = (source, plan.Asset(member));
                if (!plan.Family) carried.Add(member.Meta.Guid);
                Delete(fileSystem, member.Glb);
                Delete(fileSystem, SidecarMeta.PathFor(member.Glb));
                LogReplaced(log, index.Relative(member.Glb), index.Relative(plan.Blend), plan.Asset(member)?.Name ?? "");
            }
        }

        // Every model document of a replaced GLB names the .blend, and the asset for a family's.
        foreach (var path in index.Files.Where(path => AssetClassifier.Classify(layout.Assets, path, ignore) == AssetClass.MeshReference))
        {
            MeshReferenceDocument document;
            try
            {
                document = MeshReferenceDocument.Load(fileSystem, path);
            }
            catch (FormatException)
            {
                continue;   // verify's finding; it names no GLB this run can follow
            }

            if (!repoint.TryGetValue(document.Source.Guid, out var target)) continue;
            fileSystem.WriteAllBytes(path, (document with { Source = target.Source, Asset = target.Asset }).WriteBytes());
            rewritten.Add(index.Relative(path));
        }

        // Anything else that named a GLB alone still names its identity, which the .blend kept;
        // only the path it spells is behind, and the importer that reads it catches it up.
        var after = AssetIndex.Scan(fileSystem, layout.Assets, ignore);
        var graph = ReferenceGraph.Build(fileSystem, layout, after, ignore, chain);
        var context = new ReferenceContext(fileSystem, layout, after, ignore);
        foreach (var path in carried.SelectMany(graph.DependentFilesOf).Distinct())
        {
            if (ReferenceChain.Rewrite(chain, context, path) is not null) rewritten.Add(after.Relative(path));
        }

        // What the watcher would do on its next drain, done now so the tree is settled when the
        // verb returns: a clip Blender re-exported under another index or hash has its document
        // brought up to date, under the identity it already has.
        var maintainer = new SidecarMaintainer(fileSystem, layout, log, ignore: ignore, importers: chain);
        foreach (var blend in placed)
        {
            var minted = AssetExtractor.MintReferences(fileSystem, layout, blend, chain, log, maintainer);
            rewritten.AddRange(minted.Written.Select(file => file.Path));
            errors.AddRange(minted.Errors);
        }

        return [.. rewritten.Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>A member's record as the model it became: under its asset, each material at the index the conversion gives its name, each image at the index of its bytes (dropped when the conversion has none; extraction then binds or extracts it anew).</summary>
    private static GlbExtraction Carried(Member member, ModelSignature after, Guid? asset)
    {
        var extraction = GlbImportSettings.ReadExtraction(member.Meta);
        return extraction with
        {
            Asset = asset,
            Materials = [.. extraction.Materials.Select(material => material with { Index = MaterialIndex(member.Before, after, material.Index)!.Value })],
            Images = [.. extraction.Images
                .Select(image => (image, at: image.Index < member.Before.ImageSha256.Count && member.Before.ImageSha256[image.Index] is { } sha ? IndexOf(after.ImageSha256, sha) : -1))
                .Where(pair => pair.at >= 0)
                .Select(pair => pair.image with { Index = pair.at, Name = $"images[{pair.at}]" })],
        };
    }

    /// <summary>A clip setting as the model it became keys it: under its asset, at the index its clip's name has in the conversion.</summary>
    private static CanonicalInlineTable Clip(CanonicalInlineTable setting, ModelSignature before, ModelSignature after, Guid? asset)
    {
        var name = setting.Value(GlbImportSettings.ClipNameKey) as string;
        if (name is null && setting.Value(GlbImportSettings.ClipIndexKey) is long index && index >= 0 && index < before.ClipOrder.Count) name = before.ClipOrder[(int)index];
        int? moved = name is not null && IndexOf(after.ClipOrder, name) is >= 0 and var at ? at : null;

        var copy = new CanonicalInlineTable();
        if (asset is { } guid) copy.Add(GlbImportSettings.ClipAssetKey, DocumentGuid.Format(guid));
        foreach (var (key, value) in setting)
        {
            if (key == GlbImportSettings.ClipAssetKey) continue;
            copy.Add(key, key == GlbImportSettings.ClipIndexKey && moved is { } position ? (long)position : value);
        }

        return copy;
    }

    private static int IndexOf(IReadOnlyList<string?> items, string item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], item, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    /// <summary>The GLB's sidecar under the same identity, for the <c>.blend</c> that stands in for it.</summary>
    private static SidecarMeta Copy(SidecarMeta meta)
    {
        var copy = new SidecarMeta(meta.Guid) { Importer = meta.Importer };
        foreach (var (domain, settings) in meta.Settings) copy.SetSetting(domain, settings);
        return copy;
    }

    private static void Delete(IFileSystem fileSystem, UPath path)
    {
        try
        {
            if (fileSystem.FileExists(path)) fileSystem.DeleteFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "to-blend: {Glb} -> {Blend} {Asset}")]
    private static partial void LogReplaced(ILogger logger, string glb, string blend, string asset);

    /// <summary>
    /// Builds each job's <c>.blend</c>: the GLBs imported with their external textures left where
    /// they are, a family member into an asset collection of its own moved to its offset and
    /// carrying its GUID in <c>paradise_guid</c>, and the file saved with paths relative to where it
    /// will live. Writes, per staged path, null or the failure, so one job that fails leaves the
    /// others built.
    /// </summary>
    private const string BuilderScript = """
        import json
        import re
        import sys
        import traceback

        import bpy
        from mathutils import Vector

        jobs_in, results_out = sys.argv[sys.argv.index('--') + 1:][:2]
        with open(jobs_in, encoding='utf-8') as source:
            jobs = json.load(source)

        # What Blender names a second datablock of a name already taken.
        RENAMED = re.compile(r'^(.*)\.[0-9]{3}$')


        def layer_of(layer, collection):
            if layer.collection == collection:
                return layer
            for child in layer.children:
                found = layer_of(child, collection)
                if found is not None:
                    return found
            return None


        def import_glb(path):
            # Unpacked, so a texture the GLB names stays that file: a dependency of the .blend.
            bpy.ops.import_scene.gltf(filepath=path, import_pack_images=False)


        def import_asset(member, shared):
            asset = member['asset']
            collection = bpy.data.collections.new(asset)
            if collection.name != asset:
                raise RuntimeError(f"a collection cannot be named '{asset}' (Blender named it '{collection.name}')")
            bpy.context.scene.collection.children.link(collection)
            view_layer = bpy.context.view_layer
            view_layer.active_layer_collection = layer_of(view_layer.layer_collection, collection)

            objects = {obj.as_pointer() for obj in bpy.data.objects}
            materials = {material.as_pointer() for material in bpy.data.materials}
            import_glb(member['glb'])

            created = [obj for obj in bpy.data.objects if obj.as_pointer() not in objects]
            inside = {obj.as_pointer() for obj in collection.all_objects}
            stray = [obj.name for obj in created if obj.as_pointer() not in inside]
            if stray:
                raise RuntimeError(f"the import left {stray} outside asset collection '{asset}'")

            # delta_location, which an animation of location does not override, and which the
            # conversion takes the instance offset back off.
            offset = Vector(member['offset'])
            for obj in created:
                if obj.parent is None:
                    obj.delta_location = obj.delta_location + offset
            collection.instance_offset = offset
            collection.asset_mark()
            # The asset's identity, as the Paradise Assets addon mints it: the converter requires it.
            collection['paradise_guid'] = member['guid']

            for material in [m for m in bpy.data.materials if m.as_pointer() not in materials]:
                renamed = RENAMED.match(material.name)
                if renamed and renamed.group(1) in shared and renamed.group(1) in bpy.data.materials:
                    material.user_remap(bpy.data.materials[renamed.group(1)])
                    bpy.data.materials.remove(material)


        def build(job):
            bpy.ops.wm.read_factory_settings(use_empty=True)
            # Factory settings keep a backup version; none belongs beside a staged file.
            bpy.context.preferences.filepaths.save_version = 0
            if 'fps' in job:
                bpy.context.scene.render.fps, bpy.context.scene.render.fps_base = job['fps']
            shared = set(job['shared_materials'])
            for member in job['members']:
                if member['asset'] is None:
                    import_glb(member['glb'])
                else:
                    import_asset(member, shared)
            bpy.ops.wm.save_as_mainfile(filepath=job['staged'], check_existing=False)
            bpy.ops.file.make_paths_relative()
            bpy.ops.wm.save_mainfile()


        results = {}
        for job in jobs:
            try:
                build(job)
                results[job['staged']] = None
            except Exception:
                results[job['staged']] = traceback.format_exc()

        with open(results_out, 'w', encoding='utf-8') as out:
            json.dump(results, out)
        """;
}
