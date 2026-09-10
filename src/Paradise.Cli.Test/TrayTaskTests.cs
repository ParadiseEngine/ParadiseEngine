using System.Text.Json;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class TrayTaskTests
{
    private const string Config = """
        {
          "version": 1,
          "groups": [{
            "id": "dialogue", "label": "Dialogue", "autoTask": "compile",
            "inputs": [
              { "path": "authoring/story", "patterns": ["*.story", "*.project"] },
              { "path": "assets/catalog.toml" }
            ],
            "outputs": ["authoring/story/generated.project", "story/output.json"],
            "tasks": [
              { "id": "compile", "label": "Compile Now", "executable": "dotnet", "arguments": ["build", "project with spaces", "-t:Generate"] },
              { "id": "check", "label": "Check Artifacts", "executable": "dotnet", "arguments": ["build", "-t:Check"] }
            ],
            "openDirectory": "authoring/story"
          }]
        }
        """;

    private static TrayTaskGroupConfiguration Group() => TrayTaskConfiguration.Parse(Config).Groups.Single();
    private static TrayTaskState State() => new("compile", false, TimeSpan.FromMilliseconds(300));

    [Test]
    public async Task configuration_keeps_arguments_separate_and_loads_through_a_mount()
    {
        using var fs = new MemoryFileSystem();
        fs.CreateDirectory("/project/authoring");
        fs.WriteAllText("/project/authoring/tray-tasks.json", Config);
        var config = TrayTaskConfiguration.Load(fs, "/project");
        await Assert.That(config.Groups.Single().Tasks[0].Arguments[1]).IsEqualTo("project with spaces");
        await Assert.That(config.Groups.Single().AutoWatch).IsFalse();
        await Assert.That(TrayTaskConfiguration.Load(fs, "/other").Groups).IsEmpty();
    }

    [Test]
    [Arguments("authoring/story/start.story")]
    [Arguments("authoring/story/nested/second.story")]
    [Arguments("authoring/story/game.project")]
    [Arguments("assets/catalog.toml")]
    public async Task configured_sources_are_observed(string path)
    {
        await Assert.That(Group().Observes(path)).IsTrue();
    }

    [Test]
    [Arguments("authoring/story/generated.project")]
    [Arguments("story/output.json")]
    [Arguments("authoring/story/obj/temporary.story")]
    [Arguments("authoring/story/bin/generated.story")]
    [Arguments(".editor/log.story")]
    [Arguments("authoring/story/notes.txt")]
    [Arguments("authoring/story-other/other.story")]
    public async Task generated_outputs_caches_and_unrelated_files_do_not_trigger_watch(string path)
    {
        await Assert.That(Group().Observes(path)).IsFalse();
    }

    [Test]
    public async Task directory_renames_and_deletes_cover_old_and_new_source_roots()
    {
        var group = Group();
        await Assert.That(group.Observes("authoring/story/chapter", structural: true)).IsTrue();
        await Assert.That(group.Observes("authoring", structural: true)).IsTrue();
        await Assert.That(group.Observes("elsewhere", structural: true)).IsFalse();
        await Assert.That(group.Observes("authoring/story/generated.project", structural: true)).IsFalse();
    }

    [Test]
    [Arguments("../outside")]
    [Arguments("/absolute")]
    [Arguments("authoring/../outside")]
    [Arguments("C:/outside")]
    [Arguments("authoring\\outside")]
    public async Task configuration_refuses_paths_outside_the_project(string path)
    {
        Assert.Throws<InvalidDataException>(() => TrayTaskConfiguration.ValidatePath(path));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [Test]
    public async Task configuration_refuses_unknown_fields_versions_duplicates_and_missing_tasks()
    {
        Assert.Throws<JsonException>(() => TrayTaskConfiguration.Parse(Config.Replace("\"autoTask\"", "\"typo\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => TrayTaskConfiguration.Parse(Config.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => TrayTaskConfiguration.Parse(Config.Replace("\"id\": \"check\"", "\"id\": \"compile\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => TrayTaskConfiguration.Parse(Config.Replace("\"autoTask\": \"compile\"", "\"autoTask\": \"missing\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => TrayTaskConfiguration.Parse("{\"version\":1,\"groups\":null}"));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [Test]
    public async Task menu_is_a_parent_with_a_checkbox_manual_tasks_cancel_status_and_folder()
    {
        var group = Group();
        var state = State();
        var opened = false;
        var menu = new TrayTaskMenu(group.Label, TrayTaskMenuItem.For(group, state, () => opened = true));
        await Assert.That(menu.Label).IsEqualTo("Dialogue");
        await Assert.That(menu.Items[0].Label()).IsEqualTo("Auto-watch");
        await Assert.That(menu.Items[0].Checked!()).IsFalse();
        menu.Items[1].Invoke();
        await Assert.That(state.TryBegin(DateTimeOffset.UnixEpoch)).IsEqualTo("compile");
        await Assert.That(menu.Items[1].IsEnabled).IsFalse();
        await Assert.That(menu.Items[2].IsEnabled).IsFalse();
        await Assert.That(menu.Items[3].IsEnabled).IsTrue();
        await Assert.That(menu.Items[5].IsEnabled).IsFalse();
        menu.Items[2].Invoke(); // Stale/native clicks cannot queue another command while busy.
        menu.Items[3].Invoke();
        await Assert.That(state.Snapshot.CancelRequested).IsTrue();
        state.Complete(130);
        await Assert.That(state.TryBegin(DateTimeOffset.MaxValue)).IsNull();
        await Assert.That(menu.Items[5].Label()).Contains("cancelled");
        menu.Items[6].Invoke();
        await Assert.That(opened).IsTrue();
    }

    [Test]
    public async Task enabling_watch_catches_up_then_debounces_saves_and_preserves_edits_during_a_compile()
    {
        var state = State();
        var now = DateTimeOffset.UnixEpoch;
        state.Observe(now);
        await Assert.That(state.TryBegin(now)).IsNull();
        state.ToggleAutoWatch();
        await Assert.That(state.TryBegin(now)).IsEqualTo("compile");
        state.Observe(now.AddMilliseconds(100));
        state.Observe(now.AddMilliseconds(200));
        state.Complete(0);
        await Assert.That(state.TryBegin(now.AddMilliseconds(499))).IsNull();
        await Assert.That(state.TryBegin(now.AddMilliseconds(500))).IsEqualTo("compile");
        state.Complete(1);
        await Assert.That(state.Snapshot.Status).Contains("failed");
        await Assert.That(state.TryBegin(DateTimeOffset.MaxValue)).IsNull();
    }

    [Test]
    public async Task disabling_watch_discards_automatic_work_but_not_manual_requests()
    {
        var state = State();
        state.ToggleAutoWatch();
        state.Observe(DateTimeOffset.UnixEpoch);
        state.Request("check");
        state.ToggleAutoWatch();
        await Assert.That(state.TryBegin(DateTimeOffset.UnixEpoch)).IsEqualTo("check");
        state.Complete(0);
        state.Observe(DateTimeOffset.UnixEpoch);
        await Assert.That(state.TryBegin(DateTimeOffset.MaxValue)).IsNull();
        await Assert.That(state.Snapshot.AutoWatch).IsFalse();
    }

    [Test]
    public async Task watcher_failure_disables_the_checkbox_without_disabling_manual_compilation()
    {
        var state = State();
        var items = TrayTaskMenuItem.For(Group(), state, null);
        state.ToggleAutoWatch();
        state.DisableAutoWatch("watcher unavailable");
        await Assert.That(items[0].IsEnabled).IsFalse();
        await Assert.That(items[0].Checked!()).IsFalse();
        items[0].Invoke();
        await Assert.That(state.Snapshot.AutoWatch).IsFalse();
        items[1].Invoke();
        await Assert.That(state.TryBegin(DateTimeOffset.UnixEpoch)).IsEqualTo("compile");
        state.Complete(0);
    }

    [Test]
    public async Task manual_task_runs_off_the_menu_thread_and_can_be_cancelled()
    {
        using var fs = new MemoryFileSystem();
        fs.CreateDirectory("/project");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new TrayTaskService(fs, "/project", TrayTaskConfiguration.Parse(Config), (_, stop) =>
        {
            started.TrySetResult();
            return stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)) ? 130 : 1;
        }, line => { if (line.Contains("cancelled", StringComparison.Ordinal)) finished.TrySetResult(); }, CancellationToken.None);
        service.Start();
        service.Menus[0].Items[1].Invoke();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(service.Menus[0].Items[0].Checked!()).IsFalse();
        service.Menus[0].Items[3].Invoke();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        service.StopAndJoin();
    }
}
