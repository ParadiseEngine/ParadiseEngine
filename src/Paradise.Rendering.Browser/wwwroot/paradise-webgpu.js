// WebGPU shim for Paradise.Rendering.Browser's BrowserRenderer.
//
// Every GPU object lives in a JS table indexed by an integer SLOT that the C# side allocates
// (BrowserRenderer.Internal.ResourceTable owns the free list and the generation counter, so a
// destroyed handle stops resolving in C# before it ever reaches this file). Creation functions
// therefore take the slot to fill rather than returning one; destruction nulls the slot.
//
// All orchestration stays in C#: this module translates descriptors into browser WebGPU calls and
// nothing else. Descriptors arrive as JSON for the one-off creation paths, and as a single binary
// buffer for per-frame submission (see submitFrame) so a frame costs ONE interop crossing rather
// than one per render command. The binary layout is defined by BrowserRenderer.Submit.cs; the two
// must be changed together.

const G = {
    device: null,
    context: null,
    canvas: null,
    format: 'bgra8unorm',
    supportsBc: false,
    adapterInfo: '',
    buffers: [],
    textures: [],       // { texture, view } - view is the default full view
    textureViews: [],
    samplers: [],
    bindGroups: [],
    pipelines: [],
    // Separate table from render pipelines: setPipeline is type-checked per pass kind, and the
    // C# side allocates slots per table (slot index == array index here).
    computePipelines: [],
    modules: [],
    // Canonical bind-group-layout JSON -> GPUBindGroupLayout. Pipelines and bind groups built from
    // the same layout content share one GPU object, which is what makes them compatible.
    layoutCache: new Map(),
    lastError: '',
};

function put(table, slot, value) {
    while (table.length <= slot) table.push(null);
    table[slot] = value;
}

// C# hands us either a byte[] (copied to a Uint8Array) or a MemoryView over wasm memory; both
// answer to this.
function toBytes(view) {
    return view instanceof Uint8Array ? view : view.slice();
}

function getLayout(layoutJson) {
    let layout = G.layoutCache.get(layoutJson);
    if (!layout) {
        layout = G.device.createBindGroupLayout({ entries: JSON.parse(layoutJson) });
        G.layoutCache.set(layoutJson, layout);
    }
    return layout;
}

// ---- device / surface ----

export async function init(canvasSelector, width, height) {
    if (!navigator.gpu) throw new Error('navigator.gpu is missing - this browser has no WebGPU support.');
    const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
    if (!adapter) throw new Error('navigator.gpu.requestAdapter returned null - no WebGPU adapter available.');

    const supportsBc = adapter.features.has('texture-compression-bc');
    G.device = await adapter.requestDevice({
        label: 'Paradise.Rendering.Browser',
        requiredFeatures: supportsBc ? ['texture-compression-bc'] : [],
    });
    // Nothing pumps WebGPU validation errors by default; record the first one so C# can surface it
    // instead of the frame silently coming back as the clear colour.
    G.device.addEventListener('uncapturederror', (e) => {
        const message = (e.error && e.error.message) ? e.error.message : String(e.error);
        if (!G.lastError) G.lastError = message;
        console.error('[paradise-webgpu] uncaptured error:', message);
    });
    G.device.lost.then((info) => {
        const message = `device lost (${info.reason}): ${info.message}`;
        if (!G.lastError) G.lastError = message;
        console.error('[paradise-webgpu]', message);
    });

    const canvas = document.querySelector(canvasSelector);
    if (!canvas) throw new Error(`No canvas matches selector '${canvasSelector}'.`);
    if (width > 0) canvas.width = width;
    if (height > 0) canvas.height = height;
    G.canvas = canvas;
    G.context = canvas.getContext('webgpu');
    if (!G.context) throw new Error("canvas.getContext('webgpu') returned null.");
    G.format = navigator.gpu.getPreferredCanvasFormat();
    G.context.configure({ device: G.device, format: G.format, alphaMode: 'opaque' });

    const info = adapter.info;
    G.supportsBc = supportsBc;
    G.adapterInfo = info ? [info.vendor, info.architecture, info.device].filter(Boolean).join(' ') : 'adapter-info-unavailable';
    return G.format;
}

