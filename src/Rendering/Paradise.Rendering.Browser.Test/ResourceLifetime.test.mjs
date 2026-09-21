import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../Paradise.Rendering.Browser/wwwroot/paradise-webgpu.js', import.meta.url), 'utf8');
let moduleId = 0;

async function createFixture({ initialize = true, canvasAvailable = true, contextAvailable = true, configureFails = false } = {}) {
    const shim = await import(`data:text/javascript;base64,${Buffer.from(`${source}\nexport { G as state };`).toString('base64')}#${moduleId++}`);
    const counts = new Map();
    const listeners = new Map();
    let fail = '';
    let loseDevice;
    const create = (kind, descriptor) => {
        if (fail === kind) throw new Error(`Failed ${kind}`);
        counts.set(kind, (counts.get(kind) ?? 0) + 1);
        return { kind, descriptor };
    };
    const device = {
        limits: { minUniformBufferOffsetAlignment: 256 },
        addEventListener: (name, callback) => listeners.set(name, callback),
        lost: new Promise((resolve) => { loseDevice = resolve; }),
        destroy: () => { counts.set('destroy', (counts.get('destroy') ?? 0) + 1); loseDevice({ reason: 'destroyed', message: '' }); },
        createShaderModule: (descriptor) => create('shader', descriptor),
        createBindGroupLayout: (descriptor) => create('layout', descriptor),
        createPipelineLayout: (descriptor) => create('pipelineLayout', descriptor),
        createRenderPipeline: (descriptor) => create('renderPipeline', descriptor),
        createComputePipeline: (descriptor) => create('computePipeline', descriptor),
        createBindGroup: (descriptor) => create('bindGroup', descriptor),
    };
    const context = {
        configure: () => { if (configureFails) throw new Error('Canvas configuration failed.'); },
        unconfigure: () => counts.set('unconfigure', (counts.get('unconfigure') ?? 0) + 1),
    };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { gpu: {
        requestAdapter: async () => ({ features: new Set(), requestDevice: async () => device }),
        getPreferredCanvasFormat: () => 'bgra8unorm',
    } } });
    globalThis.document = { querySelector: () => canvasAvailable
        ? { getContext: () => contextAvailable ? context : null } : null };
    globalThis.GPUColorWrite = { ALL: 15 };
    if (initialize) {
        await shim.init('#canvas', 1, 1);
        shim.createShaderModule(0, 'shader source', 'test');
    }
    return { shim, counts, listeners, setFailure: (kind) => { fail = kind; } };
}

const renderDescriptor = JSON.stringify({
    label: 'render', vs: 0, vsEntry: 'vertex', fs: -1, groups: [[]],
    vertexLayouts: [], topology: 'triangle-list', stripIndexFormat: null,
});
const computeDescriptor = JSON.stringify({ label: 'compute', cs: 0, csEntry: 'compute', groups: [[]] });

test('render pipelines, compute pipelines and groups share layouts until the final owner retires', async () => {
    const { shim, counts } = await createFixture();
    try {
        shim.createPipeline(0, renderDescriptor);
        shim.createComputePipeline(0, computeDescriptor);
        shim.createBindGroup(0, '[]', '[]', 'group');
        assert.equal(counts.get('layout'), 1);
        assert.equal(shim.state.layoutCache.size, 1);
        shim.destroyPipeline(0);
        shim.destroyPipeline(0);
        shim.destroyComputePipeline(0);
        assert.equal(shim.state.layoutCache.size, 1);
        shim.destroyBindGroup(0);
        assert.equal(shim.state.layoutCache.size, 0);
        shim.destroyShaderModule(0);
        assert.equal(shim.state.modules[0], null);

        shim.createShaderModule(0, 'replacement', 'replacement');
        shim.createComputePipeline(0, computeDescriptor);
        assert.equal(counts.get('layout'), 2);
        shim.destroyComputePipeline(0);
        assert.equal(shim.state.layoutCache.size, 0);
    } finally { shim.dispose(); }
});

for (const kind of ['pipelineLayout', 'renderPipeline', 'computePipeline', 'bindGroup']) {
    test(`failed ${kind} creation rolls back layout ownership while another group remains live`, async () => {
        const { shim, setFailure } = await createFixture();
        try {
            shim.createBindGroup(0, '[]', '[]', 'survivor');
            setFailure(kind);
            const create = kind === 'computePipeline' ? () => shim.createComputePipeline(0, computeDescriptor)
                : kind === 'bindGroup' ? () => shim.createBindGroup(1, '[]', '[]', 'failed')
                    : () => shim.createPipeline(0, renderDescriptor);
            assert.throws(create, new RegExp(`Failed ${kind}`));
            assert.equal(shim.state.layoutCache.size, 1);
            shim.destroyBindGroup(0);
            assert.equal(shim.state.layoutCache.size, 0);
        } finally { shim.dispose(); }
    });
}

test('dispose removes all JS roots and ignores late events from the old device', async () => {
    const { shim, counts, listeners } = await createFixture();
    shim.createPipeline(0, renderDescriptor);
    shim.createComputePipeline(0, computeDescriptor);
    shim.createBindGroup(0, '[]', '[]', 'group');

    shim.dispose();
    shim.dispose();
    await Promise.resolve();
    listeners.get('uncapturederror')({ error: { message: 'late error' } });

    for (const value of Object.values(shim.state)) {
        if (Array.isArray(value)) assert.equal(value.length, 0);
        if (value instanceof Map) assert.equal(value.size, 0);
    }
    assert.equal(shim.state.device, null);
    assert.equal(shim.state.context, null);
    assert.equal(shim.state.canvas, null);
    assert.equal(shim.takeError(), '');
    assert.equal(counts.get('destroy'), 1);
    assert.equal(counts.get('unconfigure'), 1);
});

for (const [name, options, message] of [
    ['missing canvas', { canvasAvailable: false }, /No canvas matches selector/],
    ['missing WebGPU context', { contextAvailable: false }, /returned null/],
    ['failed canvas configuration', { configureFails: true }, /Canvas configuration failed/],
]) {
    test(`initialization cleans up its new device after ${name}`, async () => {
        const { shim, counts, listeners } = await createFixture({ initialize: false, ...options });

        await assert.rejects(shim.init('#canvas', 1, 1), message);
        await Promise.resolve();
        listeners.get('uncapturederror')({ error: { message: 'late initialization error' } });

        assert.equal(counts.get('destroy'), 1);
        assert.equal(shim.state.device, null);
        assert.equal(shim.state.context, null);
        assert.equal(shim.state.canvas, null);
        assert.equal(shim.state.layoutCache.size, 0);
        assert.equal(shim.takeError(), '');
        shim.dispose();
        assert.equal(counts.get('destroy'), 1);
    });
}
