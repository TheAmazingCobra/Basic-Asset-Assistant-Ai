using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Marks vertices that lie on a UV seam: vertices whose position coincides with another vertex
    /// (within <see cref="PositionEpsilon"/>) but whose UV0 differs by more than <see cref="UvThreshold"/>.
    /// These vertices are duplicated at texture seams; collapsing across them smears the texture, so they
    /// are locked when <see cref="MeshSimplifierOptions.PreserveUVSeams"/> is enabled.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct MarkUVSeamVerticesJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<float4> VertexTexCoord0Buffer;
        [ReadOnly]
        public NativeBitArray VertexIsDiscardedBits;

        public float PositionEpsilon;
        public float UvThreshold;

        public NativeBitArray VertexIsUVSeamBits;

        public void Execute()
        {
            var vertexCount = Mesh.vertexCount;
            VertexIsUVSeamBits.Resize(vertexCount);
            VertexIsUVSeamBits.Clear();

            // No UV channel means there is nothing to tear; leave all bits clear.
            if (VertexTexCoord0Buffer.Length == 0 || vertexCount == 0)
            {
                return;
            }

            var uvThresholdSq = UvThreshold * UvThreshold;
            var positionEpsilon = math.max(PositionEpsilon, 0f);

            UnsafeKdTree kdTree = new(Allocator.Temp);
            kdTree.Initialize(VertexPositionBuffer);
            UnsafeList<int> coincidentVertices = new(16, Allocator.Temp);

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                if (VertexIsDiscardedBits.IsSet(vertexIndex))
                {
                    continue;
                }

                kdTree.QueryPointsInSphere(VertexPositionBuffer, VertexPositionBuffer[vertexIndex], positionEpsilon, ref coincidentVertices);

                var uv = VertexTexCoord0Buffer[vertexIndex];
                foreach (var coincidentVertexIndex in coincidentVertices)
                {
                    if (coincidentVertexIndex == vertexIndex)
                    {
                        continue;
                    }
                    if (math.distancesq(uv, VertexTexCoord0Buffer[coincidentVertexIndex]) > uvThresholdSq)
                    {
                        VertexIsUVSeamBits.Set(vertexIndex, true);
                        break;
                    }
                }

                coincidentVertices.Clear();
            }

            coincidentVertices.Dispose();
            kdTree.Dispose();
        }
    }
}
