using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
namespace BAAA.Simplifier
{
    struct MergeFactory
    {
        public NativeArray<float3> VertexPositionBuffer;
        public NativeArray<uint> VertexBlendIndicesBuffer;
        public NativeArray<float> VertexBlendWeightBuffer;
        /// <summary>
        /// Per vertex lock flags. Empty when nothing is locked.
        /// </summary>
        public NativeBitArray VertexIsLockedBits;
        public NativeList<BlendShapeData> BlendShapes;
        /// <summary>
        /// Per vertex bit mask of the blend shapes that move the vertex (see BuildBlendShapeMasksJob). Empty when the stretch rule is off.
        /// </summary>
        public NativeArray<ulong> VertexBlendShapeMasks;
        public float MinBoneOverlap;
        public float BlendShapeStretch;
        public NativeArray<ErrorQuadric> VertexErrorQuadrics;
        public NativeArray<AttributeErrorQuadric> VertexAttributeErrorQuadrics;
        public NativeArray<float4> VertexTexCoord0Buffer;
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        public NativeBitArray VertexIsBorderEdgeBits;
        public NativeBitArray VertexIsUVSeamBits;
        public NativeBitArray PreserveBorderEdgesBoneIndices;
        public NativeArray<float3> TriangleNormals;
        /// <summary>
        /// Per vertex bit mask of the sub meshes (materials) the vertex belongs to.
        /// Empty when sub mesh boundary preservation is disabled.
        /// </summary>
        public NativeArray<uint> VertexContainingSubMeshIndices;
        public bool PreserveBorderEdges;
        public bool PreserveSurfaceCurvature;
        public bool PreserveSubMeshBoundaries;
        public bool PreserveUVSeams;
        public bool ConstrainOptimalPosition;
        public float MaxCollapseDisplacementFactor;
        public bool UseAttributeAwareError;
        public float UvErrorWeight;
        /// <summary>
        /// Current triangles plus position groups, for the seam and border rules.
        /// </summary>
        public SeamTopology Seams;

        PreservedVertexPredicator PreservedVertexPredicator => new()
        {
            VertexBlendIndicesBuffer = VertexBlendIndicesBuffer,
            VertexIsBorderEdgeBits = VertexIsBorderEdgeBits,
            VertexIsUVSeamBits = VertexIsUVSeamBits,
            PreserveBorderEdgesBoneIndices = PreserveBorderEdgesBoneIndices,
            VertexIsLockedBits = VertexIsLockedBits,
            VertexBoneCount = VertexBlendIndicesBuffer.Length / VertexPositionBuffer.Length,
            PreserveBorderEdges = PreserveBorderEdges,
            PreserveUVSeams = PreserveUVSeams,
        };

        [BurstCompile(CompileSynchronously = true)]
        static class ProfilerMarkers
        {
            public static readonly ProfilerMarker TryComputeMerge = new(nameof(TryComputeMerge));
            public static readonly ProfilerMarker ComputeCurvatureError = new(nameof(ComputeCurvatureError));
        }

        /// <summary>
        /// Builds the best collapse for this vertex pair, or returns false.
        /// <para>
        /// When both vertices are free this is the original Meshia merge (optimal position, blended attributes).
        /// Otherwise one vertex moves onto the other, which stays exactly where it is and keeps its attributes.
        /// A free vertex may move onto anything, a border vertex only along its border, and a seam vertex only
        /// together with the rest of its position group, each member onto its partner in the target group. When both
        /// directions are allowed the cheaper one wins. Every rule (locks, bone overlap, blend shape stretch, sub mesh
        /// boundaries) has to pass for every moving pair, and a group costs the sum of its pairs.
        /// </para>
        /// </summary>
        public bool TryComputeMerge(int2 vertices, out VertexMerge merge)
        {
            merge = new VertexMerge
            {
                VertexAIndex = vertices.x,
                VertexBIndex = vertices.y,
                Position = float.NaN,
                OptimalUv = float.NaN,
                Cost = float.PositiveInfinity,
                Kind = VertexMerge.KindFull,
            };
            if (Seams.IsEnabled)
            {
                if (!Seams.IsLive(vertices.x) || !Seams.IsLive(vertices.y))
                {
                    // discarded, or left without triangles by the collapse being applied right now
                    return false;
                }
                if (Seams.SameGroup(vertices.x, vertices.y))
                {
                    // copies of one point (seam twins, smart links between them) are never welded
                    return false;
                }
                var classX = ClassOf(vertices.x);
                var classY = ClassOf(vertices.y);
                if (classX != SeamTopology.Free || classY != SeamTopology.Free)
                {
                    return TryComputeMove(vertices.x, vertices.y, classX, classY, ref merge);
                }
            }
            if (!TryComputeMerge(vertices, out var position, out var optimalUv, out var cost))
            {
                return false;
            }
            merge.Position = position;
            merge.OptimalUv = optimalUv;
            merge.Cost = cost;
            merge.VertexAVersion = Seams.VertexVersions[vertices.x];
            merge.VertexBVersion = Seams.VertexVersions[vertices.y];
            return true;
        }

