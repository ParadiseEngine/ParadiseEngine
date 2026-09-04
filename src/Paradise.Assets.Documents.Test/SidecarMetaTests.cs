using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The sidecar: identity and import settings as OPEN domain tables. There is
/// no kind — what an asset is, is derived from its path, and what a domain's settings mean is the
/// owning build step's question, not the format's. There is no recorded hash: a checkout is not
/// an identity.
/// </summary>
public class SidecarMetaTests
{
    private const string AssetGuid = "3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041";

    [Test]
    public async Task a_minimal_sidecar_is_two_lines()
    {
        var meta = new SidecarMeta(Guid.Parse(AssetGuid));

        await Assert.That(meta.Write()).IsEqualTo(
            $"schema_version = 1\nguid = \"{AssetGuid}\"\n");
    }

    [Test]
    public async Task settings_write_under_their_domain_header()
    {
        var meta = new SidecarMeta(Guid.Parse(AssetGuid));
        meta.SetSetting("texture", new CanonicalTomlTable { { "preset", "normal" } });

        await Assert.That(meta.Write()).IsEqualTo(
            $"schema_version = 1\nguid = \"{AssetGuid}\"\n\n[texture]\npreset = \"normal\"\n");
    }

    [Test]
    public async Task a_sidecar_round_trips_byte_for_byte()
    {
        var text = $"schema_version = 1\nguid = \"{AssetGuid}\"\n\n[texture]\npreset = \"color-linear\"\n";

        var meta = SidecarMeta.Parse(text, "fire.png.meta");

        await Assert.That(meta.Setting("texture")!.Value("preset")).IsEqualTo("color-linear");
        await Assert.That(meta.Write()).IsEqualTo(text);
    }

    [Test]
    public async Task an_unrecognised_settings_domain_survives_a_round_trip()
    {
        // The format carries settings opaquely; which domains EXIST is the pipeline's registry,
        // and verify polices it there. A sidecar from a newer pipeline must not lose data here.
        var text = $"schema_version = 1\nguid = \"{AssetGuid}\"\n\n[mesh]\nlods = 3\n";

        var meta = SidecarMeta.Parse(text, "crate.glb.meta");

        await Assert.That(meta.Setting("mesh")!.Value("lods")).IsEqualTo(3L);
        await Assert.That(meta.Write()).IsEqualTo(text);
    }

    [Test]
    public async Task the_importer_is_written_beside_the_guid_and_read_back()
    {
        var meta = new SidecarMeta(Guid.Parse(AssetGuid)) { Importer = "mesh" };

        var text = meta.Write();
        var parsed = SidecarMeta.Parse(text, "x.meta");

        await Assert.That(text).Contains($"guid = \"{AssetGuid}\"\nimporter = \"mesh\"\n");
        await Assert.That(parsed.Importer).IsEqualTo("mesh");
        await Assert.That(parsed.Write()).IsEqualTo(text);
    }

    [Test]
    public async Task a_sidecar_from_before_the_field_reads_with_no_importer()
    {
        var parsed = SidecarMeta.Parse($"schema_version = 1\nguid = \"{AssetGuid}\"\n", "x.meta");

        await Assert.That(parsed.Importer).IsNull();
    }

    [Test]
    public async Task an_empty_importer_is_refused()
    {
        // Empty is neither "decide for me" (absent) nor a name; refusing it keeps the two apart.
        await Assert.That(() => SidecarMeta.Parse($"schema_version = 1\nguid = \"{AssetGuid}\"\nimporter = \"\"\n", "x.meta"))
            .Throws<SidecarMetaException>();
    }

    [Test]
    public async Task minting_yields_a_fresh_nonempty_guid()
    {
        var first = SidecarMeta.Mint();
        var second = SidecarMeta.Mint();

        await Assert.That(first.Guid).IsNotEqualTo(Guid.Empty);
        await Assert.That(first.Guid).IsNotEqualTo(second.Guid);
    }

    [Test]
    public async Task sidecar_paths_are_the_asset_path_plus_the_suffix()
    {
        var sidecar = SidecarMeta.PathFor("/game/assets/models/crate.glb");

        await Assert.That(sidecar).IsEqualTo(new UPath("/game/assets/models/crate.glb.meta"));
        await Assert.That(SidecarMeta.IsSidecarPath(sidecar)).IsTrue();
        await Assert.That(SidecarMeta.IsSidecarPath("/game/assets/models/crate.glb")).IsFalse();
        await Assert.That(SidecarMeta.AssetPathFor(sidecar)).IsEqualTo(new UPath("/game/assets/models/crate.glb"));
    }

    [Test]
    [Arguments("schema_version = 9\nguid = \"3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041\"\n", "schema_version = 9")]
    [Arguments("schema_version = 1\nguid = \"nope\"\n", "non-empty UUID")]
    [Arguments("schema_version = 1\nguid = \"3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041\"\nx = 1\n", "unknown key 'x'")]
    public async Task invalid_sidecars_name_the_offence(string toml, string fragment)
    {
        var error = await Assert.That(() => SidecarMeta.Parse(toml, "bad.meta"))
            .Throws<SidecarMetaException>();

        await Assert.That(error!.Message).Contains(fragment);
        await Assert.That(error.Message).Contains("bad.meta");
    }

    [Test]
    public async Task a_structural_key_cannot_name_a_settings_domain()
    {
        // 'guid' as a domain would write a duplicate root key; the refusal names the mistake
        // instead of failing inside the writer.
        var meta = new SidecarMeta(Guid.Parse(AssetGuid));

        var error = await Assert.That(() => meta.SetSetting("guid", new CanonicalTomlTable()))
            .Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("sidecar field");
    }

    [Test]
    public async Task a_recorded_hash_is_read_and_never_written()
    {
        var digest = new string('a', 64);
        var text = $"schema_version = 1\nguid = \"{AssetGuid}\"\nhash = \"{digest}\"\n";

        var meta = SidecarMeta.Parse(text, "fire.png.meta");

        await Assert.That(meta.Hash).IsEqualTo(digest);
        await Assert.That(meta.Write()).IsEqualTo($"schema_version = 1\nguid = \"{AssetGuid}\"\n");
    }

    [Test]
    public async Task save_and_load_go_through_the_filesystem()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets/models");
        var meta = SidecarMeta.Mint();
        var path = SidecarMeta.PathFor("/game/assets/models/crate.glb");

        meta.Save(fileSystem, path);
        var reread = SidecarMeta.Load(fileSystem, path);

        await Assert.That(reread.Guid).IsEqualTo(meta.Guid);
    }
}
