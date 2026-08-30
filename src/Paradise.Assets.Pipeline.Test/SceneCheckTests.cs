using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class SceneCheckTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    /// <summary>
    /// Valid but not canonical: spacing a machine write never produces.
    /// </summary>
    /// <remarks>
    /// Note what is NOT usable here any more — reordering the payload keys. A component's fields
    /// sit flat and their order comes from the document, because payload order is data the writer
    /// must preserve. So <c>Name</c> before <c>Guid</c> round-trips byte for byte and is
    /// perfectly canonical; only the things the writer actually normalizes (spacing, blank lines
    /// before headers, float spelling) can be non-canonical.
    /// </remarks>
    private static readonly string NonCanonical =
        "schema_version=1\n\n[[objects]]\n\n[[objects.components]]\n" +
        $"id = \"{DocumentGuid.Format(WellKnownComponents.MetaId)}\"\n" +
        "type = \"meta\"\n" +
        "Guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\n" +
        "Name = \"crate\"\n";

    [Test]
    public async Task canonical_documents_pass()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalScene(fileSystem, "/game/assets/scenes/a.scene");

        var results = SceneCheck.Run(fileSystem, s_layout);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Outcome).IsEqualTo(SceneCheckOutcome.Canonical);
    }

    [Test]
    public async Task a_hand_edited_document_is_reported_without_fix()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/scenes");
        fileSystem.WriteAllText("/game/assets/scenes/edited.scene", NonCanonical);

        var results = SceneCheck.Run(fileSystem, s_layout);

        await Assert.That(results[0].Outcome).IsEqualTo(SceneCheckOutcome.NotCanonical);
        // Reported only — check never mutates without --fix.
        await Assert.That(fileSystem.ReadAllText("/game/assets/scenes/edited.scene")).IsEqualTo(NonCanonical);
    }

    [Test]
    public async Task fix_rewrites_into_canonical_form_and_the_recheck_passes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/scenes");
        fileSystem.WriteAllText("/game/assets/scenes/edited.scene", NonCanonical);

        var fixResults = SceneCheck.Run(fileSystem, s_layout, fix: true);
        var recheck = SceneCheck.Run(fileSystem, s_layout);

        await Assert.That(fixResults[0].Outcome).IsEqualTo(SceneCheckOutcome.Rewritten);
        await Assert.That(recheck[0].Outcome).IsEqualTo(SceneCheckOutcome.Canonical);
        // Canonical order is MODEL order, and the model puts Guid before Name.
        await Assert.That(fileSystem.ReadAllText("/game/assets/scenes/edited.scene"))
            .Contains("Guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\nName = \"crate\"\n");
    }

    [Test]
    public async Task an_invalid_document_is_reported_and_never_touched_by_fix()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/scenes");
        fileSystem.WriteAllText("/game/assets/scenes/broken.scene", "schema_version = 7\n");

        var results = SceneCheck.Run(fileSystem, s_layout, fix: true);

        await Assert.That(results[0].Outcome).IsEqualTo(SceneCheckOutcome.Invalid);
        await Assert.That(results[0].Message).Contains("schema_version");
        await Assert.That(fileSystem.ReadAllText("/game/assets/scenes/broken.scene")).IsEqualTo("schema_version = 7\n");
    }

    [Test]
    public async Task results_cover_every_scene_document_in_path_order()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalScene(fileSystem, "/game/assets/scenes/b.scene");
        ProjectVerifierTests.WriteCanonicalScene(fileSystem, "/game/assets/scenes/a.scene");

        var results = SceneCheck.Run(fileSystem, s_layout);

        await Assert.That(results.Select(result => result.Path.GetName()).ToArray())
            .IsEquivalentTo(new[] { "a.scene", "b.scene" });
    }
}
