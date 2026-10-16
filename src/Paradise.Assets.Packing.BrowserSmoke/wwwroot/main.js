import { dotnet } from './_framework/dotnet.js';

const result = document.getElementById('result');
try {
    const [{ getAssemblyExports, getConfig }, response] = await Promise.all([
        dotnet.create(), fetch('smoke.pak'),
    ]);
    if (!response.ok) throw new Error(`smoke.pak: HTTP ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    result.textContent = await exports.Paradise.Assets.Packing.BrowserSmoke.Program.Verify(bytes);
    document.title = 'PACK-OK';
} catch (error) {
    result.textContent = `PACK-FAIL: ${error.stack || error}`;
    document.title = 'PACK-FAIL';
    console.error(error);
}
