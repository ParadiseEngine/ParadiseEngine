using System;

namespace Paradise.Rendering.Graph;

/// <summary>What a pass's recorder is handed: the encoder positioned inside its pass, and the
/// bind groups the graph resolved for it. Groups declared on the pass are bound by index here
/// rather than by handle, so the recorder never holds a handle a resize could have retired.</summary>
/// <remarks>The encoder is held by value, which is safe because it is nothing but a reference to
/// the caller's writer; a copy appends to the same stream. A <c>ref</c> field would be the natural
/// shape and is not allowed for a ref struct.</remarks>
public ref struct PassRecording
{
    public RenderCommandEncoder Encoder;
    private readonly ReadOnlySpan<BindGroupHandle> _groups;
    private readonly string _passName;

    internal PassRecording(RenderCommandEncoder encoder, ReadOnlySpan<BindGroupHandle> groups, string passName)
    {
        Encoder = encoder;
        _groups = groups;
        _passName = passName;
    }

    /// <summary>The resolved group declared at <paramref name="groupIndex"/>.</summary>
    public BindGroupHandle BindGroup(uint groupIndex)
    {
        if (groupIndex >= (uint)_groups.Length || !_groups[(int)groupIndex].IsValid)
            throw new InvalidOperationException($"Pass '{_passName}' declared no bind group at index {groupIndex}.");
        return _groups[(int)groupIndex];
    }

    /// <summary>Bind the declared group at <paramref name="groupIndex"/>.</summary>
    public void SetBindGroup(uint groupIndex) => Encoder.SetBindGroup(groupIndex, BindGroup(groupIndex));
}
