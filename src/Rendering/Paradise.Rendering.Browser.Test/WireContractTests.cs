using System.Text.RegularExpressions;

namespace Paradise.Rendering.Browser.Test;

/// <summary>Keeps paradise-webgpu.js's frame decoder in step with the managed encoder.</summary>
/// <remarks>RenderCommandKind values and the record strides are a wire contract with the JS shim; a
/// reordered enum or a resized record would otherwise decode the wrong command silently.</remarks>
public partial class WireContractTests
{
    private static readonly string s_script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "paradise-webgpu.js"));

    [Test]
    public async Task every_command_kind_has_a_decoder_case_with_its_value()
    {
        // Each decoder case is labelled with the member it decodes: `case 5: { // SetBindGroup`.
        var decoded = DecoderCase().Matches(s_script)
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

        var mismatches = Enum.GetValues<RenderCommandKind>()
            .Where(kind => !decoded.TryGetValue(kind.ToString(), out var value) || value != (int)kind)
            .Select(kind => $"{kind} = {(int)kind}, decoder: {(decoded.TryGetValue(kind.ToString(), out var value) ? value : "missing")}")
            .ToArray();

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task record_strides_match()
    {
        await Assert.That(Constant("PASS_STRIDE")).IsEqualTo(BrowserFrameLayout.PassStride);
        await Assert.That(Constant("OP_STRIDE")).IsEqualTo(BrowserFrameLayout.OpStride);
    }

    private static int Constant(string name)
    {
        var match = Regex.Match(s_script, $@"const {name} = (\d+);", RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    [GeneratedRegex(@"case (?<value>\d+):\s*\{?\s*// (?<name>[A-Za-z]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DecoderCase();
}
