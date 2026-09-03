// Bootstrap: start the .NET runtime, hand the managed side the scene name and canvas size, then
// pump requestAnimationFrame. The managed entry point is never run - the app is driven purely
// through its [JSExport] surface (see Program.cs).
import { dotnet } from './_framework/dotnet.js';

const status = document.getElementById('status');
const canvas = document.getElementById('gpu-canvas');

try {
    if (!navigator.gpu) throw new Error('this browser exposes no navigator.gpu (WebGPU required)');

    const { getAssemblyExports, getConfig } = await dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    const program = exports.Paradise.Rendering.Browser.Sample.Program;

    const query = new URLSearchParams(location.search);
    const scene = query.get('scene') || 'cube';
    // Canvas size and instance count are query-driven so the same page serves as the perf harness.
    canvas.width = Number(query.get('w')) || canvas.width;
    canvas.height = Number(query.get('h')) || canvas.height;
    document.title = `Paradise browser renderer - ${scene}`;
    const hostModuleUrl = new URL('paradise-sample-host.js', document.baseURI).href;
    // ?log=debug turns the engine's own diagnostics up — the PBR cluster dump among them. Passed
    // as an argument because wasm has no environment to read one from.
    await program.InitAsync(
        scene, hostModuleUrl, canvas.width, canvas.height,
        Number(query.get('boxes')) || 0, query.get('log') || '');

    const frame = () => {
        program.OnAnimationFrame();
        requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
} catch (error) {
    // The managed side writes its own SAMPLE-FAIL when it can; this covers everything before the
    // runtime is up (no WebGPU, a failed download, a broken import).
    status.textContent = `SAMPLE-FAIL: ${error && error.message ? error.message : error}`;
    console.error(error);
}
