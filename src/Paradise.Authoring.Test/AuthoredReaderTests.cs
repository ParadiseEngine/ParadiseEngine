using System.Collections.Immutable;
using System.Numerics;
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
    // One id per fixture record below. Declared once and interpolated into both the source and the
    // lookup, because a GUID typed out twice is a GUID that eventually differs in one character and
    // fails as "registry did not read it" rather than as the typo it is.
    private const string ThingId = "d0000000-0000-4000-8000-000000000001";
    private const string RichId = "d0000000-0000-4000-8000-000000000002";
    private const string MoodyId = "d0000000-0000-4000-8000-000000000003";
    private const string PositionalId = "d0000000-0000-4000-8000-000000000004";
    private const string FrozenId = "d0000000-0000-4000-8000-000000000005";
    private const string NullyId = "d0000000-0000-4000-8000-000000000006";
    private const string PlacedId = "d0000000-0000-4000-8000-000000000007";
    private const string HolderId = "d0000000-0000-4000-8000-000000000007";

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
            // The facade the host-kind structs' signatures are compiled against; without it a
            // snippet touching HostLocalPosition.Value fails with CS0012 on Vector3.
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Numerics.Vectors").Location),
            MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Paradise.Export.Data.Color32).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "Game",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // BOTH generators, as a real build runs them. The identity diagnostics (PAUT005/006) come
        // from the schema generator, because that is the one that runs without the registry opt-in.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoringSchemaGenerator().AsSourceGenerator(),
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
        var found = typed.TryRead(new Guid(id), document.RootElement.Clone(), out var component);
        if (!found || component is null)
        {
            throw new InvalidOperationException($"registry did not read '{id}'");
        }
        return component;
    }

    /// <summary>The fallback path: resolved by fully qualified type name rather than by id.</summary>
    private static object ReadComponentByType(object registry, string fullTypeName, string json)
    {
        using var document = JsonDocument.Parse(json);
        var typed = (IAuthoredComponentRegistry)registry;
        if (!typed.TryReadByType(fullTypeName, document.RootElement.Clone(), out var component) ||
            component is null)
        {
            throw new InvalidOperationException($"registry did not read '{fullTypeName}'");
        }
        return component;
    }

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target);

    // ----------------------------------------------------------------------------------------

    private const string PrimitivesSource = $$"""
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        [assembly: AuthoredRegistry]

        namespace Game;

        [Guid("{{ThingId}}")]
        [Authored]
        public sealed record Thing
        {
            public float Speed { get; set; } = 2.5f;
            public int Count { get; set; } = 7;
            public bool Armed { get; set; } = true;
            public string Label { get; set; } = "untouched";
            public int? Budget { get; set; }
            public float? Bias { get; set; } = 0.25f;
        }
        """;

    private const string PlacedSource = $$"""
        using System.Numerics;
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        [assembly: AuthoredRegistry]

        namespace Game;

        [Guid("{{PlacedId}}")]
        [Authored]
        public sealed record Placed
        {
            public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
        }
        """;

    /// <summary>
    /// The generated matrix reader agrees with the contract's own, byte for byte.
    ///
    /// <b>Written by <c>ExportJsonWriter</c> and read by the GENERATOR's helper.</b> That pairing
    /// is the whole point: <c>ReadMatrix4x4</c> is a hand-copied index transpose of
    /// <c>Matrix4x4Converter.Read</c>, living in a second place, and nothing else fails if the two
    /// drift. A round trip through both is the only thing that notices.
    ///
    /// The matrix is deliberately NON-SYMMETRIC and has a translation. A symmetric one round-trips
    /// through a transposed reader unchanged, which is exactly the bug this is here to catch — and
    /// the translation is the half that shows up as every object loading at the origin.
    /// </summary>
    [Test]
    public async Task a_matrix_round_trips_through_the_wire_order_and_the_generated_reader()
    {
        var world = Paradise.Export.Geometry.ContractMatrix.Trs(
            new Vector3(1f, 2f, 3f),
            Quaternion.CreateFromYawPitchRoll(0.5f, 0.25f, 0.125f),
            new Vector3(2f, 3f, 4f));

        // The wire order spelled out rather than produced by a writer: column-major float[16],
        // M11 M21 M31 M41 … — the transpose of memory order. An independent restatement, so the
        // reader agreeing with it is a fact about the format rather than about one code path.
        var m = world;
        var payload = $$"""
            {"World":[{{m.M11}},{{m.M21}},{{m.M31}},{{m.M41}},{{m.M12}},{{m.M22}},{{m.M32}},{{m.M42}},{{m.M13}},{{m.M23}},{{m.M33}},{{m.M43}},{{m.M14}},{{m.M24}},{{m.M34}},{{m.M44}}]}
            """;

        var (registry, diagnostics) = Run(PlacedSource);
        await Assert.That(diagnostics).IsEmpty();
        var placed = ReadComponent(registry!, PlacedId, payload);

        await Assert.That((Matrix4x4)Prop(placed, "World")!).IsEqualTo(world);
    }

    /// <summary>The registration ceremony is gone: [assembly: AuthoredRegistry], no context,
    /// no [JsonSerializable] anywhere, and the payload materializes.</summary>
    [Test]
    public async Task primitives_read_and_absent_properties_keep_their_initializers()
    {
        var (registry, diagnostics) = Run(PrimitivesSource);

        await Assert.That(diagnostics).IsEmpty();
        var thing = ReadComponent(registry!, ThingId, """{"Speed": 9.5, "Armed": false}""");

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
        var thing = ReadComponent(registry!, ThingId, """{"speed": 1.5, "COUNT": 3}""");

        await Assert.That((float)Prop(thing, "Speed")!).IsEqualTo(1.5f);
        await Assert.That((int)Prop(thing, "Count")!).IsEqualTo(3);
    }

    /// <summary>The addon writes null for a string with no value; the property goes null exactly
    /// as it did through the context.</summary>
    [Test]
    public async Task a_null_string_assigns_null()
    {
        var (registry, _) = Run(PrimitivesSource);
        var thing = ReadComponent(registry!, ThingId, """{"Label": null}""");

        await Assert.That((string?)Prop(thing, "Label")).IsNull();
    }

    /// <summary>
    /// A nullable VALUE leaf reads its number, rather than materializing null.
    /// </summary>
    /// <remarks>The first nullable value types to reach the schema arrived with HostEnvironment's
    /// ShadowMapSize and ShadowBlur, and the model names the unwrap they depend on as a path where
    /// a value once came through null. The schema half was pinned when they landed; this is the
    /// generated READER half, which nothing covered.</remarks>
    [Test]
    public async Task a_nullable_value_leaf_reads_its_number()
    {
        var (registry, _) = Run(PrimitivesSource);
        var thing = ReadComponent(registry!, ThingId, """{"Budget": 4, "Bias": 0.5}""");

        await Assert.That((int?)Prop(thing, "Budget")).IsEqualTo(4);
        await Assert.That((float?)Prop(thing, "Bias")).IsEqualTo(0.5f);
    }

    /// <summary>An omitted nullable leaf keeps the record's own initializer — absent means "the
    /// declaration decides", which for these two is what "leave the renderer's own" rests on.</summary>
    [Test]
    public async Task an_omitted_nullable_value_leaf_keeps_its_initializer()
    {
        var (registry, _) = Run(PrimitivesSource);
        var thing = ReadComponent(registry!, ThingId, """{"Speed": 1.0}""");

        await Assert.That((int?)Prop(thing, "Budget")).IsNull();
        await Assert.That((float?)Prop(thing, "Bias")).IsEqualTo(0.25f);
    }

    /// <summary>
    /// The repair path. A payload whose id matches nothing still loads when it names its type —
    /// which is the whole reason the type name travels beside an id no human can read.
    /// </summary>
    [Test]
    public async Task a_payload_resolves_by_type_name_when_the_id_is_unknown()
    {
        var (registry, _) = Run(PrimitivesSource);

        var thing = ReadComponentByType(registry!, "Game.Thing", """{"Speed": 4.5}""");
        await Assert.That((float)Prop(thing, "Speed")!).IsEqualTo(4.5f);

        // ...and an unknown id really is unknown: the fallback is a second attempt, not a first.
        var typed = (IAuthoredComponentRegistry)registry!;
        using var payload = JsonDocument.Parse("""{"Speed": 4.5}""");
        await Assert.That(typed.TryRead(Guid.NewGuid(), payload.RootElement.Clone(), out _)).IsFalse();
        await Assert.That(typed.TryReadByType("Game.Missing", payload.RootElement.Clone(), out _))
            .IsFalse();
    }

    /// <summary>The registry publishes ids as GUIDs, so a caller can ask what it can materialize
    /// without parsing anything.</summary>
    [Test]
    public async Task the_registry_publishes_its_ids()
    {
        var (registry, _) = Run(PrimitivesSource);
        await Assert.That(((IAuthoredComponentRegistry)registry!).ComponentIds)
            .IsEquivalentTo(new[] { new Guid(ThingId) });
    }

    /// <summary>The full wire vocabulary in one record, shaped exactly as the addon writes it:
    /// enum as its member name, vectors and quaternions as float arrays, color as {r,g,b,a},
    /// composed groups as nested objects, lists as arrays of values.</summary>
    [Test]
    public async Task the_whole_vocabulary_reads_from_the_addon_wire_format()
    {
        var (registry, diagnostics) = Run($$"""
            using System.Collections.Generic;
            using System.Numerics;
            using System.Runtime.InteropServices;
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

            [Guid("{{RichId}}")]
            [Authored]
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
        var rich = ReadComponent(registry!, RichId, """
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

    private const string EnumSource = $$"""
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        [assembly: AuthoredRegistry]

        namespace Game;

        public enum Mode { Idle = 0, Chase = 1, Flee = 2 }

        [Guid("{{MoodyId}}")]
        [Authored]
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
        var named = ReadComponent(registry!, MoodyId, """{"Mode": "Flee"}""");
        await Assert.That(Prop(named, "Mode")!.ToString()).IsEqualTo("Flee");

        var lowered = ReadComponent(registry!, MoodyId, """{"Mode": "chase"}""");
        await Assert.That(Prop(lowered, "Mode")!.ToString()).IsEqualTo("Chase");

        var numeric = ReadComponent(registry!, MoodyId, """{"Mode": 2}""");
        await Assert.That(Prop(numeric, "Mode")!.ToString()).IsEqualTo("Flee");
    }

    /// <summary>A positional record cannot be constructed then assigned; the error names it and
    /// the registry leaves it out rather than emitting code that cannot compile.</summary>
    [Test]
    public async Task a_positional_record_is_reported_not_guessed_at()
    {
        var (registry, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{PositionalId}}")]
            [Authored]
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
        var (_, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{FrozenId}}")]
            [Authored]
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
        var (registry, _) = Run($$"""
            using System.Collections.Generic;
            using System.Numerics;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            public sealed record Part { public float Weight { get; set; } = 3f; }

            [Guid("{{NullyId}}")]
            [Authored]
            public sealed record Nully
            {
                public float Speed { get; set; } = 2.5f;
                public Vector3 Home { get; set; } = new(1f, 2f, 3f);
                public Part Body { get; set; } = new() { Weight = 9f };
                public List<float> Offsets { get; set; } = [4f];
                public string Label { get; set; } = "kept-unless-nulled";
            }
            """);
        var nully = ReadComponent(registry!, NullyId, """
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
        var (registry, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            public sealed record Piece(float Weight);

            [Guid("{{HolderId}}")]
            [Authored]
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
        var (registry, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{ThingId}}")]
            [Authored]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(registry).IsNull();
        await Assert.That(diagnostics).IsEmpty();
    }

    /// <summary>
    /// <c>[Authored]</c> with no <c>[Guid]</c> beside it fails the BUILD.
    ///
    /// The pair IS the declaration: without the second half the component has no identity, and
    /// every payload it ever produced would resolve to nothing. Caught here rather than at load
    /// time, where the symptom is an empty entity and no message naming this type.
    /// </summary>
    [Test]
    public async Task authored_without_a_guid_beside_it_fails_the_build()
    {
        var (registry, diagnostics) = Run("""
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Authored]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(registry).IsNull();
        var reported = diagnostics.Single(d => d.Id == "PAUT005");
        var message = reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("'Thing'");
        await Assert.That(message).Contains("Guid");
    }

    /// <summary>And a <c>[Guid]</c> that is not a GUID fails it too, quoting what was written —
    /// otherwise the failure is a FormatException raised whenever something happens to reflect
    /// over the type, arbitrarily far from the declaration.</summary>
    [Test]
    public async Task a_guid_attribute_that_is_not_a_guid_fails_the_build()
    {
        var (registry, diagnostics) = Run("""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("game.thing")]
            [Authored]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(registry).IsNull();
        var reported = diagnostics.Single(d => d.Id == "PAUT005");
        await Assert.That(reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("game.thing");
    }

    /// <summary>A <c>[Guid]</c> with no <c>[Authored]</c> is just a type with a GUID — plenty of
    /// those exist, and none of them is authored data.</summary>
    [Test]
    public async Task a_guid_alone_declares_nothing()
    {
        var (registry, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{ThingId}}")]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(registry).IsNull();
        await Assert.That(diagnostics).IsEmpty();
    }

    /// <summary>
    /// An uppercase id is the SAME id — otherwise a copy typed in the other case would quietly
    /// register as a second component.
    ///
    /// Case is the only spelling left to get wrong: the C# compiler rejects every other form of
    /// <c>[Guid]</c> argument itself (CS0591), braces included, so the generator never sees one.
    /// </summary>
    [Test]
    public async Task id_spellings_are_canonicalized_before_they_are_compared()
    {
        var shouted = ThingId.ToUpperInvariant();
        var (registry, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{shouted}}")]
            [Authored]
            public sealed record Thing
            {
                public float Speed { get; set; } = 2.5f;
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        // Declared uppercase, found by the canonical lowercase form.
        var thing = ReadComponent(registry!, ThingId, """{"Speed": 8}""");
        await Assert.That((float)Prop(thing, "Speed")!).IsEqualTo(8f);
    }

    /// <summary>Two records sharing an id is almost always a copy-paste, and silent: the registry
    /// would keep one reader and materialize the wrong record for half the payloads. Both are
    /// named in full, because only one of them gets the squiggle — and a copy-paste that lands in
    /// another namespace shares its short name, so short names would print the pair twice.</summary>
    [Test]
    public async Task two_components_sharing_an_id_fail_the_build()
    {
        var (_, diagnostics) = Run($$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            // Same short name in two namespaces: the shape a copy-paste actually takes, and the
            // one a short-name message would render as "'Health' and 'Health'".
            namespace Game.Combat
            {
                [Guid("{{ThingId}}")]
                [Authored]
                public sealed record Health { public float Speed { get; set; } }
            }

            namespace Game.Ui
            {
                [Guid("{{ThingId}}")]
                [Authored]
                public sealed record Health { public float Speed { get; set; } }
            }
            """);

        var reported = diagnostics.Single(d => d.Id == "PAUT006");
        var message = reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("Game.Combat.Health");
        await Assert.That(message).Contains("Game.Ui.Health");
    }

    // ---- typed host kinds ------------------------------------------------------------------

    private const string HostBoundId = "d0000000-0000-4000-8000-000000000010";

    /// <summary>The generated reader wraps a host-typed field's wire value back into the host
    /// struct, and parses a HostId binding's guid from the canonical string.</summary>
    [Test]
    public async Task host_bound_fields_read_back_through_the_generated_reader()
    {
        var (registry, _) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                [AuthoredByHost<HostId>] public Guid Ident { get; set; }
                public HostLocalPosition Position { get; set; }
                public HostLocalScale Scale { get; set; }
            }
            """);

        var component = ReadComponent(registry!, HostBoundId, """
            {
              "Ident": "3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8",
              "Position": [1.5, 2.0, -0.5],
              "Scale": [2.0, 2.0, 2.0]
            }
            """);

        await Assert.That(Prop(component, "Ident"))
            .IsEqualTo(new Guid("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8"));
        var position = (HostLocalPosition)Prop(component, "Position")!;
        await Assert.That(position.Value).IsEqualTo(new System.Numerics.Vector3(1.5f, 2.0f, -0.5f));
        var scale = (HostLocalScale)Prop(component, "Scale")!;
        await Assert.That(scale.Value).IsEqualTo(new System.Numerics.Vector3(2.0f, 2.0f, 2.0f));
    }

    /// <summary>The whole point of the typed spelling: a value kind declares the type the host
    /// writes, so a field of another type is PAUT010 rather than a schema no host can fill.</summary>
    [Test]
    public async Task a_field_type_disagreeing_with_its_value_kind_fails_the_build()
    {
        var (_, diagnostics) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                [AuthoredByHost<HostLocalPosition>] public float Height { get; set; }
            }
            """);

        var reported = diagnostics.Single(d => d.Id == "PAUT010");
        var message = reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("Height");
        await Assert.That(message).Contains("HostLocalPosition");
    }

    /// <summary>A value kind is one concrete value of one field; a whole record cannot be one.</summary>
    [Test]
    public async Task a_value_kind_on_a_type_fails_the_build()
    {
        var (_, diagnostics) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            [AuthoredByHost<HostId>]
            public sealed record Placed
            {
                public float Height { get; set; }
            }
            """);

        await Assert.That(diagnostics.Single(d => d.Id == "PAUT011").GetMessage(
            System.Globalization.CultureInfo.InvariantCulture)).Contains("HostId");
    }

    /// <summary>Typed as one kind, attributed as another: the attribute wins, and PAUT012 says
    /// the disagreement out loud.</summary>
    [Test]
    public async Task a_host_typed_field_with_a_disagreeing_attribute_warns()
    {
        var (_, diagnostics) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                [AuthoredByHost<HostLocalScale>] public HostLocalPosition Position { get; set; }
            }
            """);

        await Assert.That(diagnostics.Single(d => d.Id == "PAUT012").Severity)
            .IsEqualTo(DiagnosticSeverity.Warning);
    }

    /// <summary>Same disagreement on a COMPOSED-typed property: PAUT012 must not be gated on the
    /// value-kind wrapper, or the schema silently publishes one kind over another's fields.</summary>
    [Test]
    public async Task a_composed_typed_field_with_a_disagreeing_attribute_warns()
    {
        var (_, diagnostics) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                [AuthoredByHost<HostLight>] public HostShape Collider { get; set; } = new();
            }
            """);

        await Assert.That(diagnostics.Single(d => d.Id == "PAUT012").Severity)
            .IsEqualTo(DiagnosticSeverity.Warning);
    }

    /// <summary>Mesh, sprite and asset keep a string hatch until bake emits the guid; entity and
    /// parent do not, because a name was never an identity.</summary>
    [Test]
    public async Task a_string_field_is_a_baked_path_for_mesh_but_not_for_parent()
    {
        var (_, diagnostics) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                [AuthoredByHost<HostMesh>] public string Mesh { get; set; } = "";
                [AuthoredByHost<HostParent>] public string Parent { get; set; } = "";
            }
            """);

        var mismatches = diagnostics.Where(d => d.Id == "PAUT010").ToList();
        await Assert.That(mismatches.Count).IsEqualTo(1);
        await Assert.That(mismatches[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Parent");
    }

    /// <summary>A composed object absent from the payload keeps the kind's defaults, which is
    /// only true when the record initializes the property with <c>new()</c> — struct
    /// <c>default</c> never runs the initializers.</summary>
    [Test]
    public async Task an_omitted_composed_object_keeps_the_kind_defaults()
    {
        var (registry, _) = Run($$"""
            using System;
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            [assembly: AuthoredRegistry]

            namespace Game;

            [Guid("{{HostBoundId}}")]
            [Authored]
            public sealed record Placed
            {
                public HostCamera Eye { get; set; } = new();
                public HostLight Lamp { get; set; } = new();
            }
            """);

        var component = ReadComponent(registry!, HostBoundId, "{ }");

        var eye = (HostCamera)Prop(component, "Eye")!;
        await Assert.That(eye.Fov).IsEqualTo(50f);
        await Assert.That(eye.Far).IsEqualTo(1000f);
        await Assert.That(eye.Rotation).IsEqualTo(System.Numerics.Quaternion.Identity);
        var lamp = (HostLight)Prop(component, "Lamp")!;
        await Assert.That(lamp.Enabled).IsTrue();
        await Assert.That(lamp.Intensity).IsEqualTo(1f);
    }
}
