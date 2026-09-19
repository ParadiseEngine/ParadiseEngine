namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Keeps native resource dependencies cached until the owning resource is released.</summary>
internal sealed class NativeResource<T>(T native, List<IDisposable> dependencies) : IDisposable where T : class
{
    public T Native { get; } = native;

    public void Dispose()
    {
        foreach (var dependency in dependencies) dependency.Dispose();
        dependencies.Clear();
    }
}