        /// <summary>
        /// The vertex's <see cref="SeamTopology"/> class, with locked and preserved vertices counted as fixed.
        /// </summary>
        public int ClassOf(int vertex) => PreservedVertexPredicator.IsPreserved(vertex) ? SeamTopology.Fixed : Seams.Classify(vertex);

        bool TryComputeMove(int vertexX, int vertexY, int classX, int classY, ref VertexMerge merge)
        {
            var pairs = new UnsafeList<int2>(4, Allocator.Temp);
            var costXY = 0f;
            var costYX = 0f;
            var canMoveX = TryBuildMove(vertexX, vertexY, classX, ref pairs, out var kindXY) && TryMoveCost(pairs, out costXY);
            var canMoveY = TryBuildMove(vertexY, vertexX, classY, ref pairs, out var kindYX) && TryMoveCost(pairs, out costYX);
            pairs.Dispose();
            if (!canMoveX && !canMoveY)
            {
                return false;
            }
            var xMoves = canMoveX && (!canMoveY || costXY <= costYX);
            var mover = xMoves ? vertexX : vertexY;
            var target = xMoves ? vertexY : vertexX;
            var kind = xMoves ? kindXY : kindYX;
            merge.VertexAIndex = mover;
            merge.VertexBIndex = target;
            merge.Kind = kind;
            merge.Position = VertexPositionBuffer[target];
            merge.OptimalUv = VertexTexCoord0Buffer.Length != 0 ? VertexTexCoord0Buffer[target].xy : float2.zero;
            merge.Cost = xMoves ? costXY : costYX;
            if (kind == VertexMerge.KindGroup)
            {
                merge.VertexAVersion = Seams.GroupVersion(mover);
                merge.VertexBVersion = Seams.GroupVersion(target);
            }
            else
            {
                merge.VertexAVersion = Seams.VertexVersions[mover];
                merge.VertexBVersion = Seams.VertexVersions[target];
            }
            return true;
        }

