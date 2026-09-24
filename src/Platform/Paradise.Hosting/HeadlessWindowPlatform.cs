using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>Provides offscreen windows with optional initial input for unattended runs.</summary>
public sealed class HeadlessWindowPlatform(IReadOnlyList<KeyboardKey>? hold = null) : IWindowPlatform
{
    public IWindow CreateWindow(in WindowOptions options) => new HeadlessWindow(options.Width, options.Height, hold ?? []);
    public void Pump()
    {
    }

    public void Dispose()
    {
    }

    private sealed class HeadlessWindow(uint width, uint height, IReadOnlyList<KeyboardKey> keys) : IWindow
    {
        private readonly object _gate = new();
        private readonly Queue<TimedWindowEvent> _events = new(keys.Select(key =>
            new TimedWindowEvent(TimeSpan.Zero, WindowEvent.Keyboard(key, pressed: true))));
        private bool _closed;

        public uint Width => width;
        public uint Height => height;
        public bool CloseRequested
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public event Action<uint, uint>? Resized
        {
            add { }
            remove { }
        }

        public void RequestClose()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }

        public bool TryReadEvent(out TimedWindowEvent input)
        {
            lock (_gate)
            {
                return _events.TryDequeue(out input);
            }
        }
        public SurfaceDescriptor CreateSurface() => SurfaceDescriptor.Headless(width, height);
        public void Dispose()
        {
        }
    }
}
