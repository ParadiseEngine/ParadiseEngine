using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Mesh;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The cooked mesh a <c>.mesh</c> or <c>.skinnedmesh</c> document stands for: the geometry the build writes and the runtime draws.</summary>
/// <remarks>
/// <para>
/// The mesh comes from the steps <see cref="MeshImporter"/> and <see cref="SkinnedMeshImporter"/>
/// cook with, so rigid draws carry their node transforms and skinned draws stay in bind space
/// exactly as in the built blob. Only a skinned mesh's skeleton path is absent: that is where the
/// build writes the skeleton, which no geometry depends on.
/// </para>
/// <para>
/// A built blob is never read back instead. Whether one under <c>build/</c> or <c>.editor/play</c>
/// is current is decided by its build index's whole environment — the texture encoder's identity,
/// the Blender that converted a model, the pipeline's version — which only a build computes;
/// matching part of it would be a second rule about what shapes a mesh blob, and cooking a model
/// takes milliseconds.
/// </para>
/// <para>
/// Each model is cooked once per instance and the result is not re-validated against the files
/// it came from, so an instance lives for one bake.
/// </para>
/// </remarks>
public sealed class CookedMeshes(IFileSystem fileSystem, AssetIndex index, ILogger log)
{
    private readonly Dictionary<(UPath Model, Guid? Asset), CookedGlb> _models = [];

    /// <summary>The mesh <paramref name="meshDocument"/> cooks to, or null with the problem appended to <paramref name="errors"/>.</summary>
    public MeshData? Read(AssetReference meshDocument, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(meshDocument);
        ArgumentNullException.ThrowIfNull(errors);

        var reference = index.Resolve(meshDocument);
        if (!reference.Found)
        {
            errors.Add($"mesh reference '{meshDocument.Path}' (guid {DocumentGuid.Format(meshDocument.Guid)}) does not resolve under assets/");
            return null;
        }

        if (MeshReferenceDocument.SlotOf(reference.Asset) is not { } slot)
        {
            errors.Add($"{reference.Path}: is not a mesh document ({MeshReferenceDocument.MeshSuffix} or {MeshReferenceDocument.SkinnedMeshSuffix})");
            return null;
        }

        if (!MeshReferenceDocument.IsGeometry(slot))
        {
            errors.Add($"{reference.Path}: is a '{MeshReferenceDocument.Spell(slot)}' document, which carries no mesh");
            return null;
        }

        try
        {
            if (MeshReferenceStep.Read(fileSystem, reference.Asset, reference.Path, slot, errors) is not { } document) return null;

            var model = index.Resolve(document.Source);
            var key = (model.Asset, document.Asset?.Guid);
            if (!_models.TryGetValue(key, out var cooked))
            {
                cooked = MeshReferenceStep.Model(fileSystem, index, reference.Path, document, model, log, errors);
                if (cooked is null) return null;
                _models.Add(key, cooked);
            }

            return MeshReferenceStep.Mesh(cooked, slot, reference.Path, model.Path, errors);
        }
        catch (IOException failure)
        {
            // The index scan is a snapshot; a document or model deleted since is a finding, not a crash.
            errors.Add($"{reference.Path}: {failure.Message}");
            return null;
        }
    }
}
