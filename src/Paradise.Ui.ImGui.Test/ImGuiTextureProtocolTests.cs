using System;
using System.Collections.Generic;
using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Dear ImGui 1.92's texture protocol, against the real natives.
///
/// This is the load-bearing test of the Hexa migration. Hexa's cimgui build strips the obsolete
/// static-atlas API, so a context that does not answer <c>ImTextureData</c> requests asserts at
/// <c>NewFrame</c> with "font atlas is not built" — meaning the whole UI stack either speaks this
/// protocol or does not start at all. It also guards the by-value <c>Vector2</c>/<c>Vector4</c>
/// ABI, which is where a binding-level mismatch would show up as silently wrong geometry rather
/// than a crash.</summary>
[NotInParallel]
public class ImGuiTextureProtocolTests
{
    private const int Width = 256;
    private const int Height = 256;

    [Test]
    public async Task atlas_walks_want_create_then_want_updates_then_ok()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var ops = new ImGuiTextureOps();
        var statuses = new List<ImTextureStatus>();

        // Frame 0 raises WantCreate. Frame 1 draws characters frame 0 never did, which
        // rasterizes new glyphs into the existing atlas and raises WantUpdates. Frame 2 repeats
        // frame 1's text, so nothing is dirty and the atlas settles at Ok.
        var texts = new[] { "hello", "XYZ@#", "XYZ@#" };
        foreach (var text in texts)
        {
            var drawData = imgui.Frame(() => ImGuiTestContext.Panel(text));
            statuses.Add(FirstTextureStatus(drawData));
            ImGuiTextureCapture.CaptureFrom(drawData, ops);
        }

        await Assert.That(statuses).IsEquivalentTo(new[]
        {
            ImTextureStatus.WantCreate,
            ImTextureStatus.WantUpdates,
            ImTextureStatus.Ok,
        });
    }

    [Test]
    public async Task capture_turns_the_atlas_into_a_create_then_an_update()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var ops = new ImGuiTextureOps();

        var first = imgui.Frame(() => ImGuiTestContext.Panel("hello"));
        ImGuiTextureCapture.CaptureFrom(first, ops);
        var second = imgui.Frame(() => ImGuiTestContext.Panel("XYZ@#"));
        var textureId = FirstTextureId(second);
        ImGuiTextureCapture.CaptureFrom(second, ops);

        var drained = new List<ImGuiTextureOp>();
        await Assert.That(ops.DrainTo(drained)).IsEqualTo(2);

        var create = drained[0];
        await Assert.That(create.Kind).IsEqualTo(ImGuiTextureOpKind.Create);
        await Assert.That(create.TextureId).IsEqualTo(textureId);
        await Assert.That(create.Pixels.Length)
            .IsEqualTo((int)(create.Width * create.Height) * ImGuiTextureOp.BytesPerPixel);

        var update = drained[1];
        await Assert.That(update.Kind).IsEqualTo(ImGuiTextureOpKind.Update);
        await Assert.That(update.TextureId).IsEqualTo(textureId);
        await Assert.That(update.Pixels.Length)
            .IsEqualTo((int)(update.Width * update.Height) * ImGuiTextureOp.BytesPerPixel);
        // The dirty rect is a patch inside the atlas, not the whole of it.
        await Assert.That(update.X + update.Width).IsLessThanOrEqualTo(create.Width);
        await Assert.That(update.Y + update.Height).IsLessThanOrEqualTo(create.Height);

        // Draw commands must name the id the capture stamped, or the renderer looks up nothing.
        var snapshot = new ImGuiDrawSnapshot();
        snapshot.Capture(second);
        await Assert.That(snapshot.CommandCount).IsGreaterThan(0);
        for (var i = 0; i < snapshot.CommandCount; i++)
        {
            await Assert.That(snapshot.Commands[i].TextureId).IsEqualTo(textureId);
        }
    }

    /// <summary>The by-value struct ABI, which is the one thing a binding can get wrong without
    /// crashing: <c>ImVec2</c> is returned in registers, and a mismatch reads back plausible
    /// nonsense instead of failing.</summary>
    [Test]
    public async Task vector_returns_round_trip_across_the_binding()
    {
        using var imgui = new ImGuiTestContext(Width, Height);
        var position = new Vector2(float.NaN, float.NaN);
        var size = new Vector2(float.NaN, float.NaN);
        var mouse = new Vector2(float.NaN, float.NaN);

        ImGuiApi.GetIO().AddMousePosEvent(64f, 96f);
        imgui.Frame(() =>
        {
            ImGuiApi.SetNextWindowPos(new Vector2(40, 48));
            ImGuiApi.SetNextWindowSize(new Vector2(140, 100));
            ImGuiApi.Begin("abi", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
            position = ImGuiApi.GetWindowPos();
            size = ImGuiApi.GetWindowSize();
            mouse = ImGuiApi.GetMousePos();
            ImGuiApi.End();
        });

        await Assert.That(position).IsEqualTo(new Vector2(40, 48));
        await Assert.That(size).IsEqualTo(new Vector2(140, 100));
        await Assert.That(mouse).IsEqualTo(new Vector2(64, 96));
    }

    private static unsafe ImTextureStatus FirstTextureStatus(ImDrawDataPtr drawData)
    {
        var textures = drawData.Handle->Textures;
        if (textures is null || textures->Size == 0)
        {
            throw new InvalidOperationException("ImDrawData carried no textures.");
        }
        return (*textures)[0].Status;
    }

    private static unsafe ulong FirstTextureId(ImDrawDataPtr drawData)
    {
        var textures = drawData.Handle->Textures;
        return (*textures)[0].GetTexID().Handle;
    }
}
