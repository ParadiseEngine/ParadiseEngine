using System.Numerics;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>
/// One character's playback: the clip it is playing, where in it, at what rate, whether it loops,
/// and the clip it is fading out of. <see cref="Advance"/> moves time, <see cref="Evaluate"/>
/// samples (blending the two clips while a fade runs) into local poses and model-space matrices
/// the caller reads from <see cref="LocalPose"/> and <see cref="ModelMatrices"/>. Allocates only in
/// the constructor: one <see cref="AnimationPlayerState"/> blob holding both sampling contexts,
/// both pose sets and the matrices.
/// </summary>
/// <remarks>
/// Holds the <see cref="NativeBlobAssetReference{T}"/>s it plays rather than raw refs so a clip
/// stays alive while queued; the asset cache that loaded them still owns disposal. The skeleton
/// is fixed at construction because the buffers are sized to it, and because a clip's tracks
/// index that skeleton's joints — playing a clip cooked for another skeleton is refused.
/// </remarks>
public sealed class AnimationPlayer : IDisposable
{
    private readonly NativeBlobAssetReference<SkeletonBlob> _skeleton;
    private readonly NativeBlobAssetReference<AnimationPlayerState> _state;
    private Slot _current;
    private Slot _outgoing;
    private float _fadeDuration;
    private float _fadeRemaining;
    private bool _disposed;

    public AnimationPlayer(NativeBlobAssetReference<SkeletonBlob> skeleton)
    {
        _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        _state = AnimationPlayerState.Create(skeleton.Value.JointCount);
        _current = Slot.Rest;
        _outgoing = Slot.Rest;
    }

    public int JointCount => _state.Value.Models.Length;

    /// <summary>The clip playing, or null for the rest pose.</summary>
    public NativeBlobAssetReference<AnimationBlob>? Current => _current.Clip;

    /// <summary>Seconds into the current clip.</summary>
    public float Time => _current.Time;

    public float Rate
    {
        get => _current.Rate;
        set => _current.Rate = value;
    }

    public bool IsLooping => _current.Loop;

    /// <summary>A non-looping clip that has reached its end (or its start when playing backwards).</summary>
    public bool IsFinished => _current.Clip is not null && !_current.Loop && (_current.Rate >= 0f ? _current.Time >= _current.Duration : _current.Time <= 0f);

    public bool IsFading => _fadeRemaining > 0f;

    /// <summary>The blend toward the current clip, 0 at the start of a fade, 1 when none runs.</summary>
    public float FadeProgress => _fadeRemaining > 0f ? 1f - _fadeRemaining / _fadeDuration : 1f;

    /// <summary>What the last <see cref="Evaluate"/> produced, one local pose per joint; read it through this <c>ref</c>, never a copy.</summary>
    public ref JointPoses LocalPose => ref _state.Value.Pose;

    /// <summary>What the last <see cref="Evaluate"/> produced, one model-space matrix per joint (row-vector convention).</summary>
    public ReadOnlySpan<Matrix4x4> ModelMatrices => _state.Value.Models.ToSpan();

    /// <summary>The whole per-character state as one blob, for a host that wants to place it itself.</summary>
    public ref AnimationPlayerState State => ref _state.Value;

    /// <summary>Starts <paramref name="clip"/>; with a positive <paramref name="fadeSeconds"/> the clip playing until now keeps advancing and blends out over that time.</summary>
    /// <exception cref="ArgumentException">The clip has a different track count than the skeleton has joints.</exception>
    public void Play(NativeBlobAssetReference<AnimationBlob> clip, float fadeSeconds = 0f, bool loop = true, float rate = 1f, float startTime = 0f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (clip.Value.TrackCount != JointCount)
        {
            throw new ArgumentException($"The clip '{clip.Value.Name.ToString()}' has {clip.Value.TrackCount} tracks; the skeleton has {JointCount} joints.", nameof(clip));
        }

        if (fadeSeconds > 0f && _current.Clip is not null)
        {
            _outgoing = _current;
            _fadeDuration = fadeSeconds;
            _fadeRemaining = fadeSeconds;
        }
        else
        {
            _outgoing = Slot.Rest;
            _fadeRemaining = 0f;
        }

        _current = new Slot(clip, loop, rate, Math.Clamp(startTime, 0f, clip.Value.Duration));
        _state.Value.Current.Invalidate();
    }

    /// <summary>Back to the rest pose, fading the playing clip out over <paramref name="fadeSeconds"/> when positive.</summary>
    public void Stop(float fadeSeconds = 0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fadeSeconds > 0f && _current.Clip is not null)
        {
            _outgoing = _current;
            _fadeDuration = fadeSeconds;
            _fadeRemaining = fadeSeconds;
        }
        else
        {
            _outgoing = Slot.Rest;
            _fadeRemaining = 0f;
        }

