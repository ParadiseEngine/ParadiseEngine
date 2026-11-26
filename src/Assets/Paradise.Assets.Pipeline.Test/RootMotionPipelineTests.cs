using System.Numerics;

using Paradise.Animation;
using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// Per-clip root motion from the Blender addon's <c>[glb] clips</c> settings to the cooked clip: the flag and root-bone
/// override are read as the addon writes them, the root bone auto-detects to the skin's root joint, an unknown bone fails
/// the build, an unflagged clip cooks the bytes it always did, and cooked travel and snap match the glTF reference.
/// </summary>
public class RootMotionPipelineTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Glb = "/game/assets/models/hero.glb";
    private const string Meta = Glb + ".meta";
    private const int RootNode = 3;

    /// <summary>A triangle skinned to root → pelvis → spine under a turned and scaled "Player_Rig"; "walk" carries the root 2 m along +Z with a bob, "idle" holds it.</summary>
    private static byte[] RigGlb(bool rootListedSecond = false)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 0f, 1f, 0f, 0f, 1.5f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));

        var body = b.AddNode(mesh: mesh, skin: 0, name: "Body");                                                      // 0
        var spine = b.AddNode(translation: [0f, 0.5f, 0f], name: "spine");                                              // 1
        var pelvis = b.AddNode(translation: [0f, 1f, 0f], name: "pelvis", children: [spine]);                           // 2
        var root = b.AddNode(name: "root", children: [pelvis]);                                                         // 3
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        var rig = b.AddNode(rotation: [turn.X, turn.Y, turn.Z, turn.W], scale: [1.5f, 1.5f, 1.5f], name: "Player_Rig", children: [root]); // 4

        int[] skin = rootListedSecond ? [pelvis, root, spine] : [root, pelvis, spine];
        var binds = new Dictionary<int, float[]> { [root] = Translation(0f, 0f, 0f), [pelvis] = Translation(0f, -1f, 0f), [spine] = Translation(0f, -1.5f, 0f) };
        b.AddSkin(skin, b.AddFloatAccessor([.. skin.SelectMany(joint => binds[joint])], "MAT4"), name: "rig");

        var three = b.AddFloatAccessor([0f, 0.5f, 1f], "SCALAR");
        var two = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var walks = b.AddFloatAccessor([0f, 0f, 0f, 0f, 0.1f, 1f, 0f, 0f, 2f], "VEC3");
        var sway = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        var sways = b.AddFloatAccessor([0f, 0f, 0f, 1f, sway.X, sway.Y, sway.Z, sway.W], "VEC4");
        var holds = b.AddFloatAccessor([0f, 0f, 0f, 0f, 0f, 0f], "VEC3");
        b.AddAnimation("walk", (root, "translation", three, walks, null), (pelvis, "rotation", two, sways, null));
        b.AddAnimation("idle", (root, "translation", two, holds, null), (pelvis, "rotation", two, sways, null));
        b.SetSceneRoots(body, rig);
        return b.Build();
    }

    private static float[] Translation(float x, float y, float z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];

    private static (MemoryFileSystem FileSystem, AssetReference Source) Project(byte[]? glb = null)
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes(Glb, glb ?? RigGlb());
        var guid = ProjectVerifierTests.Mint(fileSystem, Glb);
        var source = new AssetReference(guid, "models/hero.glb");
        foreach (var (name, index) in new[] { ("walk", 0), ("idle", 1) })
        {
            var path = $"/game/assets/models/hero.{name}.anim";
            fileSystem.WriteAllBytes(path, new MeshReferenceDocument(source, MeshSlot.Clip, name, index).WriteBytes());
            ProjectVerifierTests.Mint(fileSystem, path);
        }

        return (fileSystem, source);
    }

    /// <summary>The domain as the Blender addon writes it (<c>glb_clips._write_domain</c>): one inline table per clip.</summary>
    private static void AuthorClips(MemoryFileSystem fileSystem, string clips) =>
        fileSystem.WriteAllText(Meta, fileSystem.ReadAllText(Meta) + $"\n[glb]\nclips = [ {clips} ]\n");

    private static BuildResult Build(MemoryFileSystem fileSystem) => new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

    private static int CookedJoint(MemoryFileSystem fileSystem, string clip)
    {
        using var animation = OzzArchive.ReadAnimation(fileSystem.ReadAllBytes($"/game/build/models/hero.{clip}.anim"));
        return animation.Value.RootMotionJoint;
    }

    private static int Joint(CookedGlb cooked, string name)
    {
        using var skeleton = OzzArchive.ReadSkeleton(cooked.Skeleton!);
        return skeleton.Value.FindJoint(name);
    }

    [Test]
    public async Task the_root_bone_auto_detects_to_the_skins_root_joint_and_an_override_names_another()
    {
        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(RigGlb()));
        var reordered = GltfCook.Cook(GltfSceneReader.ReadGeometry(RigGlb(rootListedSecond: true)));
        var rigid = GltfCook.Cook(GltfSceneReader.ReadGeometry(CrateGlb()));

        var detected = GltfCook.RootMotionJoint(cooked, null, out _);
        var overridden = GltfCook.RootMotionJoint(cooked, "pelvis", out _);
        var unknown = GltfCook.RootMotionJoint(cooked, "tail", out var unknownProblem);
        var skinless = GltfCook.RootMotionJoint(rigid, null, out var skinlessProblem);

        // The whole node tree is the skeleton, so the skin's root is not joint 0: Body and Player_Rig come first.
        await Assert.That(detected).IsEqualTo(Joint(cooked, "root"));
        await Assert.That(detected).IsNotEqualTo(0);
        await Assert.That(GltfCook.RootMotionJoint(reordered, null, out _)).IsEqualTo(Joint(reordered, "root"));
        await Assert.That(overridden).IsEqualTo(Joint(cooked, "pelvis"));
        await Assert.That(GltfCook.RootMotionJoint(cooked, "Player_Rig", out _)).IsEqualTo(Joint(cooked, "Player_Rig"));
        await Assert.That(unknown).IsEqualTo(AnimationBlob.NoRootMotion);
        await Assert.That(unknownProblem).Contains("'tail'");
        await Assert.That(skinless).IsEqualTo(AnimationBlob.NoRootMotion);
        await Assert.That(skinlessProblem).Contains("root_bone");
    }

    [Test]
    public async Task cooked_root_motion_travels_and_snaps_as_the_glTF_reference_does()
    {
        const float tolerance = 1e-3f;
        var asset = GltfSceneReader.ReadGeometry(RigGlb());
        var cooked = GltfCook.Cook(asset);
        var archive = GltfCook.BuildClip(cooked.Clips[0], cooked.Skeleton!, rootMotionJoint: GltfCook.RootMotionJoint(cooked, null, out _));
        using var skeleton = OzzArchive.ReadSkeleton(cooked.Skeleton!);
        using var clip = OzzArchive.ReadAnimation(archive);
        using var sampler = new RootMotionSampler(skeleton);
        using var player = new AnimationPlayer(skeleton);
        var playback = player.Play(clip, loop: false);
        var reference = new GltfAnimationRig(asset);
        var walk = asset.Animations[0];
        var skin = cooked.Mesh.Skin!;
        var meshNode = asset.Instances[0].NodeIndex;
        var ours = new Matrix4x4[skin.Joints.Length];
        var theirs = new Matrix4x4[skin.Joints.Length];
        reference.EvaluatePose(null, 0f);
        var rest = reference.WorldTranslation(RootNode);

        var worstTravel = 0f;
        var worstPalette = 0f;
        float[] times = [0f, 0.2f, 0.45f, 0.5f, 0.8f, 1f];
        for (var i = 0; i < times.Length; i++)
        {
            reference.EvaluatePose(walk, times[i]);
            var at = reference.WorldTranslation(RootNode);
            if (i > 0)
            {
                reference.EvaluatePose(walk, times[i - 1]);
                var travelled = at - reference.WorldTranslation(RootNode);
                var travel = sampler.Delta(clip, times[i - 1], times[i] - times[i - 1], loop: false).Translation;
                worstTravel = MathF.Max(worstTravel, Vector2.Distance(new Vector2(travel.X, travel.Z), new Vector2(travelled.X, travelled.Z)));
                await Assert.That(travel.Y).IsEqualTo(0f);
            }

            reference.EvaluatePose(walk, times[i]);
            reference.SnapOverRest(RootNode, rest);
            reference.ComputeJointPalette(0, meshNode, theirs);
            player.Seek(playback, times[i]);
            player.Evaluate();
            SkinningPalette.Compute(player.ModelMatrices, skin.Joints, skin.InverseBindMatrices, cooked.Mesh.Draws[0].NodeIndex, ours);
            for (var j = 0; j < ours.Length; j++) worstPalette = MathF.Max(worstPalette, MaxAbs(ours[j] - theirs[j]));
        }

        // The pose at the clip's end, last evaluated above, against the reference left unsnapped: the snap is what agrees.
        reference.EvaluatePose(walk, 1f);
        reference.ComputeJointPalette(0, meshNode, theirs);
        var unsnapped = Enumerable.Range(0, ours.Length).Max(j => MaxAbs(ours[j] - theirs[j]));

        await Assert.That(worstTravel).IsLessThan(tolerance);
        await Assert.That(worstPalette).IsLessThan(tolerance);
        await Assert.That(unsnapped).IsGreaterThan(1f);
        // The walk really travels, turned by the rig, so the comparison is not of two zeros.
        await Assert.That(sampler.Delta(clip, 0f, 1f, loop: false).Translation.Length()).IsEqualTo(3f).Within(tolerance);
    }

    [Test]
    public async Task a_clip_the_addon_flagged_cooks_with_its_skin_root_and_the_others_cook_the_bytes_they_always_did()
    {
        var (fileSystem, _) = Project();
        using var project = fileSystem;
        AuthorClips(fileSystem, "{ index = 0, name = \"walk\", root_motion = true }");

        var result = Build(fileSystem);
        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(RigGlb()));

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(CookedJoint(fileSystem, "walk")).IsEqualTo(Joint(cooked, "root"));
        await Assert.That(CookedJoint(fileSystem, "idle")).IsEqualTo(AnimationBlob.NoRootMotion);
        await Assert.That(fileSystem.ReadAllBytes("/game/build/models/hero.idle.anim"))
            .IsEquivalentTo(GltfCook.BuildClip(cooked.Clips[1], cooked.Skeleton!), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_root_bone_override_cooks_that_joint_and_an_unflagged_override_changes_nothing()
    {
        var (fileSystem, _) = Project();
        using var project = fileSystem;
        AuthorClips(fileSystem, "{ index = 0, name = \"walk\", root_motion = true, root_bone = \"pelvis\" }, { index = 1, name = \"idle\", root_motion = false, root_bone = \"pelvis\" }");

        var result = Build(fileSystem);
        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(RigGlb()));

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(CookedJoint(fileSystem, "walk")).IsEqualTo(Joint(cooked, "pelvis"));
        await Assert.That(CookedJoint(fileSystem, "idle")).IsEqualTo(AnimationBlob.NoRootMotion);
    }

    [Test]
    public async Task a_root_bone_the_model_lacks_fails_the_build_and_verify_naming_the_bone_and_the_sidecar()
    {
        var (fileSystem, _) = Project();
        using var project = fileSystem;
        AuthorClips(fileSystem, "{ index = 0, name = \"walk\", root_motion = true, root_bone = \"tail\" }");

        var result = Build(fileSystem);
        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(result.Succeeded).IsFalse();
        var error = result.Errors.Single();
        await Assert.That(error).Contains("hero.walk.anim");
        await Assert.That(error).Contains("'tail'");
        await Assert.That(error).Contains("root motion");
        await Assert.That(error).Contains("hero.glb.meta");
        await Assert.That(findings.Single(finding => finding.Severity == VerifySeverity.Error).Message).Contains("'tail'");
    }

    [Test]
    public async Task toggling_the_flag_re_cooks_the_clip()
    {
        var (fileSystem, _) = Project();
        using var project = fileSystem;
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
        var before = fileSystem.ReadAllBytes("/game/build/models/hero.walk.anim");

        AuthorClips(fileSystem, "{ index = 0, name = \"walk\", root_motion = true }");
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
        var flagged = CookedJoint(fileSystem, "walk");

        fileSystem.WriteAllText(Meta, fileSystem.ReadAllText(Meta).Replace("root_motion = true", "root_motion = false", StringComparison.Ordinal));
        await Assert.That(Build(fileSystem).Errors).IsEmpty();

        await Assert.That(flagged).IsNotEqualTo(AnimationBlob.NoRootMotion);
        await Assert.That(fileSystem.ReadAllBytes("/game/build/models/hero.walk.anim")).IsEquivalentTo(before, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_setting_follows_its_clip_by_name_when_a_re_export_moved_it_and_another_models_setting_is_not_this_ones()
    {
        var (fileSystem, _) = Project();
        using var project = fileSystem;
        // Recorded when walk was clip 1; the re-export put it first. An asset's entry belongs to that model of a .blend.
        AuthorClips(fileSystem, "{ index = 1, name = \"walk\", root_motion = true }, { asset = \"0b5e8a52-5d8e-4f43-9b0a-2f6c1f0c7a11\", index = 1, name = \"idle\", root_motion = true }");

        var result = Build(fileSystem);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(CookedJoint(fileSystem, "walk")).IsNotEqualTo(AnimationBlob.NoRootMotion);
        await Assert.That(CookedJoint(fileSystem, "idle")).IsEqualTo(AnimationBlob.NoRootMotion);
    }

    [Test]
    public async Task clip_settings_reconcile_with_the_clip_table_as_the_addons_panel_shows_them()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes(Glb, RigGlb());
        ProjectVerifierTests.Mint(fileSystem, Glb);
        var hero = Guid.Parse("0b5e8a52-5d8e-4f43-9b0a-2f6c1f0c7a11");
        AuthorClips(fileSystem, string.Join(", ",
            "{ index = 0, name = \"Walk_Old\", root_motion = true }",        // renamed in place: stays at its index
            "{ index = 5, name = \"Sprint\", root_motion = true }",          // moved: follows its name
            "{ index = 7, name = \"Gone\", root_motion = true }",            // past the end, name nowhere: applies to none
            "{ index = 2, root_motion = true, root_bone = \"pelvis\" }",
            "{ index = 2, root_motion = false }",                            // a repeated index keeps the last
            $"{{ asset = \"{hero}\", index = 1, name = \"Idle\", root_motion = true }}"));
        var meta = SidecarMeta.Load(fileSystem, Meta);
        string[] clips = ["Walk", "Idle", "Turn", "Sprint"];

        await Assert.That(GlbImportSettings.ClipSetting(meta, null, clips, 0)!.RootMotion).IsTrue();
        await Assert.That(GlbImportSettings.ClipSetting(meta, null, clips, 3)).IsEqualTo(new GlbClipSetting(null, 3, "Sprint", true, null));
        await Assert.That(GlbImportSettings.ClipSetting(meta, null, clips, 1)).IsNull();
        await Assert.That(GlbImportSettings.ClipSetting(meta, null, clips, 2)).IsEqualTo(new GlbClipSetting(null, 2, "", false, null));
        await Assert.That(GlbImportSettings.ClipSetting(meta, hero, clips, 1)).IsEqualTo(new GlbClipSetting(hero, 1, "Idle", true, null));
        await Assert.That(GlbImportSettings.ReadClipSettings(meta).Count).IsEqualTo(6);
    }

    [Test]
    public async Task a_malformed_clip_setting_is_a_verify_error_naming_the_domain_and_the_settings_survive_other_writes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes(Glb, RigGlb());
        ProjectVerifierTests.Mint(fileSystem, Glb);
        AuthorClips(fileSystem, "{ index = 0, name = \"walk\", root_motion = \"yes\" }");

        var finding = ProjectVerifier.Verify(fileSystem, s_layout).Single(each => each.Message.Contains("[glb]", StringComparison.Ordinal));

        await Assert.That(finding.Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(finding.Message).Contains("[glb].clips");
        await Assert.That(finding.Message).Contains("root_motion");

        fileSystem.WriteAllText(Meta, fileSystem.ReadAllText(Meta).Replace("\"yes\"", "true", StringComparison.Ordinal));
        var meta = SidecarMeta.Load(fileSystem, Meta);
        GlbImportSettings.WriteOptimization(meta, Paradise.Animation.Offline.AnimationOptimizer.Setting.Default);
        meta.Save(fileSystem, Meta);

        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(each => each.Message.Contains("[glb]", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(GlbImportSettings.ReadClipSettings(SidecarMeta.Load(fileSystem, Meta)).Single()).IsEqualTo(new GlbClipSetting(null, 0, "walk", true, null));
    }

    private static byte[] CrateGlb()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position)), name: "Crate");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 0f, 2f], "VEC3");
        b.AddAnimation("Slide", (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        return b.Build();
    }

    private static float MaxAbs(Matrix4x4 m)
    {
        var max = 0f;
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) max = MathF.Max(max, MathF.Abs(v));
        return max;
    }
}
