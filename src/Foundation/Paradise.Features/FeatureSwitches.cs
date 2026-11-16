using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Paradise.Features;

/// <summary>Shares feature declarations, overrides and settings across the process.</summary>
/// <remarks>
/// <para>Declarations and switches stay together so subsystems cannot receive mismatched
/// configuration. Overrides are retained by name before declaration; unclaimed names appear in
/// <see cref="Unknown"/> so stale entries and typos remain visible.</para>
/// <para>Every write publishes one immutable <see cref="FeatureSnapshot"/>, a layer as a whole.
/// Lookups read the latest snapshot without locking; <see cref="Capture"/> hands it out so a
/// sequence of reads sees one configuration. Writes and their notifications share a critical
/// section on the writer's thread, serializing concurrent writers. See
/// <c>Paradise.Features.CoyoteTest</c> for concurrency coverage.</para>
/// </remarks>
public sealed class FeatureSwitches : IFeatureSwitches
{
    private FeatureSnapshot _current = FeatureSnapshot.Empty;

    /// <summary>Serializes publications and notifications; lookups do not take this lock.</summary>
    /// <remarks>
    /// <para>Keep notifications inside the lock so concurrent writers cannot announce stale state.
    /// <c>Monitor</c> is reentrant, allowing handlers to set other switches; such notifications
    /// run nested. GPU owners instead poll transitions at frame boundaries.</para>
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

    /// <summary>A configuration seeded with a document's switches and feature settings.</summary>
    public FeatureSwitches(EngineConfiguration configuration) => Apply(configuration);

    /// <inheritdoc/>
    public event Action<FeatureId, bool>? Changed;

    /// <inheritdoc/>
    public event Action<FeatureId, FeatureSettings>? SettingsChanged;

    /// <inheritdoc/>
    public IReadOnlyCollection<FeatureDefinition> Definitions => Capture().Definitions;

    /// <summary>Gets overridden or configured names without a matching declaration.</summary>
    /// <remarks>Hosts decide whether stale entries or typos are fatal; returning them as data keeps
    /// this assembly independent of logging.</remarks>
    public IReadOnlyCollection<string> Unknown
    {
        get
        {
            var snapshot = Capture();
            return snapshot.Overrides.Keys.Concat(snapshot.Settings.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !FeatureId.TryParse(name, out var id) || !snapshot.Declared.ContainsKey(id))
                .ToArray();
        }
    }

    /// <inheritdoc/>
    public FeatureSnapshot Capture() => Volatile.Read(ref _current);

