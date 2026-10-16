using System.Globalization;

namespace Paradise.Assets.Documents.Test;

/// <summary>Pins canonical float formatting to CPython's <c>repr</c>.</summary>
/// <remarks>Expectations were checked against CPython 3.13; the Blender writer formats floats with <c>repr</c>.</remarks>
public class CanonicalFloatFormatTests
{
    [Test]
    [Arguments(0.0, "0.0")]
    [Arguments(1.0, "1.0")]
    [Arguments(-1.0, "-1.0")]
    [Arguments(0.5, "0.5")]
    [Arguments(0.1, "0.1")]
    [Arguments(2.5, "2.5")]
    [Arguments(100.0, "100.0")]
    [Arguments(3.14159, "3.14159")]
    [Arguments(0.007, "0.007")]
    [Arguments(0.0001, "0.0001")]
    public async Task ordinary_values_are_positional(double value, string expected)
    {
        await Assert.That(CanonicalTomlWriter.FormatFloat(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0.00001, "1e-05")]
    [Arguments(1.5e-7, "1.5e-07")]
    [Arguments(1e16, "1e+16")]
    [Arguments(9.87e22, "9.87e+22")]
    [Arguments(1e100, "1e+100")]
    [Arguments(5e-324, "5e-324")]
    [Arguments(1.7976931348623157e308, "1.7976931348623157e+308")]
    public async Task tiny_and_huge_values_use_scientific_notation_with_a_signed_exponent(double value, string expected)
    {
        await Assert.That(CanonicalTomlWriter.FormatFloat(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task the_positional_cutoff_sits_exactly_at_ten_to_the_sixteenth()
    {
        // The upper cutoff must match Python even when .NET's round-trip format chooses differently.
        await Assert.That(CanonicalTomlWriter.FormatFloat(1e15)).IsEqualTo("1000000000000000.0");
        await Assert.That(CanonicalTomlWriter.FormatFloat(1234567890123456.0)).IsEqualTo("1234567890123456.0");
        await Assert.That(CanonicalTomlWriter.FormatFloat(1e16)).IsEqualTo("1e+16");
    }

    [Test]
    public async Task the_scientific_cutoff_sits_exactly_below_ten_to_the_minus_fourth()
    {
        await Assert.That(CanonicalTomlWriter.FormatFloat(0.0001)).IsEqualTo("0.0001");
        await Assert.That(CanonicalTomlWriter.FormatFloat(0.00012)).IsEqualTo("0.00012");
        await Assert.That(CanonicalTomlWriter.FormatFloat(0.00001)).IsEqualTo("1e-05");
    }

    [Test]
    public async Task specials_use_toml_tokens_and_negative_zero_keeps_its_sign()
    {
        await Assert.That(CanonicalTomlWriter.FormatFloat(double.NaN)).IsEqualTo("nan");
        await Assert.That(CanonicalTomlWriter.FormatFloat(double.PositiveInfinity)).IsEqualTo("inf");
        await Assert.That(CanonicalTomlWriter.FormatFloat(double.NegativeInfinity)).IsEqualTo("-inf");
        var negativeZeroText = CanonicalTomlWriter.FormatFloat(-0.0);
        await Assert.That(negativeZeroText).IsEqualTo("-0.0");
        await Assert.That(BitConverter.DoubleToInt64Bits(double.Parse(negativeZeroText, CultureInfo.InvariantCulture)))
            .IsEqualTo(long.MinValue);
    }

    [Test]
    [Arguments(0.1)]
    [Arguments(1.0 / 3.0)]
    [Arguments(12345.6789)]
    [Arguments(4.9e-300)]
    [Arguments(2.2250738585072014e-308)]
    [Arguments(1e21)]
    [Arguments(123456.78901234567)]
    public async Task finite_outputs_parse_back_to_the_same_bits(double value)
    {
        var text = CanonicalTomlWriter.FormatFloat(value);
        var parsed = double.Parse(text, CultureInfo.InvariantCulture);

        await Assert.That(BitConverter.DoubleToInt64Bits(parsed)).IsEqualTo(BitConverter.DoubleToInt64Bits(value));
    }
}
