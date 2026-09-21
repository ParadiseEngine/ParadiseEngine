using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Paradise.Features;

/// <summary>Shares feature declarations, overrides and settings across the process.</summary>
/// <remarks>
/// <para>Declarations and switches stay together so subsystems cannot receive mismatched
/// configuration. Overrides are retained by name before declaration; unclaimed names appear in
/// <see cref="Unknown"/> so stale entries and typos remain visible.</para>
/// <para>Reads are lock-free and thread-safe. Writes and <see cref="Changed"/> notifications
/// share a critical section on the writer's thread, preserving notification order.
/// See <c>Paradise.Features.CoyoteTest</c> for concurrency coverage.</para>
/// </remarks>
public sealed class FeatureSwitches : IFeatureSwitches
{
    private readonly ConcurrentDictionary<FeatureId, FeatureDefinition> _declared = new();
    private readonly ConcurrentDictionary<string, bool> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FeatureSettings> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes writes and their <see cref="Changed"/> notifications without blocking reads.</summary>
    /// <remarks>
    /// <para>Keep notifications inside the lock so concurrent writers cannot announce stale state.
    /// Subscribers may retract state in response, as <c>IRenderFeature.OnEnabledChanged</c> does.
    /// <c>Monitor</c> is reentrant, allowing handlers to set other switches.</para>
    /// <para>Use <c>object</c>: Coyote 1.7.11 intercepts <c>Monitor.Enter</c>/<c>Exit</c>, but not
    /// <c>System.Threading.Lock.EnterScope</c>.</para>
    /// </remarks>
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

    /// <summary>Gets overridden or configured names without a matching declaration.</summary>
    /// <remarks>Hosts decide whether stale entries or typos are fatal; returning them as data keeps
    /// this assembly independent of logging.</remarks>
    public IReadOnlyCollection<string> Unknown =>
        _overrides.Keys.Concat(_settings.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !FeatureId.TryParse(name, out var id) || !_declared.ContainsKey(id))
            .ToArray();

    /// <summary>Declares a feature, accepting repeated identical declarations from shared consumers.</summary>
    /// <exception cref="InvalidOperationException">The same name is already declared with a
    /// different default or summary.</exception>
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

    /// <summary>Sets <paramref name="id"/> immediately for subsequent reads.</summary>
    /// <remarks>Render features observe changes next frame; gated systems observe them next
    /// schedule run.</remarks>
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

    /// <summary>Applies a layer's switches and settings under one write lock, preserving unmentioned names.</summary>
    /// <remarks>Runtime reloads raise <see cref="Changed"/> and <see cref="SettingsChanged"/> for
    /// changed values.</remarks>
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
            // Retain malformed names for Unknown; they have no id for notifications.
            if (!ReferenceEquals(before, settings) && !string.Equals(before.Text, settings.Text, StringComparison.Ordinal)
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
        // Hold the lock across the layer so other writers cannot interleave changes.
        lock (_writeLock)
        {
            foreach (var (name, enabled) in overrides)
            {
                // Preserve invalid names for Unknown; they cannot raise Changed.
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