        /// <summary>
        /// The (mover, target) pairs that collapse when <paramref name="mover"/> goes onto <paramref name="target"/>,
        /// or false when its class doesn't allow that move. Runs again right before a collapse is applied, on the
        /// current triangles, so the pairs are always rebuilt the same way.
        /// </summary>
        public bool TryBuildMove(int mover, int target, int moverClass, ref UnsafeList<int2> pairs, out int kind)
        {
            pairs.Clear();
            kind = VertexMerge.KindHalfEdge;
            switch (moverClass)
            {
                case SeamTopology.Free:
                    pairs.Add(new int2(mover, target));
                    return true;
                case SeamTopology.BorderLine:
                    if (!Seams.HasOpenEdgeBetween(mover, target))
                    {
                        return false;
                    }
                    pairs.Add(new int2(mover, target));
                    return true;
                case SeamTopology.SeamLine:
                    kind = VertexMerge.KindGroup;
                    if (!Seams.TryPairGroups(mover, target, ref pairs))
                    {
                        return false;
                    }
                    var predicator = PreservedVertexPredicator;
                    foreach (var pair in pairs)
                    {
                        // a locked twin keeps the whole group in place
                        if (predicator.IsPreserved(pair.x))
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    return false;
            }
        }

        bool TryMoveCost(UnsafeList<int2> pairs, out float cost)
        {
            cost = 0f;
            foreach (var pair in pairs)
            {
                if (!PassesPairRules(pair.x, pair.y))
                {
                    return false;
                }
                cost += HalfEdgeCost(pair.x, pair.y);
            }
            return math.isfinite(cost);
        }

        bool PassesPairRules(int mover, int target)
        {
            if (PreserveSubMeshBoundaries
                && VertexContainingSubMeshIndices.Length != 0
                && VertexContainingSubMeshIndices[mover] != VertexContainingSubMeshIndices[target])
            {
                return false;
            }
            if (MinBoneOverlap > 0f && !HasEnoughBoneOverlap(mover, target))
            {
                return false;
            }
            if (BlendShapeStretch > 0f && IsTornByBlendShapes(mover, target))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Quadric error of mover and target together, at the target's own position (and UV0).
        /// </summary>
        float HalfEdgeCost(int mover, int target)
        {
            var position = VertexPositionBuffer[target];
            float error;
            if (UseAttributeAwareError && UvErrorWeight > 0f && VertexAttributeErrorQuadrics.Length != 0)
            {
                var uv = VertexTexCoord0Buffer.Length != 0 ? VertexTexCoord0Buffer[target].xy : float2.zero;
                error = (VertexAttributeErrorQuadrics[mover] + VertexAttributeErrorQuadrics[target]).ComputeError(position, uv * UvErrorWeight);
            }
            else
            {
                error = (VertexErrorQuadrics[mover] + VertexErrorQuadrics[target]).ComputeError(position);
            }
            return error + CurvatureError(new int2(mover, target));
        }

        public bool TryComputeMerge(int2 vertices, out float3 position, out float2 optimalUv, out float cost)
        {
            using (ProfilerMarkers.TryComputeMerge.Auto())
            {
                optimalUv = default;

                // Material boundary lock: never merge vertices that belong to a different set of
                // sub meshes (materials). Merging across a material boundary corrupts the per sub mesh
                // vertex ranges reconstructed in WriteToMeshDataJob (the bit mask is not updated after a merge)
                // and bleeds materials across their boundary.
                if (PreserveSubMeshBoundaries
                    && VertexContainingSubMeshIndices.Length != 0
                    && VertexContainingSubMeshIndices[vertices.x] != VertexContainingSubMeshIndices[vertices.y])
                {
                    position = float.NaN;
                    optimalUv = float.NaN;
                    cost = float.PositiveInfinity;
                    return false;
                }

                // Asset Assistant rules. They sit here so they apply to every merge, smart links included.
                if (MinBoneOverlap > 0f && !HasEnoughBoneOverlap(vertices.x, vertices.y))
                {
                    return Reject(out position, out optimalUv, out cost);
                }
                if (BlendShapeStretch > 0f && IsTornByBlendShapes(vertices.x, vertices.y))
                {
                    return Reject(out position, out optimalUv, out cost);
                }

                // Attribute aware path: include UV0 in the error metric and solve position + UV together.
                if (UseAttributeAwareError && UvErrorWeight > 0f && VertexAttributeErrorQuadrics.Length != 0)
                {
                    return TryComputeMergeAttributeAware(vertices, out position, out optimalUv, out cost);
                }

                var q = VertexErrorQuadrics[vertices.x] + VertexErrorQuadrics[vertices.y];

                var positionX = VertexPositionBuffer[vertices.x];
                var positionY = VertexPositionBuffer[vertices.y];

                float vertexError;

                var preservedVertexPredicator = PreservedVertexPredicator;

                var preserveX = preservedVertexPredicator.IsPreserved(vertices.x);
                var preserveY = preservedVertexPredicator.IsPreserved(vertices.y);
                if (preserveX && preserveY)
                {
                    position = float.NaN;
                    cost = float.PositiveInfinity;
                    return false;
                }
                else if (preserveX)
                {
                    position = positionX;
                    goto ComputeVertexError;
                }
                else if (preserveY)
                {
                    position = positionY;
                    goto ComputeVertexError;
                }

                var determinant = q.Determinant1();
                var hasOptimalPosition = determinant != 0;
                if (hasOptimalPosition)
                {
                    var optimalPosition = new float3
                    {
                        x = -1 / determinant * q.Determinant2(),
                        y = 1 / determinant * q.Determinant3(),
                        z = -1 / determinant * q.Determinant4(),
                    };

                    // A near singular quadric can produce inf or NaN here. Use the candidates instead.
                    if (!math.all(math.isfinite(optimalPosition)))
                    {
                        hasOptimalPosition = false;
                    }

                    // Anti self-intersection: the unconstrained quadric optimum can fly far away from the
                    // collapsed edge (especially on flat or near singular regions), producing spikes that poke
                    // through other surfaces. Reject it when it lands outside the local neighborhood of the edge
                    // and fall back to the endpoint/midpoint candidates instead.
                    if (ConstrainOptimalPosition)
                    {
                        var edgeMidpoint = (positionX + positionY) * 0.5f;
                        var maxDisplacement = MaxCollapseDisplacementFactor * math.distance(positionX, positionY);
                        if (math.distance(optimalPosition, edgeMidpoint) > maxDisplacement)
                        {
                            hasOptimalPosition = false;
                        }
                    }

                    if (hasOptimalPosition)
                    {
                        position = optimalPosition;
                        goto ComputeVertexError;
                    }
                }

                {
                    var positionZ = (positionX + positionY) * 0.5f;
                    var errorX = q.ComputeError(positionX);
                    var errorY = q.ComputeError(positionY);
                    var errorZ = q.ComputeError(positionZ);

                    if (errorX < errorY)
                    {
                        if (errorX < errorZ)
                        {
                            position = positionX;
                            vertexError = errorX;

                        }
                        else
                        {
                            position = positionZ;
                            vertexError = errorZ;
                        }
                    }
                    else
                    {
                        if (errorY < errorZ)
                        {
                            position = positionY;
                            vertexError = errorY;
                        }
                        else
                        {
                            position = positionZ;
                            vertexError = errorZ;
                        }
                    }

                    goto ApplyCurvatureError;
                }


            ComputeVertexError:
                vertexError = q.ComputeError(position);

            ApplyCurvatureError:
                var curvatureError = PreserveSurfaceCurvature ? ComputeCurvatureError(vertices) : 0;

                cost = vertexError + curvatureError;
                return KeepFinite(ref position, ref optimalUv, ref cost);
            }

        }
        bool TryComputeMergeAttributeAware(int2 vertices, out float3 position, out float2 optimalUv, out float cost)
        {
            var q = VertexAttributeErrorQuadrics[vertices.x] + VertexAttributeErrorQuadrics[vertices.y];
            var weight = UvErrorWeight;

            var positionX = VertexPositionBuffer[vertices.x];
            var positionY = VertexPositionBuffer[vertices.y];
            var uvX = VertexTexCoord0Buffer.Length != 0 ? VertexTexCoord0Buffer[vertices.x].xy : float2.zero;
            var uvY = VertexTexCoord0Buffer.Length != 0 ? VertexTexCoord0Buffer[vertices.y].xy : float2.zero;

            var preservedVertexPredicator = PreservedVertexPredicator;
            var preserveX = preservedVertexPredicator.IsPreserved(vertices.x);
            var preserveY = preservedVertexPredicator.IsPreserved(vertices.y);

            if (preserveX && preserveY)
            {
                position = float.NaN;
                optimalUv = float.NaN;
                cost = float.PositiveInfinity;
                return false;
            }
            else if (preserveX)
            {
                position = positionX;
                optimalUv = uvX;
                cost = q.ComputeError(positionX, uvX * weight) + CurvatureError(vertices);
                return KeepFinite(ref position, ref optimalUv, ref cost);
            }
            else if (preserveY)
            {
                position = positionY;
                optimalUv = uvY;
                cost = q.ComputeError(positionY, uvY * weight) + CurvatureError(vertices);
                return KeepFinite(ref position, ref optimalUv, ref cost);
            }

            var solved = q.TrySolveOptimal(out var solvedPosition, out var solvedWeightedUv);
            if (solved && !(math.all(math.isfinite(solvedPosition)) && math.all(math.isfinite(solvedWeightedUv))))
            {
                solved = false;
            }

            // Anti self-intersection: reject the optimum if it flies away from the collapsed edge.
            if (solved && ConstrainOptimalPosition)
            {
                var edgeMidpoint = (positionX + positionY) * 0.5f;
                var maxDisplacement = MaxCollapseDisplacementFactor * math.distance(positionX, positionY);
                if (math.distance(solvedPosition, edgeMidpoint) > maxDisplacement)
                {
                    solved = false;
                }
            }

            if (solved)
            {
                position = solvedPosition;
                optimalUv = solvedWeightedUv / weight;
                cost = q.ComputeError(solvedPosition, solvedWeightedUv);
            }
            else
            {
                // Fall back to the endpoint / midpoint candidates evaluated with their own UVs.
                var midpoint = (positionX + positionY) * 0.5f;
                var uvMid = (uvX + uvY) * 0.5f;

                var errorX = q.ComputeError(positionX, uvX * weight);
                var errorY = q.ComputeError(positionY, uvY * weight);
                var errorMid = q.ComputeError(midpoint, uvMid * weight);

                if (errorX <= errorY && errorX <= errorMid)
                {
                    position = positionX;
                    optimalUv = uvX;
                    cost = errorX;
                }
                else if (errorY <= errorMid)
                {
                    position = positionY;
                    optimalUv = uvY;
                    cost = errorY;
                }
                else
                {
                    position = midpoint;
                    optimalUv = uvMid;
                    cost = errorMid;
                }
            }

            cost += CurvatureError(vertices);
            return KeepFinite(ref position, ref optimalUv, ref cost);
        }

        const float BoneOverlapTolerance = 1e-4f;

        static bool Reject(out float3 position, out float2 optimalUv, out float cost)
        {
            position = float.NaN;
            optimalUv = float.NaN;
            cost = float.PositiveInfinity;
            return false;
        }

        /// <summary>
        /// Keeps NaN and infinity out of the merge queue. A NaN cost would sort first and a NaN position would be applied.
        /// </summary>
        static bool KeepFinite(ref float3 position, ref float2 optimalUv, ref float cost)
        {
            if (math.isfinite(cost) && math.all(math.isfinite(position)) && math.all(math.isfinite(optimalUv)))
            {
                return true;
            }
            return Reject(out position, out optimalUv, out cost);
        }

        /// <summary>
        /// The sum over bones of min(weightA, weightB) has to reach <see cref="MinBoneOverlap"/>.
        /// Meshes without bone indices always pass. Bone indices without weights count as one bone at full weight.
        /// </summary>
        bool HasEnoughBoneOverlap(int vertexA, int vertexB)
        {
            var vertexCount = VertexPositionBuffer.Length;
            if (VertexBlendIndicesBuffer.Length == 0 || vertexCount == 0)
            {
                return true;
            }
            var indexDimension = VertexBlendIndicesBuffer.Length / vertexCount;
            var weightDimension = VertexBlendWeightBuffer.Length / vertexCount;
            var overlap = 0f;
            for (int slot = 0; slot < indexDimension; slot++)
            {
                var bone = VertexBlendIndicesBuffer[vertexA * indexDimension + slot];
                if (FirstSlotOfBone(vertexA, bone, indexDimension) != slot)
                {
                    continue;
                }
                overlap += math.min(
                    TotalBoneWeight(vertexA, bone, indexDimension, weightDimension),
                    TotalBoneWeight(vertexB, bone, indexDimension, weightDimension));
            }
            return overlap + BoneOverlapTolerance >= MinBoneOverlap;
        }

        int FirstSlotOfBone(int vertex, uint bone, int indexDimension)
        {
            for (int slot = 0; slot < indexDimension; slot++)
            {
                if (VertexBlendIndicesBuffer[vertex * indexDimension + slot] == bone)
                {
                    return slot;
                }
            }
            return -1;
        }

        float TotalBoneWeight(int vertex, uint bone, int indexDimension, int weightDimension)
        {
            var weight = 0f;
            for (int slot = 0; slot < indexDimension; slot++)
            {
                if (VertexBlendIndicesBuffer[vertex * indexDimension + slot] != bone)
                {
                    continue;
                }
                if (weightDimension == 0)
                {
                    weight += slot == 0 ? 1f : 0f;
                }
                else if (slot < weightDimension)
                {
                    weight += VertexBlendWeightBuffer[vertex * weightDimension + slot];
                }
            }
            return weight;
        }

        /// <summary>
        /// True when some blend shape (last frame) moves the two vertices apart by more than
        /// <see cref="BlendShapeStretch"/> times their distance plus the slack. Only the shapes flagged
        /// for either vertex are checked.
        /// </summary>
        bool IsTornByBlendShapes(int vertexA, int vertexB)
        {
            var vertexCount = VertexPositionBuffer.Length;
            if (VertexBlendShapeMasks.Length == 0 || vertexCount == 0)
            {
                return false;
            }
            var words = VertexBlendShapeMasks.Length / vertexCount;
            var limit = BlendShapeStretch * math.distance(VertexPositionBuffer[vertexA], VertexPositionBuffer[vertexB]) + MeshSimplifierOptions.BlendShapeStretchSlack;
            var limitSq = limit * limit;
            for (int word = 0; word < words; word++)
            {
                var bits = VertexBlendShapeMasks[vertexA * words + word] | VertexBlendShapeMasks[vertexB * words + word];
                while (bits != 0ul)
                {
                    var shapeIndex = (word << 6) + math.tzcnt(bits);
                    bits &= bits - 1ul;
                    if (shapeIndex >= BlendShapes.Length)
                    {
                        break;
                    }
                    var frames = BlendShapes[shapeIndex].Frames;
                    var deltas = frames[frames.Length - 1].DeltaVertices;
                    if (math.distancesq(deltas[vertexA], deltas[vertexB]) > limitSq)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        float CurvatureError(int2 vertices) => PreserveSurfaceCurvature ? ComputeCurvatureError(vertices) : 0f;

        float ComputeCurvatureError(int2 vertices)
        {

            using (ProfilerMarkers.ComputeCurvatureError.Auto())
            {
                var distance = math.distance(VertexPositionBuffer[vertices.x], VertexPositionBuffer[vertices.y]);
                using UnsafeHashSet<int> vertexXContainingTriangles = new(8, Allocator.Temp);

                using UnsafeList<int> vertexXOrYContainingTriangles = new(16, Allocator.Temp);


                foreach (var vertexXContainingTriangle in VertexContainingTriangles.GetValuesForKey(vertices.x))
                {
                    vertexXContainingTriangles.Add(vertexXContainingTriangle);
                    vertexXOrYContainingTriangles.Add(vertexXContainingTriangle);
                }


                using UnsafeList<int> vertexXAndYContainingTriangles = new(8, Allocator.Temp);

                foreach (var vertexYContainingTriangle in VertexContainingTriangles.GetValuesForKey(vertices.y))
                {
                    if (vertexXContainingTriangles.Contains(vertexYContainingTriangle))
                    {
                        vertexXAndYContainingTriangles.Add(vertexYContainingTriangle);
                    }
                    else
                    {
                        vertexXOrYContainingTriangles.Add(vertexYContainingTriangle);
                    }
                }

                vertexXContainingTriangles.Dispose();

                var maxDot = 0f;

                foreach (var vertexXOrYContainingTriangle in vertexXOrYContainingTriangles)
                {
                    var vertexXOrYContainingTriangleNormal = TriangleNormals[vertexXOrYContainingTriangle];

                    foreach (var vertexXAndYContainingTriangle in vertexXAndYContainingTriangles)
                    {
                        var vertexXAndYContainingTriangleNormal = TriangleNormals[vertexXAndYContainingTriangle];
                        var dot = math.dot(vertexXOrYContainingTriangleNormal, vertexXAndYContainingTriangleNormal);
                        maxDot = math.max(dot, maxDot);
                    }
                }
                return distance * maxDot;
            }
        }
    }
}


