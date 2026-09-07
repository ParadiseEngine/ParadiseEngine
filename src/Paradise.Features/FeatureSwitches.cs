using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Paradise.Features;

/// <summary>The engine's feature configuration: the features a build declares and the switch on
/// each one. One object per process, handed to every subsystem that has something to switch.
///
/// <para><b>Declarations and switches are ONE object deliberately.</b> A switch means nothing
/// without the declaration that gives it a default and a description, and a declaration nobody
/// can flip is documentation. Splitting them only creates a pair that must be passed together
/// and can be passed mismatched.</para>
///
/// <para><b>Order does not matter.</b> Overrides are applied by NAME and kept whether or not the
/// feature has been declared yet, because a config file is read at startup and the subsystems
/// that own the features are constructed after it. A name no declaration ever claims stays
/// visible in <see cref="Unknown"/> instead of disappearing, so a typo in a config file does not
/// read as a feature that is simply off.</para>
///
/// <para>Reads are lock-free and safe from any thread — a render thread asks per frame while a
/// debug panel flips a switch on another. Writes are serialized against each other, and
/// <see cref="Changed"/> is raised inside that same critical section on the thread that made the
/// change, so the last announcement always describes the state everyone can now read (see
/// <c>_writeLock</c>, and <c>Paradise.Features.CoyoteTest</c> for the interleavings that
/// pins).</para></summary>
public sealed class FeatureSwitches : IFeatureSwitches
{
    private readonly ConcurrentDictionary<FeatureId, FeatureDefinition> _declared = new();
    private readonly ConcurrentDictionary<string, bool> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FeatureSettings> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes WRITES, and holds while <see cref="Changed"/> is raised. Reads never
    /// take it — a render thread asking per frame must not queue behind a debug panel.
    ///
    /// <para><b>The event is raised inside it on purpose.</b> Deciding "did this change?" and
    /// announcing it are one step: two threads setting the same feature to different values
    /// would otherwise both decide, then announce in whichever order they were scheduled, and the
    /// LAST announcement could contradict the state everybody now reads. A feature that acts on
    /// the announcement — releasing a target, retracting a plan — would be left disagreeing with
    /// its own switch, which is precisely the class of bug
    /// <c>IRenderFeature.OnEnabledChanged</c> exists to prevent. Handlers therefore run under the
    /// lock; <c>Monitor</c> is reentrant, so a handler that flips another switch nests rather
    /// than deadlocks.</para>
    ///
    /// <para>An <c>object</c> rather than a <c>System.Threading.Lock</c>: Coyote (1.7.11) rewrites
    /// <c>Monitor.Enter</c>/<c>Exit</c> and cannot intercept <c>Lock.EnterScope</c>, so the newer
    /// type would make the interleavings around this lock invisible to
    /// <c>Paradise.Features.CoyoteTest</c>.</para></summary>
    private readonly object _writeLock = new();

    /// <summary>An empty configuration: nothing declared, nothing overridden.</summary>
    public FeatureSwitches()
    {
    }

    /// <summary>A configuration seeded with <paramref name="overrides"/> — the config file, the
    /// environment and the command line, already merged.</summary>
    public FeatureSwitches(FeatureOverrides overrides) => Apply(overrides);

    /// <summary>A configuration seeded with a whole document: its switches AND what each feature
    /// is configured with.</summary>
    public FeatureSwitches(EngineConfiguration configuration) => Apply(configuration);

    /// <inheritdoc/>
    public event Action<FeatureId, bool>? Changed;

    /// <inheritdoc/>
    public event Action<FeatureId, FeatureSettings>? SettingsChanged;

    /// <inheritdoc/>
    public IReadOnlyCollection<FeatureDefinition> Definitions => _declared.Values.ToArray();

    /// <summary>The overridden names no declaration claims: a feature from another build, a
    /// feature that was removed, or a typo. Reported rather than thrown, because only the host
    /// knows whether a stale line in its config file is worth failing over — and reported rather
    /// than logged, so this assembly needs no logging dependency to say it.</summary>
    public IReadOnlyCollection<string> Unknown =>
        _overrides.Keys.Concat(_settings.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !FeatureId.TryParse(name, out var id) || !_declared.ContainsKey(id))
            .ToArray();

    /// <summary>Declares a feature. Idempotent for an identical declaration, so a switchboard
    /// shared by two renderers sees the built-ins declared twice and minds neither.</summary>
    /// <exception cref="InvalidOperationException">The same name is already declared with a
    /// different default or summary — two owners for one switch, which no runtime rule can
    /// resolve.</exception>
    public FeatureDefinition Declare(FeatureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var existing = _declared.GetOrAdd(definition.Id, definition);
        if (!existing.Equals(definition))
        {
            throw new InvalidOperationException(
                $"Feature '{definition.Name}' is already declared with a different default or summary " +
                $"(was default {existing.EnabledByDefault}, now {definition.EnabledByDefault}). " +
                "One feature name has one owner.");
        }
        return existing;
    }

