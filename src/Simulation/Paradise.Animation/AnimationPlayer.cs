using System.Numerics;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>Plays any number of clips on one skeleton and composes them into local poses and model-space matrices.</summary>
/// <remarks>
/// <para>Each <see cref="PlaybackHandle"/> is one clip instance with its own clock, weight and sampling cache, so a clip
/// may play several times at once. Playbacks belong to layers, composed bottom to top over the rest pose. A layer first
/// blends its playbacks by relative weight — multiplying every weight in a layer by one positive factor changes
/// nothing — then overrides or adds to the pose beneath by its own weight in 0..1, scaled per joint by an optional
/// <see cref="JointMask"/>. A lone playback therefore keeps full influence at any positive weight: fade its layer, or
/// <see cref="Stop(float)"/> it, to reveal what lies beneath.</para>
/// <para>Weights fade over elapsed seconds, and an interrupted fade continues from the value it reached, so the pose never
/// jumps; playbacks keep advancing while they fade out. <c>Play</c> and <c>Stop</c> are conveniences over these fades
/// that also end the playbacks they replace. A sync group drives looping playbacks from one normalized phase, so gaits
/// of different lengths stay in step while their weights change.</para>
/// <para>Hosts call <see cref="Advance"/> then <see cref="Evaluate"/>. Neither allocates, nor does changing a weight, fade,
/// rate, time or mask; adding a playback, layer or sync group beyond the reserved capacity grows storage, and the output
/// pose and matrices never move. Clip, mask and skeleton references keep assets reachable, but their owners dispose
/// them, after the player stops using them. One owner drives a player; separate players may evaluate concurrently when
/// they share only immutable assets.</para>
/// </remarks>
public sealed class AnimationPlayer : IDisposable
{
    private const int None = -1;
    private static int s_nextId;

    private readonly int _id;
    private readonly int _jointCount;
    private readonly ulong _skeletonFingerprint;
    private readonly NativeBlobAssetReference<SkeletonBlob> _skeleton;
    private readonly NativeBlobAssetReference<PlayerBuffers> _buffers;

    private Playback[] _playbacks = [];
    private PlaybackHandle[] _playbackOrder = [];
    private int _playbackCount;
    private int _freePlayback = None;

    private Layer[] _layers = [];
    private LayerHandle[] _layerOrder = [];
    private int _layerCount;
    private int _freeLayer = None;

    private SyncGroup[] _groups = [];
    private int _groupCount;
    private int _freeGroup = None;

    private bool _disposed;

