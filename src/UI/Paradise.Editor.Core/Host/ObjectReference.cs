using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Core.Host;

/// <summary>A reference from one authored field to another object in the same scene, by identity.</summary>
/// <remarks>Authored as a reference, exported as a value: the editor's bake resolves it, the
/// engine only ever sees what was baked. Asset references reuse <c>Paradise.Authoring.AssetReference</c>.</remarks>
public readonly record struct ObjectReference(NodeId Target);
