using Paradise.Animation;

using TUnit.Assertions.Enums;

namespace Paradise.Assets.Pipeline.Test;

public class ClipFingerprintTests
{
    [Test]
    [Arguments(0, 1, 2)]
    [Arguments(0, 2, 1)]
    [Arguments(1, 0, 2)]
    [Arguments(1, 2, 0)]
    [Arguments(2, 0, 1)]
    [Arguments(2, 1, 0)]
    public async Task channel_order_and_clip_name_do_not_change_the_fingerprint(int first, int second, int third)
    {
        var clip = Clip();
        var reordered = clip with
        {
            Name = "Renamed",
            Channels = [clip.Channels[first + 1], clip.Channels[second + 1], clip.Channels[third + 1], clip.Channels[0]],
        };
        var originalBytes = ClipFormat.Write(clip);
        var reorderedBytes = ClipFormat.Write(reordered);

        await Assert.That(GltfCook.ClipFingerprint(reordered)).IsEqualTo(GltfCook.ClipFingerprint(clip));
        await Assert.That(ClipFormat.Write(clip)).IsEquivalentTo(originalBytes, CollectionOrdering.Matching);
        await Assert.That(ClipFormat.Write(reordered)).IsEquivalentTo(reorderedBytes, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("joint")]
    [Arguments("path")]
    [Arguments("interpolation")]
    [Arguments("time")]
    [Arguments("value")]
    public async Task animation_content_still_changes_the_fingerprint(string change)
    {
        var clip = Clip();
        var channel = clip.Channels[0];
        var changed = change switch
        {
            "joint" => channel with { Joint = 2 },
            "path" => channel with { Path = ChannelPath.Translation },
            "interpolation" => channel with { Step = true },
            "time" => channel with { Times = [0f, 0.5f] },
            "value" => channel with { Values = [1f, 1f, 1f, 2f, 1f, 1f] },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var edited = clip with { Channels = [changed, .. clip.Channels.Skip(1)] };

        await Assert.That(GltfCook.ClipFingerprint(edited)).IsNotEqualTo(GltfCook.ClipFingerprint(clip));
    }

    private static ClipData Clip() => new("Idle",
    [
        new ClipChannelData(1, ChannelPath.Scale, false, [0f, 1f], [1f, 1f, 1f, 1f, 1f, 1f]),
        new ClipChannelData(0, ChannelPath.Translation, false, [0f, 1f], [0f, 0f, 0f, 1f, 0f, 0f]),
        new ClipChannelData(0, ChannelPath.Rotation, false, [0f, 1f], [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f]),
        new ClipChannelData(0, ChannelPath.Scale, false, [0f, 1f], [1f, 1f, 1f, 1f, 1f, 1f]),
    ]);
}