    /// <inheritdoc cref="Declare(FeatureDefinition)"/>
    public FeatureDefinition Declare(string id, bool enabledByDefault = true, string summary = "") =>
        Declare(new FeatureDefinition(id, enabledByDefault, summary));

    /// <inheritdoc/>
    public bool IsEnabled(FeatureId id)
    {
        if (id.IsEmpty) return false;
        if (_overrides.TryGetValue(id.Value, out var overridden)) return overridden;
        return _declared.TryGetValue(id, out var definition) && definition.EnabledByDefault;
    }

    /// <inheritdoc/>
    public bool TryGetDefinition(FeatureId id, out FeatureDefinition definition) =>
        _declared.TryGetValue(id, out definition!);

    /// <inheritdoc/>
    public FeatureSettings SettingsFor(FeatureId id) =>
        id.IsEmpty ? FeatureSettings.None
            : _settings.TryGetValue(id.Value, out var settings) ? settings : FeatureSettings.None;

    /// <summary>Turns <paramref name="id"/> on or off now. The next thing that asks sees the new
    /// state — a render feature stops declaring its passes on the next frame, a gated system
    /// stops running on the next schedule run.</summary>
    public void Set(FeatureId id, bool enabled)
    {
        if (id.IsEmpty) throw new ArgumentException("A switch needs a feature name.", nameof(id));
        lock (_writeLock)
        {
            var before = IsEnabled(id);
            _overrides[id.Value] = enabled;
            if (before != enabled) Changed?.Invoke(id, enabled);
        }
    }

    /// <inheritdoc cref="Set(FeatureId, bool)"/>
    public void Set(string id, bool enabled) => Set(new FeatureId(id), enabled);

    /// <summary>Drops the override on <paramref name="id"/>, returning it to the state its
    /// declaration asked for.</summary>
    public void Reset(FeatureId id)
    {
        lock (_writeLock)
        {
            var before = IsEnabled(id);
            _overrides.TryRemove(id.Value, out _);
            var after = IsEnabled(id);
            if (before != after) Changed?.Invoke(id, after);
        }
    }

    /// <summary>Applies a whole configuration layer: its switches and its settings, as one write.
    /// Every name it mentions takes its value; names it does not mention keep theirs.
    ///
    /// <para>Callable at runtime, which is what makes an <c>engine.json</c> re-read a live
    /// change: <see cref="Changed"/> and <see cref="SettingsChanged"/> both fire for what
    /// actually moved.</para></summary>
    public void Apply(EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_writeLock)
        {
            Apply(configuration.Features);
            foreach (var (name, settings) in configuration.Settings) SetSettings(name, settings);
        }
    }

    /// <summary>Replaces what one feature is configured with. The settings object is replaced
    /// whole — see <see cref="EngineConfiguration.Merge"/> for why there is no deep merge.</summary>
    public void SetSettings(FeatureId id, FeatureSettings settings)
    {
        if (id.IsEmpty) throw new ArgumentException("Settings need a feature name.", nameof(id));
        SetSettings(id.Value, settings);
    }

    private void SetSettings(string name, FeatureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writeLock)
        {
            var before = _settings.TryGetValue(name, out var existing) ? existing : FeatureSettings.None;
            _settings[name] = settings;
            // A malformed name has no id to announce with; it is still kept, so Unknown reports it
            // rather than a stale line disappearing — the same rule the switches follow.
            if (!ReferenceEquals(before, settings) && !string.Equals(before.Json, settings.Json, StringComparison.Ordinal)
                && FeatureId.TryParse(name, out var id))
            {
                SettingsChanged?.Invoke(id, settings);
            }
        }
    }

    /// <summary>Applies a layer's switches over what is already set. Every name it mentions takes
    /// its value; names it does not mention keep theirs.</summary>
    public void Apply(FeatureOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        // One lock for the whole layer, not one per name: a layer is applied as a unit, and half
        // of a config file is not a state anybody should be able to observe or react to.
        lock (_writeLock)
        {
            foreach (var (name, enabled) in overrides)
            {
                // A malformed name has no id to raise Changed with and no feature to reach; it is
                // kept so Unknown can report it, which is the whole reason it is not dropped here.
                if (!FeatureId.TryParse(name, out var id))
                {
                    _overrides[name] = enabled;
                    continue;
                }
                Set(id, enabled);
            }
        }
    }

    /// <summary>Every feature's effective state right now, as a layer: what to write back to a
    /// config file, or hand to a second process so it renders the same frame.</summary>
    public FeatureOverrides Snapshot()
    {
        var states = new List<KeyValuePair<string, bool>>(_declared.Count + _overrides.Count);
        foreach (var definition in _declared.Values)
            states.Add(new KeyValuePair<string, bool>(definition.Name, IsEnabled(definition.Id)));
        foreach (var (name, enabled) in _overrides)
            if (!FeatureId.TryParse(name, out var id) || !_declared.ContainsKey(id))
                states.Add(new KeyValuePair<string, bool>(name, enabled));
        return FeatureOverrides.From(states);
    }
}
