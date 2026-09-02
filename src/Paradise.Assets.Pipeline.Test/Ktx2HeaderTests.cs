using System.Buffers.Binary;

namespace Paradise.Assets.Pipeline.Test;

public class Ktx2HeaderTests
{
    [Test]
    public async Task validation_rejects_garbage_and_accepts_a_valid_header()
    {
        await Assert.That(Ktx2Header.IsValid(new byte[10], out _)).IsFalse();
        await Assert.That(Ktx2Header.IsValid(Header(vkFormat: 0, transfer: 1), out _)).IsTrue();

        var noLevels = Header(vkFormat: 0, transfer: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(noLevels.AsSpan(40), 0);
        await Assert.That(Ktx2Header.IsValid(noLevels, out var error)).IsFalse();
        await Assert.That(error).Contains("mip");
    }

    [Test]
    public async Task force_linear_retags_the_dfd_and_the_srgb_block_format_together()
    {
        // BC7_SRGB_BLOCK (146) → BC7_UNORM_BLOCK (145): a reader trusting vkFormat would
        // otherwise still decode sRGB after the DFD said linear.
        var bytes = Header(vkFormat: 146, transfer: 2);

        Ktx2Header.ForceLinearTransfer(bytes);

        await Assert.That(bytes[TransferOffset]).IsEqualTo((byte)1);
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12))).IsEqualTo(145u);
    }

    [Test]
    public async Task force_linear_leaves_an_undefined_format_and_a_linear_container_alone()
    {
        var undefined = Header(vkFormat: 0, transfer: 2);
        Ktx2Header.ForceLinearTransfer(undefined);
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(undefined.AsSpan(12))).IsEqualTo(0u);
        await Assert.That(undefined[TransferOffset]).IsEqualTo((byte)1);

        var linear = Header(vkFormat: 145, transfer: 1);
        var before = (byte[])linear.Clone();
        Ktx2Header.ForceLinearTransfer(linear);
        await Assert.That(linear).IsEquivalentTo(before);
    }

    [Test]
    [Arguments(43u, 37u)]
    [Arguments(134u, 133u)]
    [Arguments(158u, 157u)]
    [Arguments(184u, 183u)]
    public async Task srgb_formats_map_to_their_unorm_twin(uint srgb, uint unorm)
    {
        await Assert.That(Ktx2Header.LinearCounterpart(srgb)).IsEqualTo(unorm);
        await Assert.That(Ktx2Header.LinearCounterpart(unorm)).IsNull();
    }

    private const int DfdOffset = 80;
    private const int TransferOffset = DfdOffset + 4 + 8 + 2;

    private static byte[] Header(uint vkFormat, byte transfer)
    {
        var bytes = new byte[DfdOffset + 32];
        Ktx2Header.Identifier.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), vkFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), DfdOffset);
        bytes[TransferOffset] = transfer;
        return bytes;
    }
}