// WebGPU guarantees this is at most 256; C# clamps up so uniform ring layouts stay adapter
// independent.
export function uniformBufferOffsetAlignment() {
    return G.device.limits.minUniformBufferOffsetAlignment;
}

export function supportsBcCompression() {
    return G.supportsBc;
}

export function adapterInfo() {
    return G.adapterInfo;
}

export function resize(width, height) {
    if (!G.canvas) return;
    G.canvas.width = width;
    G.canvas.height = height;
    // Reconfiguring after a size change drops the stale swapchain textures; the format and device
    // are unchanged.
    G.context.configure({ device: G.device, format: G.format, alphaMode: 'opaque' });
}

// Returns the first error seen since the last call, and clears it. Hosts poll this to turn silent
// GPU validation failures into a visible status.
export function takeError() {
    const error = G.lastError;
    G.lastError = '';
    return error;
}

export function dispose() {
    if (G.device) G.device.destroy();
    G.device = null;
}

// ---- shader modules ----

export function createShaderModule(slot, wgsl, label) {
    put(G.modules, slot, G.device.createShaderModule({ code: wgsl, label }));
}

// ---- buffers ----

export function createBuffer(slot, size, usage, label) {
    put(G.buffers, slot, G.device.createBuffer({ size, usage, label }));
}

export function writeBuffer(index, offset, data) {
    G.device.queue.writeBuffer(G.buffers[index], offset, toBytes(data));
}

export function destroyBuffer(index) {
    const buffer = G.buffers[index];
    if (buffer) buffer.destroy();
    G.buffers[index] = null;
}

// ---- textures / views / samplers ----

export function createTexture(slot, descJson) {
    const d = JSON.parse(descJson);
    const texture = G.device.createTexture({
        label: d.label,
        size: { width: d.width, height: d.height, depthOrArrayLayers: d.layers },
        mipLevelCount: d.mips,
        sampleCount: d.samples,
        dimension: d.dimension,
        format: d.format,
        usage: d.usage,
    });
    // One default full view per texture mirrors the Dawn backend's TextureEntry: bind groups that
    // name a texture (rather than an explicit view) and depth attachments without a DepthView use it.
    put(G.textures, slot, { texture, view: texture.createView() });
}

export function writeTexture(index, mipLevel, data, bytesPerRow, rowsPerImage, width, height) {
    G.device.queue.writeTexture(
        { texture: G.textures[index].texture, mipLevel },
        toBytes(data),
        { bytesPerRow, rowsPerImage },
        { width, height, depthOrArrayLayers: 1 });
}

export function destroyTexture(index) {
    const entry = G.textures[index];
    if (entry) entry.texture.destroy();
    G.textures[index] = null;
}

export function createTextureView(slot, textureIndex, dimension, baseArrayLayer, arrayLayerCount, label) {
    const view = G.textures[textureIndex].texture.createView({
        label,
        dimension,
        baseArrayLayer,
        arrayLayerCount,
        baseMipLevel: 0,
        mipLevelCount: 1,
    });
    put(G.textureViews, slot, view);
}

export function destroyTextureView(index) {
    // Views have no explicit destroy in WebGPU; dropping the reference is the whole teardown.
    G.textureViews[index] = null;
}

export function createSampler(slot, descJson) {
    const d = JSON.parse(descJson);
    const desc = {
        label: d.label,
        addressModeU: d.addressU,
        addressModeV: d.addressV,
        addressModeW: d.addressW,
        magFilter: d.magFilter,
        minFilter: d.minFilter,
        mipmapFilter: d.mipFilter,
        maxAnisotropy: d.maxAnisotropy,
    };
    // A compare function is what makes this a sampler_comparison (shadow-map depth compare);
    // WebGPU rejects the key being present-but-undefined, so only set it when asked for.
    if (d.compare) desc.compare = d.compare;
    put(G.samplers, slot, G.device.createSampler(desc));
}

export function destroySampler(index) {
    G.samplers[index] = null;
}

// ---- bind groups ----

export function createBindGroup(slot, layoutJson, entriesJson, label) {
    const entries = JSON.parse(entriesJson).map((e) => {
        let resource;
        switch (e.kind) {
            case 0: resource = { buffer: G.buffers[e.index], offset: e.offset, size: e.size }; break;
            case 1: resource = G.textures[e.index].view; break;
            case 2: resource = G.samplers[e.index]; break;
            default: resource = G.textureViews[e.index]; break;
        }
        return { binding: e.binding, resource };
    });
    put(G.bindGroups, slot, G.device.createBindGroup({ label, layout: getLayout(layoutJson), entries }));
}

