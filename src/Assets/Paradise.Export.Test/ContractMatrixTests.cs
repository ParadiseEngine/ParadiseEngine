using System.Numerics;
using Paradise.Export.Geometry;

namespace Paradise.Export.Tests;

// Pins ContractMatrix.Trs to the contract's COLUMN-VECTOR convention (the transpose of
// System.Numerics' native row-vector composition): translation in M14/M24/M34, scale on the
// diagonal. The column-major wire spelling this used to also pin died with the baked World
// matrix in contract v6 — no matrix crosses the wire in engine types any more — but hosts still
// build matrices through this helper, so the in-memory convention stays pinned.
public class ContractMatrixTests
{
    [Test]
    public async Task translation_lands_in_the_last_column()
    {
        var m = ContractMatrix.Trs(new Vector3(1f, 2f, 3f), Quaternion.Identity, Vector3.One);

        await Assert.That(m.M14).IsEqualTo(1f);
        await Assert.That(m.M24).IsEqualTo(2f);
        await Assert.That(m.M34).IsEqualTo(3f);
        await Assert.That(m.M44).IsEqualTo(1f);
    }

    [Test]
    public async Task identity_trs_is_the_identity_matrix()
    {
        await Assert.That(ContractMatrix.Trs(Vector3.Zero, Quaternion.Identity, Vector3.One))
            .IsEqualTo(Matrix4x4.Identity);
    }

    [Test]
    public async Task scale_lands_on_the_diagonal()
    {
        var m = ContractMatrix.Trs(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 3f, 4f));

        await Assert.That(m.M11).IsEqualTo(2f);
        await Assert.That(m.M22).IsEqualTo(3f);
        await Assert.That(m.M33).IsEqualTo(4f);
    }
}
