using System;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
namespace BAAA.Simplifier
{
    [BurstCompile(CompileSynchronously = true, DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    struct SimplifyJob : IJob
    {
        [BurstCompile(CompileSynchronously = true)]
        internal static class ProfilerMarkers
        {
            public static readonly ProfilerMarker MergeVertexAttributeData = new(nameof(MergeVertexAttributeData));
            public static readonly ProfilerMarker ResolveMergedVertexReferences = new(nameof(ResolveMergedVertexReferences));
            public static readonly ProfilerMarker CollectMergedVertexContainingTriangles = new(nameof(CollectMergedVertexContainingTriangles));
            public static readonly ProfilerMarker RecomputeMerges = new(nameof(RecomputeMerges));
            public static readonly ProfilerMarker ApplyMerge = new(nameof(ApplyMerge));
            public static readonly ProfilerMarker DiscardNonReferencedVertices = new(nameof(DiscardNonReferencedVertices));
        }
        public NativeArray<float3> VertexPositionBuffer;
        public NativeArray<float4> VertexNormalBuffer;
        public NativeArray<float4> VertexTangentBuffer;
        public NativeArray<float4> VertexColorBuffer;
        public NativeArray<float4> VertexTexCoord0Buffer;
        public NativeArray<float4> VertexTexCoord1Buffer;
        public NativeArray<float4> VertexTexCoord2Buffer;
        public NativeArray<float4> VertexTexCoord3Buffer;
        public NativeArray<float4> VertexTexCoord4Buffer;
        public NativeArray<float4> VertexTexCoord5Buffer;
        public NativeArray<float4> VertexTexCoord6Buffer;
        public NativeArray<float4> VertexTexCoord7Buffer;

        public NativeArray<float> VertexBlendWeightBuffer;
        public NativeArray<uint> VertexBlendIndicesBuffer;

        public NativeArray<uint> VertexContainingSubMeshIndices;


        public NativeList<BlendShapeData> BlendShapes;

        public NativeArray<int3> Triangles;
        public NativeArray<int> VertexVersions;
        public NativeArray<ErrorQuadric> VertexErrorQuadrics;
        public NativeArray<AttributeErrorQuadric> VertexAttributeErrorQuadrics;
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        public NativeParallelMultiHashMap<int, int> VertexMergeOpponentVertices;
        public NativeBitArray VertexIsBorderEdgeBits;
        public NativeBitArray VertexIsUVSeamBits;
        public NativeBitArray VertexIsLockedBits;
        public NativeArray<ulong> VertexBlendShapeMasks;
        public NativeList<float> ErrorCurve;
        public bool RecordErrorCurve;
        public NativeArray<int> PositionGroupNext;
        public NativeArray<int> PositionGroupId;
        public NativeMinPriorityQueue<VertexMerge> VertexMerges;
        public MeshSimplifierOptions Options;

        public NativeBitArray DiscardedVertex;

        public NativeBitArray DiscardedTriangle;
        public NativeBitArray PreserveBorderEdgesBoneIndices;

        public NativeArray<float3> TriangleNormals;
        public NativeHashSet<int2> SmartLinks;
        [ReadOnly]

        public Mesh.MeshData Mesh;

        public MeshSimplificationTarget SimplificationTarget;
        private int VertexCount;
        private int TriangleCount;
        private float errorCurveMax;
        private int errorCurveCursor;
        readonly SeamTopology Seams => new()
        {
            Triangles = Triangles,
            VertexContainingTriangles = VertexContainingTriangles,
            DiscardedVertex = DiscardedVertex,
            GroupNext = PositionGroupNext,
            GroupId = PositionGroupId,
            VertexVersions = VertexVersions,
        };
        readonly MergeFactory MergeFactory => new()
        {
            Seams = Seams,
            VertexPositionBuffer = VertexPositionBuffer,
            VertexBlendIndicesBuffer = VertexBlendIndicesBuffer,
            VertexBlendWeightBuffer = VertexBlendWeightBuffer,
            VertexIsLockedBits = VertexIsLockedBits,
            BlendShapes = BlendShapes,
            VertexBlendShapeMasks = VertexBlendShapeMasks,
            MinBoneOverlap = Options.MinBoneOverlap,
            BlendShapeStretch = Options.BlendShapeStretch,
            VertexErrorQuadrics = VertexErrorQuadrics,
            VertexAttributeErrorQuadrics = VertexAttributeErrorQuadrics,
            VertexTexCoord0Buffer = VertexTexCoord0Buffer,
            TriangleNormals = TriangleNormals,
            VertexContainingTriangles = VertexContainingTriangles,

            VertexIsBorderEdgeBits = VertexIsBorderEdgeBits,
            VertexIsUVSeamBits = VertexIsUVSeamBits,
            PreserveBorderEdgesBoneIndices = PreserveBorderEdgesBoneIndices,
            VertexContainingSubMeshIndices = Options.PreserveSubMeshBoundaries ? VertexContainingSubMeshIndices : default,
            PreserveBorderEdges = Options.PreserveBorderEdges,
            PreserveSurfaceCurvature = Options.PreserveSurfaceCurvature,
            PreserveSubMeshBoundaries = Options.PreserveSubMeshBoundaries,
            PreserveUVSeams = Options.PreserveUVSeams,
            ConstrainOptimalPosition = Options.ConstrainOptimalPosition,
            MaxCollapseDisplacementFactor = Options.MaxCollapseDisplacementFactor,
            UseAttributeAwareError = Options.UseAttributeAwareError,
            UvErrorWeight = Options.UvErrorWeight,

        };

        readonly PreservedVertexPredicator PreservedVertexPredicator => new()
        {
            VertexBlendIndicesBuffer = VertexBlendIndicesBuffer,
            VertexIsBorderEdgeBits = VertexIsBorderEdgeBits,
            VertexIsUVSeamBits = VertexIsUVSeamBits,
            PreserveBorderEdgesBoneIndices = PreserveBorderEdgesBoneIndices,
            VertexIsLockedBits = VertexIsLockedBits,
            VertexBoneCount = VertexBlendIndicesBuffer.Length / VertexPositionBuffer.Length,
            PreserveBorderEdges = Options.PreserveBorderEdges,
            PreserveUVSeams = Options.PreserveUVSeams,
        };

        public void Execute()
        {
            VertexCount = DiscardedVertex.Length - DiscardedVertex.CountBits(0, DiscardedVertex.Length);
            TriangleCount = DiscardedTriangle.Length - DiscardedTriangle.CountBits(0, DiscardedTriangle.Length);
            BeginErrorCurve();
            switch (SimplificationTarget.Kind)
            {
                case MeshSimplificationTargetKind.RelativeVertexCount:
                    {
                        var targetVertexCount = (int)(Mesh.vertexCount * SimplificationTarget.Value);
                        while (targetVertexCount < VertexCount && VertexMerges.TryDequeue(out var merge))
                        {
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                            }
                        }
                    }
                    break;
                case MeshSimplificationTargetKind.AbsoluteVertexCount:
                    {
                        var targetVertexCount = (int)SimplificationTarget.Value;
                        while (targetVertexCount < VertexCount && VertexMerges.TryDequeue(out var merge))
                        {
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                            }
                        }
                    }
                    break;
                case MeshSimplificationTargetKind.ScaledTotalError:
                    {

                        var vertexPositions = Mesh.GetVertexPositions();

                        MinMaxBounds bounds = new()
                        {
                            Max = float.NegativeInfinity,
                            Min = float.PositiveInfinity,
                        };

                        for (int vertexIndex = 0; vertexIndex < vertexPositions.Length; vertexIndex++)
                        {
                            if (DiscardedVertex.IsSet(vertexIndex))
                            {
                                continue;
                            }
                            bounds.Encapsulate(vertexPositions[vertexIndex]);
                        }

                        if (!bounds.IsValid)
                        {
                            return;
                        }

                        var boundsScale = math.lengthsq(bounds.Extents);
                        var vertexCountScale = Mesh.vertexCount;
                        var maxTotalError = SimplificationTarget.Value * boundsScale * vertexCountScale;
                        var totalError = 0f;

                        while (VertexMerges.TryPeek(out var merge) && totalError + merge.Cost < maxTotalError)
                        {
                            VertexMerges.Dequeue();
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                                totalError += merge.Cost;
                            }
                        }
                    }
                    break;
                case MeshSimplificationTargetKind.AbsoluteTotalError:
                    {
                        var maxTotalError = SimplificationTarget.Value;
                        var totalError = 0f;

                        while (VertexMerges.TryPeek(out var merge) && totalError + merge.Cost < maxTotalError)
                        {
                            VertexMerges.Dequeue();
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                                totalError += merge.Cost;
                            }
                        }
                    }
                    break;

                case MeshSimplificationTargetKind.RelativeTriangleCount:
                    {
                        var targetTriangleCount = (int)(Triangles.Length * SimplificationTarget.Value);
                        while (targetTriangleCount < TriangleCount && VertexMerges.TryDequeue(out var merge))
                        {
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                            }
                        }
                    }
                    break;
                case MeshSimplificationTargetKind.AbsoluteTriangleCount:
                    {
                        var targetTriangleCount = (int)SimplificationTarget.Value;
                        while (targetTriangleCount < TriangleCount && VertexMerges.TryDequeue(out var merge))
                        {
                            if (IsValidMerge(merge))
                            {
                                ApplyMergeAndRecord(merge);
                            }
                        }
                    }
                    break;
            }
        }

        void BeginErrorCurve()
        {
            if (!RecordErrorCurve)
            {
                return;
            }
            ErrorCurve.Clear();
            ErrorCurve.Resize(Triangles.Length + 1, NativeArrayOptions.UninitializedMemory);
            for (int triangleCount = 0; triangleCount < ErrorCurve.Length; triangleCount++)
            {
                ErrorCurve[triangleCount] = triangleCount >= TriangleCount ? 0f : float.PositiveInfinity;
            }
            errorCurveMax = 0f;
            errorCurveCursor = TriangleCount - 1;
        }

        /// <summary>
        /// Applies the merge, then writes the running max of the applied merge costs to every triangle
        /// count this merge reached for the first time. A group collapse is one step with its summed cost.
        /// </summary>
        void ApplyMergeAndRecord(VertexMerge merge)
        {
            if (!ApplyMerge(merge))
            {
                return;
            }
            if (!RecordErrorCurve)
            {
                return;
            }
            errorCurveMax = math.max(errorCurveMax, merge.Cost);
            while (errorCurveCursor >= 0 && errorCurveCursor >= TriangleCount)
            {
                ErrorCurve[errorCurveCursor] = errorCurveMax;
                errorCurveCursor--;
            }
        }

        /// <summary>
        /// True when this merge would leave a locked vertex without triangles, which would discard it.
        /// The triangles that go away are the ones holding both vertices.
        /// </summary>
        readonly bool WouldOrphanLockedVertex(int vertexA, int vertexB)
        {
            var vertexALocked = VertexIsLockedBits.IsSet(vertexA);
            var vertexBLocked = VertexIsLockedBits.IsSet(vertexB);
            if (vertexALocked && vertexBLocked)
            {
                return true;
            }
            if ((vertexALocked || vertexBLocked) && !HasTriangleWithout(vertexA, vertexB) && !HasTriangleWithout(vertexB, vertexA))
            {
                // the locked one survives the merge but would keep no triangle
                return true;
            }
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertexB))
            {
                var triangle = Triangles[triangleIndex];
                if (!math.any(triangle == vertexA))
                {
                    continue;
                }
                var thirdVertex = math.csum(triangle) - vertexA - vertexB;
                if (VertexIsLockedBits.IsSet(thirdVertex) && !HasTriangleNotHoldingBoth(thirdVertex, vertexA, vertexB))
                {
                    return true;
                }
            }
            return false;
        }

        readonly bool HasTriangleWithout(int vertex, int otherVertex)
        {
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertex))
            {
                if (!math.any(Triangles[triangleIndex] == otherVertex))
                {
                    return true;
                }
            }
            return false;
        }

        readonly bool HasTriangleNotHoldingBoth(int vertex, int vertexA, int vertexB)
        {
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertex))
            {
                var triangle = Triangles[triangleIndex];
                if (!(math.any(triangle == vertexA) && math.any(triangle == vertexB)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Rebuilds the blend shape mask of a vertex after its deltas changed in a merge.
        /// </summary>
        void RefreshBlendShapeMask(int vertex)
        {
            var vertexCount = VertexPositionBuffer.Length;
            if (VertexBlendShapeMasks.Length == 0 || vertexCount == 0)
            {
                return;
            }
            var words = VertexBlendShapeMasks.Length / vertexCount;
            var thresholdSq = MeshSimplifierOptions.BlendShapeMaskThreshold * MeshSimplifierOptions.BlendShapeMaskThreshold;
            for (int word = 0; word < words; word++)
            {
                VertexBlendShapeMasks[vertex * words + word] = 0ul;
            }
            var shapeCount = math.min(BlendShapes.Length, words << 6);
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var frames = BlendShapes[shapeIndex].Frames;
                if (frames.Length == 0)
                {
                    continue;
                }
                if (math.lengthsq(frames[frames.Length - 1].DeltaVertices[vertex]) > thresholdSq)
                {
                    VertexBlendShapeMasks[vertex * words + (shapeIndex >> 6)] |= 1ul << (shapeIndex & 63);
                }
            }
        }

        readonly bool IsValidMerge(VertexMerge merge)
        {
            var vertexA = merge.VertexAIndex;
            var vertexB = merge.VertexBIndex;
            bool versionCheck = HasValidVersion(merge);
            if (!versionCheck)
            {
                return false;
            }
            if (merge.Kind != VertexMerge.KindFull)
            {
                return IsValidMove(merge);
            }
            if (Seams.IsEnabled)
            {
                // a full merge moves both ends, so both still have to be free on the current triangles
                var factory = MergeFactory;
                if (factory.ClassOf(vertexA) != SeamTopology.Free || factory.ClassOf(vertexB) != SeamTopology.Free)
                {
                    return false;
                }
            }
            if (WillMakeContainingTriangleFlipped(merge, vertexA, vertexB))
            {
                return false;
            }
            if (WillMakeContainingTriangleFlipped(merge, vertexB, vertexA))
            {
                return false;
            }
            if (VertexIsLockedBits.Length != 0 && WouldOrphanLockedVertex(vertexA, vertexB))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Rebuilds a half edge or group move on the current triangles. It has to come out as the same kind of
        /// move, and every pair has to pass: no flipped or sliver triangle around the mover, no locked vertex left
        /// without triangles. One failing pair rejects the whole group.
        /// </summary>
        readonly bool IsValidMove(VertexMerge merge)
        {
            if (Seams.SameGroup(merge.VertexAIndex, merge.VertexBIndex))
            {
                return false;
            }
            var factory = MergeFactory;
            var pairs = new UnsafeList<int2>(4, Allocator.Temp);
            var valid = factory.TryBuildMove(merge.VertexAIndex, merge.VertexBIndex, factory.ClassOf(merge.VertexAIndex), ref pairs, out var kind) && kind == merge.Kind;
            if (valid)
            {
                foreach (var pair in pairs)
                {
                    if (WillMakeContainingTriangleFlipped(VertexPositionBuffer[pair.y], pair.x, pair.y)
                        || (VertexIsLockedBits.Length != 0 && WouldOrphanLockedVertex(pair.x, pair.y)))
                    {
                        valid = false;
                        break;
                    }
                }
            }
            pairs.Dispose();
            return valid;
        }

        private readonly bool HasValidVersion(VertexMerge merge)
        {
            if (merge.Kind == VertexMerge.KindGroup)
            {
                var seams = Seams;
                return merge.VertexAVersion == seams.GroupVersion(merge.VertexAIndex) & merge.VertexBVersion == seams.GroupVersion(merge.VertexBIndex);
            }
            return merge.VertexAVersion == VertexVersions[merge.VertexAIndex] & merge.VertexBVersion == VertexVersions[merge.VertexBIndex];
        }

        readonly bool WillMakeContainingTriangleFlipped(VertexMerge merge, int vertex, int opponentVertex)
            => WillMakeContainingTriangleFlipped(merge.Position, vertex, opponentVertex);

        readonly bool WillMakeContainingTriangleFlipped(float3 position, int vertex, int opponentVertex)
        {
            foreach (var vertexAContainingTriangleIndex in VertexContainingTriangles.GetValuesForKey(vertex))
            {
                var triangle = Triangles[vertexAContainingTriangleIndex];

                if (math.any(triangle == opponentVertex))
                {
                    continue;
                }
                int vertex1, vertex2;

                if (triangle.x == vertex)
                {
                    vertex1 = triangle.y;
                    vertex2 = triangle.z;
                }
                else if (triangle.y == vertex)
                {
                    vertex1 = triangle.z;
                    vertex2 = triangle.x;
                }
                else
                {
                    vertex1 = triangle.x;
                    vertex2 = triangle.y;
                }
                var triangleVertexPositions = new float3x3
                {
                    c0 = position,
                    c1 = VertexPositionBuffer[vertex1],
                    c2 = VertexPositionBuffer[vertex2],
                };


                var originalTriangleNormal = TriangleNormals[vertexAContainingTriangleIndex];
                // A zero area triangle has no normal to flip (its plane is all zero now instead of NaN).
                if (math.all(originalTriangleNormal == 0f))
                {
                    continue;
                }
                var crossAfterMerge = math.cross(triangleVertexPositions.c1 - triangleVertexPositions.c0, triangleVertexPositions.c2 - triangleVertexPositions.c0);

                // Anti self-intersection: reject collapses that nearly flatten a triangle into a sliver
                // (its area shrinks toward zero). Such near degenerate triangles render as visible
                // cracks / self-intersection and produce unstable normals.
                if (Options.ConstrainOptimalPosition)
                {
                    var crossBeforeMerge = math.cross(triangleVertexPositions.c1 - VertexPositionBuffer[vertex], triangleVertexPositions.c2 - VertexPositionBuffer[vertex]);
                    const float degenerateAreaRatioSq = 1e-3f * 1e-3f;
                    if (math.lengthsq(crossAfterMerge) < math.lengthsq(crossBeforeMerge) * degenerateAreaRatioSq)
                    {
                        return true;
                    }
                }

                var triangleNormalAfterMerge = math.normalize(crossAfterMerge);
                var dot = math.dot(originalTriangleNormal, triangleNormalAfterMerge);

                if (dot < Options.MinNormalDot)
                {
                    return true;
                }
            }
            return false;
        }

        readonly bool IsDiscardedVertex(int vertex) => DiscardedVertex.IsSet(vertex);
        
        void DiscardVertex(int vertex)
        {
            if (!IsDiscardedVertex(vertex))
            {
                DiscardedVertex.Set(vertex, true);
                VertexCount--;
                VertexVersions[vertex]++;
            }

        }
        readonly bool IsDiscardedTriangle(int triangleIndex) => DiscardedTriangle.IsSet(triangleIndex);
        void DiscardTriangle(int triangleIndex)
        {
            if (!IsDiscardedTriangle(triangleIndex))
            {
                DiscardedTriangle.Set(triangleIndex, true);
                TriangleCount--;
            }

        }
        public bool ApplyMerge(VertexMerge merge)
        {
            using (ProfilerMarkers.ApplyMerge.Auto())
            {
                if (merge.Kind != VertexMerge.KindFull)
                {
                    return ApplyMove(merge);
                }

                var vertexA = merge.VertexAIndex;
                var vertexB = merge.VertexBIndex;
                int2 vertexPair = new(math.min(vertexA, vertexB), math.max(vertexA, vertexB));

                var isSmartLink = SmartLinks.Contains(vertexPair);

                var vertexAIsBorderEdge = VertexIsBorderEdgeBits.IsSet(vertexA);
                var vertexBIsBorderEdge = VertexIsBorderEdgeBits.IsSet(vertexB);

                var containsBorderEdge = vertexAIsBorderEdge | vertexBIsBorderEdge;

                var preservedVertexPredicator = PreservedVertexPredicator;

                var shouldPreserveVertexA = preservedVertexPredicator.IsPreserved(vertexA);

                var shouldPreserveVertexB = preservedVertexPredicator.IsPreserved(vertexB);

                if (shouldPreserveVertexB)
                {
                    (vertexA, vertexB) = (vertexB, vertexA);
                }

                if (!(shouldPreserveVertexA | shouldPreserveVertexB))
                {
                    MergeVertexAttributeData(vertexA, vertexB, merge.Position);
                    RefreshBlendShapeMask(vertexA);

                    // Attribute aware solve already produced the UV that minimizes texture distortion;
                    // use it instead of the linear/barycentric blend.
                    if (Options.UseAttributeAwareError && Options.UvErrorWeight > 0f && VertexTexCoord0Buffer.Length != 0)
                    {
                        var mergedTexCoord0 = VertexTexCoord0Buffer[vertexA];
                        mergedTexCoord0.xy = merge.OptimalUv;
                        VertexTexCoord0Buffer[vertexA] = mergedTexCoord0;
                    }
                }

                VertexIsBorderEdgeBits.Set(vertexA, containsBorderEdge);
                VertexIsBorderEdgeBits.Set(vertexB, containsBorderEdge);



                VertexErrorQuadrics.ElementAt(vertexA) += VertexErrorQuadrics[vertexB];
                if (Options.UseAttributeAwareError && VertexAttributeErrorQuadrics.Length != 0)
                {
                    VertexAttributeErrorQuadrics.ElementAt(vertexA) += VertexAttributeErrorQuadrics[vertexB];
                }

                VertexVersions.ElementAt(vertexA)++;

                var nonReferencedVertices = new UnsafeList<int>(16, Allocator.Temp);
                CollapseInto(vertexA, vertexB, ref nonReferencedVertices);
                if (!nonReferencedVertices.Contains(vertexA))
                {
                    RecomputeMergesAround(vertexA, vertexB);
                }
                DiscardNonReferencedVertices(nonReferencedVertices);
                nonReferencedVertices.Dispose();
                return true;
            }
        }

        /// <summary>
        /// Points every triangle of vertexB at vertexA, drops the triangles that held both, and collects the
        /// vertices left without a triangle (vertexB always).
        /// </summary>
        void CollapseInto(int vertexA, int vertexB, ref UnsafeList<int> nonReferencedVertices)
        {
            {

                using var vertexBContainingTriangles = new UnsafeList<int>(16, Allocator.Temp);
                foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertexB))
                {
                    vertexBContainingTriangles.Add(triangleIndex);
                }

                VertexContainingTriangles.Remove(vertexB);
                nonReferencedVertices.Add(vertexB);

                // Replace reference to vertexB in triangles to vertexA.
                using (ProfilerMarkers.ResolveMergedVertexReferences.Auto())
                {
                    using var discardingTriangles = new UnsafeList<int>(vertexBContainingTriangles.Length, Allocator.Temp);
                    using (ProfilerMarkers.CollectMergedVertexContainingTriangles.Auto())
                    {
                        foreach (var triangleIndex in vertexBContainingTriangles)
                        {
                            ref var triangleVertices = ref GetTriangleVertices(triangleIndex);


                            // Replace vertexB in triangle to vertexA.
                            if (math.any(triangleVertices == vertexA))
                            {
                                // Triangle vertices (a, b, ?) => (a, a, ?)
                                // The triangle has only 2 vertices... discarding it
                                discardingTriangles.Add(triangleIndex);
                            }
                            else
                            {
                                // Triangle vertices (b, ?, ?) => (a, ?, ?)
                                triangleVertices = math.select(triangleVertices, vertexA, triangleVertices == vertexB);
                                VertexContainingTriangles.Add(vertexA, triangleIndex);
                            }
                        }
                    }


                    foreach (var triangleIndex in discardingTriangles)
                    {
                        // Discard vertex which doesn't belong to any triangle.

                        // We need to collect them to discardingTriangles temporary
                        // because the vertex potentially gets new belonging triangle
                        // while iterating.
                        var triangleVertices = GetTriangleVertices(triangleIndex);
                        for (int i = 0; i < 3; i++)
                        {
                            var vertex = triangleVertices[i];
                            if (vertex == vertexB)
                            {
                                continue;
                            }
                            VertexContainingTriangles.Remove(vertex, triangleIndex);
                            if (!VertexContainingTriangles.ContainsKey(vertex))
                            {

                                nonReferencedVertices.Add(vertex);
                            }
                        }
                        DiscardTriangle(triangleIndex);
                    }
                }


            }
        }

        /// <summary>
        /// Recomputes the merges of vertexA (the survivor) with its opponents and takes over the opponents of vertexB.
        /// </summary>
        void RecomputeMergesAround(int vertexA, int vertexB)
        {
            {
                // Replace all reference to vertexB in merge opponents lookup.
                // Also, we need to recompute merges.

                {
                    using (ProfilerMarkers.RecomputeMerges.Auto())
                    {
                        foreach (var vertexAOpponentVertex in VertexMergeOpponentVertices.GetValuesForKey(vertexA))
                        {
                            if (MergeFactory.TryComputeMerge(new int2(vertexA, vertexAOpponentVertex), out VertexMerge merge))
                            {
                                VertexMerges.Enqueue(merge);
                            }

                        }

                        // Recompute merge with vertexB since it was merged into vertexA
                        {
                            using var vertexBOpponentVertices = new UnsafeList<int>(16, Allocator.Temp);
                            foreach (var vertexBOpponentVertex in VertexMergeOpponentVertices.GetValuesForKey(vertexB))
                            {
                                vertexBOpponentVertices.Add(vertexBOpponentVertex);
                            }
                            foreach (var vertexBOpponentVertex in vertexBOpponentVertices)
                            {
                                if (vertexBOpponentVertex == vertexA)
                                {
                                    continue;
                                }

                                foreach (var vertexAOpponentVertex in VertexMergeOpponentVertices.GetValuesForKey(vertexA))
                                {
                                    if (vertexBOpponentVertex == vertexAOpponentVertex)
                                    {
                                        goto NextVertexBOpponent;
                                    }
                                }

                                if (MergeFactory.TryComputeMerge(new int2(vertexA, vertexBOpponentVertex), out VertexMerge merge))
                                {
                                    VertexMerges.Enqueue(merge);
                                    VertexMergeOpponentVertices.Add(vertexA, vertexBOpponentVertex);
                                    VertexMergeOpponentVertices.Add(vertexBOpponentVertex, vertexA);
                                }
                            NextVertexBOpponent:;
                            }
                        }
                    }

                }
            }
        }

        /// <summary>
        /// Discards the collected vertices that are not discarded yet and unlinks them from the merge opponents.
        /// </summary>
        void DiscardNonReferencedVertices(UnsafeList<int> nonReferencedVertices)
        {
            {
                using (ProfilerMarkers.DiscardNonReferencedVertices.Auto())
                {

                    foreach (var nonReferencedVertex in nonReferencedVertices)
                    {
                        if (IsDiscardedVertex(nonReferencedVertex))
                        {
                            continue;
                        }
                        using var opponentVertices = new UnsafeList<int>(16, Allocator.Temp);
                        foreach (var opponentVertex in VertexMergeOpponentVertices.GetValuesForKey(nonReferencedVertex))
                        {
                            opponentVertices.Add(opponentVertex);
                        }
                        VertexMergeOpponentVertices.Remove(nonReferencedVertex);
                        foreach (var opponentVertex in opponentVertices)
                        {

                            VertexMergeOpponentVertices.Remove(opponentVertex, nonReferencedVertex);
                        }
                        DiscardVertex(nonReferencedVertex);
                    }
                }
            }
        }
        /// <summary>
        /// Half edge or group collapse: every mover goes onto its target, which keeps its position and attributes.
        /// All pairs change the triangles first, then the merges around every target are recomputed on the final
        /// triangles (all sides of the seam), then the vertices left without triangles are discarded.
        /// </summary>
        bool ApplyMove(VertexMerge merge)
        {
            var factory = MergeFactory;
            var pairs = new UnsafeList<int2>(4, Allocator.Temp);
            if (!factory.TryBuildMove(merge.VertexAIndex, merge.VertexBIndex, factory.ClassOf(merge.VertexAIndex), ref pairs, out var kind) || kind != merge.Kind)
            {
                pairs.Dispose();
                return false;
            }
            var nonReferencedVertices = new UnsafeList<int>(16, Allocator.Temp);
            foreach (var pair in pairs)
            {
                var mover = pair.x;
                var target = pair.y;
                if (VertexIsBorderEdgeBits.IsSet(mover))
                {
                    VertexIsBorderEdgeBits.Set(target, true);
                }
                VertexErrorQuadrics.ElementAt(target) += VertexErrorQuadrics[mover];
                if (Options.UseAttributeAwareError && VertexAttributeErrorQuadrics.Length != 0)
                {
                    VertexAttributeErrorQuadrics.ElementAt(target) += VertexAttributeErrorQuadrics[mover];
                }
                VertexVersions.ElementAt(target)++;
                CollapseInto(target, mover, ref nonReferencedVertices);
            }
            foreach (var pair in pairs)
            {
                if (!nonReferencedVertices.Contains(pair.y))
                {
                    RecomputeMergesAround(pair.y, pair.x);
                }
            }
            DiscardNonReferencedVertices(nonReferencedVertices);
            nonReferencedVertices.Dispose();
            pairs.Dispose();
            return true;
        }

        readonly ref int3 GetTriangleVertices(int triangleIndex)
        {
            return ref Triangles.ElementAt(triangleIndex);
        }
        void MergeVertexAttributeData(int vertexA, int vertexB, float3 mergePosition)
        {
            using (ProfilerMarkers.MergeVertexAttributeData.Auto())
            {
                if (Options.UseBarycentricCoordinateInterpolation)
                {

                    foreach (var vertexAContainingTriangleIndex in VertexContainingTriangles.GetValuesForKey(vertexA))
                    {
                        var triangle = Triangles[vertexAContainingTriangleIndex];
                        if (math.any(triangle == vertexB))
                        {
                            float3x3 triangleVertexPositions = new()
                            {
                                c0 = VertexPositionBuffer[triangle.x],
                                c1 = VertexPositionBuffer[triangle.y],
                                c2 = VertexPositionBuffer[triangle.z],
                            };


                            float lerpFactor = ComputeLerpFactor(vertexA, vertexB, mergePosition);
                            var barycentricCoordinate = ComputeBarycentricCoordinate(triangleVertexPositions, mergePosition);


                            VertexPositionBuffer[vertexA] = mergePosition;
                            MergeNormalVertexAttribute(VertexNormalBuffer, triangle, vertexA, barycentricCoordinate);
                            MergeNormalVertexAttribute(VertexTangentBuffer, triangle, vertexA, barycentricCoordinate);

                            MergeVectorVertexAttribute(VertexColorBuffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord0Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord1Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord2Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord3Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord4Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord5Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord6Buffer, triangle, vertexA, barycentricCoordinate);
                            MergeVectorVertexAttribute(VertexTexCoord7Buffer, triangle, vertexA, barycentricCoordinate);


                            MergeBlendWeightAndIndices(vertexA, vertexB, lerpFactor);

                            MergeBlendShapes(triangle, vertexA, barycentricCoordinate);
                            return;
                        }
                    }
                }
                else
                {
                    float lerpFactor = ComputeLerpFactor(vertexA, vertexB, mergePosition);

                    VertexPositionBuffer[vertexA] = mergePosition;

                    MergeNormalVertexAttribute(VertexNormalBuffer, vertexA, vertexB, lerpFactor);
                    MergeNormalVertexAttribute(VertexTangentBuffer, vertexA, vertexB, lerpFactor);

                    MergeVectorVertexAttribute(VertexColorBuffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord0Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord1Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord2Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord3Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord4Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord5Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord6Buffer, vertexA, vertexB, lerpFactor);
                    MergeVectorVertexAttribute(VertexTexCoord7Buffer, vertexA, vertexB, lerpFactor);

                    MergeBlendWeightAndIndices(vertexA, vertexB, lerpFactor);

                    MergeBlendShapes(vertexA, vertexB, lerpFactor);
                }
            }



        }
        readonly float ComputeLerpFactor(int vertexA, int vertexB, float3 mergePosition)
        {
            var a = VertexPositionBuffer[vertexA];
            var b = VertexPositionBuffer[vertexB];
            var c = mergePosition;
            var ab = b - a;
            var ac = c - a;
            return math.saturate(math.dot(ab, ac) / math.lengthsq(ab));
        }

        readonly float3 ComputeBarycentricCoordinate(float3x3 triangleVertexPositions, float3 position)
        {
            var AB = triangleVertexPositions.c1 - triangleVertexPositions.c0;
            var AC = triangleVertexPositions.c2 - triangleVertexPositions.c0;
            var AP = position - triangleVertexPositions.c0;

            var dotABAB = math.dot(AB, AB);
            var dotABAC = math.dot(AB, AC);
            var dotACAC = math.dot(AC, AC);
            var dotAPAB = math.dot(AP, AB);
            var dotAPAC = math.dot(AP, AC);
            var denom = dotABAB * dotACAC - dotABAC * dotABAC;

            // Make sure the denominator is not too small to cause math problems
            const float DenomEpilson = 0.00000001f;
            if (math.abs(denom) < DenomEpilson)
            {
                denom = DenomEpilson;
            }

            var y = (dotACAC * dotAPAB - dotABAC * dotAPAC) / denom;
            var z = (dotABAB * dotAPAC - dotABAC * dotAPAB) / denom;
            var x = 1 - y - z;
            return new(x, y, z);
        }

        static void MergeVectorVertexAttribute(Span<float4> vertexAttributeData, int vertexA, int vertexB, float lerpFactor)
        {
            if (!vertexAttributeData.IsEmpty)
            {
                vertexAttributeData[vertexA] = math.lerp(vertexAttributeData[vertexA], vertexAttributeData[vertexB], lerpFactor);
            }
        }
        static void MergeVectorVertexAttribute(Span<float4> vertexAttributeData, int3 triangle, int destinationVertex, float3 barycentricCoordinate)
        {
            if (!vertexAttributeData.IsEmpty)
            {
                vertexAttributeData[destinationVertex] = vertexAttributeData[triangle.x] * barycentricCoordinate.x + vertexAttributeData[triangle.y] * barycentricCoordinate.y + vertexAttributeData[triangle.z] * barycentricCoordinate.z;
            }
        }
        static void MergeNormalVertexAttribute(Span<float4> vertexAttributeData, int vertexA, int vertexB, float lerpFactor)
        {
            if (!vertexAttributeData.IsEmpty)
            {
                vertexAttributeData[vertexA].xyz = math.normalizesafe(math.lerp(vertexAttributeData[vertexA].xyz, vertexAttributeData[vertexB].xyz, lerpFactor));
            }
        }
        static void MergeNormalVertexAttribute(Span<float4> vertexAttributeData, int3 triangle, int destinationVertex, float3 barycentricCoordinate)
        {
            if (!vertexAttributeData.IsEmpty)
            {
                vertexAttributeData[destinationVertex].xyz = math.normalizesafe(vertexAttributeData[triangle.x].xyz * barycentricCoordinate.x + vertexAttributeData[triangle.y].xyz * barycentricCoordinate.y + vertexAttributeData[triangle.z].xyz * barycentricCoordinate.z);
            }
        }

        void MergeBlendShapes(int vertexA, int vertexB, float lerpFactor)
        {
            for (int shapeIndex = 0; shapeIndex < BlendShapes.Length; shapeIndex++)
            {
                var frames = BlendShapes[shapeIndex].Frames;
                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    var deltaVertices = frame.DeltaVertices;
                    var deltaNormals = frame.DeltaNormals;
                    var deltaTangents = frame.DeltaTangents;
                    deltaVertices[vertexA] = math.lerp(deltaVertices[vertexA], deltaVertices[vertexB], lerpFactor);


                    deltaNormals[vertexA] = math.lerp(deltaNormals[vertexA], deltaNormals[vertexB], lerpFactor);

                    deltaTangents[vertexA] = math.lerp(deltaTangents[vertexA], deltaTangents[vertexB], lerpFactor);
                }
            }
        }


        void MergeBlendShapes(int3 triangle, int destinationVertex, float3 barycentricCoordinate)
        {
            for (int shapeIndex = 0; shapeIndex < BlendShapes.Length; shapeIndex++)
            {
                var frames = BlendShapes[shapeIndex].Frames;
                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    var deltaVertices = frame.DeltaVertices;
                    var deltaNormals = frame.DeltaNormals;
                    var deltaTangents = frame.DeltaTangents;
                    deltaVertices[destinationVertex] = deltaVertices[triangle.x] * barycentricCoordinate.x + deltaVertices[triangle.y] * barycentricCoordinate.y + deltaVertices[triangle.z] * barycentricCoordinate.z;
                    deltaNormals[destinationVertex] = deltaNormals[triangle.x] * barycentricCoordinate.x + deltaNormals[triangle.y] * barycentricCoordinate.y + deltaNormals[triangle.z] * barycentricCoordinate.z;
                    deltaTangents[destinationVertex] = deltaTangents[triangle.x] * barycentricCoordinate.x + deltaTangents[triangle.y] * barycentricCoordinate.y + deltaTangents[triangle.z] * barycentricCoordinate.z;


                }
            }
        }

        [SkipLocalsInit]
        private void MergeBlendWeightAndIndices(int vertexA, int vertexB, float lerpFactor)
        {
            if (VertexBlendWeightBuffer.Length != 0 && VertexBlendIndicesBuffer.Length != 0)
            {
                var vertexBlendWeights = VertexBlendWeightBuffer.AsSpan();
                var vertexBlendIndices = VertexBlendIndicesBuffer.AsSpan();

                var dimension = VertexBlendIndicesBuffer.Length / VertexPositionBuffer.Length;

                Span<float> blendWeightsAB = stackalloc float[dimension * 2];
                Span<uint> blendIndicesAB = stackalloc uint[dimension * 2];

                var blendWeightsA = blendWeightsAB[..dimension];
                vertexBlendWeights.Slice(vertexA * dimension, dimension).CopyTo(blendWeightsA);
                var blendWeightsB = blendWeightsAB.Slice(dimension, dimension);
                vertexBlendWeights.Slice(vertexB * dimension, dimension).CopyTo(blendWeightsB);

                var blendIndicesA = blendIndicesAB[..dimension];
                vertexBlendIndices.Slice(vertexA * dimension, dimension).CopyTo(blendIndicesA);
                var blendIndicesB = blendIndicesAB.Slice(dimension, dimension);
                vertexBlendIndices.Slice(vertexB * dimension, dimension).CopyTo(blendIndicesB);

                foreach (ref var weightA in blendWeightsA)
                {
                    weightA *= 1 - lerpFactor;
                }
                foreach (ref var weightB in blendWeightsB)
                {
                    weightB *= lerpFactor;
                }

                for (int a = 0; a < blendIndicesA.Length; a++)
                {
                    var indexA = blendIndicesA[a];

                    for (int b = 0; b < blendIndicesB.Length; b++)
                    {
                        var indexB = blendIndicesB[b];

                        if (indexA == indexB)
                        {
                            ref var weightB = ref blendWeightsB[b];

                            blendWeightsA[a] += weightB;
                            weightB = float.NegativeInfinity;

                            break;
                        }
                    }
                }

                var mergedBlendWeights = vertexBlendWeights.Slice(vertexA * dimension, dimension);
                var mergedBlendIndices = vertexBlendIndices.Slice(vertexA * dimension, dimension);

                for (int mergedBlendWeightIndex = 0; mergedBlendWeightIndex < dimension; mergedBlendWeightIndex++)
                {
                    var maxBlendWeightIndex = 0;
                    var maxBlendWeight = blendWeightsAB[maxBlendWeightIndex];
                    for (int i = 1; i < blendWeightsAB.Length; i++)
                    {
                        var blendWeight = blendWeightsAB[i];

                        if (blendWeight > maxBlendWeight)
                        {
                            maxBlendWeight = blendWeight;
                            maxBlendWeightIndex = i;
                        }
                    }
                    mergedBlendWeights[mergedBlendWeightIndex] = maxBlendWeight;
                    mergedBlendIndices[mergedBlendWeightIndex] = blendIndicesAB[maxBlendWeightIndex];

                    blendWeightsAB[maxBlendWeightIndex] = float.NegativeInfinity;
                }

                var mergedBlendWeightSum = 0f;
                foreach (var weight in mergedBlendWeights)
                {
                    mergedBlendWeightSum += weight;
                }

                var mergedBlendWeightNormalizer = math.rcp(mergedBlendWeightSum);

                foreach (ref var weight in mergedBlendWeights)
                {
                    weight *= mergedBlendWeightNormalizer;
                }
            }
        }
    }


    struct PreservedVertexPredicator
    {
        public NativeArray<uint> VertexBlendIndicesBuffer;
        public NativeBitArray VertexIsLockedBits;
        public NativeBitArray VertexIsBorderEdgeBits;
        public NativeBitArray VertexIsUVSeamBits;
        public NativeBitArray PreserveBorderEdgesBoneIndices;
        public int VertexBoneCount;
        public bool PreserveBorderEdges;
        public bool PreserveUVSeams;
        public readonly bool IsPreserved(int vertexIndex)
        {
            // Locked vertices never move and never go away.
            if (VertexIsLockedBits.Length != 0 && VertexIsLockedBits.IsSet(vertexIndex))
            {
                return true;
            }
            // UV seam vertices are duplicated at texture seams; collapsing across them smears the texture.
            if (PreserveUVSeams && VertexIsUVSeamBits.Length != 0 && VertexIsUVSeamBits.IsSet(vertexIndex))
            {
                return true;
            }
            if (VertexIsBorderEdgeBits.IsSet(vertexIndex))
            {
                if (PreserveBorderEdges)
                {
                    return true;
                }
                if (VertexBlendIndicesBuffer.Length > 0 && PreserveBorderEdgesBoneIndices.Length > 0)
                {
                    var vertexBlendIndices = VertexBlendIndicesBuffer.GetSubArray(vertexIndex * VertexBoneCount, VertexBoneCount);
                    for (int i = 0; i < vertexBlendIndices.Length; i++)
                    {
                        if (PreserveBorderEdgesBoneIndices.IsSet((int)vertexBlendIndices[i]))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}


