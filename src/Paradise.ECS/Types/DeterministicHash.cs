using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Stateless, order-independent deterministic hashing for seeded randomness.
/// <para>
/// Intended use: per-(seed, entity, tick) random streams for parallel systems. Because every
/// draw is a pure function of its inputs — <c>Hash01(worldSeed, entity.Id, tick)</c> — the
/// result does not depend on iteration order, thread count, or how many other draws happened
/// first. This makes it the precondition for fully-parallel system execution: two systems (or
/// two chunks of the same system) can consume "random" values concurrently and still produce
/// bit-identical simulations. Extra values distinguish independent streams for the same entity
/// and tick (e.g. a stream index per decision).
/// </para>
/// <para>
/// STABILITY PROMISE: the output of every method in this class is a PERSISTENT CONTRACT.
/// Values are derived from SplitMix64 mixing and must NEVER change across engine versions once
/// shipped — save files, replays, and cross-machine lockstep all depend on replaying the exact
/// same streams. Do not "improve" the mixing function, the combining order, the floating-point
/// scaling, or the range mapping; any change is a save/replay-breaking event.
/// </para>
/// <para>
/// Inputs are combined by mixing each value in sequence (<c>Mix(Mix(seed) + a)</c> …), never by
/// pre-folding them together, so argument order matters: <c>Hash(s, 1, 0) != Hash(s, 0, 1)</c>.
/// Signed <see cref="long"/> (and therefore <see cref="int"/>, which implicitly widens to
/// <see cref="long"/>) inputs are sign-extended and reinterpreted as <see cref="ulong"/>, so
/// <c>Hash(s, -1)</c> equals <c>Hash(s, 0xFFFF_FFFF_FFFF_FFFF)</c>.
/// </para>
/// </summary>
public static class DeterministicHash
{
    /// <summary>SplitMix64 increment (2^64 / golden ratio); part of the persistent contract.</summary>
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15ul;

    /// <summary>Scale factor mapping the top 53 hash bits to a double in [0, 1): 1.0 / 2^53.</summary>
    private const double Double53Scale = 1.0 / (1ul << 53);

    /// <summary>Scale factor mapping the top 24 hash bits to a float in [0, 1): 1.0f / 2^24.</summary>
    private const float Float24Scale = 1.0f / (1 << 24);

