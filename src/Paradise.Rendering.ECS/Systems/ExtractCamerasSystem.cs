using Paradise.ECS;

namespace Paradise.Rendering.ECS.Systems;

/// <summary>
/// ECS chunk system that reads <see cref="CameraComponent"/> from every matching chunk and
/// appends one <see cref="ExtractedCamera"/> per entity to the current frame's
/// <see cref="ExtractionContext.Packets"/>.
/// <para>
/// The View and Projection matrices are multiplied at extract time so the renderer receives a
/// single ready-to-use ViewProjection matrix — the live ECS world is never accessed after
/// the extraction pass completes.
/// </para>
/// </summary>
public ref partial struct ExtractCamerasSystem : IChunkSystem
{
    public ReadOnlySpan<CameraComponent> CameraComponents;

    public void ExecuteChunk()
    {
        var packets = ExtractionContext.Packets!;
        for (int i = 0; i < CameraComponents.Length; i++)
        {
            ref readonly var cam = ref CameraComponents[i];

            // Resolve the render-view target: uint.MaxValue (-1) means swapchain surface.
            var targetView = cam.TargetView == uint.MaxValue
                ? RenderViewHandle.Invalid
                : new RenderViewHandle(cam.TargetView, 1u);

            packets.AppendCamera(new ExtractedCamera
            {
                ViewProjection = cam.View * cam.Projection,
                TargetView = targetView,
            });
        }
    }
}
