using System.Numerics;

namespace Paradise.ECS.Test;

/// <summary>
/// Tests for <see cref="DeterministicHash"/>.
/// The golden values pin the SplitMix64-based algorithm: DeterministicHash output is a
/// PERSISTENT CONTRACT (saves/replays/lockstep depend on it), so if an "improvement" to the
/// mixing ever changes these values, these tests MUST fail.
/// </summary>
public sealed class DeterministicHashTests
{
    // ---- Golden values (persistent contract) ----

    [Test]
    public async Task Hash_Seed0_MatchesCanonicalSplitMix64Vector()
    {
        // First output of reference SplitMix64 seeded with 0 (Steele, Lea & Flood).
        await Assert.That(DeterministicHash.Hash(0ul)).IsEqualTo(0xE220A8397B1DCDAFul);
    }

    [Test]
    public async Task Hash_GoldenValues_AllArities()
    {
        await Assert.That(DeterministicHash.Hash(42ul)).IsEqualTo(0xBDD732262FEB6E95ul);
        await Assert.That(DeterministicHash.Hash(0xDEADBEEFul)).IsEqualTo(0x4ADFB90F68C9EB9Bul);
        await Assert.That(DeterministicHash.Hash(1ul, 2ul)).IsEqualTo(0xBCD9DBB49673066Bul);
        await Assert.That(DeterministicHash.Hash(1ul, 2ul, 3ul)).IsEqualTo(0x6AE515C1C0AC7E37ul);
        await Assert.That(DeterministicHash.Hash(1ul, 2ul, 3ul, 4ul)).IsEqualTo(0x336C3255CBCC01FEul);
        await Assert.That(DeterministicHash.Hash(1ul, 2ul, 3ul, 4ul, 5ul)).IsEqualTo(0x141EA6A5B8259323ul);
    }

    [Test]
    public async Task Hash_SignedOverloads_SignExtend_GoldenValue()
    {
        // -1 sign-extends to 0xFFFF_FFFF_FFFF_FFFF.
        await Assert.That(DeterministicHash.Hash(7ul, -1)).IsEqualTo(0x3D41BF495CD3075Ful);
        await Assert.That(DeterministicHash.Hash(7ul, -1L)).IsEqualTo(DeterministicHash.Hash(7ul, ulong.MaxValue));
        await Assert.That(DeterministicHash.Hash(7ul, 5)).IsEqualTo(DeterministicHash.Hash(7ul, 5ul));
        await Assert.That(DeterministicHash.Hash(7ul, 5, 6)).IsEqualTo(DeterministicHash.Hash(7ul, 5ul, 6ul));
    }

    [Test]
    public async Task Hash01_And_HashFloat01_GoldenValues()
    {
        // (Hash(0) >> 11) * 2^-53 and (Hash(0) >> 40) * 2^-24 — both exactly representable.
        await Assert.That(DeterministicHash.Hash01(0ul)).IsEqualTo(0.8833108082136426);
        await Assert.That(DeterministicHash.Hash01(42ul, 7ul)).IsEqualTo(0.9929056469208896);
        await Assert.That(DeterministicHash.HashFloat01(0ul)).IsEqualTo(0.883310794830322265625f);
    }

    [Test]
    public async Task HashRange_GoldenValues()
    {
        await Assert.That(DeterministicHash.HashRange(0, 100, 0ul)).IsEqualTo(88);
        await Assert.That(DeterministicHash.HashRange(-50, 50, 123ul, 9L)).IsEqualTo(34);
    }

    // ---- Determinism ----

    [Test]
    public async Task Hash_SameInputs_SameOutputs()
    {
        for (ulong i = 0; i < 100; i++)
        {
            await Assert.That(DeterministicHash.Hash(i, i * 3, i * 7)).IsEqualTo(DeterministicHash.Hash(i, i * 3, i * 7));
        }
    }

    // ---- Overload / argument-order independence ----

    [Test]
    public async Task Hash_ArgumentOrder_Matters()
    {
        const ulong seed = 0xABCDEF;
        await Assert.That(DeterministicHash.Hash(seed, 1ul, 0ul)).IsNotEqualTo(DeterministicHash.Hash(seed, 0ul, 1ul));
        await Assert.That(DeterministicHash.Hash(seed, 1ul, 2ul, 0ul)).IsNotEqualTo(DeterministicHash.Hash(seed, 2ul, 1ul, 0ul));
        await Assert.That(DeterministicHash.Hash(seed, 1ul, 2ul, 3ul, 4ul)).IsNotEqualTo(DeterministicHash.Hash(seed, 4ul, 3ul, 2ul, 1ul));
    }

    [Test]
    public async Task Hash_Arity_Matters()
    {
        const ulong seed = 0xABCDEF;
        await Assert.That(DeterministicHash.Hash(seed)).IsNotEqualTo(DeterministicHash.Hash(seed, 0ul));
        await Assert.That(DeterministicHash.Hash(seed, 1ul)).IsNotEqualTo(DeterministicHash.Hash(seed, 1ul, 0ul));
        await Assert.That(DeterministicHash.Hash(seed, 1ul, 2ul)).IsNotEqualTo(DeterministicHash.Hash(seed, 1ul, 2ul, 0ul));
    }

