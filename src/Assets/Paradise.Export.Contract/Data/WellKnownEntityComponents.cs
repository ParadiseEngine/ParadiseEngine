#nullable enable
using System;

namespace Paradise.Export.Data
{
    /// <summary>The runtime IDs for the format's <c>meta</c> and <c>transform</c> components.</summary>
    /// <remarks>
    /// The loader uses them for identity, hierarchy and local placement; games declare all other components.
    /// The pipeline test pins these IDs to <c>WellKnownComponents</c>, avoiding a dependency on the authoring package.
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

        // meta fields (guid values travel as canonical guid strings)

        /// <summary>The entity's identity. Unique per document.</summary>
        public const string Guid = "Guid";

        /// <summary>Display name. Diagnostics and readability; not unique, not identity.</summary>
        public const string Name = "Name";

        /// <summary>The parent entity's <see cref="Guid"/>, or absent for a root.</summary>
        public const string Parent = "Parent";

        // transform fields (JSON number arrays)

        /// <summary>Local translation as <c>[x, y, z]</c>, engine convention (Y-up, metres).</summary>
        public const string Position = "Position";

        /// <summary>Local rotation as <c>[x, y, z, w]</c>.</summary>
        public const string Rotation = "Rotation";

        /// <summary>Local scale as <c>[x, y, z]</c>.</summary>
        public const string Scale = "Scale";
    }
}
