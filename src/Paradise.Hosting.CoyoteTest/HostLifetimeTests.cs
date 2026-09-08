using Microsoft.Coyote.Specifications;

namespace Paradise.Hosting.CoyoteTest;

/// <summary>Explores startup, close and consumer lifetime without native window calls.</summary>
public static class HostLifetimeTests
{
    public static async Task FailedStartup_ReleasesPresentationWithoutConstructingIt()
    {
        var lifetime = new HostLifetime();
        var failure = new InvalidOperationException("Simulation startup failed.");
        var simulation = Task.Run(async () =>
        {
            lifetime.Fail(failure);
            lifetime.MarkSimulationUnavailable();
            await lifetime.PresentationStopped.ConfigureAwait(false);
        });
        var presentation = Task.Run(async () =>
        {
            var initialized = await lifetime.SimulationReady.ConfigureAwait(false);
            Specification.Assert(!initialized, "A failed simulation must not permit presentation creation.");
            lifetime.MarkPresentationStopped();
        });
        await Task.WhenAll(simulation, presentation).ConfigureAwait(false);
        Specification.Assert(lifetime.StopRequested && ReferenceEquals(lifetime.Failure, failure), "Startup failure must stop the host.");
    }

    public static async Task CloseRacingPresentationStartup_KeepsWorldAliveUntilConsumerExits()
    {
        var lifetime = new HostLifetime();
        var world = new BorrowedWorld();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulation = Task.Run(async () =>
        {
            lifetime.MarkSimulationReady();
            await closed.Task.ConfigureAwait(false);
            await lifetime.PresentationStopped.ConfigureAwait(false);
            world.Dispose();
        });
        var presentation = Task.Run(async () =>
        {
            Specification.Assert(await lifetime.SimulationReady.ConfigureAwait(false), "Simulation must initialize before presentation.");
            // Startup may already have passed the stop check when the main thread closes.
            await closed.Task.ConfigureAwait(false);
            world.Read();
            await Task.Yield();
            world.Read();
            world.ReleaseConsumer();
            lifetime.MarkPresentationStopped();
        });
        var close = Task.Run(() =>
        {
            lifetime.RequestStop();
            closed.SetResult();
        });
        await Task.WhenAll(simulation, presentation, close).ConfigureAwait(false);
        Specification.Assert(world.Disposed, "The simulation must eventually dispose its world.");
    }

    public static async Task RenderFailureRacingClose_PreservesFailureAndConsumerDisposalOrder()
    {
        var lifetime = new HostLifetime();
        var world = new BorrowedWorld();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Frame failed.");
        var simulation = Task.Run(async () =>
        {
            lifetime.MarkSimulationReady();
            await failed.Task.ConfigureAwait(false);
            await lifetime.PresentationStopped.ConfigureAwait(false);
            world.Dispose();
        });
        var presentation = Task.Run(async () =>
        {
            Specification.Assert(await lifetime.SimulationReady.ConfigureAwait(false), "Simulation must initialize before presentation.");
            lifetime.Fail(failure);
            failed.SetResult();
            // Disposal and an outstanding capture can still read simulation-owned data.
            await Task.Yield();
            world.Read();
            world.ReleaseConsumer();
            lifetime.MarkPresentationStopped();
        });
        var close = Task.Run(lifetime.RequestStop);
        await Task.WhenAll(simulation, presentation, close).ConfigureAwait(false);
        Specification.Assert(ReferenceEquals(lifetime.Failure, failure), "Close must not erase a worker failure.");
        Specification.Assert(world.Disposed, "The simulation must eventually dispose its world.");
    }

    public static async Task ResizeRacingConsumer_PreservesWholeSizeAndZeroDimensions()
    {
        var lifetime = new HostLifetime();
        var writer = Task.Run(async () =>
        {
            lifetime.Resize(100, 200);
            await Task.Yield();
            lifetime.Resize(0, 0);
            await Task.Yield();
            lifetime.Resize(uint.MaxValue, uint.MaxValue);
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                if (lifetime.TryTakeResize(out var width, out var height))
                {
                    Specification.Assert((width, height) is (100, 200) or (0, 0) or (uint.MaxValue, uint.MaxValue),
                        "A resize must not combine dimensions from different events.");
                }

                await Task.Yield();
            }
        });
        await Task.WhenAll(writer, reader).ConfigureAwait(false);
        lifetime.Resize(0, 0);
        Specification.Assert(lifetime.TryTakeResize(out var finalWidth, out var finalHeight), "The final resize must remain available.");
        Specification.Assert(finalWidth == 0 && finalHeight == 0, "Minimization is a real resize.");
        Specification.Assert(!lifetime.TryTakeResize(out _, out _), "A resize is consumed once.");
    }

    private sealed class BorrowedWorld
    {
        private readonly object _gate = new();
        private bool _consumerReleased;
        private bool _disposed;

        public bool Disposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public void Read()
        {
            lock (_gate)
            {
                Specification.Assert(!_disposed, "Presentation used a simulation world after it was disposed.");
            }
        }

        public void ReleaseConsumer()
        {
            lock (_gate)
            {
                _consumerReleased = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Specification.Assert(_consumerReleased, "Simulation disposal overtook its presentation consumer.");
                _disposed = true;
            }
        }
    }
}
