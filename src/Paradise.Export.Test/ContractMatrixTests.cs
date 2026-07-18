using System.Numerics;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins ContractMatrix to Unity's column-vector layout: when serialized column-major, translation
// must land at flat indices 12/13/14 (last column), as Unity's Matrix4x4.TRS / localToWorldMatrix
// did — NOT at 3/7/11, which is System.Numerics' native row-vector placement.
public class ContractMatrixTests
{
    private static float[] Serialize(Matrix4x4 m)
    {
        var entity = new LevelEntityData { WorldMatrix = m };
        JsonArray arr = (JsonArray)JsonNode.Parse(ExportJsonWriter.SerializeToString(entity))!["WorldMatrix"]!;
        var flat = new float[16];
        for (int i = 0; i < 16; i++)
        {
            flat[i] = (float)arr[i]!;
        }

        return flat;
    }

    [Test]
    public async Task translation_lands_in_last_column()
    {
        float[] m = Serialize(ContractMatrix.Trs(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One));

        await Assert.That(m[12]).IsEqualTo(1f);
        await Assert.That(m[13]).IsEqualTo(2f);
        await Assert.That(m[14]).IsEqualTo(3f);
        await Assert.That(m[15]).IsEqualTo(1f);
    }

    [Test]
    public async Task identity_trs_is_identity_matrix()
    {
        float[] m = Serialize(ContractMatrix.Trs(Vector3.Zero, Quaternion.Identity, Vector3.One));
        float[] expected = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        // Order-sensitive: assert element-by-element so a permuted/transposed layout fails.
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(m[i]).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task scale_lands_on_diagonal()
    {
        float[] m = Serialize(ContractMatrix.Trs(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 3f, 4f)));
        await Assert.That(m[0]).IsEqualTo(2f);
        await Assert.That(m[5]).IsEqualTo(3f);
        await Assert.That(m[10]).IsEqualTo(4f);
    }
}