    /// <summary>
    /// SplitMix64 output function (Steele, Lea &amp; Flood): adds the golden-gamma increment and
    /// applies the murmur-style finalizer. Bijective on <see cref="ulong"/>, passes BigCrush.
    /// </summary>
    /// <param name="z">The state value to mix.</param>
    /// <returns>The mixed 64-bit value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += GoldenGamma;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
            return z ^ (z >> 31);
        }
    }

    // ---- Hash: raw 64-bit ------------------------------------------------------------------

    /// <summary>Hashes a single seed to a uniformly distributed 64-bit value.</summary>
    /// <param name="seed">The seed value.</param>
    /// <returns>A deterministic 64-bit hash of the input.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed) => Mix(seed);

    /// <summary>Hashes a seed combined with one additional value.</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, ulong a) => Mix(unchecked(Mix(seed) + a));

    /// <summary>Hashes a seed combined with two additional values.</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, ulong a, ulong b) => Mix(unchecked(Hash(seed, a) + b));

    /// <summary>Hashes a seed combined with three additional values.</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, ulong a, ulong b, ulong c) => Mix(unchecked(Hash(seed, a, b) + c));

    /// <summary>Hashes a seed combined with four additional values.</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, ulong a, ulong b, ulong c, ulong d) => Mix(unchecked(Hash(seed, a, b, c) + d));

    /// <summary>Hashes a seed combined with one signed value (sign-extended; accepts <see cref="int"/> implicitly).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, long a) => Hash(seed, unchecked((ulong)a));

    /// <summary>Hashes a seed combined with two signed values (sign-extended; accepts <see cref="int"/> implicitly).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, long a, long b) => Hash(seed, unchecked((ulong)a), unchecked((ulong)b));

    /// <summary>Hashes a seed combined with three signed values (sign-extended; accepts <see cref="int"/> implicitly).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, long a, long b, long c)
        => Hash(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c));

    /// <summary>Hashes a seed combined with four signed values (sign-extended; accepts <see cref="int"/> implicitly).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic 64-bit hash of the inputs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ulong seed, long a, long b, long c, long d)
        => Hash(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c), unchecked((ulong)d));

    // ---- Hash01: uniform double in [0, 1) --------------------------------------------------

    /// <summary>Hashes a single seed to a uniform double in [0, 1) using the top 53 hash bits.</summary>
    /// <param name="seed">The seed value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed) => (Hash(seed) >> 11) * Double53Scale;

    /// <summary>Hashes a seed and one value to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, ulong a) => (Hash(seed, a) >> 11) * Double53Scale;

    /// <summary>Hashes a seed and two values to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, ulong a, ulong b) => (Hash(seed, a, b) >> 11) * Double53Scale;

    /// <summary>Hashes a seed and three values to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, ulong a, ulong b, ulong c) => (Hash(seed, a, b, c) >> 11) * Double53Scale;

    /// <summary>Hashes a seed and four values to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, ulong a, ulong b, ulong c, ulong d) => (Hash(seed, a, b, c, d) >> 11) * Double53Scale;

    /// <summary>Hashes a seed and one signed value (sign-extended; accepts <see cref="int"/> implicitly) to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, long a) => Hash01(seed, unchecked((ulong)a));

    /// <summary>Hashes a seed and two signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, long a, long b) => Hash01(seed, unchecked((ulong)a), unchecked((ulong)b));

    /// <summary>Hashes a seed and three signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, long a, long b, long c)
        => Hash01(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c));

    /// <summary>Hashes a seed and four signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform double in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Hash01(ulong seed, long a, long b, long c, long d)
        => Hash01(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c), unchecked((ulong)d));

    // ---- HashFloat01: uniform float in [0, 1) ----------------------------------------------

    /// <summary>Hashes a single seed to a uniform float in [0, 1) using the top 24 hash bits.</summary>
    /// <param name="seed">The seed value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed) => (Hash(seed) >> 40) * Float24Scale;

    /// <summary>Hashes a seed and one value to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, ulong a) => (Hash(seed, a) >> 40) * Float24Scale;

    /// <summary>Hashes a seed and two values to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, ulong a, ulong b) => (Hash(seed, a, b) >> 40) * Float24Scale;

    /// <summary>Hashes a seed and three values to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, ulong a, ulong b, ulong c) => (Hash(seed, a, b, c) >> 40) * Float24Scale;

    /// <summary>Hashes a seed and four values to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, ulong a, ulong b, ulong c, ulong d) => (Hash(seed, a, b, c, d) >> 40) * Float24Scale;

    /// <summary>Hashes a seed and one signed value (sign-extended; accepts <see cref="int"/> implicitly) to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, long a) => HashFloat01(seed, unchecked((ulong)a));

    /// <summary>Hashes a seed and two signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, long a, long b) => HashFloat01(seed, unchecked((ulong)a), unchecked((ulong)b));

    /// <summary>Hashes a seed and three signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, long a, long b, long c)
        => HashFloat01(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c));

    /// <summary>Hashes a seed and four signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform float in [0, 1).</summary>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HashFloat01(ulong seed, long a, long b, long c, long d)
        => HashFloat01(seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c), unchecked((ulong)d));

    // ---- HashRange: uniform int in [minInclusive, maxExclusive) ----------------------------

    /// <summary>Hashes a single seed to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed)
        => MapToRange(Hash(seed), minInclusive, maxExclusive);

    /// <summary>Hashes a seed and one value to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, ulong a)
        => MapToRange(Hash(seed, a), minInclusive, maxExclusive);

    /// <summary>Hashes a seed and two values to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, ulong a, ulong b)
        => MapToRange(Hash(seed, a, b), minInclusive, maxExclusive);

    /// <summary>Hashes a seed and three values to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, ulong a, ulong b, ulong c)
        => MapToRange(Hash(seed, a, b, c), minInclusive, maxExclusive);

    /// <summary>Hashes a seed and four values to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, ulong a, ulong b, ulong c, ulong d)
        => MapToRange(Hash(seed, a, b, c, d), minInclusive, maxExclusive);

    /// <summary>Hashes a seed and one signed value (sign-extended; accepts <see cref="int"/> implicitly) to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, long a)
        => HashRange(minInclusive, maxExclusive, seed, unchecked((ulong)a));

    /// <summary>Hashes a seed and two signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value (e.g. an entity id).</param>
    /// <param name="b">The second additional value (e.g. a tick).</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, long a, long b)
        => HashRange(minInclusive, maxExclusive, seed, unchecked((ulong)a), unchecked((ulong)b));

    /// <summary>Hashes a seed and three signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, long a, long b, long c)
        => HashRange(minInclusive, maxExclusive, seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c));

    /// <summary>Hashes a seed and four signed values (sign-extended; accepts <see cref="int"/> implicitly) to a uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <param name="seed">The seed value.</param>
    /// <param name="a">The first additional value.</param>
    /// <param name="b">The second additional value.</param>
    /// <param name="c">The third additional value.</param>
    /// <param name="d">The fourth additional value.</param>
    /// <returns>A deterministic value in the requested range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minInclusive"/> is not less than <paramref name="maxExclusive"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashRange(int minInclusive, int maxExclusive, ulong seed, long a, long b, long c, long d)
        => HashRange(minInclusive, maxExclusive, seed, unchecked((ulong)a), unchecked((ulong)b), unchecked((ulong)c), unchecked((ulong)d));

    /// <summary>
    /// Maps a uniform 64-bit hash onto [minInclusive, maxExclusive) with a Lemire-style
    /// multiply-shift: <c>min + high64(hash * range)</c> via 128-bit multiplication.
    /// Rejection-free: with a 64-bit input and a range of at most 2^32, every bucket receives
    /// either ⌊2^64/range⌋ or ⌈2^64/range⌉ inputs, so the worst-case probability skew is below
    /// 2^-32 — statistically indistinguishable from uniform for gameplay purposes, and (unlike
    /// rejection sampling) always a single draw, which the persistent-output contract requires.
    /// </summary>
    /// <param name="hash">The uniform 64-bit hash value to map.</param>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <returns>A value in the requested range.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MapToRange(ulong hash, int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong offset = Math.BigMul(hash, range, out _);
        return (int)(minInclusive + (long)offset);
    }
}
