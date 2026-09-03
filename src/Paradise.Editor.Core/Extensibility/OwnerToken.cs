namespace Paradise.Editor.Core.Extensibility;

/// <summary>Who registered something, so everything an owner added can be removed together.</summary>
/// <remarks>Built-in panels and a future extension register through the same registrar and
/// carry a token alike; nothing registers anonymously. That is the whole extension mechanism,
/// and it exists from the first panel so it is proven rather than promised.</remarks>
public readonly record struct OwnerToken(string Id)
{
    public override string ToString() => Id;
}
