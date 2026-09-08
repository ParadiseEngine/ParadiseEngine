#nullable enable
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>Builds right-handed, column-vector transforms in the export contract's layout.</summary>
    /// <remarks>
    /// Transposes System.Numerics row-vector TRS so translation occupies flat column-major indices
    /// 12, 13 and 14; inputs retain their handedness.
    /// </remarks>
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
