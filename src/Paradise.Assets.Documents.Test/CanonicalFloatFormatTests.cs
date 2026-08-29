namespace Paradise.Assets.Documents.Test;

/// <summary>
/// Pins the float layout to CPython's <c>repr</c>, digit for digit. Each expectation below was
/// checked against CPython 3.13 <c>repr()</c>; the Python mirror gets this behaviour for free
/// (its writer literally calls <c>repr</c>), so these vectors are where a C#-side drift fails.
/// </summary>
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
    public async Task tiny_and_huge_values_are_scientific_with_signed_two_digit_exponent(double value, string expected)
    {
        await Assert.That(CanonicalTomlWriter.FormatFloat(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task the_positional_cutoff_sits_exactly_at_ten_to_the_sixteenth()
    {
        // Python: repr(1e15) is positional, repr(1e16) is scientific. The boundary digits
        // matter because a one-off here silently splits every large float between the writers.
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
        await Assert.That(CanonicalTomlWriter.FormatFloat(-0.0)).IsEqualTo("-0.0");
    }

    [Test]
    public async Task every_finite_output_parses_back_to_the_same_double()
    {
        // Shortest-round-trip is a property, not a formatting choice: whatever the layout, the
        // digits must reproduce the bits.
        double[] values = [0.1, 1.0 / 3.0, 12345.6789, 4.9e-300, 2.2250738585072014e-308, 1e21, 123456.78901234567];
        foreach (var value in values)
        {
            var text = CanonicalTomlWriter.FormatFloat(value);
            await Assert.That(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(value);
        }
    }
}