        _current = Slot.Rest;
    }

    /// <summary>Moves both clips by <paramref name="deltaSeconds"/> at their rates — a looping clip wraps, a one-shot clamps — and runs the fade down; the fade's clock is unscaled.</summary>
    public void Advance(float deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current.Advance(deltaSeconds);
        if (_fadeRemaining <= 0f) return;

        _outgoing.Advance(deltaSeconds);
        _fadeRemaining -= deltaSeconds;
        if (_fadeRemaining <= 0f)
        {
            _fadeRemaining = 0f;
            _outgoing = Slot.Rest;
        }
    }

    /// <summary>Samples the clips at their current times, blends them by the fade, and walks the hierarchy into <see cref="ModelMatrices"/>.</summary>
    public void Evaluate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref var state = ref _state.Value;
        Sample(in _current, ref state.Current, ref state.Pose);
        if (_fadeRemaining > 0f)
        {
            Sample(in _outgoing, ref state.Outgoing, ref state.OutgoingPose);
            JointPoses.Blend(ref state.OutgoingPose, ref state.Pose, FadeProgress, ref state.Pose);
        }

        LocalToModel.Compute(ref _skeleton.Value, ref state.Pose, state.Models.ToSpan());
    }

    private void Sample(in Slot slot, ref SamplingContext context, ref JointPoses pose)
    {
        if (slot.Clip is null)
        {
            pose.CopyFrom(_skeleton.Value.RestPoses.ToSpan());
            return;
        }

        context.Sample(ref slot.Clip.Value, slot.Duration > 0f ? slot.Time / slot.Duration : 0f, ref pose);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Dispose();
    }

    /// <summary>One playing clip: which, where, how fast, and whether it wraps.</summary>
    private struct Slot(NativeBlobAssetReference<AnimationBlob>? clip, bool loop, float rate, float time)
    {
        public static Slot Rest => new(null, false, 1f, 0f);

        public readonly NativeBlobAssetReference<AnimationBlob>? Clip = clip;
        public readonly bool Loop = loop;
        public float Rate = rate;
        public float Time = time;

        public float Duration => Clip?.Value.Duration ?? 0f;

        public void Advance(float deltaSeconds)
        {
            if (Clip is null) return;
            var duration = Duration;
            Time += deltaSeconds * Rate;
            if (Loop)
            {
                Time = Time - MathF.Floor(Time / duration) * duration;
                if (Time < 0f || Time >= duration) Time = 0f;
            }
            else
            {
                Time = Math.Clamp(Time, 0f, duration);
            }
        }
    }
}

/// <summary>
/// Everything an <see cref="AnimationPlayer"/> owns per character, as one native blob: the two
/// sampling cursors, the two pose sets and the model matrices, sized to one skeleton. One
/// allocation per character, one region a frame touches; the clip and skeleton it plays are the
/// player's references, not the blob's, so this stays free of anything the GC must root.
/// </summary>
public struct AnimationPlayerState
{
    public SamplingContext Current;
    public SamplingContext Outgoing;
    public JointPoses Pose;
    public JointPoses OutgoingPose;
    public BlobArray<Matrix4x4> Models;

    public static NativeBlobAssetReference<AnimationPlayerState> Create(int jointCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jointCount);
        var builder = new StructBuilder<AnimationPlayerState>();
        SamplingContext.Set(builder, ref builder.Value.Current, jointCount);
        SamplingContext.Set(builder, ref builder.Value.Outgoing, jointCount);
        JointPoses.Set(builder, ref builder.Value.Pose, jointCount);
        JointPoses.Set(builder, ref builder.Value.OutgoingPose, jointCount);
        builder.SetArray(ref builder.Value.Models, new Matrix4x4[jointCount], alignment: 16);
        return builder.CreateNativeBlobAssetReference();
    }
}

/// <summary>The joint palette a skinned mesh's vertex shader consumes, from model-space matrices and the mesh's skin (<c>MeshSkin</c> in <c>Paradise.Assets.Mesh</c>, handed over as spans so this assembly need not know the mesh format).</summary>
public static class SkinningPalette
{
    /// <summary>Row-vector convention: <c>palette[i] = inverseBind[i] × model[joints[i]] × inverse(model[meshJoint])</c>; a negative <paramref name="meshJoint"/> means the mesh sits at the model's origin.</summary>
    /// <exception cref="ArgumentException">Mismatched skin arrays, or a joint outside the skeleton.</exception>
    public static void Compute(ReadOnlySpan<Matrix4x4> models, ReadOnlySpan<int> joints, ReadOnlySpan<Matrix4x4> inverseBinds, int meshJoint, Span<Matrix4x4> palette)
    {
        if (inverseBinds.Length != joints.Length) throw new ArgumentException($"{joints.Length} joints and {inverseBinds.Length} inverse-bind matrices.", nameof(inverseBinds));
        if (palette.Length < joints.Length) throw new ArgumentException($"The palette holds {palette.Length} of {joints.Length} slots.", nameof(palette));
        if (meshJoint >= models.Length) throw new ArgumentException($"Mesh joint {meshJoint} is outside the {models.Length}-joint skeleton.", nameof(meshJoint));

        var inverseMeshWorld = Matrix4x4.Identity;
        if (meshJoint >= 0 && !Matrix4x4.Invert(models[meshJoint], out inverseMeshWorld)) inverseMeshWorld = Matrix4x4.Identity;
        for (var i = 0; i < joints.Length; i++)
        {
            var joint = joints[i];
            if (joint < 0 || joint >= models.Length) throw new ArgumentException($"Palette slot {i} names joint {joint} of {models.Length}.", nameof(joints));
            palette[i] = inverseBinds[i] * models[joint] * inverseMeshWorld;
        }
    }
}
