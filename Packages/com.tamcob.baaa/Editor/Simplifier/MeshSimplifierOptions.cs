#nullable enable
using System;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.CompilerServices;
namespace BAAA.Simplifier
{
    [Serializable]
    internal struct MeshSimplifierOptions : IEquatable<MeshSimplifierOptions>
    {
        public static MeshSimplifierOptions Default => new()
        {
            PreserveBorderEdges = false,
            PreserveSurfaceCurvature = false,
            UseBarycentricCoordinateInterpolation = false,
            MinNormalDot = 0.2f,
            EnableSmartLink = true,
            VertexLinkDistance = 0.0001f,
            VertexLinkMinNormalDot = 0.95f,
            VertexLinkColorDistance = 0.01f,
            VertexLinkUvDistance = 0.001f,

            // Appearance-preserving improvements (enabled by default; set to false to restore legacy behavior).
            PreserveSubMeshBoundaries = true,
            // UV seam locking is the most reduction-limiting option (it can keep the result well above the
            // requested count), so it is opt-in. Texture mapping is still protected softly by UseAttributeAwareError.
            PreserveUVSeams = false,
            ConstrainOptimalPosition = true,
            MaxCollapseDisplacementFactor = 2f,
            UseAttributeAwareError = true,
            UvErrorWeight = 1f,

            // Asset Assistant merge rules, off unless the caller sets them.
            MinBoneOverlap = 0f,
            BlendShapeStretch = 0f,
        };

        /// <summary>
        /// If you want to suppress hole generation during simplification, enable this option.
        /// </summary>
        [Tooltip("If you want to suppress hole generation during simplification, enable this option.")]
        public bool PreserveBorderEdges;
        public bool PreserveSurfaceCurvature;
        /// <summary>
        /// If you find that the texture is distorted, try toggling this option.
        /// </summary>
        [Tooltip("If you find that the texture is distorted, try toggling this option.")]
        public bool UseBarycentricCoordinateInterpolation;
        /// <summary>
        /// If this option is enabled, vertices that are not originally connected but are close to each other will be included in the first merge candidates. <br/>
        /// Increases the initialization cost.
        /// </summary>
        [Tooltip("If this option is enabled, vertices that are not originally connected but are close to each other will be included in the first merge candidates. \n" +
            "Increases the initialization cost.")]
        public bool EnableSmartLink;
        [Range(-1, 1)]
        public float MinNormalDot;
        /// <summary>
        /// When smart link is enabled, this is used to select candidates for merging vertices that are not originally connected to each other. <br/>
        /// Increasing this value also increases the initialization cost.
        /// </summary>
        [Tooltip("When smart link is enabled, this is used to select candidates for merging vertices that are not originally connected to each other. \n" +
            "Increasing this value also increases the initialization cost.")]
        public float VertexLinkDistance;
        [Range(-1, 1)]
        public float VertexLinkMinNormalDot;
        // This could be HDR color, so there is no Range.
        public float VertexLinkColorDistance;
        [Range(0, 1.41421356237f)]
        public float VertexLinkUvDistance;

        /// <summary>
        /// Prevents merging vertices that belong to different sub meshes (materials). <br/>
        /// Keeps material boundaries crisp and avoids material bleeding when simplifying multi-material meshes (e.g. avatars).
        /// </summary>
        [Tooltip("Prevents merging vertices that belong to different sub meshes (materials).\n" +
            "Keeps material boundaries crisp and avoids material bleeding when simplifying multi-material meshes.")]
        public bool PreserveSubMeshBoundaries;
        /// <summary>
        /// Locks vertices that lie on a UV seam (coincident in position but discontinuous in UV) so textures are not torn apart.
        /// </summary>
        [Tooltip("Locks vertices that lie on a UV seam (coincident in position but discontinuous in UV) so textures are not torn apart.")]
        public bool PreserveUVSeams;
        /// <summary>
        /// Constrains the optimal collapse position to the neighborhood of the collapsed edge, suppressing spikes that poke through other surfaces (self-intersection).
        /// </summary>
        [Tooltip("Constrains the optimal collapse position to the neighborhood of the collapsed edge, suppressing spikes that poke through other surfaces.")]
        public bool ConstrainOptimalPosition;
        /// <summary>
        /// When <see cref="ConstrainOptimalPosition"/> is enabled, the optimal position is rejected if it is farther than this factor times the edge length from the edge midpoint.
        /// </summary>
        [Tooltip("When Constrain Optimal Position is enabled, the optimal position is rejected if it is farther than this factor times the edge length from the edge midpoint.")]
        [Min(0)]
        public float MaxCollapseDisplacementFactor;
        /// <summary>
        /// Includes UV coordinates in the error metric itself (attribute-aware quadric error). <br/>
        /// Greatly reduces texture distortion at high reduction ratios at the cost of extra initialization time and memory.
        /// </summary>
        [Tooltip("Includes UV coordinates in the error metric itself (attribute-aware quadric error).\n" +
            "Greatly reduces texture distortion at high reduction ratios at the cost of extra initialization time and memory.")]
        public bool UseAttributeAwareError;
        /// <summary>
        /// Relative importance of UV preservation versus geometric shape when <see cref="UseAttributeAwareError"/> is enabled. <br/>
        /// Larger values preserve the texture mapping more aggressively; smaller values favor geometric accuracy.
        /// </summary>
        [Tooltip("Relative importance of UV preservation versus geometric shape when Use Attribute Aware Error is enabled.\n" +
            "Larger values preserve the texture mapping more aggressively; smaller values favor geometric accuracy.")]
        [Min(0)]
        public float UvErrorWeight;

