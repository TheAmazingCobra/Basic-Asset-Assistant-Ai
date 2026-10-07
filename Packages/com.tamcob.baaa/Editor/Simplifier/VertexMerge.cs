using System;
using Unity.Mathematics;
namespace BAAA.Simplifier
{
    struct VertexMerge : IComparable<VertexMerge>
    {
        /// <summary>Both vertices meet at <see cref="Position"/> and their attributes blend (the original Meshia merge).</summary>
        public const int KindFull = 0;
        /// <summary>Vertex A moves onto vertex B, which keeps its position and attributes.</summary>
        public const int KindHalfEdge = 1;
        /// <summary>Every live member of A's position group moves onto its partner in B's group, as one step.</summary>
        public const int KindGroup = 2;

        public int VertexAIndex, VertexBIndex;
        /// <summary>
        /// For <see cref="KindGroup"/> these are the group versions (see <see cref="SeamTopology.GroupVersion"/>).
        /// </summary>
        public int VertexAVersion, VertexBVersion;
        public float3 Position;
        /// <summary>
        /// Optimal merged UV0 from the attribute aware solve. Only meaningful when
        /// <see cref="MeshSimplifierOptions.UseAttributeAwareError"/> is enabled.
        /// </summary>
        public float2 OptimalUv;
        /// <summary>
        /// For <see cref="KindGroup"/> the sum over all moving pairs.
        /// </summary>
        public float Cost;
        public int Kind;

        public int CompareTo(VertexMerge other)
        {
            return Cost.CompareTo(other.Cost);
        }
    }
}


