using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// GOLDEN TEST. The Godot ProjectSettings export uses a permissive collision matrix (Godot has no
// global layer-vs-layer matrix) and default render settings; for a default project this matched
// the real Unity export byte-for-byte. Fixture: originally a verbatim copy of
// ~/proj/ParadiseUnityEditor/data/ProjectSettings.json, extended 2026-07-15 with the
// Physics.Dynamics section (global solver tuning) — a Godot-era additive contract extension the
// Unity tool never emitted; its default values reproduce the previously hardcoded solver
// behavior, so a document without the section reads identically.
public class ProjectSettingsGoldenTests
{
    [Test]
    public async Task serialized_project_settings_match_unity_baseline()
    {
        var settings = new ProjectSettingsData();
        // Permissive: every layer collides with every layer (-1 = all bits) — matches Unity's
        // default matrix. Render settings defaults (RenderScale 1, MSAA off, anisotropic 16,
        // specular-AA 0.5/0.25) already equal the baseline.
        settings.Physics.CollisionMatrix.LayerMasks = Enumerable.Repeat(-1, 32).ToList();

        string actual = Normalize(ExportJsonWriter.SerializeToString(settings));
        string expected = Normalize(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProjectSettings.expected.json")));

        await Assert.That(actual).IsEqualTo(expected);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
