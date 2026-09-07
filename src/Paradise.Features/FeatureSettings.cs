namespace Paradise.Features;

/// <summary>One feature's settings: the table written under its name in the configuration's
/// <c>settings</c> section, carried as the text it was written in and bound to a type by whichever
/// reader understands that text — <c>FeatureSettingsToml.Read</c> in <c>Paradise.Features.Toml</c>.
///
/// <para><b>Why the switch and the settings are separate sections.</b> A feature name is FLAT and
/// dotted, and letting a feature's value be a table would make <c>[features.rendering]</c> with
/// <c>bloom = false</c> mean a feature called <c>rendering</c> with a setting called
/// <c>bloom</c> — the nested spelling nobody meant, accepted silently. Keeping the two apart lets
/// <c>features</c> refuse every table and say what to write instead.</para>
///
/// <para><b>Why the binding is not here.</b> This assembly has no package references, because
/// <c>Paradise.ECS</c> references it; it therefore cannot name a TOML type, and carries the
/// payload as text instead. Only the reader that produced that text can bind it, which is the
/// same seam the reader already sits on. Nothing is converted on the way through: a settings type
/// is bound from what a person wrote.</para>
///
/// <para><b>Why a type and not a bag of getters.</b> Settings that deserve a name deserve a
/// record: one place holding the defaults, the units in the property names, and the whole shape
/// visible at once. A bag of <c>GetSingle("intensity", 1f)</c> calls would spread the defaults
/// across every call site instead.</para></summary>
public sealed class FeatureSettings
{
    /// <summary>No settings were written for this feature. Binding it hands back the type's own
    /// defaults, so a caller needs no branch — the same reason every collection-shaped result in
    /// this repo is empty rather than null.</summary>
    public static FeatureSettings None { get; } = new("", "");

    internal FeatureSettings(string name, string text)
    {
        Name = name;
        Text = text;
    }

    /// <summary>The feature these settings belong to, so a binder that cannot make sense of them
    /// names the line of the file that has to be fixed.</summary>
    public string Name { get; }

    /// <summary>The settings exactly as the configuration spelled them, in that configuration's
    /// own syntax. Bind it with the matching reader's <c>Read</c>; it is also what to log, or to
    /// hand to a parser of the game's own.</summary>
    public string Text { get; }

    /// <summary>True when nothing was written: a binder answers with the type's defaults.</summary>
    public bool IsEmpty => Text.Length == 0;

    public override string ToString() => IsEmpty ? "(no settings)" : Text;
}
