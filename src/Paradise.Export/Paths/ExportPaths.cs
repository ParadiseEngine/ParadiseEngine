#nullable enable
using System;
using System.IO;

namespace Paradise.Export.Paths
{
    /// <summary>
    /// Resolves export output paths under a repository-root <c>data/</c> directory: scenes to
    /// <c>data/scenes/</c>, materials to <c>data/materials/</c>, prefabs to <c>data/prefabs/</c>.
    ///
    /// Ported from ParadiseUnityEditor's SceneExportPaths, but made engine-neutral: instead of
    /// resolving the root from <c>Application.dataPath</c>, the data directory is supplied by the
    /// engine adapter (the Godot plugin passes the globalized project root + "/data").
    /// </summary>
    public sealed class ExportPaths
    {
        private readonly string _dataDir;
        private readonly string _scenesDir;

        public ExportPaths(string dataDir)
        {
            _dataDir = Path.GetFullPath(dataDir);
            _scenesDir = Path.Combine(_dataDir, "scenes");
        }

        public string DataDir => _dataDir;
        public string ScenesDir => _scenesDir;

        public string GetLevelDataOutputPath(string sceneName) =>
            Path.Combine(_scenesDir, $"{sceneName}.json");

        public string GetNavMeshOutputPath(string sceneName) =>
            Path.Combine(_scenesDir, $"{sceneName}.navmesh.bin");

        public string GetNavMeshFileField(string sceneName) =>
            Path.GetFileName(GetNavMeshOutputPath(sceneName));

        public string GetProjectSettingsOutputPath() =>
            Path.Combine(_dataDir, "ProjectSettings.json");

        /// <summary>Absolute output path for a material/texture field path like
        /// <c>materials/foo.json</c> (resolved under the data directory).</summary>
        public string GetMaterialDataOutputPath(string materialField) =>
            Path.Combine(_dataDir, materialField.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Absolute output path for a mesh field like <c>meshes/foo.glb</c>.</summary>
        public string GetMeshOutputPath(string meshField) =>
            Path.Combine(_dataDir, meshField.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Maps a source model reference — a <c>res://</c> path (e.g. a node's
        /// <c>SceneFilePath</c> or an authored <c>ModelPath</c>) or a filesystem path — to its
        /// contract mesh field: the path RELATIVE to the data directory
        /// (<c>res://data/Models/knight.glb</c> → <c>Models/knight.glb</c>). The runtime resolves
        /// mesh fields under <c>data/</c> (<c>Path.Combine(dataDir, field)</c>), so a reference
        /// that resolves OUTSIDE the data directory is unreachable at runtime and returns
        /// <c>null</c> (the caller warns). <c>res://</c> is the project root, whose child
        /// <c>data/</c> is this instance's data directory.
        /// </summary>
        public string? DataRelativeMeshField(string resOrPath)
        {
            if (string.IsNullOrWhiteSpace(resOrPath))
            {
                return null;
            }

            string normalized = resOrPath.Replace('\\', '/');
            string projectRoot = Path.GetDirectoryName(_dataDir) ?? _dataDir;

            string full;
            if (normalized.StartsWith("res://", StringComparison.Ordinal))
            {
                full = Path.GetFullPath(Path.Combine(projectRoot, normalized["res://".Length..]));
            }
            else if (Path.IsPathRooted(normalized))
            {
                full = Path.GetFullPath(normalized);
            }
            else
            {
                full = Path.GetFullPath(Path.Combine(projectRoot, normalized));
            }

            string relative = Path.GetRelativePath(_dataDir, full).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return null;
            }

            return relative;
        }

        /// <summary>Absolute output path for a prefab field like <c>prefabs/foo.json</c>.</summary>
        public string GetPrefabDataOutputPath(string prefabField) =>
            Path.Combine(_dataDir, prefabField.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Maps a prefab's project path (or name) to its <c>prefabs/&lt;relative&gt;.json</c> contract
        /// field, mirroring the Unity tool's <c>prefabs/</c> layout. A leading <c>res://</c> and a
        /// redundant leading <c>prefabs/</c> are stripped so the field is not double-nested.
        /// </summary>
        public static string PrefabFileField(string prefabPathOrName)
        {
            string normalized = prefabPathOrName.Replace('\\', '/');
            if (normalized.StartsWith("res://", StringComparison.Ordinal))
            {
                normalized = normalized["res://".Length..];
            }

            normalized = normalized.Trim('/');
            if (normalized.Length == 0)
            {
                return "prefabs/prefab.json";
            }

            // Ordinal (not IgnoreCase) to match Godot's lowercase path convention and a
            // case-sensitive VFS: "Prefabs/" is a different directory than "prefabs/".
            if (normalized.StartsWith("prefabs/", StringComparison.Ordinal))
            {
                normalized = normalized["prefabs/".Length..];
            }

            return $"prefabs/{Path.ChangeExtension(normalized, ".json")}";
        }

        /// <summary>
        /// Maps a material's name (or project-relative source path) to its
        /// <c>materials/&lt;name&gt;.json</c> contract field, mirroring the Unity tool's
        /// <c>materials/</c> layout. The field is the stable id stored in entity material slots.
        /// </summary>
        public static string MaterialFileField(string materialNameOrPath)
        {
            string normalized = materialNameOrPath.Replace('\\', '/').Trim('/');
            // Sub-resource materials ("res://scene.tscn::mat_ball1") take their name from the
            // sub-resource id — previously every embedded material of a scene collapsed onto
            // the scene's filename and collided.
            int subResource = normalized.LastIndexOf("::", StringComparison.Ordinal);
            string name = subResource >= 0
                ? normalized[(subResource + 2)..]
                : normalized.Length == 0 ? "material" : Path.GetFileNameWithoutExtension(normalized);
            if (name.Length == 0)
            {
                name = "material";
            }
            return $"materials/{name}.json";
        }

        public void EnsureOutputDirectory()
        {
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_scenesDir);
        }
    }
}
