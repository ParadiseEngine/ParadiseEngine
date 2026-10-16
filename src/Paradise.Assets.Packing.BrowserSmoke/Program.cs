using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

using Paradise.Assets.Packing.Smoke;

namespace Paradise.Assets.Packing.BrowserSmoke;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static void Main() { }

    [JSExport]
    public static Task<string> Verify(byte[] pack) => SmokeCheck.VerifyBytesAsync(pack);
}