    /// <param name="skeleton">Sizes every buffer; clips must have one track per joint.</param>
    /// <param name="playbackCapacity">Playbacks to reserve storage and sampling caches for; more grow the storage when added.</param>
    public AnimationPlayer(NativeBlobAssetReference<SkeletonBlob> skeleton, int playbackCapacity = 2)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentOutOfRangeException.ThrowIfNegative(playbackCapacity);
        _skeleton = skeleton;
        _id = NextId();
        _jointCount = skeleton.Value.JointCount;
        _skeletonFingerprint = skeleton.Value.Fingerprint();
        _buffers = PlayerBuffers.Create(ref skeleton.Value);
        GrowPlaybacks(playbackCapacity);
        BaseLayer = AddLayerCore(AnimationLayerMode.Override, 1f, null);
    }

    public int JointCount => _jointCount;

    public NativeBlobAssetReference<SkeletonBlob> Skeleton => _skeleton;

    /// <summary>The bottom layer, created with the player and never removed: override, weight 1, no mask. Playbacks added without a layer play here.</summary>
    public LayerHandle BaseLayer { get; }

    /// <summary>Live playbacks in evaluation order, oldest first; read it before the next call that adds or removes one.</summary>
    public ReadOnlySpan<PlaybackHandle> Playbacks => _playbackOrder.AsSpan(0, _playbackCount);

    /// <summary>Layers bottom to top; read it before the next call that adds or removes one.</summary>
    public ReadOnlySpan<LayerHandle> Layers => _layerOrder.AsSpan(0, _layerCount);

    /// <summary>What the last <see cref="Evaluate"/> produced, one local pose per joint (the rest pose before the first); read it through this <c>ref</c>, never a copy.</summary>
    public ref JointPoses LocalPose => ref _buffers.Value.Pose;

    /// <summary>What the last <see cref="Evaluate"/> produced, one model-space matrix per joint (row-vector convention).</summary>
    public ReadOnlySpan<Matrix4x4> ModelMatrices => _buffers.Value.Models.ToSpan();

    /// <summary>Grows storage so this many playbacks, layers (the base layer included) and sync groups exist without allocating as they are added.</summary>
    public void Reserve(int playbacks, int layers = 1, int syncGroups = 0)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(playbacks);
        ArgumentOutOfRangeException.ThrowIfNegative(layers);
        ArgumentOutOfRangeException.ThrowIfNegative(syncGroups);
        GrowPlaybacks(playbacks);
        GrowLayers(layers);
        GrowGroups(syncGroups);
    }

    /// <summary>Starts <paramref name="clip"/> in the base layer at <paramref name="weight"/>, leaving every other playback as it is.</summary>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite weight, or a non-finite rate or time.</exception>
    public PlaybackHandle Add(NativeBlobAssetReference<AnimationBlob> clip, float weight = 1f, bool loop = true, float rate = 1f, float time = 0f) =>
        Add(clip, BaseLayer, weight, loop, rate, time);

    /// <summary>Starts <paramref name="clip"/> in an override layer at <paramref name="weight"/>, leaving every other playback as it is.</summary>
    /// <param name="time">Seconds into the clip: wrapped for a looping clip, clamped for a one-shot.</param>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count, the layer is additive, or its handle is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite weight, or a non-finite rate or time.</exception>
    public PlaybackHandle Add(NativeBlobAssetReference<AnimationBlob> clip, LayerHandle layer, float weight = 1f, bool loop = true, float rate = 1f, float time = 0f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var layerSlot = LayerSlot(layer, nameof(layer));
        RequireMode(layerSlot, AnimationLayerMode.Override, nameof(layer));
        CheckTracks(ref clip.Value, nameof(clip));
        CheckWeight(weight, nameof(weight));
        CheckFinite(rate, nameof(rate));
        CheckFinite(time, nameof(time));
        return AddCore(clip, null, clip.Value.Duration, layerSlot, weight, loop, rate, time);
    }

    /// <summary>Starts an additive clip in an additive layer at <paramref name="weight"/>, leaving every other playback as it is.</summary>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count, the layer overrides, or its handle is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite weight, or a non-finite rate or time.</exception>
    public PlaybackHandle Add(NativeBlobAssetReference<AdditiveAnimationBlob> clip, LayerHandle layer, float weight = 1f, bool loop = true, float rate = 1f, float time = 0f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var layerSlot = LayerSlot(layer, nameof(layer));
        RequireMode(layerSlot, AnimationLayerMode.Additive, nameof(layer));
        CheckTracks(ref clip.Value.Deltas, nameof(clip));
        CheckWeight(weight, nameof(weight));
        CheckFinite(rate, nameof(rate));
        CheckFinite(time, nameof(time));
        return AddCore(null, clip, clip.Value.Deltas.Duration, layerSlot, weight, loop, rate, time);
    }

    /// <summary>Makes <paramref name="clip"/> the base layer's only playback, cross-fading from whatever it shows now over <paramref name="fadeSeconds"/>.</summary>
    /// <remarks>See <see cref="Play(NativeBlobAssetReference{AnimationBlob}, LayerHandle, float, bool, float, float)"/>.</remarks>
    public PlaybackHandle Play(NativeBlobAssetReference<AnimationBlob> clip, float fadeSeconds = 0f, bool loop = true, float rate = 1f, float time = 0f) =>
        Play(clip, BaseLayer, fadeSeconds, loop, rate, time);

    /// <summary>Makes <paramref name="clip"/> the layer's only playback, cross-fading from whatever the layer shows now over <paramref name="fadeSeconds"/>.</summary>
    /// <remarks>The layer's other playbacks fade out from their current weights and are removed when they reach zero,
    /// all at once for a zero fade. A layer showing nothing — empty, stopped or at zero weights — fades in from the pose
    /// beneath instead, and a running <see cref="Stop(LayerHandle, float)"/> is reversed. The layer's own weight is left
    /// as set.</remarks>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count, the layer is additive, or its handle is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite fade, or a non-finite rate or time.</exception>
    public PlaybackHandle Play(NativeBlobAssetReference<AnimationBlob> clip, LayerHandle layer, float fadeSeconds = 0f, bool loop = true, float rate = 1f, float time = 0f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var layerSlot = LayerSlot(layer, nameof(layer));
        RequireMode(layerSlot, AnimationLayerMode.Override, nameof(layer));
        CheckTracks(ref clip.Value, nameof(clip));
        CheckSeconds(fadeSeconds, nameof(fadeSeconds));
        CheckFinite(rate, nameof(rate));
        CheckFinite(time, nameof(time));
        return PlayCore(clip, null, clip.Value.Duration, layerSlot, fadeSeconds, loop, rate, time);
    }

    /// <summary>Makes an additive clip the additive layer's only playback, as <see cref="Play(NativeBlobAssetReference{AnimationBlob}, LayerHandle, float, bool, float, float)"/> does.</summary>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count, the layer overrides, or its handle is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite fade, or a non-finite rate or time.</exception>
    public PlaybackHandle Play(NativeBlobAssetReference<AdditiveAnimationBlob> clip, LayerHandle layer, float fadeSeconds = 0f, bool loop = true, float rate = 1f, float time = 0f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var layerSlot = LayerSlot(layer, nameof(layer));
        RequireMode(layerSlot, AnimationLayerMode.Additive, nameof(layer));
        CheckTracks(ref clip.Value.Deltas, nameof(clip));
        CheckSeconds(fadeSeconds, nameof(fadeSeconds));
        CheckFinite(rate, nameof(rate));
        CheckFinite(time, nameof(time));
        return PlayCore(null, clip, clip.Value.Deltas.Duration, layerSlot, fadeSeconds, loop, rate, time);
    }

    /// <summary>Fades the base layer to the rest pose over <paramref name="fadeSeconds"/>, then removes its playbacks.</summary>
    public void Stop(float fadeSeconds = 0f) => Stop(BaseLayer, fadeSeconds);

    /// <summary>Fades the layer out to the pose beneath over <paramref name="fadeSeconds"/>, then removes its playbacks; a zero fade removes them at once.</summary>
    /// <remarks>The layer's weight is left as set, ready for the next <c>Play</c>. A playback added to the layer while it
    /// fades out is removed with the rest; <c>Play</c> reverses the fade instead.</remarks>
    public void Stop(LayerHandle layer, float fadeSeconds = 0f)
    {
        var slot = LayerSlot(layer, nameof(layer));
        CheckSeconds(fadeSeconds, nameof(fadeSeconds));
        ref var state = ref _layers[slot];
        if (fadeSeconds == 0f || !HasContent(slot))
        {
            MarkLayer(slot, None);
            Compact();
            state.Presence = Fade.At(1f);
            state.Stopping = false;
            return;
        }

        state.Presence.Start(0f, fadeSeconds);
        state.Stopping = true;
    }

    public bool Contains(PlaybackHandle playback)
    {
        ThrowIfDisposed();
        return playback.Player == _id && (uint)playback.Slot < (uint)_playbacks.Length
            && _playbacks[playback.Slot].Live && _playbacks[playback.Slot].Generation == playback.Generation;
    }

    /// <summary>Removes the playback at once; the pose changes by its contribution on the next <see cref="Evaluate"/>.</summary>
    public void Remove(PlaybackHandle playback) => RemoveNow(PlaybackSlot(playback, nameof(playback)));

    /// <summary>Sets the playback's weight relative to the others of its layer, ending any fade it runs.</summary>
    public void SetWeight(PlaybackHandle playback, float weight)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        CheckWeight(weight, nameof(weight));
        ref var state = ref _playbacks[slot];
        state.Weight = Fade.At(weight);
        state.RemoveWhenFaded = false;
    }

    /// <summary>Fades the playback's weight from its current value to <paramref name="weight"/> over <paramref name="seconds"/> of elapsed time, replacing any fade it runs.</summary>
    public void FadeWeight(PlaybackHandle playback, float weight, float seconds)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        CheckWeight(weight, nameof(weight));
        CheckSeconds(seconds, nameof(seconds));
        ref var state = ref _playbacks[slot];
        state.Weight.Start(weight, seconds);
        state.RemoveWhenFaded = false;
    }

    /// <summary>Fades the playback to weight 0 over <paramref name="seconds"/>, then removes it; a zero duration removes it at once.</summary>
    /// <remarks>Within its layer weights are relative, so a lone playback holds its pose until removed; <see cref="Stop(LayerHandle, float)"/> fades a whole layer out.</remarks>
    public void FadeOut(PlaybackHandle playback, float seconds)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        CheckSeconds(seconds, nameof(seconds));
        if (seconds == 0f)
        {
            RemoveNow(slot);
            return;
        }

        ref var state = ref _playbacks[slot];
        state.Weight.Start(0f, seconds);
        state.RemoveWhenFaded = true;
    }

    /// <summary>Fades <paramref name="target"/> to weight 1 and every other playback of its layer to 0 over <paramref name="seconds"/>, from the weights they have now.</summary>
    /// <remarks>The layer's weights and running fades are first rescaled to sum to 1, which leaves the pose unchanged, so
    /// an uninterrupted cross-fade moves linearly from the current mix to the target, and an interruption continues from
    /// wherever it is. A playback already fading out keeps its schedule, so interruptions never postpone its end: it
    /// reaches zero within one fade of leaving however often cross-fades restart. Playbacks faded out stay at weight 0
    /// unless a <see cref="FadeOut"/> or <c>Play</c> scheduled their removal. A layer showing nothing switches to the
    /// target at once; <c>Play</c> fades such a layer in from the pose beneath.</remarks>
    public void CrossFade(PlaybackHandle target, float seconds)
    {
        var slot = PlaybackSlot(target, nameof(target));
        CheckSeconds(seconds, nameof(seconds));
        CrossFadeCore(slot, seconds, removeOthers: false);
    }

    /// <summary>Moves the playback to <paramref name="time"/> seconds, wrapped for a looping clip and clamped for a one-shot; it takes no time step.</summary>
    /// <exception cref="InvalidOperationException">A sync group drives the playback; set the group's phase instead.</exception>
    public void Seek(PlaybackHandle playback, float time)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        CheckFinite(time, nameof(time));
        ref var state = ref _playbacks[slot];
        if (state.Group != None) throw new InvalidOperationException("A sync group drives this playback's clock; set the group's phase instead.");
        state.Time = Place(time, state.Duration, state.Loop);
    }

    /// <summary>Sets the playback's rate; negative plays backwards. A sync group's rate drives a member; its own applies again once it leaves.</summary>
    public void SetRate(PlaybackHandle playback, float rate)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        CheckFinite(rate, nameof(rate));
        _playbacks[slot].Rate = rate;
    }

    /// <summary>Stops the playback's clock; its weight still fades.</summary>
    /// <exception cref="InvalidOperationException">A sync group drives the playback; set the group's rate instead.</exception>
    public void Pause(PlaybackHandle playback)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        ref var state = ref _playbacks[slot];
        if (state.Group != None) throw new InvalidOperationException("A sync group drives this playback's clock; set the group's rate instead.");
        state.Paused = true;
    }

    public void Resume(PlaybackHandle playback) => _playbacks[PlaybackSlot(playback, nameof(playback))].Paused = false;

    public PlaybackState GetState(PlaybackHandle playback)
    {
        ref var state = ref _playbacks[PlaybackSlot(playback, nameof(playback))];
        var finished = !state.Loop && (state.Rate >= 0f ? state.Time >= state.Duration : state.Time <= 0f);
        return new PlaybackState(
            new LayerHandle(_id, state.Layer, _layers[state.Layer].Generation),
            state.Group == None ? default : new SyncGroupHandle(_id, state.Group, _groups[state.Group].Generation),
            state.Weight.Value, state.Weight.Target, state.Weight.IsActive, state.RemoveWhenFaded,
            state.Time, state.Duration, state.Rate, state.Loop, state.Paused, finished);
    }

    /// <summary>Adds a layer on top of the others.</summary>
    /// <param name="weight">How much of the layer shows over the pose beneath, in 0..1.</param>
    /// <param name="mask">Scales the weight per joint; null applies it to every joint.</param>
    /// <exception cref="ArgumentException">The mask was made for another skeleton.</exception>
    public LayerHandle AddLayer(AnimationLayerMode mode, float weight = 1f, JointMask? mask = null)
    {
        ThrowIfDisposed();
        if (mode is not (AnimationLayerMode.Override or AnimationLayerMode.Additive)) throw new ArgumentOutOfRangeException(nameof(mode), mode, "A layer overrides or adds.");
        CheckLayerWeight(weight, nameof(weight));
        CheckMask(mask, nameof(mask));
        return AddLayerCore(mode, weight, mask);
    }

    public bool Contains(LayerHandle layer)
    {
        ThrowIfDisposed();
        return layer.Player == _id && (uint)layer.Slot < (uint)_layers.Length
            && _layers[layer.Slot].Live && _layers[layer.Slot].Generation == layer.Generation;
    }

    /// <summary>Removes the layer and its playbacks at once.</summary>
    /// <exception cref="InvalidOperationException">The base layer, which stays for the player's life.</exception>
    public void Remove(LayerHandle layer)
    {
        var slot = LayerSlot(layer, nameof(layer));
        if (layer == BaseLayer) throw new InvalidOperationException("The base layer stays for the player's life.");
        MarkLayer(slot, None);
        Compact();
        var index = _layerOrder.AsSpan(0, _layerCount).IndexOf(layer);
        Array.Copy(_layerOrder, index + 1, _layerOrder, index, _layerCount - index - 1);
        _layerOrder[--_layerCount] = default;
        ref var state = ref _layers[slot];
        state = default(Layer) with { Generation = state.Generation + 1, NextFree = _freeLayer };
        _freeLayer = slot;
    }

    /// <summary>Sets how much of the layer shows over the pose beneath, in 0..1, ending any weight fade it runs.</summary>
    public void SetWeight(LayerHandle layer, float weight)
    {
        var slot = LayerSlot(layer, nameof(layer));
        CheckLayerWeight(weight, nameof(weight));
        _layers[slot].Weight = Fade.At(weight);
    }

    /// <summary>Fades the layer's weight from its current value to <paramref name="weight"/> in 0..1 over <paramref name="seconds"/>, replacing any weight fade it runs.</summary>
    public void FadeWeight(LayerHandle layer, float weight, float seconds)
    {
        var slot = LayerSlot(layer, nameof(layer));
        CheckLayerWeight(weight, nameof(weight));
        CheckSeconds(seconds, nameof(seconds));
        _layers[slot].Weight.Start(weight, seconds);
    }

    /// <summary>Scales the layer's weight per joint by <paramref name="mask"/>; null applies it to every joint.</summary>
    /// <exception cref="ArgumentException">The mask was made for another skeleton.</exception>
    public void SetMask(LayerHandle layer, JointMask? mask)
    {
        var slot = LayerSlot(layer, nameof(layer));
        CheckMask(mask, nameof(mask));
        _layers[slot].Mask = mask;
    }

    public LayerState GetState(LayerHandle layer)
    {
        ref var state = ref _layers[LayerSlot(layer, nameof(layer))];
        return new LayerState(state.Mode, state.Weight.Value, state.Weight.Target, state.Weight.IsActive || state.Presence.IsActive,
            state.EffectiveWeight, state.Stopping, state.Mask);
    }

    /// <summary>Adds a group whose members follow one normalized phase, advancing at <paramref name="rate"/> cycles of their weighted mean duration per cycle.</summary>
    /// <remarks>Each <see cref="Advance"/> moves the phase by the elapsed time over the members' durations averaged by their
    /// playback weights; at zero total weight the phase holds. Members sample at that phase of their own clips, so a
    /// walk and a run of different lengths keep their steps aligned whatever their weights — provided their cycles start
    /// at matching contacts.</remarks>
    public SyncGroupHandle AddSyncGroup(float rate = 1f)
    {
        ThrowIfDisposed();
        CheckFinite(rate, nameof(rate));
        if (_freeGroup == None) GrowGroups(Math.Max(2, _groups.Length * 2));
        var slot = _freeGroup;
        ref var group = ref _groups[slot];
        _freeGroup = group.NextFree;
        group.NextFree = None;
        group.Live = true;
        group.Phase = 0f;
        group.Rate = rate;
        group.Members = 0;
        _groupCount++;
        return new SyncGroupHandle(_id, slot, group.Generation);
    }

    public bool Contains(SyncGroupHandle group)
    {
        ThrowIfDisposed();
        return group.Player == _id && (uint)group.Slot < (uint)_groups.Length
            && _groups[group.Slot].Live && _groups[group.Slot].Generation == group.Generation;
    }

    /// <summary>Removes the group; its members keep their times and continue on their own clocks.</summary>
    public void Remove(SyncGroupHandle group)
    {
        var slot = GroupSlot(group, nameof(group));
        for (var i = 0; i < _playbackCount; i++)
        {
            ref var playback = ref _playbacks[_playbackOrder[i].Slot];
            if (playback.Group == slot) playback.Group = None;
        }

        ref var state = ref _groups[slot];
        state = default(SyncGroup) with { Generation = state.Generation + 1, NextFree = _freeGroup };
        _freeGroup = slot;
        _groupCount--;
    }

    /// <summary>Sets the group's rate in cycles per cycle duration; negative plays its members backwards.</summary>
    public void SetRate(SyncGroupHandle group, float rate)
    {
        var slot = GroupSlot(group, nameof(group));
        CheckFinite(rate, nameof(rate));
        _groups[slot].Rate = rate;
    }

    /// <summary>Moves the group to <paramref name="phase"/>, wrapped into 0..1, and its members with it; it takes no time step.</summary>
    public void SetPhase(SyncGroupHandle group, float phase)
    {
        var slot = GroupSlot(group, nameof(group));
        CheckFinite(phase, nameof(phase));
        _groups[slot].Phase = Wrap(phase);
        for (var i = 0; i < _playbackCount; i++)
        {
            ref var playback = ref _playbacks[_playbackOrder[i].Slot];
            if (playback.Group == slot) playback.Time = _groups[slot].Phase * playback.Duration;
        }
    }

    /// <summary>Drives the playback's clock from the group: it jumps to the group's phase, or an empty group takes the playback's.</summary>
    /// <exception cref="InvalidOperationException">A one-shot or paused playback, which keeps its own clock.</exception>
    public void Synchronize(PlaybackHandle playback, SyncGroupHandle group)
    {
        var slot = PlaybackSlot(playback, nameof(playback));
        var groupSlot = GroupSlot(group, nameof(group));
        ref var state = ref _playbacks[slot];
        if (!state.Loop) throw new InvalidOperationException("Only a looping playback joins a sync group; a one-shot keeps its own clock.");
        if (state.Paused) throw new InvalidOperationException("A paused playback cannot join a sync group; resume it first.");
        if (state.Group == groupSlot) return;
        if (state.Group != None) _groups[state.Group].Members--;
        ref var target = ref _groups[groupSlot];
        if (target.Members == 0) target.Phase = Wrap(state.Time / state.Duration);
        else state.Time = target.Phase * state.Duration;
        state.Group = groupSlot;
        target.Members++;
    }

    /// <summary>Returns the playback to its own clock and rate from where the group left it.</summary>
    public void Desynchronize(PlaybackHandle playback)
    {
        ref var state = ref _playbacks[PlaybackSlot(playback, nameof(playback))];
        if (state.Group == None) return;
        _groups[state.Group].Members--;
        state.Group = None;
    }

    public SyncGroupState GetState(SyncGroupHandle group)
    {
        ref var state = ref _groups[GroupSlot(group, nameof(group))];
        return new SyncGroupState(state.Phase, state.Rate, state.Members);
    }

    /// <summary>Moves every clock by <paramref name="deltaSeconds"/> at its rate — a looping clip wraps, a one-shot clamps — and runs every fade by the same elapsed time, whatever the rates.</summary>
    /// <remarks>Sync groups advance by the weights at the start of the step. Fades that end at zero remove the playbacks they were told to.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite step.</exception>
    public void Advance(float deltaSeconds)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds), deltaSeconds, "Elapsed time is finite and non-negative.");
        if (deltaSeconds == 0f) return;

        AdvanceGroups(deltaSeconds);
        var removals = false;
        for (var i = 0; i < _playbackCount; i++)
        {
            ref var playback = ref _playbacks[_playbackOrder[i].Slot];
            if (playback.Group != None) playback.Time = _groups[playback.Group].Phase * playback.Duration;
            else if (!playback.Paused) playback.Time = Place(playback.Time + deltaSeconds * playback.Rate, playback.Duration, playback.Loop);

            if (playback.Weight.Step(deltaSeconds) && playback.RemoveWhenFaded && playback.Weight.Value == 0f)
            {
                playback.Removing = true;
                removals = true;
            }
        }

        for (var i = 0; i < _layerCount; i++)
        {
            var slot = _layerOrder[i].Slot;
            ref var layer = ref _layers[slot];
            layer.Weight.Step(deltaSeconds);
            if (layer.Presence.Step(deltaSeconds) && layer.Stopping && layer.Presence.Value == 0f)
            {
                MarkLayer(slot, None);
                layer.Presence = Fade.At(1f);
                layer.Stopping = false;
                removals = true;
            }
        }

        if (removals) Compact();
    }

    /// <summary>Samples every contributing playback at its time, composes the layers over the rest pose into <see cref="LocalPose"/> and walks the hierarchy into <see cref="ModelMatrices"/>.</summary>
    /// <remarks>Takes no time step: evaluating twice gives the same pose. Layers beneath the topmost unmasked override at
    /// full weight are not sampled, since it hides them.</remarks>
    public void Evaluate()
    {
        ThrowIfDisposed();
        ref var buffers = ref _buffers.Value;
        var posed = false;
        for (var i = OpaqueLayer(); i < _layerCount; i++)
        {
            var slot = _layerOrder[i].Slot;
            ref var layer = ref _layers[slot];
            var weight = layer.EffectiveWeight;
            if (!(weight > 0f)) continue;
            var count = Contributors(slot, out var last);
            if (count == 0) continue;

            if (layer.Mode == AnimationLayerMode.Override && weight >= 1f && layer.Mask is null)
            {
                Mix(slot, count, last, ref buffers.Rest, ref buffers.Pose, ref buffers);
                posed = true;
                continue;
            }

            var additive = layer.Mode == AnimationLayerMode.Additive;
            ref var fallback = ref additive ? ref buffers.Identity : ref buffers.Rest;
            Mix(slot, count, last, ref fallback, ref buffers.Mix, ref buffers);
            if (!posed)
            {
                buffers.Pose.CopyFrom(ref buffers.Rest);
                posed = true;
            }

            if (additive) JointPoses.ApplyAdditive(ref buffers.Pose, ref buffers.Mix, weight, layer.Mask, ref buffers.Pose);
            else JointPoses.Blend(ref buffers.Pose, ref buffers.Mix, weight, layer.Mask, ref buffers.Pose);
        }

        if (!posed) buffers.Pose.CopyFrom(ref buffers.Rest);
        LocalToModel.Compute(ref _skeleton.Value, ref buffers.Pose, buffers.Models.ToSpan());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffers.Dispose();
        foreach (var playback in _playbacks) playback.Context?.Dispose();
    }

    private PlaybackHandle AddCore(NativeBlobAssetReference<AnimationBlob>? clip, NativeBlobAssetReference<AdditiveAnimationBlob>? additive, float duration, int layer, float weight, bool loop, float rate, float time)
    {
        if (_freePlayback == None) GrowPlaybacks(Math.Max(4, _playbacks.Length * 2));
        var slot = _freePlayback;
        ref var playback = ref _playbacks[slot];
        _freePlayback = playback.NextFree;
        playback.NextFree = None;
        playback.Live = true;
        playback.Clip = clip;
        playback.Additive = additive;
        playback.Layer = layer;
        playback.Group = None;
        playback.Duration = duration;
        playback.Loop = loop;
        playback.Rate = rate;
        playback.Time = Place(time, duration, loop);
        playback.Paused = false;
        playback.RemoveWhenFaded = false;
        playback.Removing = false;
        playback.Weight = Fade.At(weight);
        playback.Context.Value.Invalidate();
        var handle = new PlaybackHandle(_id, slot, playback.Generation);
        _playbackOrder[_playbackCount++] = handle;
        return handle;
    }

    private PlaybackHandle PlayCore(NativeBlobAssetReference<AnimationBlob>? clip, NativeBlobAssetReference<AdditiveAnimationBlob>? additive, float duration, int layer, float fadeSeconds, bool loop, float rate, float time)
    {
        var showed = HasContent(layer);
        var handle = AddCore(clip, additive, duration, layer, fadeSeconds > 0f ? 0f : 1f, loop, rate, time);
        ref var state = ref _layers[layer];
        state.Stopping = false;
        if (fadeSeconds == 0f)
        {
            MarkLayer(layer, handle.Slot);
            Compact();
            state.Presence = Fade.At(1f);
            return handle;
        }

        CrossFadeCore(handle.Slot, fadeSeconds, removeOthers: true);
        if (!showed)
        {
            // Relative weights cannot fade in from nothing, so the layer's presence carries the fade.
            state.Presence = Fade.At(0f);
            state.Presence.Start(1f, fadeSeconds);
        }
        else if (state.Presence.Value < 1f || state.Presence.Target < 1f)
        {
            state.Presence.Start(1f, fadeSeconds);
        }

        return handle;
    }

    private void CrossFadeCore(int target, float seconds, bool removeOthers)
    {
        var layer = _playbacks[target].Layer;
        var sum = 0d;
        for (var i = 0; i < _playbackCount; i++)
        {
            ref var playback = ref _playbacks[_playbackOrder[i].Slot];
            if (playback.Layer == layer) sum += playback.Weight.Value;
        }

        var scale = sum > 0d ? (float)(1d / sum) : 1f;
        var removals = false;
        for (var i = 0; i < _playbackCount; i++)
        {
            var slot = _playbackOrder[i].Slot;
            ref var playback = ref _playbacks[slot];
            if (playback.Layer != layer) continue;
            // One factor for every weight and running schedule leaves the pose as it is.
            playback.Weight.Scale(scale);
            if (slot == target)
            {
                playback.Weight.Start(1f, seconds);
                playback.RemoveWhenFaded = false;
                continue;
            }

            // Restarting a fade-out from its current weight would halve it at every interruption without ever
            // reaching zero, keeping every outgoing playback alive while cross-fades keep coming.
            if (seconds == 0f || !playback.Weight.IsFadingOut) playback.Weight.Start(0f, seconds);
            playback.RemoveWhenFaded |= removeOthers;
            if (playback.RemoveWhenFaded && playback.Weight.Value == 0f)
            {
                playback.Removing = true;
                removals = true;
            }
        }

        if (removals) Compact();
    }

    private LayerHandle AddLayerCore(AnimationLayerMode mode, float weight, JointMask? mask)
    {
        if (_freeLayer == None) GrowLayers(Math.Max(2, _layers.Length * 2));
        var slot = _freeLayer;
        ref var layer = ref _layers[slot];
        _freeLayer = layer.NextFree;
        layer.NextFree = None;
        layer.Live = true;
        layer.Mode = mode;
        layer.Mask = mask;
        layer.Weight = Fade.At(weight);
        layer.Presence = Fade.At(1f);
        layer.Stopping = false;
        var handle = new LayerHandle(_id, slot, layer.Generation);
        _layerOrder[_layerCount++] = handle;
        return handle;
    }

    /// <summary>Whether any playback of the layer has a positive weight, so the layer shows something at its weight.</summary>
    private bool HasContent(int layer) => Contributors(layer, out _) > 0;

    private int Contributors(int layer, out int last)
    {
        var count = 0;
        last = None;
        for (var i = 0; i < _playbackCount; i++)
        {
            var slot = _playbackOrder[i].Slot;
            ref var playback = ref _playbacks[slot];
            if (playback.Layer != layer || !(playback.Weight.Value > 0f)) continue;
            count++;
            last = slot;
        }

        return count;
    }

    /// <summary>The topmost layer that replaces every joint — an unmasked override at full weight with something to show — below which nothing shows; 0 when there is none.</summary>
    private int OpaqueLayer()
    {
        for (var i = _layerCount - 1; i > 0; i--)
        {
            var slot = _layerOrder[i].Slot;
            ref var layer = ref _layers[slot];
            if (layer.Mode == AnimationLayerMode.Override && layer.EffectiveWeight >= 1f && layer.Mask is null && HasContent(slot)) return i;
        }

        return 0;
    }

    /// <summary>The layer's playbacks blended by weight into <paramref name="target"/>: one is sampled straight in, two take one lerp, more accumulate.</summary>
    private void Mix(int layer, int count, int last, ref JointPoses fallback, ref JointPoses target, ref PlayerBuffers buffers)
    {
        if (count == 1)
        {
            Sample(last, ref target);
            return;
        }

        if (count == 2)
        {
            var first = None;
            for (var i = 0; i < _playbackCount && first == None; i++)
            {
                var slot = _playbackOrder[i].Slot;
                if (slot != last && _playbacks[slot].Layer == layer && _playbacks[slot].Weight.Value > 0f) first = slot;
            }

            var a = _playbacks[first].Weight.Value;
            var b = _playbacks[last].Weight.Value;
            var largest = MathF.Max(a, b);
            Sample(first, ref target);
            Sample(last, ref buffers.Scratch);
            JointPoses.Blend(ref target, ref buffers.Scratch, b / largest / (a / largest + b / largest), ref target);
            return;
        }

        ref var blender = ref buffers.Blender;
        blender.Reset();
        for (var i = 0; i < _playbackCount; i++)
        {
            var slot = _playbackOrder[i].Slot;
            ref var playback = ref _playbacks[slot];
            if (playback.Layer != layer || !(playback.Weight.Value > 0f)) continue;
            Sample(slot, ref buffers.Scratch);
            blender.Add(ref buffers.Scratch, playback.Weight.Value);
        }

        blender.Resolve(ref fallback, ref target);
    }

    private void Sample(int slot, ref JointPoses output)
    {
        ref var playback = ref _playbacks[slot];
        ref var clip = ref playback.Clip is { } absolute ? ref absolute.Value : ref playback.Additive!.Value.Deltas;
        playback.Context.Value.Sample(ref clip, playback.Duration > 0f ? playback.Time / playback.Duration : 0f, ref output);
    }

    private void AdvanceGroups(float seconds)
    {
        if (_groupCount == 0) return;
        for (var g = 0; g < _groups.Length; g++)
        {
            _groups[g].WeightSum = 0d;
            _groups[g].WeightedDuration = 0d;
        }

        for (var i = 0; i < _playbackCount; i++)
        {
            ref var playback = ref _playbacks[_playbackOrder[i].Slot];
            if (playback.Group == None) continue;
            ref var group = ref _groups[playback.Group];
            group.WeightSum += playback.Weight.Value;
            group.WeightedDuration += (double)playback.Weight.Value * playback.Duration;
        }

        for (var g = 0; g < _groups.Length; g++)
        {
            ref var group = ref _groups[g];
            if (!group.Live || !(group.WeightSum > 0d) || group.Rate == 0f) continue;
            var cycle = group.WeightedDuration / group.WeightSum;
            group.Phase = Wrap(group.Phase + (float)(seconds * group.Rate / cycle));
        }
    }

    /// <summary>Marks every playback of the layer but <paramref name="except"/> for <see cref="Compact"/>.</summary>
    private void MarkLayer(int layer, int except)
    {
        for (var i = 0; i < _playbackCount; i++)
        {
            var slot = _playbackOrder[i].Slot;
            if (slot != except && _playbacks[slot].Layer == layer) _playbacks[slot].Removing = true;
        }
    }

    private void RemoveNow(int slot)
    {
        _playbacks[slot].Removing = true;
        Compact();
    }

    /// <summary>Releases every playback marked for removal and closes the gaps in the evaluation order.</summary>
    private void Compact()
    {
        var kept = 0;
        for (var i = 0; i < _playbackCount; i++)
        {
            var handle = _playbackOrder[i];
            ref var playback = ref _playbacks[handle.Slot];
            if (!playback.Removing)
            {
                _playbackOrder[kept++] = handle;
                continue;
            }

            if (playback.Group != None) _groups[playback.Group].Members--;
            playback.Live = false;
            playback.Removing = false;
            playback.Clip = null;
            playback.Additive = null;
            playback.Group = None;
            playback.Generation++;
            playback.NextFree = _freePlayback;
            _freePlayback = handle.Slot;
        }

        _playbackOrder.AsSpan(kept, _playbackCount - kept).Clear();
        _playbackCount = kept;
    }

    private void GrowPlaybacks(int capacity)
    {
        var old = _playbacks.Length;
        if (capacity <= old) return;
        Array.Resize(ref _playbacks, capacity);
        Array.Resize(ref _playbackOrder, capacity);
        // Every slot owns its sampling cache for the player's life, so growth moves no blob and a reused slot allocates nothing.
        for (var slot = capacity - 1; slot >= old; slot--)
        {
            ref var playback = ref _playbacks[slot];
            playback.Context = SamplingContext.Create(_jointCount);
            playback.Group = None;
            playback.NextFree = _freePlayback;
            _freePlayback = slot;
        }
    }

    private void GrowLayers(int capacity)
    {
        var old = _layers.Length;
        if (capacity <= old) return;
        Array.Resize(ref _layers, capacity);
        Array.Resize(ref _layerOrder, capacity);
        for (var slot = capacity - 1; slot >= old; slot--)
        {
            _layers[slot].NextFree = _freeLayer;
            _freeLayer = slot;
        }
    }

    private void GrowGroups(int capacity)
    {
        var old = _groups.Length;
        if (capacity <= old) return;
        Array.Resize(ref _groups, capacity);
        for (var slot = capacity - 1; slot >= old; slot--)
        {
            _groups[slot].NextFree = _freeGroup;
            _freeGroup = slot;
        }
    }

    private int PlaybackSlot(PlaybackHandle handle, string parameter)
    {
        ThrowIfDisposed();
        if (handle.Player != _id) throw new ArgumentException(handle.Player == 0 ? "The playback handle is default." : "The playback belongs to another player.", parameter);
        var slot = handle.Slot;
        if ((uint)slot >= (uint)_playbacks.Length || !_playbacks[slot].Live || _playbacks[slot].Generation != handle.Generation)
        {
            throw new ArgumentException("The playback was removed.", parameter);
        }

        return slot;
    }

    private int LayerSlot(LayerHandle handle, string parameter)
    {
        ThrowIfDisposed();
        if (handle.Player != _id) throw new ArgumentException(handle.Player == 0 ? "The layer handle is default." : "The layer belongs to another player.", parameter);
        var slot = handle.Slot;
        if ((uint)slot >= (uint)_layers.Length || !_layers[slot].Live || _layers[slot].Generation != handle.Generation)
        {
            throw new ArgumentException("The layer was removed.", parameter);
        }

        return slot;
    }

    private int GroupSlot(SyncGroupHandle handle, string parameter)
    {
        ThrowIfDisposed();
        if (handle.Player != _id) throw new ArgumentException(handle.Player == 0 ? "The sync group handle is default." : "The sync group belongs to another player.", parameter);
        var slot = handle.Slot;
        if ((uint)slot >= (uint)_groups.Length || !_groups[slot].Live || _groups[slot].Generation != handle.Generation)
        {
            throw new ArgumentException("The sync group was removed.", parameter);
        }

        return slot;
    }

    private void RequireMode(int layer, AnimationLayerMode mode, string parameter)
    {
        if (_layers[layer].Mode == mode) return;
        throw new ArgumentException(mode == AnimationLayerMode.Additive
            ? "An additive clip plays in an additive layer."
            : "An additive layer plays additive clips; build one with AdditiveAnimationBuilder.", parameter);
    }

    private void CheckTracks(ref AnimationBlob clip, string parameter)
    {
        if (clip.TrackCount != _jointCount)
        {
            throw new ArgumentException($"The clip '{clip.Name.ToString()}' has {clip.TrackCount} tracks; the skeleton has {_jointCount} joints.", parameter);
        }
    }

    private void CheckMask(JointMask? mask, string parameter)
    {
        if (mask is not null && (mask.JointCount != _jointCount || mask.SkeletonFingerprint != _skeletonFingerprint))
        {
            throw new ArgumentException("The mask was made for another skeleton.", parameter);
        }
    }

    private static void CheckWeight(float weight, string parameter)
    {
        if (!float.IsFinite(weight) || weight < 0f) throw new ArgumentOutOfRangeException(parameter, weight, "A playback weight is finite and non-negative.");
    }

    private static void CheckLayerWeight(float weight, string parameter)
    {
        if (!(weight >= 0f && weight <= 1f)) throw new ArgumentOutOfRangeException(parameter, weight, "A layer weight is in 0..1.");
    }

    private static void CheckSeconds(float seconds, string parameter)
    {
        if (!float.IsFinite(seconds) || seconds < 0f) throw new ArgumentOutOfRangeException(parameter, seconds, "A fade lasts a finite, non-negative number of seconds.");
    }

    private static void CheckFinite(float value, string parameter)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameter, value, "The value is not finite.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>A time within the clip: wrapped into 0..duration for a looping clip, clamped for a one-shot.</summary>
    private static float Place(float time, float duration, bool loop)
    {
        if (!(duration > 0f)) return 0f;
        if (!loop) return Math.Clamp(time, 0f, duration);
        var wrapped = time - MathF.Floor(time / duration) * duration;
        // Accept the in-range case rather than reject the out-of-range ones: a step that overflowed to infinity
        // wraps to NaN, which fails every comparison and must fall back to 0 as well.
        return wrapped >= 0f && wrapped < duration ? wrapped : 0f;
    }

    /// <summary>A phase within 0..1; like <see cref="Place"/>, the NaN an overflowed step wraps to falls back to 0.</summary>
    private static float Wrap(float phase)
    {
        var wrapped = phase - MathF.Floor(phase);
        return wrapped >= 0f && wrapped < 1f ? wrapped : 0f;
    }

    private static int NextId()
    {
        int id;
        do id = Interlocked.Increment(ref s_nextId);
        while (id == 0);
        return id;
    }

    /// <summary>A value that moves linearly toward a target over elapsed seconds, then holds.</summary>
    private struct Fade
    {
        public float Value;
        private float _from;
        private float _to;
        private float _elapsed;
        private float _duration;

        public readonly bool IsActive => _duration > 0f;

        public readonly bool IsFadingOut => _duration > 0f && _to == 0f;

        public readonly float Target => _duration > 0f ? _to : Value;

        public static Fade At(float value) => new() { Value = value };

        /// <summary>Multiplies the value and the running schedule by <paramref name="factor"/>.</summary>
        public void Scale(float factor)
        {
            Value *= factor;
            _from *= factor;
            _to *= factor;
        }

        /// <summary>Moves from the current value; a zero duration arrives at once.</summary>
        public void Start(float target, float seconds)
        {
            if (seconds <= 0f)
            {
                this = At(target);
                return;
            }

            _from = Value;
            _to = target;
            _elapsed = 0f;
            _duration = seconds;
        }

        /// <returns>Whether the fade ended in this step.</returns>
        public bool Step(float seconds)
        {
            if (_duration <= 0f) return false;
            _elapsed += seconds;
            if (_elapsed >= _duration)
            {
                this = At(_to);
                return true;
            }

            Value = _from + (_to - _from) * (_elapsed / _duration);
            return false;
        }
    }

    private struct Playback
    {
        public NativeBlobAssetReference<SamplingContext> Context;
        public NativeBlobAssetReference<AnimationBlob>? Clip;
        public NativeBlobAssetReference<AdditiveAnimationBlob>? Additive;
        public int Generation;
        public int NextFree;
        public bool Live;
        public bool Removing;
        public int Layer;
        public int Group;
        public float Duration;
        public float Time;
        public float Rate;
        public bool Loop;
        public bool Paused;
        public bool RemoveWhenFaded;
        public Fade Weight;
    }

    private struct Layer
    {
        public int Generation;
        public int NextFree;
        public bool Live;
        public AnimationLayerMode Mode;
        public JointMask? Mask;
        public Fade Weight;

        /// <summary>The <c>Play</c>/<c>Stop</c> fade, separate from the weight the owner sets; 1 when neither runs.</summary>
        public Fade Presence;

        public bool Stopping;

        public readonly float EffectiveWeight => Weight.Value * Presence.Value;
    }

    private struct SyncGroup
    {
        public int Generation;
        public int NextFree;
        public bool Live;
        public float Phase;
        public float Rate;
        public int Members;
        public double WeightSum;
        public double WeightedDuration;
    }
}