export function destroyBindGroup(index) {
    G.bindGroups[index] = null;
}

// ---- pipelines ----

export function createPipeline(slot, descJson) {
    const d = JSON.parse(descJson);
    const desc = {
        label: d.label,
        // Groups are dense (C# fills declared gaps with empty layouts, as the Dawn backend does);
        // a program that reflects no bindings keeps WebGPU's implicit layout.
        layout: d.groups
            ? G.device.createPipelineLayout({ bindGroupLayouts: d.groups.map((g) => getLayout(JSON.stringify(g))) })
            : 'auto',
        vertex: {
            module: G.modules[d.vs],
            entryPoint: d.vsEntry,
            buffers: d.vertexLayouts.map((l) => ({
                arrayStride: l.stride,
                stepMode: l.stepMode,
                attributes: l.attributes,
            })),
        },
        primitive: {
            // No cull mode: the contract has none, so every pipeline renders double-sided under
            // WebGPU's default CCW front face (which matches glTF winding), exactly as the Dawn
            // backend does.
            topology: d.topology,
            ...(d.stripIndexFormat ? { stripIndexFormat: d.stripIndexFormat } : {}),
        },
        multisample: { count: 1, mask: 0xFFFFFFFF },
    };
    // No fragment stage means a depth-only pipeline (the shadow caster): legal in WebGPU as long as
    // a depth-stencil state is present.
    if (d.fs >= 0) {
        desc.fragment = {
            module: G.modules[d.fs],
            entryPoint: d.fsEntry,
            targets: [{ format: d.colorFormat, blend: blendState(d.blend), writeMask: GPUColorWrite.ALL }],
        };
    }
    if (d.depth) {
        desc.depthStencil = {
            format: d.depth.format,
            depthWriteEnabled: d.depth.write,
            depthCompare: d.depth.compare,
        };
    }
    put(G.pipelines, slot, G.device.createRenderPipeline(desc));
}

