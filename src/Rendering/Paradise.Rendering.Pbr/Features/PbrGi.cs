using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>An authored probe volume: where the probe grid starts, how far apart its probes are,
/// and how many there are per axis. Overrides the fit to the static scene.</summary>
public sealed record PbrProbeVolume(Vector3 Origin, Vector3 Spacing, int CountX, int CountY, int CountZ);

/// <summary>Probe global illumination: a grid of irradiance probes updated every frame by rays
/// traced into the scene's bounding volume hierarchy, replacing the sky ambient for surfaces
/// inside the volume. Everything has a default; <see cref="Volume"/> null fits the static scene.</summary>
public sealed record PbrGi
{
    public bool Enabled { get; init; }

    /// <summary>Uses world-space light bounds at ray hits; disable to compare unculled lighting and timings.</summary>
    public bool LightCullingEnabled { get; init; } = true;

    /// <summary>Minimum fitted probe spacing in metres; 0 chooses spacing from MaxProbes.</summary>
    public float ProbeSpacing { get; init; }

    /// <summary>Rays traced per probe per update, 8 to 256; changes take effect on the next frame.</summary>
    public int RaysPerProbe { get; init; } = 128;

    /// <summary>How much of the previous frame's irradiance survives an update: 0.97 converges
    /// over a second or two and rejects ray noise; lower reacts faster and shimmers more.</summary>
    public float Hysteresis { get; init; } = 0.97f;

    /// <summary>Upper bound on probes when the volume is fitted automatically.</summary>
    public int MaxProbes { get; init; } = 4096;

    /// <summary>Probes traced per frame; 0 traces the whole volume every frame.</summary>
    public int ProbesPerFrame { get; init; }

    /// <summary>Preserves overlapping probes when an authored volume moves by whole grid cells.</summary>
    /// <remarks>Requires Volume; sub-cell movement is accumulated against the resident grid.</remarks>
    public bool Scrolling { get; init; }

    /// <summary>Optional world position receiving half the update budget after invalidated probes.</summary>
    /// <remarks>The remaining budget sweeps the volume so distant probes cannot starve.</remarks>
    public Vector3? UpdateFocus { get; init; }

    /// <summary>Scales the indirect light the probes contribute.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>Lookup bias off the shaded surface along its normal, as a fraction of probe spacing.</summary>
    public float NormalBias { get; init; } = 0.1f;

    /// <summary>Lookup bias toward the viewer, as a fraction of probe spacing.</summary>
    public float ViewBias { get; init; } = 0.3f;

    /// <summary>World-space padding around the static scene when the volume is fitted.</summary>
    public float FitMargin { get; init; } = 0.5f;

    /// <summary>An authored volume, or null to fit the static scene's bounds.</summary>
    public PbrProbeVolume? Volume { get; init; }
}