/// <summary>The player's fixed-size working set in one native blob: the output pose and matrices, the rest and identity fallbacks, and scratch for sampling and blending.</summary>
/// <remarks>Sized by the skeleton alone, so adding playbacks never moves it. Per-playback sampling caches are separate blobs.</remarks>
internal struct PlayerBuffers
{
    public JointPoses Pose;
    public JointPoses Mix;
    public JointPoses Scratch;
    public JointPoses Rest;
    public JointPoses Identity;
    public PoseBlender Blender;
    public BlobArray<Matrix4x4> Models;

    public static NativeBlobAssetReference<PlayerBuffers> Create(ref SkeletonBlob skeleton)
    {
        var jointCount = skeleton.JointCount;
        var builder = new StructBuilder<PlayerBuffers>();
        JointPoses.Set(builder, ref builder.Value.Pose, jointCount);
        JointPoses.Set(builder, ref builder.Value.Mix, jointCount);
        JointPoses.Set(builder, ref builder.Value.Scratch, jointCount);
        JointPoses.Set(builder, ref builder.Value.Rest, jointCount);
        JointPoses.Set(builder, ref builder.Value.Identity, jointCount);
        PoseBlender.Set(builder, ref builder.Value.Blender, jointCount);
        builder.SetArray(ref builder.Value.Models, new Matrix4x4[jointCount], alignment: 16);
        var buffers = builder.CreateNativeBlobAssetReference();
        ref var value = ref buffers.Value;
        value.Rest.CopyFrom(skeleton.RestPoses.ToSpan());
        value.Pose.CopyFrom(ref value.Rest);
        LocalToModel.Compute(ref skeleton, ref value.Pose, value.Models.ToSpan());
        return buffers;
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
