using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Computes the per triangle attribute aware quadric (position + weighted UV0).
    /// Mirrors <see cref="ComputeTriangleNormalsAndErrorQuadricsJob"/> but in the extended 5D space.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct ComputeTriangleAttributeErrorQuadricsJob : IJobParallelForDefer
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<float4> VertexTexCoord0Buffer;
        [ReadOnly]
        public NativeArray<int3> Triangles;
        public float UvWeight;
        [WriteOnly]
        public NativeArray<AttributeErrorQuadric> TriangleAttributeErrorQuadrics;

        public void Execute(int triangleIndex)
        {
            var triangle = Triangles[triangleIndex];
            var p1 = VertexPositionBuffer[triangle.x];
            var p2 = VertexPositionBuffer[triangle.y];
            var p3 = VertexPositionBuffer[triangle.z];

            var hasUv = VertexTexCoord0Buffer.Length != 0;
            var u1 = hasUv ? VertexTexCoord0Buffer[triangle.x].xy * UvWeight : float2.zero;
            var u2 = hasUv ? VertexTexCoord0Buffer[triangle.y].xy * UvWeight : float2.zero;
            var u3 = hasUv ? VertexTexCoord0Buffer[triangle.z].xy * UvWeight : float2.zero;

            TriangleAttributeErrorQuadrics[triangleIndex] = AttributeErrorQuadric.FromTriangle(p1, p2, p3, u1, u2, u3);
        }
    }
}