    // ---- Avalanche smoke ----

    [Test]
    public async Task Hash_SingleInputBitFlip_ChangesAboutHalfTheOutputBits()
    {
        long totalFlipped = 0;
        int samples = 0;
        for (int i = 0; i < 64; i++)
        {
            ulong x = DeterministicHash.Hash((ulong)i); // spread sample points over the input space
            ulong hx = DeterministicHash.Hash(x);
            for (int bit = 0; bit < 64; bit++)
            {
                totalFlipped += BitOperations.PopCount(hx ^ DeterministicHash.Hash(x ^ (1ul << bit)));
                samples++;
            }
        }

        double average = (double)totalFlipped / samples;
        await Assert.That(average).IsGreaterThan(28.0);
        await Assert.That(average).IsLessThan(36.0);
    }

    [Test]
    public async Task Hash_CombinedValueBitFlip_ChangesAboutHalfTheOutputBits()
    {
        const ulong seed = 0x123456789ABCDEFul;
        long totalFlipped = 0;
        int samples = 0;
        for (int i = 0; i < 64; i++)
        {
            ulong a = DeterministicHash.Hash((ulong)i);
            ulong h = DeterministicHash.Hash(seed, a);
            for (int bit = 0; bit < 64; bit++)
            {
                totalFlipped += BitOperations.PopCount(h ^ DeterministicHash.Hash(seed, a ^ (1ul << bit)));
                samples++;
            }
        }

        double average = (double)totalFlipped / samples;
        await Assert.That(average).IsGreaterThan(28.0);
        await Assert.That(average).IsLessThan(36.0);
    }

    // ---- Hash01 / HashFloat01 bounds ----

    [Test]
    public async Task Hash01_AlwaysInUnitInterval()
    {
        for (ulong i = 0; i < 10_000; i++)
        {
            double v = DeterministicHash.Hash01(i, i * 31);
            await Assert.That(v).IsGreaterThanOrEqualTo(0.0);
            await Assert.That(v).IsLessThan(1.0);
        }

        // Edge inputs.
        await Assert.That(DeterministicHash.Hash01(ulong.MaxValue)).IsLessThan(1.0);
        await Assert.That(DeterministicHash.HashFloat01(ulong.MaxValue)).IsLessThan(1.0f);
    }

    [Test]
    public async Task HashFloat01_AlwaysInUnitInterval()
    {
        for (ulong i = 0; i < 10_000; i++)
        {
            float v = DeterministicHash.HashFloat01(i, i * 17, i + 3);
            await Assert.That(v).IsGreaterThanOrEqualTo(0.0f);
            await Assert.That(v).IsLessThan(1.0f);
        }
    }

    // ---- HashRange bounds + rough uniformity ----

    [Test]
    public async Task HashRange_AlwaysWithinBounds()
    {
        for (ulong i = 0; i < 10_000; i++)
        {
            int v = DeterministicHash.HashRange(-3, 7, 0xC0FFEEul, i);
            await Assert.That(v).IsGreaterThanOrEqualTo(-3);
            await Assert.That(v).IsLessThan(7);
        }

        // Full int range and single-element range.
        await Assert.That(DeterministicHash.HashRange(int.MinValue, int.MaxValue, 1ul))
            .IsGreaterThanOrEqualTo(int.MinValue);
        await Assert.That(DeterministicHash.HashRange(5, 6, 99ul)).IsEqualTo(5);
    }

    [Test]
    public async Task HashRange_RoughlyUniform()
    {
        const int bucketCount = 10;
        const int sampleCount = 100_000;
        const double expected = (double)sampleCount / bucketCount;

        var counts = new int[bucketCount];
        for (ulong i = 0; i < sampleCount; i++)
        {
            counts[DeterministicHash.HashRange(0, bucketCount, 0xFEEDul, i)]++;
        }

        // Loose chi-square-ish tolerance: each bucket within 3% of expected (~30 sigma would be
        // needed to trip this for a uniform source; the run is fully deterministic anyway).
        double chiSquare = 0;
        foreach (int count in counts)
        {
            await Assert.That((double)count).IsGreaterThan(expected * 0.97);
            await Assert.That((double)count).IsLessThan(expected * 1.03);
            chiSquare += (count - expected) * (count - expected) / expected;
        }

        // 9 degrees of freedom; 50 is far beyond any plausible uniform fluctuation.
        await Assert.That(chiSquare).IsLessThan(50.0);
    }

    [Test]
    public async Task HashRange_InvalidBounds_Throws()
    {
        await Assert.That(() => DeterministicHash.HashRange(5, 5, 1ul)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => DeterministicHash.HashRange(5, 4, 1ul)).Throws<ArgumentOutOfRangeException>();
    }
}
