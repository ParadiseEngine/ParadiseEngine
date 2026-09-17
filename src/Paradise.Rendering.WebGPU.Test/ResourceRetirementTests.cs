using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using WebGpuSharp.Marshalling;

namespace Paradise.Rendering.WebGPU.Test;

public class ResourceRetirementTests
{
    [Test]
    public async Task offscreen_work_completes_after_inputs_are_destroyed_without_retaining_native_wrappers()
    {
        using var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        var program = ShaderProgramLoader.Load(typeof(ResourceRetirementTests).Assembly, "Shaders.resourceRetirement");
        var output = renderer.CreateBuffer(new BufferDesc("RetirementOutput", 4, BufferUsage.Storage | BufferUsage.CopySrc));
        BufferHandle previousInput = default;
        TextureHandle previousTexture = default;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var retired = SubmitAndRetire(renderer, program, output, (uint)(iteration + 1) * 7);
            var bytes = renderer.ReadbackBuffer(output, 0, 4);
            await Assert.That(BitConverter.ToUInt32(bytes)).IsEqualTo((uint)(iteration + 1) * 448);

            await Assert.That(() => renderer.UpdateBuffer<uint>(retired.Input, 0, new uint[] { 1 }))
                .Throws<StaleHandleException>();
            await Assert.That(() => renderer.WriteTexture(retired.Texture, 0, new byte[4], 4, 1, 1, 1))
                .Throws<StaleHandleException>();
            await Assert.That(() => renderer.SubmitOffscreen(retired.Stream)).Throws<StaleHandleException>();

            if (previousInput.IsValid)
            {
                await Assert.That(retired.Input.Index).IsEqualTo(previousInput.Index);
                await Assert.That(retired.Input.Generation).IsNotEqualTo(previousInput.Generation);
                await Assert.That(retired.Texture.Index).IsEqualTo(previousTexture.Index);
                await Assert.That(retired.Texture.Generation).IsNotEqualTo(previousTexture.Generation);
            }
            previousInput = retired.Input;
            previousTexture = retired.Texture;

            // Readback waits for submitted work but never presents. A frame-deferred release
            // queue would still root these wrappers even after all GPU work has completed.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            foreach (var reference in retired.NativeReferences)
                await Assert.That(reference.IsAlive).IsFalse();
            GC.KeepAlive(renderer);
        }
        renderer.DestroyBuffer(output);
    }

    [Test]
    public async Task dispose_releases_explicit_views_and_compute_pipeline_slots()
    {
        using var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        var texture = renderer.CreateTexture(TextureDescriptor());
        renderer.CreateTextureView(new TextureViewDesc("RetirementView", texture, TextureViewDimension.D2, 0, 1));
        var program = ShaderProgramLoader.Load(typeof(ResourceRetirementTests).Assembly, "Shaders.resourceRetirement");
        renderer.CreateComputePipeline(program);
        await Assert.That(renderer.DeviceForTest.TextureViews.Count).IsEqualTo(1);
        await Assert.That(renderer.DeviceForTest.ComputePipelines.Count).IsEqualTo(1);

        renderer.Dispose();

        await Assert.That(renderer.DeviceForTest.TextureViews.Count).IsEqualTo(0);
        await Assert.That(renderer.DeviceForTest.ComputePipelines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task dispose_destroys_a_retained_native_device_without_reporting_device_loss()
    {
        var log = new DeviceLog();
        using var renderer = TryCreateHeadlessOrSkip(log);
        if (renderer is null) return;
        var borrowedDevice = renderer.NativeDevice;
        var instance = renderer.DeviceForTest.Instance;
        var lost = WebGPUMarshal.GetHandle(borrowedDevice).GetLostFuture();
        await Assert.That(LossCompleted(instance, lost)).IsFalse();

        renderer.Dispose();
        var started = Stopwatch.GetTimestamp();
        var completed = LossCompleted(instance, lost);
        while (!completed && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(1).ConfigureAwait(false);
            completed = LossCompleted(instance, lost);
        }

        await Assert.That(completed).IsTrue();
        await Assert.That(log.Events.Any(entry => entry.Level == LogLevel.Critical || entry.Id.Id == 62)).IsFalse();
        GC.KeepAlive(borrowedDevice);
        GC.KeepAlive(renderer);
    }

    [Test]
    public async Task failed_capture_wait_settles_every_request_and_destroys_its_staging_buffer()
    {
        using var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        var first = new TaskCompletionSource<ColorReadback>();
        var second = new TaskCompletionSource<ColorReadback>();
        var cancelled = new TaskCompletionSource<ColorReadback>();
        cancelled.SetCanceled();
        var requests = new[] { first, second, cancelled };
        var buffers = requests.Select(_ => renderer.NativeDevice.CreateBuffer(new WebGpuSharp.BufferDescriptor
        {
            Label = "FailedCaptureStaging", Size = 256,
            Usage = WebGpuSharp.BufferUsage.MapRead | WebGpuSharp.BufferUsage.CopyDst,
        })).ToArray();
        var pending = requests.Select((request, index) => (request, buffers[index], 1u, 1u, 256u)).ToList();
        var timeout = new TimeoutException("Injected queue completion timeout.");

        var thrown = await Assert.That(() => renderer.CompletePendingCaptures(pending, () => throw timeout))
            .Throws<TimeoutException>();

        await Assert.That(ReferenceEquals(thrown, timeout)).IsTrue();
        await Assert.That(first.Task.IsFaulted && second.Task.IsFaulted).IsTrue();
        await Assert.That(ReferenceEquals(first.Task.Exception!.InnerException, timeout)).IsTrue();
        await Assert.That(ReferenceEquals(second.Task.Exception!.InnerException, timeout)).IsTrue();
        await Assert.That(cancelled.Task.IsCanceled).IsTrue();
        foreach (var buffer in buffers)
            await Assert.That(() => buffer.MapSync(WebGpuSharp.MapMode.Read, 0, 4, 5_000_000_000UL))
                .Throws<WebGpuSharp.WebGPUException>().WithMessageContaining("destroyed");
    }

    private static unsafe bool LossCompleted(WebGpuSharp.Instance instance, WebGpuSharp.Future future)
    {
        var info = new WebGpuSharp.FutureWaitInfo { Future = future };
        var status = WebGPUMarshal.GetHandle(instance).WaitAny(1, &info, 0);
        if (status is not (WebGpuSharp.WaitStatus.Success or WebGpuSharp.WaitStatus.TimedOut))
            throw new InvalidOperationException($"Device loss wait failed: {status}.");
        GC.KeepAlive(instance);
        return info.Completed;
    }

    private sealed class DeviceLog : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, EventId Id)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Enqueue((logLevel, eventId));
    }

    private sealed record RetiredResources(
        BufferHandle Input, TextureHandle Texture, RenderCommandStream Stream, WeakReference[] NativeReferences);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetiredResources SubmitAndRetire(WebGpuRenderer renderer, ShaderProgramDesc program, BufferHandle output, uint multiplier)
    {
        var input = renderer.CreateBufferWithData<uint>(new BufferDesc("RetirementInput", 0, BufferUsage.Storage), new uint[] { multiplier });
        var texture = renderer.CreateTexture(TextureDescriptor());
        renderer.WriteTexture(texture, 0, new byte[] { 64, 0, 0, 255 }, 4, 1, 1, 1);
        var view = renderer.CreateTextureView(new TextureViewDesc("RetirementView", texture, TextureViewDimension.D2, 0, 1));
        var sampler = renderer.CreateSampler(new SamplerDesc(
            "RetirementSampler", SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Nearest, SamplerFilterMode.Nearest, SamplerFilterMode.Nearest, 1));
        var pipeline = renderer.CreateComputePipeline(program);
        var group = renderer.CreateBindGroup(new BindGroupDesc("RetirementGroup", program.Layout.Groups[0], new[]
        {
            BindGroupEntryDesc.ForBuffer(0, input, 0, 4),
            BindGroupEntryDesc.ForTextureView(1, view),
            BindGroupEntryDesc.ForSampler(2, sampler),
            BindGroupEntryDesc.ForBuffer(3, output, 0, 4),
        }));
        var device = renderer.DeviceForTest;
        WeakReference[] references =
        [
            new(device.ResolveBuffer(input)),
            new(device.ResolveTexture(texture).Texture),
            new(device.ResolveTexture(texture).View),
            new(device.ResolveTextureView(view)),
            new(device.ResolveSampler(sampler)),
            new(device.ResolveBindGroup(group)),
            new(device.ResolveComputePipeline(pipeline)),
        ];
        var writer = new ArrayBufferWriter<RenderCommand>();
        var encoder = new RenderCommandEncoder(writer);
        encoder.BeginComputePass();
        encoder.SetComputePipeline(pipeline);
        encoder.SetBindGroup(0, group);
        encoder.Dispatch(new DispatchCommand(1, 1, 1));
        encoder.EndComputePass();
        var stream = new RenderCommandStream(writer.WrittenMemory, ReadOnlyMemory<RenderPassDesc>.Empty);

        renderer.SubmitOffscreen(stream);
        renderer.DestroyBindGroup(group);
        renderer.DestroySampler(sampler);
        renderer.DestroyTextureView(view);
        renderer.DestroyTexture(texture);
        renderer.DestroyBuffer(input);
        renderer.DestroyComputePipeline(pipeline);
        // Repeated release must remain harmless even without a retirement queue.
        renderer.DestroyBuffer(input);
        renderer.DestroyTexture(texture);

        return new RetiredResources(input, texture, stream, references);
    }

    private static TextureDesc TextureDescriptor() => new(
        "RetirementTexture", 1, 1, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba8Unorm,
        TextureUsage.TextureBinding | TextureUsage.CopyDst);

    private static WebGpuRenderer? TryCreateHeadlessOrSkip(ILogger? logger = null)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(logger: logger);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }
}
