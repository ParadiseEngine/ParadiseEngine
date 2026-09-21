using Paradise.Assets.Pipeline;

namespace Paradise.Cli.Test;

public class TrayExtensionRegistrationTests
{
    private sealed class Extension(Func<IReadOnlyList<TrayTaskGroup>> create) : ITrayExtension
    {
        public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context) => create();
    }

    private sealed class Context : ITrayExtensionContext
    {
        public string ProjectDirectory => "/project";
        public Task<int> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => Task.FromResult(0);
        public void Log(string message) { }
    }

    private sealed class DisposableImporter : IAssetImporter, IDisposable
    {
        public string Name => "test";
        public bool RecordsIdentity => false;
        public int Disposals { get; private set; }
        public bool Claims(ImportCandidate candidate) => false;
        public bool Import(ImportContext context, List<string> errors) => false;
        public void Dispose() => Disposals++;
    }

    private static TrayTaskGroup Group(string id) => new()
    {
        Id = id, Label = id, AutoTask = "run",
        Tasks = [new("run", "Run", _ => Task.FromResult(0))],
    };

    [Test]
    public async Task invalid_registration_cannot_leave_partial_groups_or_hide_later_extensions()
    {
        var errors = new List<string>();
        ITrayExtension[] extensions =
        [
            new Extension(() => [Group("first")]),
            new Extension(() => [Group("partial"), Group("first")]),
            new Extension(() => throw new InvalidDataException("broken registration")),
            new Extension(() => [Group("last")]),
        ];
        var config = TrayTaskConfiguration.Register(extensions, new Context(), errors.Add);
        await Assert.That(config.Groups.Select(group => group.Id).SequenceEqual(["first", "last"])).IsTrue();
        await Assert.That(errors.Count).IsEqualTo(2);
    }

    [Test]
    public async Task loader_disposes_owned_instances_once_without_disposing_caller_importers()
    {
        var builtIn = new DisposableImporter();
        var loaded = new DisposableImporter();
        using var extensions = new LoadedExtensions([builtIn], _ => { });
        extensions.Add(loaded, false);
        extensions.Dispose();
        extensions.Dispose();
        await Assert.That(builtIn.Disposals).IsEqualTo(0);
        await Assert.That(loaded.Disposals).IsEqualTo(1);
    }
}
