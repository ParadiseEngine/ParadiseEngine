using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Window layout persisted through a MOUNT instead of ImGui's own file IO.
///
/// ImGui cannot be pointed at an <c>IFileSystem</c> — replacing <c>ImFileOpen</c> is a
/// compile-time option in <c>imconfig.h</c> and this binding ships prebuilt natives — so layout
/// crosses as a string and the host decides where it lands. These tests mount memory, which means
/// the whole round trip happens with nothing on disk: no fixture, no temp directory, no file left
/// behind by a test that throws.</summary>
[NotInParallel]
public class ImGuiLayoutTests
{
    private const uint Width = 320;
    private const uint Height = 240;
    private static readonly UPath LayoutPath = "/layout/imgui.ini";

    /// <summary>Draws one window, recording where ImGui actually put it. <paramref name="request"/>
    /// is applied as FirstUseEver, so a restored layout takes precedence over it — which is the
    /// property under test.</summary>
    private sealed class PlacedWindow(Vector2 request)
    {
        public Vector2 Position { get; private set; }
        public Vector2 Size { get; private set; }

        public void Draw()
        {
            ImGuiApi.SetNextWindowPos(request, ImGuiCond.FirstUseEver);
            ImGuiApi.SetNextWindowSize(new Vector2(180, 120), ImGuiCond.FirstUseEver);
            ImGuiApi.Begin("persisted");
            Position = ImGuiApi.GetWindowPos();
            Size = ImGuiApi.GetWindowSize();
            ImGuiApi.End();
        }
    }

    private static void Tick(ImGuiUiCore core, double seconds)
    {
        core.Input.Tick(seconds);
        core.AcquireSnapshotForRender(new List<ImGuiTextureOp>(), out _);
    }

    [Test]
    public async Task layout_round_trips_through_a_memory_mount()
    {
        using var store = new MemoryFileSystem();

        // Session one: place the window somewhere deliberate, then persist.
        using (var first = new ImGuiUiCore(Width, Height))
        {
            var window = new PlacedWindow(new Vector2(200, 150));
            first.AddDraw(window.Draw);
            Tick(first, 0.0);
            ImGuiApi.SetNextWindowPos(new Vector2(200, 150), ImGuiCond.Always);
            Tick(first, 1.0 / 60.0);
            await Assert.That(window.Position).IsEqualTo(new Vector2(200, 150));

            first.SaveLayout(store, LayoutPath);
        }

        // It really went through the mount, and it really is ImGui's ini text.
        await Assert.That(store.FileExists(LayoutPath)).IsTrue();
        var written = Encoding.UTF8.GetString(store.ReadAllBytes(LayoutPath));
        await Assert.That(written).Contains("[Window][persisted]");
        await Assert.That(written).Contains("Pos=200,150");

        // Session two: a fresh context asks for somewhere else as FirstUseEver, and the restored
        // layout must win — which is also why a stray imgui.ini makes a suite order-dependent.
        using var second = new ImGuiUiCore(Width, Height);
        var restored = new PlacedWindow(new Vector2(10, 10));
        second.AddDraw(restored.Draw);
        await Assert.That(second.TryLoadLayout(store, LayoutPath)).IsTrue();
        Tick(second, 0.0);

        await Assert.That(restored.Position).IsEqualTo(new Vector2(200, 150));
    }

    [Test]
    public async Task a_first_run_has_nothing_to_load_and_says_so()
    {
        using var store = new MemoryFileSystem();
        using var core = new ImGuiUiCore(Width, Height);
        var window = new PlacedWindow(new Vector2(10, 10));
        core.AddDraw(window.Draw);

        // Absent layout is a first run, not an error, and the window falls back to what the
        // caller asked for.
        await Assert.That(core.TryLoadLayout(store, LayoutPath)).IsFalse();
        Tick(core, 0.0);
        await Assert.That(window.Position).IsEqualTo(new Vector2(10, 10));
    }

    /// <summary>Saving creates the directory it was pointed at: a host naming a path inside its
    /// mount should not have to prepare the tree first.</summary>
    [Test]
    public async Task saving_into_a_directory_that_does_not_exist_yet_creates_it()
    {
        using var store = new MemoryFileSystem();
        using var core = new ImGuiUiCore(Width, Height);
        core.AddDraw(new PlacedWindow(new Vector2(10, 10)).Draw);
        Tick(core, 0.0);

        await Assert.That(store.DirectoryExists("/layout")).IsFalse();
        core.SaveLayout(store, LayoutPath);
        await Assert.That(store.FileExists(LayoutPath)).IsTrue();
    }

    /// <summary>Persisting through a mount takes ImGui's own file IO out of the picture, so a
    /// host cannot end up writing both.</summary>
    [Test]
    public async Task persisting_through_a_mount_disables_imgui_s_own_file()
    {
        using var store = new MemoryFileSystem();
        using var core = new ImGuiUiCore(Width, Height);
        core.AddDraw(new PlacedWindow(new Vector2(10, 10)).Draw);

        await Assert.That(IniFilename()).IsEqualTo("imgui.ini"); // ImGui's default, untouched
        core.SaveLayout(store, LayoutPath);
        await Assert.That(IniFilename()).IsNull();
    }

    private static unsafe string? IniFilename() =>
        System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ImGuiApi.GetIO().IniFilename);
}
