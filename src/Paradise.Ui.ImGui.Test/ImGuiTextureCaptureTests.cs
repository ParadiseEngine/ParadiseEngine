using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;

namespace Paradise.Ui.ImGui.Test;

/// <summary>The capture side driven against a HAND-BUILT <c>ImTextureData</c> rather than a real
/// ImGui frame.
///
/// <c>ImGuiTextureProtocolTests</c> covers what a live context actually does, but it can only see
/// the statuses ImGui chooses to raise — which is why <c>WantDestroy</c> had no coverage at all
/// and shipped with a contract bug. When a texture is recycled is ImGui's scheduling decision,
/// not a contract, so waiting for one is both slow and unreliable. Fabricating the struct tests
/// OUR branch of the protocol deterministically: the accessors are plain cimgui calls on the
/// pointer we hand them and need no context.
///
/// No <c>[NotInParallel]</c>: nothing here touches the process-global current context.</summary>
public class ImGuiTextureCaptureTests
{
    /// <summary>One <c>ImTextureData</c> and the <c>ImDrawData</c> that points at it, in
    /// unmanaged memory shaped the way ImGui hands it to a backend.</summary>
    private sealed unsafe class FakeFrame : IDisposable
    {
        private readonly ImTextureData* _texture;
        private readonly ImVector<ImTextureDataPtr>* _textures;
        private readonly ImDrawData* _drawData;
        private readonly byte* _pixels;
        private readonly int _pixelBytes;

        public FakeFrame(int uniqueId, int width, int height, ImTextureStatus status,
            ImTextureFormat format = ImTextureFormat.Rgba32)
        {
            _pixelBytes = width * height * ImGuiTextureOp.BytesPerPixel;
            _pixels = (byte*)NativeMemory.Alloc((nuint)_pixelBytes);
            // Each byte is its own offset, so a test can assert WHICH bytes a rect copy took
            // rather than only how many.
            for (var i = 0; i < _pixelBytes; i++) _pixels[i] = (byte)i;

            _texture = (ImTextureData*)NativeMemory.AllocZeroed((nuint)sizeof(ImTextureData));
            _texture->UniqueID = uniqueId;
            _texture->Status = status;
            _texture->Format = format;
            _texture->Width = width;
            _texture->Height = height;
            _texture->BytesPerPixel = ImGuiTextureOp.BytesPerPixel;
            _texture->Pixels = _pixels;

            _textures = (ImVector<ImTextureDataPtr>*)NativeMemory.AllocZeroed((nuint)sizeof(ImVector<ImTextureDataPtr>));
            _textures->PushBack(new ImTextureDataPtr(_texture));

            _drawData = (ImDrawData*)NativeMemory.AllocZeroed((nuint)sizeof(ImDrawData));
            _drawData->Valid = 1;
            _drawData->Textures = _textures;
        }

        public ImTextureDataPtr Texture => new(_texture);
        public ImDrawDataPtr DrawData => new(_drawData);
        public byte PixelAt(int index) => _pixels[index];

        public void SetUpdateRect(ushort x, ushort y, ushort w, ushort h) =>
            _texture->UpdateRect = new ImTextureRect { X = x, Y = y, W = w, H = h };

        public void SetTexId(ulong id) => new ImTextureDataPtr(_texture).SetTexID(new ImTextureID(id));

        public void Dispose()
        {
            _textures->Free();
            NativeMemory.Free(_textures);
            NativeMemory.Free(_drawData);
            NativeMemory.Free(_texture);
            NativeMemory.Free(_pixels);
        }
    }

    private static List<ImGuiTextureOp> Capture(FakeFrame frame)
    {
        var ops = new ImGuiTextureOps();
        ImGuiTextureCapture.CaptureFrom(frame.DrawData, ops);
        var drained = new List<ImGuiTextureOp>();
        ops.DrainTo(drained);
        return drained;
    }

