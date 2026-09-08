namespace Paradise.Features;

/// <summary>Carries one feature's settings as text for its format-specific reader.</summary>
/// <remarks>
/// The producing reader binds this payload to the game's settings type. Keeping binding outside
/// this assembly avoids parser dependencies in ECS and preserves the configuration format.
/// </remarks>
public sealed class FeatureSettings
{
    /// <summary>Empty settings that bind to the settings type's defaults.</summary>
    public static FeatureSettings None { get; } = new("", "");

    internal FeatureSettings(string name, string text)
    {
        Name = name;
        Text = text;
    }

    /// <summary>The feature name used in binding errors.</summary>
    public string Name { get; }

    /// <summary>Settings text in the producing reader's format.</summary>
    public string Text { get; }

    /// <summary>True when nothing was written: a binder answers with the type's defaults.</summary>
    public bool IsEmpty => Text.Length == 0;

    public override string ToString() => IsEmpty ? "(no settings)" : Text;
}
