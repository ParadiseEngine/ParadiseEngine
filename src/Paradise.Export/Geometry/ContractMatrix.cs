#nullable enable
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>
    /// Builds the 4×4 transform matrices in the export contract's layout. Unity emitted
    /// <c>Matrix4x4.TRS(...)</c> / <c>localToWorldMatrix</c>, whose basis vectors live in the
    /// matrix *columns* and whose translation lives in the last column (column-vector
    /// convention). After the JSON writer flattens column-major, translation lands at flat
    /// indices 12, 13, 14.
    ///
    /// System.Numerics uses the opposite (row-vector) convention — <c>CreateTranslation</c> puts
    /// translation in M41/M42/M43 (flat indices 3/7/11). To reproduce Unity's bytes we build the
    /// row-vector TRS and transpose it, yielding the column-vector layout the contract expects.
    ///
    /// The contract is right-handed (Godot-native), so inputs are the Godot values verbatim; this
    /// type only changes matrix *layout* (row-vector → column-vector), never handedness.
    /// </summary>
    public static class ContractMatrix
    {
        public static Matrix4x4 Trs(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Matrix4x4 rowVector =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(translation);
            return Matrix4x4.Transpose(rowVector);
        }
    }
}