    [Test]
    public async Task want_create_copies_every_pixel_and_stamps_an_id_one_past_the_unique_id()
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, ImTextureStatus.WantCreate);

        var ops = Capture(frame);

        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);
        // Offset by one: 0 is ImGui's null id, and a draw command carrying it asserts.
        await Assert.That(ops[0].TextureId).IsEqualTo(8ul);
        await Assert.That(ops[0].Width).IsEqualTo(4u);
        await Assert.That(ops[0].Height).IsEqualTo(3u);
        await Assert.That(ops[0].Pixels.Length).IsEqualTo(4 * 3 * 4);
        for (var i = 0; i < ops[0].Pixels.Length; i++)
        {
            await Assert.That(ops[0].Pixels[i]).IsEqualTo(frame.PixelAt(i));
        }
        await Assert.That(frame.Texture.GetTexID().Handle).IsEqualTo(8ul);
        await Assert.That(frame.Texture.Status).IsEqualTo(ImTextureStatus.Ok);
    }

    /// <summary>The rect copy is row-by-row out of a wider texture, which is the one place a
    /// stride mistake would produce a plausible-looking op with the wrong bytes in it.</summary>
    [Test]
    public async Task want_updates_copies_the_rect_tightly_packed_out_of_the_wider_texture()
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, ImTextureStatus.WantUpdates);
        frame.SetTexId(8);
        frame.SetUpdateRect(x: 1, y: 1, w: 2, h: 2);

        var ops = Capture(frame);

        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Update);
        await Assert.That(ops[0].TextureId).IsEqualTo(8ul);
        await Assert.That((ops[0].X, ops[0].Y, ops[0].Width, ops[0].Height)).IsEqualTo((1u, 1u, 2u, 2u));

        // Source pitch is 4 px x 4 B = 16. Row y=1 starts at 16, and x=1 is +4, so the rect's
        // first row is bytes 20..27 and its second row (y=2) is 36..43 — tightly packed into 16
        // bytes with the row gap dropped.
        var expected = new byte[] { 20, 21, 22, 23, 24, 25, 26, 27, 36, 37, 38, 39, 40, 41, 42, 43 };
        await Assert.That(ops[0].Pixels).IsEquivalentTo(expected);
        await Assert.That(frame.Texture.Status).IsEqualTo(ImTextureStatus.Ok);
    }

    /// <summary>Regression: the id has to be cleared BEFORE the status is reported. ImGui's atlas
    /// asserts a destroyed texture carries no id before it removes the ImTextureData, and this
    /// branch shipped without it because a live context never raised WantDestroy in the
    /// suite.</summary>
    [Test]
    public async Task want_destroy_emits_the_op_then_clears_the_id_before_reporting_destroyed()
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, ImTextureStatus.WantDestroy);
        frame.SetTexId(8);

        var ops = Capture(frame);

        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Destroy);
        await Assert.That(ops[0].TextureId).IsEqualTo(8ul);
        await Assert.That(ops[0].Pixels).IsEmpty();
        await Assert.That(frame.Texture.GetTexID().IsNull).IsTrue();
        await Assert.That(frame.Texture.Status).IsEqualTo(ImTextureStatus.Destroyed);
    }

    [Test]
    public async Task want_destroy_on_a_texture_that_was_never_created_enqueues_nothing()
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, ImTextureStatus.WantDestroy);

        var ops = Capture(frame);

        // No id was ever handed out, so no GPU object exists to free.
        await Assert.That(ops).IsEmpty();
        await Assert.That(frame.Texture.Status).IsEqualTo(ImTextureStatus.Destroyed);
    }

    [Test]
    [Arguments(ImTextureStatus.Ok)]
    [Arguments(ImTextureStatus.Destroyed)]
    public async Task a_settled_texture_asks_for_nothing(ImTextureStatus status)
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, status);
        frame.SetTexId(8);

        await Assert.That(Capture(frame)).IsEmpty();
        await Assert.That(frame.Texture.Status).IsEqualTo(status);
    }

    /// <summary>Alpha8 would upload a quarter of the bytes the renderer expects and show as
    /// garbage, so it is refused rather than reinterpreted.</summary>
    [Test]
    public async Task a_format_the_renderer_cannot_upload_is_refused()
    {
        using var frame = new FakeFrame(uniqueId: 7, width: 4, height: 3, ImTextureStatus.WantCreate,
            ImTextureFormat.Alpha8);

        await Assert.That(() => Capture(frame)).Throws<NotSupportedException>();
    }
}
