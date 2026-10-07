using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace BAAA.Simplifier
{
    [BurstCompile(CompileSynchronously = true)]
    struct ComputeMergesJob : IJobParallelForDefer
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<ErrorQuadric> VertexErrorQuadrics;
        [ReadOnly]
        public NativeArray<AttributeErrorQuadric> VertexAttributeErrorQuadrics;
        [ReadOnly]
        public NativeArray<float4> VertexTexCoord0Buffer;
        [ReadOnly]
        public NativeArray<float3> TriangleNormals;
        [ReadOnly]
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        [ReadOnly]
        public NativeBitArray VertexIsBorderEdgeBits;
        [ReadOnly]
        public NativeBitArray VertexIsUVSeamBits;
        [ReadOnly]
        public NativeArray<int2> Edges;
        [ReadOnly]
        public NativeArray<uint> VertexBlendIndicesBuffer;
        [ReadOnly]
        public NativeArray<float> VertexBlendWeightBuffer;
        [ReadOnly]
        public NativeBitArray VertexIsLockedBits;
        [ReadOnly]
        public NativeList<BlendShapeData> BlendShapes;
        [ReadOnly]
        public NativeArray<ulong> VertexBlendShapeMasks;
        [ReadOnly]
        public NativeArray<uint> VertexContainingSubMeshIndices;

        [ReadOnly]
        public NativeBitArray PreserveBorderEdgesBoneIndices;
        [ReadOnly]
        public NativeArray<int3> Triangles;
        [ReadOnly]
        public NativeBitArray DiscardedVertex;
        [ReadOnly]
        public NativeArray<int> PositionGroupNext;
        [ReadOnly]
        public NativeArray<int> PositionGroupId;
        [ReadOnly]
        public NativeArray<int> VertexVersions;
        [WriteOnly]
        public NativeArray<VertexMerge> UnorderedDirtyVertexMerges;
        public bool PreserveBorderEdges;
        public bool PreserveSurfaceCurvature;
        public bool PreserveSubMeshBoundaries;
        public bool PreserveUVSeams;
        public bool ConstrainOptimalPosition;
        public float MaxCollapseDisplacementFactor;
        public bool UseAttributeAwareError;
        public float UvErrorWeight;
        public float MinBoneOverlap;
        public float BlendShapeStretch;
        public void Execute(int index)
        {
            var mergeFactory = new MergeFactory
            {
                VertexPositionBuffer = VertexPositionBuffer,
                VertexErrorQuadrics = VertexErrorQuadrics,
                VertexAttributeErrorQuadrics = VertexAttributeErrorQuadrics,
                VertexTexCoord0Buffer = VertexTexCoord0Buffer,
                VertexContainingTriangles = VertexContainingTriangles,
                VertexIsBorderEdgeBits = VertexIsBorderEdgeBits,
                VertexIsUVSeamBits = VertexIsUVSeamBits,
                TriangleNormals = TriangleNormals,
                PreserveBorderEdges = PreserveBorderEdges,
                PreserveSurfaceCurvature = PreserveSurfaceCurvature,
                PreserveSubMeshBoundaries = PreserveSubMeshBoundaries,
                PreserveUVSeams = PreserveUVSeams,
                ConstrainOptimalPosition = ConstrainOptimalPosition,
                MaxCollapseDisplacementFactor = MaxCollapseDisplacementFactor,
                UseAttributeAwareError = UseAttributeAwareError,
                UvErrorWeight = UvErrorWeight,
                PreserveBorderEdgesBoneIndices = PreserveBorderEdgesBoneIndices,
                VertexBlendIndicesBuffer = VertexBlendIndicesBuffer,
                VertexContainingSubMeshIndices = VertexContainingSubMeshIndices,
                VertexBlendWeightBuffer = VertexBlendWeightBuffer,
                VertexIsLockedBits = VertexIsLockedBits,
                BlendShapes = BlendShapes,
                VertexBlendShapeMasks = VertexBlendShapeMasks,
                MinBoneOverlap = MinBoneOverlap,
                BlendShapeStretch = BlendShapeStretch,
                Seams = new SeamTopology
                {
                    Triangles = Triangles,
                    VertexContainingTriangles = VertexContainingTriangles,
                    DiscardedVertex = DiscardedVertex,
                    GroupNext = PositionGroupNext,
                    GroupId = PositionGroupId,
                    VertexVersions = VertexVersions,
                },
            };
            var edge = Edges[index];
            if (!mergeFactory.TryComputeMerge(edge, out VertexMerge merge))
            {
                merge = new()
                {
                    VertexAIndex = edge.x,
                    VertexBIndex = edge.y,
                    VertexAVersion = 0,
                    VertexBVersion = 0,
                    Position = float.NaN,
                    OptimalUv = float.NaN,
                    Cost = float.PositiveInfinity,
                };
            }
            UnorderedDirtyVertexMerges[index] = merge;

        }
    }
}


