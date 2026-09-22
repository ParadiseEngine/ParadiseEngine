namespace Paradise.Authoring;

/// <summary>
/// What the host tells an <see cref="AuthoredButtonAttribute"/> method: which document it was
/// invoked for and which component the button belonged to. Host paths, not mounts — the method
/// runs inside a CLI process and mounts the project itself if it needs one.
/// </summary>
public sealed record AuthorActionContext
{
    /// <summary>The asset project root (the directory holding <c>assets/project.toml</c>), absolute.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>The document the button was drawn for — a <c>.prefab</c> under <c>assets/</c>, absolute.</summary>
    public required string Document { get; init; }

    /// <summary>The component id (GUID) the action was declared on.</summary>
    public required string ComponentId { get; init; }

    /// <summary>The scene object the component sits on, when the host knows one.</summary>
    public string? EntityId { get; init; }

    /// <summary>The requested value for a toggle invocation, otherwise null.</summary>
    public bool? Value { get; init; }

    /// <summary>True when the editor invokes a declared save hook.</summary>
    public bool IsSave { get; init; }

    /// <summary>Host-owned toggle state for this document's component instance.</summary>
    public IReadOnlyDictionary<string, bool> ToggleValues { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Generic updates returned to the editor after successful invocation.</summary>
    public AuthorActionResult Result { get; } = new();
}

/// <summary>Editor-neutral updates from a successful authored action.</summary>
public sealed record AuthorActionResult
{
    public Dictionary<string, bool> Toggles { get; init; } = new(StringComparer.Ordinal);
    public bool DocumentChanged { get; set; }
    public List<AuthorActionOverlay> Overlays { get; init; } = [];
}

/// <summary>One named triangle overlay in world-space engine coordinates, outside authored document data.</summary>
public sealed record AuthorActionOverlay
{
    public required string Id { get; init; }
    public bool Visible { get; init; }
    public float[] Vertices { get; init; } = [];
    public int[] Indices { get; init; } = [];
    public float[] Color { get; init; } = [0.15f, 0.65f, 0.95f, 0.35f];
}
