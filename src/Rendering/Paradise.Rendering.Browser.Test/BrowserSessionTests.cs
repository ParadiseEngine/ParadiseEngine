using Paradise.Rendering.Browser.Internal;

namespace Paradise.Rendering.Browser.Test;

/// <summary>One renderer per page (#300): the shim's device and tables are module state, so a
/// second live renderer would overwrite them.</summary>
public class BrowserSessionTests
{
    [Test]
    public async Task a_second_claim_is_refused_until_the_first_is_released()
    {
        var session = new BrowserSession();
        session.Claim();

        var error = await Assert.That(session.Claim).Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("already live");
        await Assert.That(session.IsClaimed).IsTrue();

        session.Release();
        session.Claim();
        await Assert.That(session.IsClaimed).IsTrue();
    }

    [Test]
    public async Task concurrent_claims_admit_exactly_one()
    {
        var session = new BrowserSession();
        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                session.Claim();
                Interlocked.Increment(ref admitted);
            }
            catch (InvalidOperationException)
            {
            }
        }))).ConfigureAwait(false);

        await Assert.That(admitted).IsEqualTo(1);
    }
}
