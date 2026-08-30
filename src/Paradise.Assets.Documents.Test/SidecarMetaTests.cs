using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Documents.Test;

public class SidecarMetaTests
{
    private const string AssetGuid = "3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041";

    [Test]
    public async Task a_minimal_sidecar_is_three_lines()
    {
        var meta = new SidecarMeta(Guid.Parse(AssetGuid), SidecarAssetKind.Mesh);

        await Assert.That(meta.Write()).IsEqualTo(
            $"schema_version = 1\nguid = \"{AssetGuid}\"\nkind = \"mesh\"\n");
    }

    [Test]
    public async Task texture_settings_write_under_their_own_header()
    {
        var meta = new SidecarMeta(Guid.Parse(AssetGuid), SidecarAssetKind.Texture)
        {
            Texture = new TextureImportSettings { Preset = TexturePreset.Normal },
        };

        await Assert.That(meta.Write()).IsEqualTo(
            $"schema_version = 1\nguid = \"{AssetGuid}\"\nkind = \"texture\"\n\n[texture]\npreset = \"normal\"\n");
    }

    [Test]
    public async Task a_sidecar_round_trips_byte_for_byte()
    {
        var text = $"schema_version = 1\nguid = \"{AssetGuid}\"\nkind = \"texture\"\n\n[texture]\npreset = \"color-linear\"\n";

        var meta = SidecarMeta.Parse(text, "fire.png.meta");

        await Assert.That(meta.Kind).IsEqualTo(SidecarAssetKind.Texture);
        await Assert.That(meta.Texture!.Preset).IsEqualTo(TexturePreset.ColorLinear);
        await Assert.That(meta.Write()).IsEqualTo(text);
    }

    [Test]
    public async Task minting_yields_a_fresh_nonempty_guid()
    {
        var first = SidecarMeta.Mint(SidecarAssetKind.Audio);
        var second = SidecarMeta.Mint(SidecarAssetKind.Audio);

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
    [Arguments("schema_version = 9\nguid = \"3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041\"\nkind = \"mesh\"\n", "schema_version = 9")]
    [Arguments("schema_version = 1\nguid = \"nope\"\nkind = \"mesh\"\n", "non-empty UUID")]
    [Arguments("schema_version = 1\nguid = \"3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041\"\nkind = \"blob\"\n", "kind = \"blob\"")]
    [Arguments("schema_version = 1\nguid = \"3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041\"\nkind = \"mesh\"\nx = 1\n", "unknown key 'x'")]
    public async Task invalid_sidecars_name_the_offence(string toml, string fragment)
    {
        var error = await Assert.That(() => SidecarMeta.Parse(toml, "bad.meta"))
            .Throws<SidecarMetaException>();

        await Assert.That(error!.Message).Contains(fragment);
        await Assert.That(error.Message).Contains("bad.meta");
    }

    [Test]
    public async Task texture_settings_on_a_non_texture_are_rejected()
    {
        var toml = $"schema_version = 1\nguid = \"{AssetGuid}\"\nkind = \"mesh\"\n\n[texture]\npreset = \"color\"\n";

        var error = await Assert.That(() => SidecarMeta.Parse(toml, "x"))
            .Throws<SidecarMetaException>();

        await Assert.That(error!.Message).Contains("must match the asset's kind");
    }

    [Test]
    public async Task an_unknown_preset_is_rejected()
    {
        var toml = $"schema_version = 1\nguid = \"{AssetGuid}\"\nkind = \"texture\"\n\n[texture]\npreset = \"shiny\"\n";

        await Assert.That(() => SidecarMeta.Parse(toml, "x")).Throws<SidecarMetaException>();
    }

    [Test]
    public async Task save_and_load_go_through_the_filesystem()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets/models");
        var meta = SidecarMeta.Mint(SidecarAssetKind.Mesh);
        var path = SidecarMeta.PathFor("/game/assets/models/crate.glb");

        meta.Save(fileSystem, path);
        var reread = SidecarMeta.Load(fileSystem, path);

        await Assert.That(reread.Guid).IsEqualTo(meta.Guid);
        await Assert.That(reread.Kind).IsEqualTo(SidecarAssetKind.Mesh);
    }
}
