using System.Numerics;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The typed form of the well-known <c>transform</c> component: no consumer hand-parses
/// Position/Rotation/Scale tables, and the codec's output is always shape-valid.
/// </summary>
public class LocalTransformCodecTests
{
    [Test]
    public async Task a_transform_round_trips_through_its_component()
    {
        var authored = new LocalTransform(new Vector3(1.5f, -2f, 0.25f), new Quaternion(0f, 0.5f, 0f, 0.5f), new Vector3(2f, 1f, 4f));

        var component = LocalTransformCodec.Write(authored);
        var read = LocalTransformCodec.Read(component.Data);

        await Assert.That(read).IsEqualTo(authored);
        await Assert.That(component.Id).IsEqualTo(WellKnownComponents.TransformId);
        await Assert.That(component.Type).IsEqualTo(WellKnownComponents.TransformType);
    }

    [Test]
    public async Task written_components_are_shape_valid_and_in_canonical_field_order()
    {
        var component = LocalTransformCodec.Write(LocalTransform.Identity);

        await Assert.That(WellKnownComponents.PayloadProblem(component)).IsNull();
        await Assert.That(component.Data.Select(pair => pair.Key).ToArray())
            .IsEquivalentTo(new[] { WellKnownComponents.Position, WellKnownComponents.Rotation, WellKnownComponents.Scale });
    }

    [Test]
    public async Task absent_fields_read_as_the_identity_parts()
    {
        // An authored transform may legitimately say only what differs from the identity.
        var data = new CanonicalTomlTable { { WellKnownComponents.Scale, new object[] { 2.0, 2.0, 2.0 } } };

        var read = LocalTransformCodec.Read(data);

        await Assert.That(read.Position).IsEqualTo(Vector3.Zero);
        await Assert.That(read.Rotation).IsEqualTo(Quaternion.Identity);
        await Assert.That(read.Scale).IsEqualTo(new Vector3(2f, 2f, 2f));
    }

    [Test]
    public async Task an_empty_payload_reads_as_the_identity()
    {
        await Assert.That(LocalTransformCodec.Read(new CanonicalTomlTable())).IsEqualTo(LocalTransform.Identity);
    }
}
