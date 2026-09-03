using Paradise.Authoring;
using Paradise.Editor.Core.Extensibility;

namespace Paradise.Editor.ImGui;

/// <summary>What a field renderer sees: the schema for the field and the authored value.</summary>
public sealed record FieldRenderContext(AuthoredFieldSchema Field, object? Value);

/// <summary>Draws one inspector row for one schema field type (<c>float</c>, <c>vector3</c>,
/// <c>asset</c>, …). Registered by owner like everything else, so a game can supply the row for
/// a type it declares.</summary>
public sealed record FieldRenderer(string FieldType, Action<FieldRenderContext> Draw);

/// <summary>The UI-layer registry the Inspector resolves rows from; Core has no counterpart
/// because drawing is not Core's business.</summary>
/// <remarks>Owner-scoped like Core's registries but NOT reachable from
/// <c>EditorRegistries.RemoveOwner</c>, which cannot know about a type in this assembly. The shell
/// owns both and tears them down together; an extension that registers rows here and is unloaded
/// through Core alone would otherwise leave them behind.</remarks>
public sealed class FieldRendererRegistry
{
    private readonly Registry<FieldRenderer> _renderers = new();

    public void Add(OwnerToken owner, FieldRenderer renderer) => _renderers.Add(owner, renderer);

    public void RemoveOwner(OwnerToken owner) => _renderers.RemoveOwner(owner);

    /// <summary>The last registration wins, so a game can override a built-in row for a type.</summary>
    public FieldRenderer? For(string fieldType) =>
        _renderers.Entries.LastOrDefault(renderer => renderer.FieldType == fieldType);
}
