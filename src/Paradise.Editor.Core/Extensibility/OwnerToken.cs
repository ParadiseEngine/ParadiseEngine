namespace Paradise.Editor.Core.Extensibility;

/// <summary>Identifies an owner whose editor registrations can be removed together.</summary>
public readonly record struct OwnerToken(string Id)
{
    public override string ToString() => Id;
}