        /// <summary>
        /// Skips a merge when the sum over bones of min(weightA, weightB) is below this value. 0 turns the rule off.
        /// </summary>
        [Range(0, 1)]
        public float MinBoneOverlap;
        /// <summary>
        /// Skips a merge when, for any blend shape (last frame), |deltaA - deltaB| is larger than
        /// this value times |posA - posB| plus <see cref="BlendShapeStretchSlack"/>. 0 turns the rule off.
        /// </summary>
        [Min(0)]
        public float BlendShapeStretch;

        /// <summary>
        /// Fixed slack of the blend shape stretch rule, in mesh units.
        /// </summary>
        internal const float BlendShapeStretchSlack = 0.0005f;
        /// <summary>
        /// A vertex is flagged for a blend shape when that shape moves it further than this. Two unflagged
        /// deltas differ by at most the slack, so only flagged shapes can break the stretch rule.
        /// </summary>
        internal const float BlendShapeMaskThreshold = BlendShapeStretchSlack * 0.5f;
        /// <summary>
        /// Vertices closer than this share a position group (seams). The bridge scales meshes to one unit across,
        /// so in practice only exact copies match.
        /// </summary>
        internal const float PositionGroupTolerance = 1e-7f;


        public readonly override bool Equals(object obj)
        {
            return obj is MeshSimplifierOptions options && Equals(options);
        }

        public readonly bool Equals(MeshSimplifierOptions other)
        {
            return PreserveBorderEdges == other.PreserveBorderEdges &&
                   PreserveSurfaceCurvature == other.PreserveSurfaceCurvature &&
                   UseBarycentricCoordinateInterpolation == other.UseBarycentricCoordinateInterpolation &&
                   EnableSmartLink == other.EnableSmartLink &&
                   MinNormalDot == other.MinNormalDot &&
                   VertexLinkDistance == other.VertexLinkDistance &&
                   VertexLinkMinNormalDot == other.VertexLinkMinNormalDot &&
                   VertexLinkColorDistance == other.VertexLinkColorDistance &&
                   VertexLinkUvDistance == other.VertexLinkUvDistance &&
                   PreserveSubMeshBoundaries == other.PreserveSubMeshBoundaries &&
                   PreserveUVSeams == other.PreserveUVSeams &&
                   ConstrainOptimalPosition == other.ConstrainOptimalPosition &&
                   MaxCollapseDisplacementFactor == other.MaxCollapseDisplacementFactor &&
                   UseAttributeAwareError == other.UseAttributeAwareError &&
                   UvErrorWeight == other.UvErrorWeight &&
                   MinBoneOverlap == other.MinBoneOverlap &&
                   BlendShapeStretch == other.BlendShapeStretch;
        }

        public readonly override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(PreserveBorderEdges);
            hashCode.Add(PreserveSurfaceCurvature);
            hashCode.Add(UseBarycentricCoordinateInterpolation);
            hashCode.Add(EnableSmartLink);
            hashCode.Add(MinNormalDot);
            hashCode.Add(VertexLinkDistance);
            hashCode.Add(VertexLinkMinNormalDot);
            hashCode.Add(VertexLinkColorDistance);
            hashCode.Add(VertexLinkUvDistance);
            hashCode.Add(PreserveSubMeshBoundaries);
            hashCode.Add(PreserveUVSeams);
            hashCode.Add(ConstrainOptimalPosition);
            hashCode.Add(MaxCollapseDisplacementFactor);
            hashCode.Add(UseAttributeAwareError);
            hashCode.Add(UvErrorWeight);
            hashCode.Add(MinBoneOverlap);
            hashCode.Add(BlendShapeStretch);
            return hashCode.ToHashCode();
        }

        public static bool operator ==(MeshSimplifierOptions left, MeshSimplifierOptions right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MeshSimplifierOptions left, MeshSimplifierOptions right)
        {
            return !(left == right);
        }
    }
}