function blendState(mode) {
    switch (mode) {
        case 1: return {
            color: { operation: 'add', srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha' },
            alpha: { operation: 'add', srcFactor: 'one', dstFactor: 'one-minus-src-alpha' },
        };
        case 2: return {
            color: { operation: 'add', srcFactor: 'one', dstFactor: 'one' },
            alpha: { operation: 'add', srcFactor: 'one', dstFactor: 'one' },
        };
        default: return undefined;
    }
}

export function destroyPipeline(index) {
    G.pipelines[index] = null;
}

export function createComputePipeline(slot, descJson) {
    const d = JSON.parse(descJson);
    put(G.computePipelines, slot, G.device.createComputePipeline({
        label: d.label,
        layout: d.groups
            ? G.device.createPipelineLayout({ bindGroupLayouts: d.groups.map((g) => getLayout(JSON.stringify(g))) })
            : 'auto',
        compute: { module: G.modules[d.cs], entryPoint: d.csEntry },
    }));
}

export function destroyComputePipeline(index) {
    G.computePipelines[index] = null;
}

// ---- frame submission ----

// Binary record sizes; mirrored by BrowserRenderer.Submit.cs. The frame buffer is
// [passCount x PASS_STRIDE bytes][opCount x OP_STRIDE bytes], little-endian throughout.
const PASS_STRIDE = 64;
const OP_STRIDE = 48;

const LOAD_OPS = ['load', 'clear'];
const STORE_OPS = ['store', 'discard'];

export function submitFrame(frame, passCount, opCount) {
    const bytes = toBytes(frame);
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const opBase = passCount * PASS_STRIDE;

    const encoder = G.device.createCommandEncoder();
    let backbuffer = null;
    let pass = null;

    for (let i = 0; i < opCount; i++) {
        const o = opBase + i * OP_STRIDE;
        switch (dv.getUint8(o)) {
            case 0: { // BeginPass
                const p = dv.getUint32(o + 4, true) * PASS_STRIDE;
                const colorCount = dv.getUint32(p, true);
                const desc = { colorAttachments: [] };
                if (colorCount > 0) {
                    const viewIndex = dv.getInt32(p + 12, true);
                    if (viewIndex < 0 && backbuffer === null) backbuffer = G.context.getCurrentTexture().createView();
                    desc.colorAttachments.push({
                        view: viewIndex < 0 ? backbuffer : G.textureViews[viewIndex],
                        loadOp: LOAD_OPS[dv.getUint32(p + 4, true)],
                        storeOp: STORE_OPS[dv.getUint32(p + 8, true)],
                        clearValue: {
                            r: dv.getFloat32(p + 16, true),
                            g: dv.getFloat32(p + 20, true),
                            b: dv.getFloat32(p + 24, true),
                            a: dv.getFloat32(p + 28, true),
                        },
                    });
                }
                if (dv.getInt32(p + 32, true) !== 0) {
                    const depthViewIndex = dv.getInt32(p + 40, true);
                    desc.depthStencilAttachment = {
                        view: depthViewIndex < 0
                            ? G.textures[dv.getInt32(p + 36, true)].view
                            : G.textureViews[depthViewIndex],
                        depthLoadOp: LOAD_OPS[dv.getUint32(p + 44, true)],
                        depthStoreOp: STORE_OPS[dv.getUint32(p + 48, true)],
                        depthClearValue: dv.getFloat32(p + 52, true),
                    };
                }
                pass = encoder.beginRenderPass(desc);
                break;
            }
            case 1: // EndPass
                pass.end();
                pass = null;
                break;
            case 2: // SetPipeline
                pass.setPipeline(G.pipelines[dv.getUint32(o + 4, true)]);
                break;
            case 3: // SetVertexBuffer
                pass.setVertexBuffer(
                    dv.getUint32(o + 4, true), G.buffers[dv.getUint32(o + 8, true)],
                    dv.getFloat64(o + 32, true), dv.getFloat64(o + 40, true));
                break;
            case 4: // SetIndexBuffer
                pass.setIndexBuffer(
                    G.buffers[dv.getUint32(o + 4, true)], dv.getUint32(o + 8, true) === 0 ? 'uint16' : 'uint32',
                    dv.getFloat64(o + 32, true), dv.getFloat64(o + 40, true));
                break;
            case 5: { // SetBindGroup
                const group = G.bindGroups[dv.getUint32(o + 8, true)];
                if (dv.getUint32(o + 12, true) !== 0) {
                    pass.setBindGroup(dv.getUint32(o + 4, true), group, [dv.getUint32(o + 16, true)]);
                } else {
                    pass.setBindGroup(dv.getUint32(o + 4, true), group);
                }
                break;
            }
            case 6: // Draw
                pass.draw(
                    dv.getUint32(o + 4, true), dv.getUint32(o + 8, true),
                    dv.getUint32(o + 12, true), dv.getUint32(o + 16, true));
                break;
            case 7: // DrawIndexed
                pass.drawIndexed(
                    dv.getUint32(o + 4, true), dv.getUint32(o + 8, true), dv.getUint32(o + 12, true),
                    dv.getInt32(o + 16, true), dv.getUint32(o + 20, true));
                break;
            case 8: // SetViewport
                pass.setViewport(
                    dv.getFloat32(o + 4, true), dv.getFloat32(o + 8, true), dv.getFloat32(o + 12, true),
                    dv.getFloat32(o + 16, true), dv.getFloat32(o + 20, true), dv.getFloat32(o + 24, true));
                break;
            // Compute passes have no pass-table record (no attachments). `pass` holds either
            // encoder kind — setBindGroup (case 5) is signature-identical on both, and the
            // opcode (2 vs 9..11) selects the pipeline table, so no mode flag is needed.
            case 9: // BeginComputePass
                pass = encoder.beginComputePass();
                break;
            case 10: // EndComputePass
                pass.end();
                pass = null;
                break;
            case 11: // SetComputePipeline
                pass.setPipeline(G.computePipelines[dv.getUint32(o + 4, true)]);
                break;
            case 12: // Dispatch
                pass.dispatchWorkgroups(
                    dv.getUint32(o + 4, true), dv.getUint32(o + 8, true), dv.getUint32(o + 12, true));
                break;
            default:
                throw new Error(`Unknown render command opcode ${dv.getUint8(o)}.`);
        }
    }

    G.device.queue.submit([encoder.finish()]);
}
