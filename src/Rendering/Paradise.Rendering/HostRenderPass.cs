namespace Paradise.Rendering;

/// <summary>A backend-specific callback that records native passes during stream submission.</summary>
/// <remarks>The browser skips these callbacks. Implementations must close every native pass they
/// open and load and store the supplied color target so composition preserves its contents.</remarks>
public abstract class HostRenderPass;

/// <summary>A native callback and the color target resolved by the frame graph.</summary>
public readonly record struct HostPassInvocation(HostRenderPass Callback, TextureViewHandle Target);