    /// <summary>Declares a feature, accepting repeated identical declarations from shared consumers.</summary>
    /// <exception cref="InvalidOperationException">The same name is already declared with a
    /// different default or summary.</exception>
    public FeatureDefinition Declare(FeatureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_writeLock)
        {
            var current = _current;
            if (current.Declared.TryGetValue(definition.Id, out var existing))
            {
                if (!existing.Equals(definition))
                {
                    throw new InvalidOperationException(
                        $"Feature '{definition.Name}' is already declared with a different default or summary " +
                        $"(was default {existing.EnabledByDefault}, now {definition.EnabledByDefault}). " +
                        "One feature name has one owner.");
                }
                return existing;
            }

            Publish(current.With(declared: current.Declared.Add(definition.Id, definition)));
            return definition;
        }
    }

    /// <inheritdoc cref="Declare(FeatureDefinition)"/>
    public FeatureDefinition Declare(string id, bool enabledByDefault = true, string summary = "") =>
        Declare(new FeatureDefinition(id, enabledByDefault, summary));

    /// <inheritdoc/>
    public bool IsEnabled(FeatureId id) => Capture().IsEnabled(id);

    /// <inheritdoc/>
    public bool TryGetDefinition(FeatureId id, out FeatureDefinition definition) =>
        Capture().TryGetDefinition(id, out definition!);

    /// <inheritdoc/>
    public FeatureSettings SettingsFor(FeatureId id) => Capture().SettingsFor(id);

    /// <summary>Sets <paramref name="id"/> immediately for subsequent reads.</summary>
    /// <remarks>Render features observe changes next frame; gated systems observe them next
    /// schedule run.</remarks>
    public void Set(FeatureId id, bool enabled)
    {
        if (id.IsEmpty) throw new ArgumentException("A switch needs a feature name.", nameof(id));
        lock (_writeLock)
        {
            var current = _current;
            var before = current.IsEnabled(id);
            Publish(current.With(overrides: current.Overrides.SetItem(id.Value, enabled)));
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
            var current = _current;
            if (!current.Overrides.ContainsKey(id.Value)) return;
            var before = current.IsEnabled(id);
            var next = current.With(overrides: current.Overrides.Remove(id.Value));
            Publish(next);
            var after = next.IsEnabled(id);
            if (before != after) Changed?.Invoke(id, after);
        }
    }

    /// <summary>Applies a layer's switches and settings as one publication, preserving unmentioned names.</summary>
    /// <remarks>Readers see the whole layer or none of it. Runtime reloads then raise
    /// <see cref="Changed"/> for switches whose effective state changed, followed by
    /// <see cref="SettingsChanged"/> for settings whose text changed.</remarks>
    public void Apply(EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_writeLock)
        {
            var before = _current;
            var overrides = Overlay(before.Overrides, configuration.Features);
            var settings = before.Settings.ToBuilder();
            var changedSettings = new List<(FeatureId Id, FeatureSettings Settings)>();
            foreach (var (name, value) in configuration.Settings)
            {
                ArgumentNullException.ThrowIfNull(value);
                var previous = settings.TryGetValue(name, out var existing) ? existing : FeatureSettings.None;
                settings[name] = value;
                // Retain malformed names for Unknown; they have no id for notifications.
                if (!ReferenceEquals(previous, value) && !string.Equals(previous.Text, value.Text, StringComparison.Ordinal)
                    && FeatureId.TryParse(name, out var id))
                {
                    changedSettings.Add((id, value));
                }
            }

            var after = before.With(overrides: overrides, settings: settings.ToImmutable());
            Publish(after);
            AnnounceSwitches(before, after, configuration.Features);
            foreach (var (id, value) in changedSettings) SettingsChanged?.Invoke(id, value);
        }
    }

    /// <summary>Replaces one feature's entire settings object.</summary>
    /// <remarks>See <see cref="EngineConfiguration.Merge"/> for why settings are not deep-merged.</remarks>
    public void SetSettings(FeatureId id, FeatureSettings settings)
    {
        if (id.IsEmpty) throw new ArgumentException("Settings need a feature name.", nameof(id));
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writeLock)
        {
            var current = _current;
            var before = current.Settings.TryGetValue(id.Value, out var existing) ? existing : FeatureSettings.None;
            Publish(current.With(settings: current.Settings.SetItem(id.Value, settings)));
            if (!ReferenceEquals(before, settings) && !string.Equals(before.Text, settings.Text, StringComparison.Ordinal))
            {
                SettingsChanged?.Invoke(id, settings);
            }
        }
    }

    /// <summary>Applies a layer's switches as one publication, preserving unmentioned names.</summary>
    /// <remarks>Readers see the whole layer or none of it; <see cref="Changed"/> then reports each
    /// feature of the layer whose effective state changed.</remarks>
    public void Apply(FeatureOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        lock (_writeLock)
        {
            var before = _current;
            var after = before.With(overrides: Overlay(before.Overrides, overrides));
            Publish(after);
            AnnounceSwitches(before, after, overrides);
        }
    }

    /// <summary>Collects effective states and undeclared overrides into a reusable layer.</summary>
    /// <remarks>Reads one captured configuration, so the layer is never half of a concurrent write.</remarks>
    public FeatureOverrides Snapshot()
    {
        var snapshot = Capture();
        var states = new List<KeyValuePair<string, bool>>(snapshot.Declared.Count + snapshot.Overrides.Count);
        foreach (var definition in snapshot.Declared.Values)
            states.Add(new KeyValuePair<string, bool>(definition.Name, snapshot.IsEnabled(definition.Id)));
        foreach (var (name, enabled) in snapshot.Overrides)
            if (!FeatureId.TryParse(name, out var id) || !snapshot.Declared.ContainsKey(id))
                states.Add(new KeyValuePair<string, bool>(name, enabled));
        return FeatureOverrides.From(states);
    }

    private void Publish(FeatureSnapshot next) => Volatile.Write(ref _current, next);

    private static ImmutableDictionary<string, bool> Overlay(ImmutableDictionary<string, bool> overrides, FeatureOverrides layer)
    {
        if (layer.Count == 0) return overrides;
        var builder = overrides.ToBuilder();
        // Invalid names are kept so Unknown reports them; they can never raise Changed.
        foreach (var (name, enabled) in layer) builder[name] = enabled;
        return builder.ToImmutable();
    }

    private void AnnounceSwitches(FeatureSnapshot before, FeatureSnapshot after, FeatureOverrides layer)
    {
        foreach (var (name, _) in layer)
        {
            if (!FeatureId.TryParse(name, out var id)) continue;
            var enabled = after.IsEnabled(id);
            if (before.IsEnabled(id) != enabled) Changed?.Invoke(id, enabled);
        }
    }
}
