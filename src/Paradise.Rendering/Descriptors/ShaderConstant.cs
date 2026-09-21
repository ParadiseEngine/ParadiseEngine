namespace Paradise.Rendering;

/// <summary>A pipeline-overridable WGSL constant, addressed by name or numeric override ID.</summary>
public readonly record struct ShaderConstant(string Key, double Value)
{
    /// <summary>Rejects ambiguous keys and non-finite values before native or browser serialization.</summary>
    public static void Validate(ReadOnlySpan<ShaderConstant> constants)
    {
        for (var i = 0; i < constants.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(constants[i].Key) || !double.IsFinite(constants[i].Value))
                throw new ArgumentException("Shader constants require non-empty keys and finite values.", nameof(constants));
            for (var j = 0; j < i; j++)
                if (string.Equals(constants[i].Key, constants[j].Key, StringComparison.Ordinal))
                    throw new ArgumentException($"Duplicate shader constant '{constants[i].Key}'.", nameof(constants));
        }
    }
}
