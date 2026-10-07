using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Accumulates the per vertex attribute aware quadric by summing the quadrics of incident triangles.
    /// Mirrors <see cref="ComputeVertexErrorQuadricsJob"/>: open border edges are constrained with
    /// position axis planes so the boundary does not drift.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct ComputeVertexAttributeErrorQuadricsJob : IJobParallelForDefer
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<int3> Triangles;
        [ReadOnly]
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        [ReadOnly]
        public NativeHashSet<int2> Edges;
        [ReadOnly]
        public NativeArray<AttributeErrorQuadric> TriangleAttributeErrorQuadrics;
        [WriteOnly]
        public NativeArray<AttributeErrorQuadric> VertexAttributeErrorQuadrics;

        public void Execute(int vertexIndex)
        {
            var vertexErrorQuadric = new AttributeErrorQuadric();
            var vertexPosition = VertexPositionBuffer[vertexIndex];
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertexIndex))
            {
                var triangle = Triangles[triangleIndex];

                int2x2 belongingEdges;
                if (vertexIndex == triangle.x)
                {
                    belongingEdges = new(triangle.xy, triangle.zx);
                }
                else if (vertexIndex == triangle.y)
                {
                    belongingEdges = new(triangle.xy, triangle.yz);
                }
                else if (vertexIndex == triangle.z)
                {
                    belongingEdges = new(triangle.yz, triangle.zx);
                }
                else
                {
                    throw new Exception();
                }

                if (!Edges.Contains(belongingEdges.c0.yx) || !Edges.Contains(belongingEdges.c1.yx))
                {
                    vertexErrorQuadric += AttributeErrorQuadric.FromPositionPlane(math.right(), vertexPosition);
                    vertexErrorQuadric += AttributeErrorQuadric.FromPositionPlane(math.up(), vertexPosition);
                    vertexErrorQuadric += AttributeErrorQuadric.FromPositionPlane(math.forward(), vertexPosition);
                }
                else
                {
                    vertexErrorQuadric += TriangleAttributeErrorQuadrics[triangleIndex];
                }
            }
            VertexAttributeErrorQuadrics[vertexIndex] = vertexErrorQuadric;
        }
    }
}
