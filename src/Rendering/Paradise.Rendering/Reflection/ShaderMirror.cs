using System.Runtime.CompilerServices;

namespace Paradise.Rendering;

/// <summary>Checks a CPU struct against the layout a shader reflects for the buffer it fills.</summary>
/// <remarks>Each field's offset and size are measured on the struct itself, so the struct is the only
/// CPU-side statement of the layout: there is no table of numbers beside it to keep in step. Name the
/// fields in the shader's order, with <see cref="ShaderMirror{TBlock}.Padding"/> for a shader field
/// the struct keeps private, then call <see cref="ShaderMirror{TBlock}.End"/>. Nothing here reflects
/// over types, so it is trimming and NativeAOT safe.</remarks>
public static class ShaderMirror
{
    /// <summary>Starts checking <paramref name="mirror"/> against the constant buffer <paramref name="blockName"/>.</summary>
    /// <exception cref="InvalidOperationException">The program reflects no such block.</exception>
    public static ShaderMirror<TBlock> Uniform<TBlock>(ShaderProgramDesc program, string blockName, ref TBlock mirror)
        where TBlock : unmanaged
    {
        ArgumentNullException.ThrowIfNull(program);
        foreach (var block in program.UniformBlocks)
        {
            if (string.Equals(block.Name, blockName, StringComparison.Ordinal))
                return new ShaderMirror<TBlock>(ref mirror, $"Uniform block '{blockName}'", block.Fields, block.SizeBytes);
        }

        throw new InvalidOperationException($"The program reflects no uniform block named '{blockName}'.");
    }

    /// <summary>Starts checking <paramref name="mirror"/> against one element of the structured buffer <paramref name="bufferName"/>.</summary>
    /// <exception cref="InvalidOperationException">The program reflects no such buffer of structs, or one whose layout could not be derived.</exception>
    public static ShaderMirror<TElement> Storage<TElement>(ShaderProgramDesc program, string bufferName, ref TElement mirror)
        where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(program);
        foreach (var block in program.StorageBlocks)
        {
            if (string.Equals(block.Name, bufferName, StringComparison.Ordinal))
                return new ShaderMirror<TElement>(ref mirror, $"Storage buffer '{bufferName}'", block.Fields, block.ElementStride);
        }

        throw new InvalidOperationException(
            $"The program reflects no structured buffer named '{bufferName}' whose element is a struct with a derivable layout.");
    }
}

/// <summary>One check in progress; see <see cref="ShaderMirror"/>.</summary>
public ref struct ShaderMirror<TBlock>
    where TBlock : unmanaged
{
    private readonly ref TBlock _mirror;
    private readonly string _what;
    private readonly UniformFieldDesc[] _reflected;
    private readonly uint _reflectedSize;
    private int _next;
    private uint _end;

    internal ShaderMirror(ref TBlock mirror, string what, UniformFieldDesc[] reflected, uint reflectedSize)
    {
        _mirror = ref mirror;
        _what = what;
        _reflected = reflected;
        _reflectedSize = reflectedSize;
    }

    /// <summary>Checks that the next reflected field has this name and sits where <paramref name="field"/> does.</summary>
    /// <param name="name">The field's name in the shader.</param>
    /// <param name="field">The field of the mirror passed to <see cref="ShaderMirror"/>.</param>
    /// <param name="trailingPadding">Bytes of the shader's field that follow <paramref name="field"/>
    /// in the struct without being part of it: the unused lanes of a vector the struct holds as a scalar.</param>
    /// <exception cref="InvalidOperationException">Names the divergence.</exception>
    public ShaderMirror<TBlock> Field<TField>(string name, scoped ref TField field, uint trailingPadding = 0)
        where TField : unmanaged
    {
        var offset = Unsafe.ByteOffset(ref Unsafe.As<TBlock, byte>(ref _mirror), ref Unsafe.As<TField, byte>(ref field));
        var size = (uint)Unsafe.SizeOf<TField>() + trailingPadding;
        if (offset < 0 || offset + size > Unsafe.SizeOf<TBlock>())
            throw new ArgumentException($"{_what}: '{name}' is not a field of the mirror being checked.", nameof(field));

        var reflected = Next(name);
        if (!string.Equals(reflected.Name, name, StringComparison.Ordinal) || reflected.Offset != offset || reflected.Size != size)
        {
            throw new InvalidOperationException(
                $"{_what} field {_next}: CPU mirror holds '{name}'@{offset}+{size} " +
                $"but the shader reflects '{reflected.Name}'@{reflected.Offset}+{reflected.Size}.");
        }

        _next++;
        _end = reflected.Offset + reflected.Size;
        return this;
    }

    /// <summary>Checks that the next reflected field has this name and lies in the struct after the fields checked so far.</summary>
    /// <remarks>For padding the shader names and the struct keeps private, which no caller can take a reference to.</remarks>
    /// <exception cref="InvalidOperationException">Names the divergence.</exception>
    public ShaderMirror<TBlock> Padding(string name)
    {
        var reflected = Next(name);
        if (!string.Equals(reflected.Name, name, StringComparison.Ordinal)
            || reflected.Offset < _end || reflected.Offset + reflected.Size > Unsafe.SizeOf<TBlock>())
        {
            throw new InvalidOperationException(
                $"{_what} field {_next}: CPU mirror holds padding '{name}' from byte {_end} " +
                $"but the shader reflects '{reflected.Name}'@{reflected.Offset}+{reflected.Size}.");
        }

        _next++;
        _end = reflected.Offset + reflected.Size;
        return this;
    }

    private readonly UniformFieldDesc Next(string name) => _next < _reflected.Length
        ? _reflected[_next]
        : throw new InvalidOperationException(
            $"{_what}: CPU mirror holds '{name}' as field {_next} but the shader reflects {_reflected.Length} fields.");

    /// <summary>Checks that every reflected field was named and that the struct is as large as the shader's.</summary>
    /// <exception cref="InvalidOperationException">Names the divergence.</exception>
    public readonly void End()
    {
        if (_next != _reflected.Length)
            throw new InvalidOperationException(
                $"{_what}: CPU mirror holds {_next} fields but the shader reflects {_reflected.Length}; the next is '{_reflected[_next].Name}'.");
        var size = (uint)Unsafe.SizeOf<TBlock>();
        if (size != _reflectedSize)
            throw new InvalidOperationException($"{_what}: CPU mirror is {size} bytes but the shader reflects {_reflectedSize}.");
    }
}
