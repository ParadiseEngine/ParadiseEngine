using System.Numerics;
using System.Text;

using DotRecast.Core;
using DotRecast.Detour.Io;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Paradise.Export.NavMesh;

using TUnit.Assertions.Enums;

namespace Paradise.Assets.Pipeline.Test;

public class NavMeshImporterTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task baked_navmesh_copies_through_with_its_manifest_identity()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var bytes = MeshBytes();
        var guid = AddNavMesh(fileSystem, bytes);
        var runner = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder());

        await Assert.That(runner.Run().Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllBytes("/game/build/levels/arena.navmesh.bin"))
            .IsEquivalentTo(bytes, CollectionOrdering.Matching);
        var manifest = BuildManifest.Load(fileSystem, "/game/build/manifest.json");
        var built = manifest.FindByGuid(DocumentGuid.Format(guid));
        await Assert.That(built).IsNotNull();
        await Assert.That(built!.Path).IsEqualTo("levels/arena.navmesh.bin");
        await Assert.That(built.Source).IsEqualTo("levels/arena.navmesh.bin");
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/levels/arena.navmesh.bin.meta").Importer)
            .IsEqualTo("navmesh");

        await Assert.That(runner.Run().Errors).IsEmpty();
        using var output = fileSystem.OpenFile("/game/build/levels/arena.navmesh.bin", FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(output);
        var mesh = new DtMeshSetReader().Read(reader);
        await Assert.That(mesh.GetTile(0).data.header.polyCount).IsEqualTo(2);
    }

    [Test]
    public async Task prefab_reference_bakes_to_the_navigation_output_path()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = AddNavMesh(fileSystem, MeshBytes());
        var scene = new PrefabDocument();
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "arena");
        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "Game.SceneNavigation", new CanonicalTomlTable
        {
            { "NavMeshFile", AssetReferenceCodec.Write(new AssetReference(guid, "levels/arena.navmesh.bin")) },
        }));
        scene.Objects.Add(root);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/arena.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/arena.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/build/levels/arena.toml"))
            .Contains("NavMeshFile = \"levels/arena.navmesh.bin\"");
        await Assert.That(fileSystem.FileExists("/game/build/levels/arena.navmesh.bin")).IsTrue();
    }

    [Test]
    [Arguments("arena.navmesh.bin", true)]
    [Arguments("ARENA.NAVMESH.BIN", true)]
    [Arguments("arena.bin", false)]
    [Arguments("arena.navmesh", false)]
    [Arguments("arena.navmesh.bin.tmp", false)]
    public async Task claims_only_the_navigation_binary_suffix(string name, bool expected)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var candidate = new ImportCandidate(fileSystem, s_layout, "/game/assets/" + name, null);

        await Assert.That(new NavMeshImporter().Claims(candidate)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(8)]
    [Arguments(64)]
    public async Task truncated_mesh_is_a_named_build_error_and_writes_no_output(int length)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        AddNavMesh(fileSystem, MeshBytes().AsSpan(0, length).ToArray());

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("levels/arena.navmesh.bin: invalid baked Detour navmesh");
        await Assert.That(fileSystem.FileExists("/game/build/levels/arena.navmesh.bin")).IsFalse();
    }

    [Test]
    public async Task a_recorded_navigation_importer_still_refuses_an_unrelated_binary()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/other.bin", MeshBytes());
        var meta = SidecarMeta.Mint();
        meta.Importer = "navmesh";
        meta.Save(fileSystem, "/game/assets/other.bin.meta");

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("other.bin");
        await Assert.That(fileSystem.FileExists("/game/build/other.bin")).IsFalse();
    }

    private static Guid AddNavMesh(MemoryFileSystem fileSystem, byte[] bytes)
    {
        fileSystem.CreateDirectory("/game/assets/levels");
        fileSystem.WriteAllBytes("/game/assets/levels/arena.navmesh.bin", bytes);
        return ProjectVerifierTests.Mint(fileSystem, "/game/assets/levels/arena.navmesh.bin");
    }

    private static byte[] MeshBytes()
    {
        var mesh = NavMeshBinaryWriter.BuildNavMesh(
            [Vector3.Zero, new Vector3(0f, 0f, 2f), new Vector3(2f, 0f, 2f), new Vector3(2f, 0f, 0f)],
            [0, 1, 2, 0, 2, 3]);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            new DtMeshSetWriter().Write(writer, mesh, RcByteOrder.LITTLE_ENDIAN, false);
        }

        return stream.ToArray();
    }
}
