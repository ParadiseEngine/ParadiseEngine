using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Dependency ownership and output stamps from one completed in-process build.</summary>
internal sealed class BuildSession
{
    private readonly Dictionary<UPath, HashSet<string>> _owners = [];
    private readonly Dictionary<string, (string Source, (long Mtime, long Size)? Stamp)> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BuildInput>> _external = new(StringComparer.Ordinal);
    private (long Mtime, long Size)? _manifest;
    private (long Mtime, long Size)? _index;

    public required BuildIndex Index { get; set; }
    public required string Environment { get; init; }
    public required string? Profile { get; init; }
    public required ProjectOutputTarget Target { get; init; }
    public required bool FoldsCase { get; init; }

    public bool IsCurrent(IFileSystem fileSystem, UPath output, string environment, string? profile, ProjectOutputTarget target)
        => Environment == environment && Profile == profile && Target == target
            && _manifest is not null && _manifest == FileStamp.Of(fileSystem, output / BuildManifest.FileName)
            && _index == FileStamp.Of(fileSystem, output / BuildIndex.FileName);

    public HashSet<string> Select(IFileSystem fileSystem, AssetIndex sources, UPath output, IReadOnlySet<UPath> changed)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changed)
        {
            if (_owners.TryGetValue(path, out var owners)) selected.UnionWith(owners);
            var asset = SidecarMeta.IsSidecarPath(path) ? SidecarMeta.AssetPathFor(path) : path;
            if (sources.IsUnderRoot(asset)) selected.Add(sources.Relative(asset));
        }

        // Output and external-input metadata are not covered by an assets-only watcher.
        foreach (var (source, inputs) in _external)
        {
            if (inputs.Any(input => !ReferenceEquals(BuildIndex.Unchanged(fileSystem, sources, input), input))) selected.Add(source);
        }

        foreach (var (path, recorded) in _outputs)
        {
            if (recorded.Stamp != FileStamp.Of(fileSystem, output / path)) selected.Add(recorded.Source);
        }

        return selected;
    }

    public void Commit(IFileSystem fileSystem, AssetIndex sources, UPath output, BuildIndex index, IReadOnlySet<string>? selected)
    {
        if (selected is not null)
        {
            foreach (var source in selected)
            {
                if (!Index.Entries.TryGetValue(source, out var old)) continue;
                foreach (var input in old.Inputs)
                {
                    var path = BuildInput.PathOf(sources.Root, input.Path);
                    if (!_owners.TryGetValue(path, out var owners)) continue;
                    owners.Remove(source);
                    if (owners.Count == 0) _owners.Remove(path);
                }

                foreach (var asset in old.Assets) _outputs.Remove(asset.Path);
                _external.Remove(source);
            }
        }

        foreach (var (source, entry) in index.Entries)
        {
            if (selected is not null && !selected.Contains(source)) continue;
            foreach (var input in entry.Inputs)
            {
                var path = BuildInput.PathOf(sources.Root, input.Path);
                if (!_owners.TryGetValue(path, out var owners)) _owners.Add(path, owners = new HashSet<string>(StringComparer.Ordinal));
                owners.Add(source);
                if (!sources.IsUnderRoot(path))
                {
                    if (!_external.TryGetValue(source, out var external)) _external.Add(source, external = []);
                    external.Add(input);
                }
            }

            foreach (var asset in entry.Assets) _outputs[asset.Path] = (source, FileStamp.Of(fileSystem, output / asset.Path));
        }

        Index = index;
        _manifest = FileStamp.Of(fileSystem, output / BuildManifest.FileName);
        _index = FileStamp.Of(fileSystem, output / BuildIndex.FileName);
    }
}
