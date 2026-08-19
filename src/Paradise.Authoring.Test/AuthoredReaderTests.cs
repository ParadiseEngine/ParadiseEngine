using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

/// <summary>
/// The generated readers, actually executed: each test compiles a tiny game assembly with the
/// generator attached, loads it, and runs the registry against real payloads shaped exactly as
/// the Godot addon writes them (AuthoredEntityCore.ValueOf) — enums as member-name strings,
/// vectors as float arrays, colors as the {r,g,b,a} object, composed groups as nested objects.
/// Asserting on the generated TEXT would only prove the code looks right; these prove it
/// reads right.
/// </summary>
public class AuthoredReaderTests
{
    private static (object? Registry, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Collections").Location),
            MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Numerics.Vector3).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Paradise.Export.Data.Color32).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "Game",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoredRegistryGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var updated, out _);
        var generatorDiagnostics = driver.GetRunResult().Diagnostics;

        if (generatorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, generatorDiagnostics);
        }

        using var stream = new MemoryStream();
        var emit = updated.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "generated code does not compile:\n" + string.Join(
                    "\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var registry = assembly.GetType("Game.AuthoredComponents")
            ?.GetField("Default", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        return (registry, generatorDiagnostics);
    }

    private static object ReadComponent(object registry, string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var typed = (IAuthoredComponentRegistry)registry;
        var found = typed.TryRead(id, document.RootElement.Clone(), out var component);
        if (!found || component is null)
        {
            throw new InvalidOperationException($"registry did not read '{id}'");
        }
        return component;
    }

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target);

    // ----------------------------------------------------------------------------------------

    private const string PrimitivesSource = """
        using Paradise.Authoring;

        [assembly: AuthoredRegistry]

        namespace Game;

        [Authored("game.thing")]
        public sealed record Thing
        {
            public float Speed { get; set; } = 2.5f;
            public int Count { get; set; } = 7;
            public bool Armed { get; set; } = true;
            public string Label { get; set; } = "untouched";
        }
        """;

    /// <summary>The registration ceremony is gone: [assembly: AuthoredRegistry], no context,
    /// no [JsonSerializable] anywhere, and the payload materializes.</summary>
    [Test]
    public async Task primitives_read_and_absent_properties_keep_their_initializers()
    {
        var (registry, diagnostics) = Run(PrimitivesSource);

        await Assert.That(diagnostics).IsEmpty();
        var thing = ReadComponent(registry!, "game.thing", """{"Speed": 9.5, "Armed": false}""");

        await Assert.That((float)Prop(thing, "Speed")!).IsEqualTo(9.5f);
        await Assert.That((bool)Prop(thing, "Armed")!).IsFalse();
        // Absent from the payload: the record's own initializers stand.
        await Assert.That((int)Prop(thing, "Count")!).IsEqualTo(7);
        await Assert.That((string)Prop(thing, "Label")!).IsEqualTo("untouched");
    }

    /// <summary>The contexts this replaces ran PropertyNameCaseInsensitive; hand-edited exports
    /// relied on it.</summary>
    [Test]
    public async Task property_names_match_case_insensitively()
    {
        var (registry, _) = Run(PrimitivesSource);
        var thing = ReadComponent(registry!, "game.thing", """{"speed": 1.5, "COUNT": 3}""");

        await Assert.That((float)Prop(thing, "Speed")!).IsEqualTo(1.5f);
        await Assert.That((int)Prop(thing, "Count")!).IsEqualTo(3);
    }

    /// <summary>The addon writes null for a string with no value; the property goes null exactly
    /// as it did through the context.</summary>
    [Test]
    public async Task a_null_string_assigns_null()
    {
        var (registry, _) = Run(PrimitivesSource);
        var thing = ReadComponent(registry!, "game.thing", """{"Label": null}""");

        await Assert.That((string?)Prop(thing, "Label")).IsNull();
    }

    /// <summary>The full wire vocabulary in one record, shaped exactly as the addon writes it:
    /// enum as its member name, vectors and quaternions as float arrays, color as {r,g,b,a},
    /// composed groups as nested objects, lists as arrays of values.</summary>
    [Test]
    public async Task the_whole_vocabulary_reads_from_the_addon_wire_format()
    {
        var (registry, diagnostics) = Run("""
            using System.Collections.Generic;
            using System.Numerics;
            using Paradise.Authoring;
            using Paradise.Export.Data;

            [assembly: AuthoredRegistry]

            namespace Game;

            public enum Mode { Idle = 0, Chase = 1, Flee = 2 }

            public sealed record Part
            {
                public float Weight { get; set; } = 1f;
                public string Tag { get; set; } = "";
            }

            [Authored("game.rich")]
            public sealed record Rich
            {
                public Mode Mode { get; set; } = Mode.Idle;
                public Vector2 Spot { get; set; }
                public Vector3 Home { get; set; }
                public Quaternion Facing { get; set; } = Quaternion.Identity;
                public Vector4 Tint { get; set; }
                public Color32 Paint { get; set; }
                public Part Body { get; set; } = new();
                public List<float> Offsets { get; set; } = new();
                public string[] Names { get; set; } = [];
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        var rich = ReadComponent(registry!, "game.rich", """
            {
                "Mode": "Flee",
                "Spot": [1.5, 2.5],
                "Home": [1, 2, 3],
                "Facing": [0, 0.7071068, 0, 0.7071068],
                "Tint": {"r": 0.25, "g": 0.5, "b": 0.75, "a": 1},
                "Paint": {"r": 1, "g": 0, "b": 0, "a": 1},
                "Body": {"Weight": 12.5, "Tag": "fin"},
                "Offsets": [0.1, 0.2, 0.3],
                "Names": ["a", "b"]
            }
            """);

        await Assert.That(Prop(rich, "Mode")!.ToString()).IsEqualTo("Flee");
        await Assert.That((System.Numerics.Vector2)Prop(rich, "Spot")!)
            .IsEqualTo(new System.Numerics.Vector2(1.5f, 2.5f));
        await Assert.That((System.Numerics.Vector3)Prop(rich, "Home")!)
            .IsEqualTo(new System.Numerics.Vector3(1f, 2f, 3f));
        await Assert.That(((System.Numerics.Quaternion)Prop(rich, "Facing")!).Y)
            .IsEqualTo(0.7071068f);
        await Assert.That((System.Numerics.Vector4)Prop(rich, "Tint")!)
            .IsEqualTo(new System.Numerics.Vector4(0.25f, 0.5f, 0.75f, 1f));
        var paint = (Paradise.Export.Data.Color32)Prop(rich, "Paint")!;
        await Assert.That(paint.R).IsEqualTo(1f);

        var body = Prop(rich, "Body")!;
        await Assert.That((float)Prop(body, "Weight")!).IsEqualTo(12.5f);
        await Assert.That((string)Prop(body, "Tag")!).IsEqualTo("fin");

        await Assert.That((List<float>)Prop(rich, "Offsets")!).IsEquivalentTo([0.1f, 0.2f, 0.3f]);
        await Assert.That((string[])Prop(rich, "Names")!).IsEquivalentTo(["a", "b"]);
    }

    private const string EnumSource = """
        using Paradise.Authoring;

        [assembly: AuthoredRegistry]

        namespace Game;

        public enum Mode { Idle = 0, Chase = 1, Flee = 2 }

        [Authored("game.moody")]
        public sealed record Moody
        {
            public Mode Mode { get; set; } = Mode.Idle;
        }
        """;

    /// <summary>The addon stores enums as member-name strings (AuthoredEntityCore.ValueOf) and
    /// the typed contract writes them through JsonStringEnumConverter, so the name is the wire
    /// form — parsed case-insensitively, as hand-edited exports rely on for property names too.
    /// The underlying integer is still accepted for tolerance.</summary>
    [Test]
    public async Task enums_read_by_member_name_case_insensitively_and_by_integer()
    {
        var (registry, diagnostics) = Run(EnumSource);

        await Assert.That(diagnostics).IsEmpty();
        var named = ReadComponent(registry!, "game.moody", """{"Mode": "Flee"}""");
        await Assert.That(Prop(named, "Mode")!.ToString()).IsEqualTo("Flee");

        var lowered = ReadComponent(registry!, "game.moody", """{"Mode": "chase"}""");
        await Assert.That(Prop(lowered, "Mode")!.ToString()).IsEqualTo("Chase");

        var numeric = ReadComponent(registry!, "game.moody", """{"Mode": 2}""");
        await Assert.That(Prop(numeric, "Mode")!.ToString()).IsEqualTo("Flee");
    }

    /// <summary>A positional record cannot be constructed then assigned; the error names it and
    /// the registry leaves it out rather than emitting code that cannot compile.</summary>
    [Test]
    public async Task a_positional_record_is_reported_not_guessed_at()
    {
        var (registry, diagnostics) = Run("""
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Authored("game.pos")]
            public sealed record Positional(float Speed);
            """);

        await Assert.That(registry).IsNull();
        await Assert.That(diagnostics.Select(d => d.Id).Distinct())
            .Contains("PAUT002");
    }

    /// <summary>Init-only and required members each get their own reason.</summary>
    [Test]
    public async Task init_only_and_required_properties_are_reported()
    {
        var (_, diagnostics) = Run("""
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Authored("game.frozen")]
            public sealed record Frozen
            {
                public float Locked { get; init; } = 1f;
                public required float Must { get; set; }
            }
            """);

        var messages = diagnostics
            .Where(d => d.Id == "PAUT003")
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        await Assert.That(messages.Any(m => m.Contains("init-only"))).IsTrue();
        await Assert.That(messages.Any(m => m.Contains("required"))).IsTrue();
    }

    /// <summary>An explicit JSON null is ABSENT for everything except a string: initializers
    /// stand, nothing throws. The addon only writes null for valueless strings today; the reader
    /// must not turn a future writer change into a crash.</summary>
    [Test]
    public async Task explicit_nulls_keep_the_initializers_instead_of_throwing()
    {
        var (registry, _) = Run("""
            using System.Collections.Generic;
            using System.Numerics;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            public sealed record Part { public float Weight { get; set; } = 3f; }

            [Authored("game.nully")]
            public sealed record Nully
            {
                public float Speed { get; set; } = 2.5f;
                public Vector3 Home { get; set; } = new(1f, 2f, 3f);
                public Part Body { get; set; } = new() { Weight = 9f };
                public List<float> Offsets { get; set; } = [4f];
                public string Label { get; set; } = "kept-unless-nulled";
            }
            """);
        var nully = ReadComponent(registry!, "game.nully", """
            {"Speed": null, "Home": null, "Body": null, "Offsets": null, "Label": null}
            """);

        await Assert.That((float)Prop(nully, "Speed")!).IsEqualTo(2.5f);
        await Assert.That((System.Numerics.Vector3)Prop(nully, "Home")!)
            .IsEqualTo(new System.Numerics.Vector3(1f, 2f, 3f));
        await Assert.That((float)Prop(Prop(nully, "Body")!, "Weight")!).IsEqualTo(9f);
        await Assert.That((List<float>)Prop(nully, "Offsets")!).IsEquivalentTo([4f]);
        // The one exception: a string null passes through, as it did with the contexts.
        await Assert.That((string?)Prop(nully, "Label")).IsNull();
    }

    /// <summary>A composed field's type without a parameterless constructor gets its own message
    /// naming both the composed type and the container — the container is where the squiggle can
    /// sit, and it is generally NOT itself the broken type.</summary>
    [Test]
    public async Task a_composed_type_without_a_ctor_names_both_types()
    {
        var (registry, diagnostics) = Run("""
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            public sealed record Piece(float Weight);

            [Authored("game.holder")]
            public sealed record Holder
            {
                public Piece Body { get; set; } = new(1f);
            }
            """);

        await Assert.That(registry).IsNull();
        var composed = diagnostics.Single(d => d.Id == "PAUT004");
        var message = composed.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("'Piece'");
        await Assert.That(message).Contains("'Holder'");
    }

    /// <summary>No opt-in, no registry: an assembly that only publishes a schema for editors
    /// must not grow public loader surface. Paradise.Export itself is such an assembly.</summary>
    [Test]
    public async Task without_the_opt_in_no_registry_is_emitted()
    {
        var (registry, diagnostics) = Run("""
            using Paradise.Authoring;

            namespace Game;

            [Authored("game.thing")]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(registry).IsNull();
        await Assert.That(diagnostics).IsEmpty();
    }

}
