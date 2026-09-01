#nullable enable
using System;

namespace Paradise.Export.Data
{
    /// <summary>
    /// The two authoring-format components a v6 document ships for every entity: <c>meta</c>
    /// (identity, name, parent) and <c>transform</c> (local TRS). The RUNTIME-VISIBLE copy of
    /// the authoring format's well-known ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A copy, because this assembly cannot reference <c>Paradise.Assets.Documents</c> (the
    /// pipeline depends on the contract, not the other way round). A test in
    /// <c>Paradise.Assets.Pipeline.Test</c> pins these against
    /// <c>WellKnownComponents</c> so the two spellings cannot drift.
    /// </para>
    /// <para>
    /// These are NOT engine authored components — v6's whole point is that the engine declares
    /// none. They are format vocabulary: the loader reads them to seed identity, hierarchy and
    /// placement, and everything else in an entity's list is the game's own declaration.
    /// </para>
    /// </remarks>
    public static class WellKnownEntityComponents
    {
        /// <summary><c>meta</c> — identity, display name, and the parent link.</summary>
        public static readonly Guid MetaId = new("0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913");

        /// <summary>The readable name of <see cref="MetaId"/>.</summary>
        public const string MetaType = "meta";

        /// <summary><c>transform</c> — the entity's LOCAL position, rotation and scale.</summary>
        public static readonly Guid TransformId = new("7e55c210-3d41-4b8a-8f26-9c0a5e71b4d2");

        /// <summary>The readable name of <see cref="TransformId"/>.</summary>
        public const string TransformType = "transform";

        // ---- meta fields (guid values travel as canonical guid strings) ----------------------

        /// <summary>The entity's identity. Unique per document.</summary>
        public const string Guid = "Guid";

        /// <summary>Display name. Diagnostics and readability; not unique, not identity.</summary>
        public const string Name = "Name";

        /// <summary>The parent entity's <see cref="Guid"/>, or absent for a root.</summary>
        public const string Parent = "Parent";

        // ---- transform fields (JSON number arrays) --------------------------------------------

        /// <summary>Local translation as <c>[x, y, z]</c>, engine convention (Y-up, metres).</summary>
        public const string Position = "Position";

        /// <summary>Local rotation as <c>[x, y, z, w]</c>.</summary>
        public const string Rotation = "Rotation";

        /// <summary>Local scale as <c>[x, y, z]</c>.</summary>
        public const string Scale = "Scale";
    }
}
