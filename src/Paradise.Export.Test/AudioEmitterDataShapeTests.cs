using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins the serialized shape of the AudioEmitter entity component, its read round-trip, and the
// normalization rule the exporter applies. The shape is a cross-repo contract: the Blender addon
// writes this JSON in pure Python from its own dataclass, so a rename here that is not mirrored
// there produces a document that reads back with a null component and an emitter that never
// makes a sound — silently, which is why it is worth pinning.
public class AudioEmitterDataShapeTests
{
    [Test]
    public async Task audio_emitter_serializes_and_round_trips()
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "ArcadeCabinet",
            Components = new EntityComponentsData
            {
                AudioEmitter = new AudioEmitterComponentData
                {
                    StartEvent = "Play_Arcade_Bed",
                    StopEvent = "Stop_Arcade_Bed",
                    PlayOnStart = true,
                    Is3D = true,
                    AttenuationScale = 2.5f,
                },
            },
        });

        string json = ExportJsonWriter.SerializeToString(level);
        JsonNode audio = JsonNode.Parse(json)!["Entities"]![0]!["Components"]!["AudioEmitter"]!;
        await Assert.That((string?)audio["StartEvent"]).IsEqualTo("Play_Arcade_Bed");
        await Assert.That((string?)audio["StopEvent"]).IsEqualTo("Stop_Arcade_Bed");
        await Assert.That((bool)audio["PlayOnStart"]!).IsTrue();
        await Assert.That((bool)audio["Is3D"]!).IsTrue();
        await Assert.That((float)audio["AttenuationScale"]!).IsEqualTo(2.5f);

        AudioEmitterComponentData round =
            ExportJsonReader.ReadLevel(json).Entities[0].Components.AudioEmitter!;
        await Assert.That(round.StartEvent).IsEqualTo("Play_Arcade_Bed");
        await Assert.That(round.StopEvent).IsEqualTo("Stop_Arcade_Bed");
        await Assert.That(round.AttenuationScale).IsEqualTo(2.5f);
    }

    [Test]
    public async Task audio_emitter_is_absent_from_older_documents()
    {
        // Every scene exported before audio existed must still read: the component is an
        // addition, not a schema break.
        LevelData read = ExportJsonReader.ReadLevel("""{"SchemaVersion":2,"Entities":[{"Id":"E"}]}""");
        await Assert.That(read.Entities[0].Components.AudioEmitter).IsNull();
    }

    [Test]
    public async Task audio_emitter_2d_music_round_trips_without_a_stop_event()
    {
        // The music case: 2D, no stop event (the runtime stops it by playing id instead).
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "Music",
            Components = new EntityComponentsData
            {
                AudioEmitter = new AudioEmitterComponentData
                {
                    StartEvent = "Play_CityPop",
                    PlayOnStart = false,
                    Is3D = false,
                },
            },
        });

        AudioEmitterComponentData round = ExportJsonReader
            .ReadLevel(ExportJsonWriter.SerializeToString(level))
            .Entities[0].Components.AudioEmitter!;
        await Assert.That(round.StopEvent).IsNull();
        await Assert.That(round.PlayOnStart).IsFalse();
        await Assert.That(round.Is3D).IsFalse();
    }

    [Test]
    public async Task audio_emitter_normalization_repairs_attenuation_scale()
    {
        // Zero or negative would collapse the authored falloff — the emitter would be either
        // silent everywhere or audible everywhere, both of which read as a broken sound rather
        // than a bad number.
        var zero = new AudioEmitterComponentData { AttenuationScale = 0f };
        zero.ValidateAndNormalize();
        await Assert.That(zero.AttenuationScale).IsEqualTo(1f);

        var negative = new AudioEmitterComponentData { AttenuationScale = -3f };
        negative.ValidateAndNormalize();
        await Assert.That(negative.AttenuationScale).IsEqualTo(1f);

        var authored = new AudioEmitterComponentData { AttenuationScale = 4f };
        authored.ValidateAndNormalize();
        await Assert.That(authored.AttenuationScale).IsEqualTo(4f);
    }
}
