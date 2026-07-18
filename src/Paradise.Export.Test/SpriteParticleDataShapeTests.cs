using System.Numerics;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins the serialized shape of the SpriteAnimation and ParticleEmitter entity components
// (schema additions for 2D flipbook animation, 2D sprite particles, and 3D voxel particles),
// their read round-trip, and the normalization rules the exporter applies before writing.
public class SpriteParticleDataShapeTests
{
    [Test]
    public async Task sprite_animation_serializes_and_round_trips()
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "Torch",
            Components = new EntityComponentsData
            {
                SpriteAnimation = new SpriteAnimationComponentData
                {
                    Sheet = "sprites/torch.ktx2",
                    Columns = 4,
                    Rows = 2,
                    FrameCount = 7,
                    Fps = 12f,
                    Loop = true,
                    QuadSize = new Vector2(0.5f, 1f),
                    Billboard = true,
                },
            },
        });

        string json = ExportJsonWriter.SerializeToString(level);
        JsonNode node = JsonNode.Parse(json)!;
        JsonNode sprite = node["Entities"]![0]!["Components"]!["SpriteAnimation"]!;
        await Assert.That((string?)sprite["Sheet"]).IsEqualTo("sprites/torch.ktx2");
        await Assert.That((int)sprite["Columns"]!).IsEqualTo(4);
        await Assert.That((int)sprite["Rows"]!).IsEqualTo(2);
        await Assert.That((int)sprite["FrameCount"]!).IsEqualTo(7);
        await Assert.That((float)sprite["Fps"]!).IsEqualTo(12f);
        await Assert.That((bool)sprite["Loop"]!).IsTrue();

        LevelData read = ExportJsonReader.ReadLevel(json);
        SpriteAnimationComponentData round = read.Entities[0].Components.SpriteAnimation!;
        await Assert.That(round.Sheet).IsEqualTo("sprites/torch.ktx2");
        await Assert.That(round.QuadSize).IsEqualTo(new Vector2(0.5f, 1f));
        await Assert.That(round.Billboard).IsTrue();
    }

    [Test]
    public async Task particle_emitter_serializes_kind_by_name_and_round_trips()
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "Dust",
            Components = new EntityComponentsData
            {
                ParticleEmitter = new ParticleEmitterComponentData
                {
                    Kind = ParticleRenderKind.Voxel,
                    MaxParticles = 32,
                    EmitRate = 20f,
                    LifetimeSeconds = 0.8f,
                    InitialSpeed = 3f,
                    SpreadDegrees = 40f,
                    Gravity = -4f,
                    Drag = 0.5f,
                    StartSize = 0.1f,
                    EndSize = 0.02f,
                    Seed = 99,
                    Color = Color32.FromRgba(1f, 0.5f, 0f),
                },
            },
        });

        string json = ExportJsonWriter.SerializeToString(level);
        JsonNode emitter = JsonNode.Parse(json)!["Entities"]![0]!["Components"]!["ParticleEmitter"]!;
        // Enum-by-name is the contract convention (a bare int would silently pass STJ defaults).
        await Assert.That((string?)emitter["Kind"]).IsEqualTo("Voxel");
        await Assert.That((int)emitter["MaxParticles"]!).IsEqualTo(32);
        await Assert.That((float)emitter["Gravity"]!).IsEqualTo(-4f);

        ParticleEmitterComponentData round =
            ExportJsonReader.ReadLevel(json).Entities[0].Components.ParticleEmitter!;
        await Assert.That(round.Kind).IsEqualTo(ParticleRenderKind.Voxel);
        await Assert.That(round.Seed).IsEqualTo(99u);
        await Assert.That(round.Drag).IsEqualTo(0.5f);
    }

    [Test]
    public async Task absent_components_stay_null_for_older_documents()
    {
        // A pre-existing (schema v2) document without the new component keys must read as null
        // components — the additions are backward-compatible.
        LevelData read = ExportJsonReader.ReadLevel("""{"SchemaVersion":2,"Entities":[{"Id":"E"}]}""");
        await Assert.That(read.Entities[0].Components.SpriteAnimation).IsNull();
        await Assert.That(read.Entities[0].Components.ParticleEmitter).IsNull();
    }

    [Test]
    public async Task sprite_normalization_derives_frame_count_and_clamps()
    {
        var sprite = new SpriteAnimationComponentData { Columns = 4, Rows = 2, FrameCount = 0, Fps = -1f };
        sprite.ValidateAndNormalize();
        await Assert.That(sprite.FrameCount).IsEqualTo(8); // full grid when unset
        await Assert.That(sprite.Fps).IsEqualTo(10f);      // invalid fps → default

        var overflow = new SpriteAnimationComponentData { Columns = 2, Rows = 2, FrameCount = 9 };
        overflow.ValidateAndNormalize();
        await Assert.That(overflow.FrameCount).IsEqualTo(4); // never beyond the grid
    }

    [Test]
    public async Task emitter_normalization_clamps_capacity_and_seed()
    {
        var emitter = new ParticleEmitterComponentData { MaxParticles = 4096, Seed = 0, EndSize = -1f, StartSize = 0.3f };
        emitter.ValidateAndNormalize();
        await Assert.That(emitter.MaxParticles).IsEqualTo(64); // runtime inline-pool cap
        await Assert.That(emitter.Seed).IsEqualTo(1u);         // xorshift must not seed 0
        await Assert.That(emitter.EndSize).IsEqualTo(0.3f);    // invalid end → start (no growth)
    }
}
