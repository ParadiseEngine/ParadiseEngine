namespace Paradise.Rendering.Graph;

/// <summary>Where in the frame a pass runs. Passes sort by <c>(int)Event + offset</c>, ties broken
/// by declaration order.
///
/// <para><b>The values are spaced on purpose.</b> An insertion point that is an ordinal — an enum
/// whose numeric value is its position — is the ceiling every extensible renderer eventually hits:
/// a game cannot land between two of them, and the engine cannot add one without renumbering
/// something already serialized. A sortable integer with gaps has neither problem. New engine
/// stages go in the gaps; a game that needs to run immediately after the built-in opaque pass but
/// before anything at <see cref="AfterOpaque"/> writes <c>Opaque + 1</c>.</para>
///
/// <para>The <c>Before</c>/<c>After</c> values are injection points and hold nothing by default.
/// The bare stage names (<see cref="Shadows"/>, <see cref="Opaque"/>, …) are where the built-in
/// passes sit, so "before the built-in opaque pass" and "after it" are both expressible without
/// knowing how many passes the stage happens to expand into this frame.</para></summary>
public enum RenderPassEvent
{
    /// <summary>Before anything else in the frame — nothing is set up yet. For work that produces
    /// an input the rest of the frame consumes, such as baking a LUT.</summary>
    BeforeShadows = 0,

    /// <summary>Built-in: one depth-only pass per shadow view (a directional or spot light
    /// contributes one, a point light six).</summary>
    Shadows = 100,

    AfterShadows = 200,
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
