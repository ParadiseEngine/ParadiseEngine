namespace Paradise.Rendering.Graph;

/// <summary>Orders passes by event value plus offset, breaking ties by declaration order.</summary>
/// <remarks>Spaced values let engine and game passes occupy gaps without renumbering stages.
/// Before/After stages are empty injection points; built-ins use the named stages.</remarks>
public enum RenderPassEvent
{
    /// <summary>Before anything else in the frame — nothing is set up yet. For work that produces
    /// an input the rest of the frame consumes, such as baking a LUT.</summary>
    BeforeShadows = 0,

    /// <summary>Built-in: one depth-only pass per shadow view (a directional or spot light
    /// contributes one, a point light six).</summary>
    Shadows = 100,

    AfterShadows = 200,

    /// <summary>Built-in: probe global illumination — the ray trace against the scene's BVH and
    /// the blends that fold the hits into the probe atlases. After the shadow maps it samples,
    /// before anything that shades.</summary>
    GlobalIllumination = 250,

    BeforePrepass = 300,

    /// <summary>Built-in: the SSAO world-position pre-pass.</summary>
    Prepass = 400,

    AfterPrepass = 500,
    BeforeOpaque = 600,

    /// <summary>Built-in: the main HDR pass — sky background, then the opaque bucket, and the blend
    /// bucket too when scene-color capture is off.</summary>
    Opaque = 700,

    AfterOpaque = 800,

    /// <summary>Built-in: the blit that copies the opaque half of scene color into a sampleable
    /// texture, so blend materials can read what is behind them.</summary>
    SceneColorCapture = 900,

    /// <summary>Built-in: the blend bucket, when scene-color capture split it out of
    /// <see cref="Opaque"/>.</summary>
    Transparent = 1000,

    AfterTransparent = 1100,
    BeforePost = 1200,

    /// <summary>Built-in: the bloom chain (bright pass, downsample, additive upsample).</summary>
    Post = 1300,

    AfterPost = 1400,
    BeforeComposite = 1500,

    /// <summary>Built-in: tonemap the HDR target onto the swapchain.</summary>
    Composite = 1600,

    AfterComposite = 1700,

    /// <summary>After the scene is composited — debug UI, editor gizmos, anything drawn over a
    /// finished frame.</summary>
    Overlay = 1800,
}
