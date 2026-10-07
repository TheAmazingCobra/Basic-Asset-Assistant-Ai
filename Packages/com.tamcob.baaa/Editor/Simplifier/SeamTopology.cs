using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Read only view of the current triangles and the position groups, used to keep seams closed and open
    /// borders in place.
    /// <para>
    /// A position group is every vertex at one position (see <see cref="BuildPositionGroupsJob"/>). A vertex whose
    /// group has two or more live members is a seam vertex. An open edge is a directed edge whose reverse is in no
    /// triangle. It is a seam edge when the other side of the seam has the matching open edge (from a member of the
    /// end group back to a member of the start group), and a true border edge otherwise.
    /// </para>
    /// </summary>
    internal struct SeamTopology
    {
        /// <summary>Alone at its position and no open edges. Moves anywhere, like before.</summary>
        public const int Free = 0;
        /// <summary>Alone at its position, one open edge in and one out, both true borders. Moves only along them.</summary>
        public const int BorderLine = 1;
        /// <summary>Shares its position and no member has a true border edge. Moves only together with its group.</summary>
        public const int SeamLine = 2;
        /// <summary>Corners, seam ends, seam and border junctions, non manifold spots. Never moves.</summary>
        public const int Fixed = 3;

        public NativeArray<int3> Triangles;
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        public NativeBitArray DiscardedVertex;
        /// <summary>Next member of the vertex's position group, as a ring. Empty turns the seam rules off.</summary>
        public NativeArray<int> GroupNext;
        /// <summary>Smallest vertex index of the vertex's position group.</summary>
        public NativeArray<int> GroupId;
        public NativeArray<int> VertexVersions;

        public readonly bool IsEnabled => GroupNext.Length != 0;

        public readonly bool SameGroup(int vertexA, int vertexB) => GroupId[vertexA] == GroupId[vertexB];

        /// <summary>
        /// Not discarded and still used by a triangle.
        /// </summary>
        public readonly bool IsLive(int vertex) => !DiscardedVertex.IsSet(vertex) && VertexContainingTriangles.ContainsKey(vertex);

        public readonly int LiveGroupSize(int vertex)
        {
            var count = 0;
            var member = vertex;
            do
            {
                if (IsLive(member))
                {
                    count++;
                }
                member = GroupNext[member];
            } while (member != vertex);
            return count;
        }

        /// <summary>
        /// Sum of the versions of every member, live or not. It changes whenever any member is merged or discarded.
        /// </summary>
        public readonly int GroupVersion(int vertex)
        {
            var sum = 0;
            var member = vertex;
            do
            {
                sum += VertexVersions[member];
                member = GroupNext[member];
            } while (member != vertex);
            return sum;
        }

        public readonly bool HasDirectedEdge(int from, int to)
        {
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(from))
            {
                var triangle = Triangles[triangleIndex];
                if ((triangle.x == from && triangle.y == to) || (triangle.y == from && triangle.z == to) || (triangle.z == from && triangle.x == to))
                {
                    return true;
                }
            }
            return false;
        }

        public readonly bool HasEdge(int vertexA, int vertexB) => HasDirectedEdge(vertexA, vertexB) || HasDirectedEdge(vertexB, vertexA);

        public readonly bool HasOpenEdgeBetween(int vertexA, int vertexB)
        {
            var forward = HasDirectedEdge(vertexA, vertexB);
            var backward = HasDirectedEdge(vertexB, vertexA);
            return forward != backward;
        }

        public readonly int Classify(int vertex)
        {
            if (!IsEnabled)
            {
                return Free;
            }
            if (LiveGroupSize(vertex) >= 2)
            {
                var member = vertex;
                do
                {
                    if (IsLive(member) && HasTrueBorderEdge(member))
                    {
                        // a seam that also touches an open border: moving it would bend the border outline
                        return Fixed;
                    }
                    member = GroupNext[member];
                } while (member != vertex);
                return SeamLine;
            }

            var outCount = 0;
            var inCount = 0;
            var trueBorderCount = 0;
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertex))
            {
                var triangle = Triangles[triangleIndex];
                var next = NextCorner(triangle, vertex);
                var previous = PreviousCorner(triangle, vertex);
                if (!HasDirectedEdge(next, vertex))
                {
                    outCount++;
                    if (!HasTwin(vertex, next))
                    {
                        trueBorderCount++;
                    }
                }
                if (!HasDirectedEdge(vertex, previous))
                {
                    inCount++;
                    if (!HasTwin(previous, vertex))
                    {
                        trueBorderCount++;
                    }
                }
            }
            if (outCount == 0 && inCount == 0)
            {
                return Free;
            }
            if (outCount == 1 && inCount == 1 && trueBorderCount == 2)
            {
                return BorderLine;
            }
            // seam ends (open edges with twins on an unsplit vertex), corners, bow ties
            return Fixed;
        }

        readonly bool HasTrueBorderEdge(int vertex)
        {
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertex))
            {
                var triangle = Triangles[triangleIndex];
                var next = NextCorner(triangle, vertex);
                var previous = PreviousCorner(triangle, vertex);
                if (!HasDirectedEdge(next, vertex) && !HasTwin(vertex, next))
                {
                    return true;
                }
                if (!HasDirectedEdge(vertex, previous) && !HasTwin(previous, vertex))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// For the open edge from -> to: true when some member of to's group has an open edge into some member of
        /// from's group, which is the other side of a seam.
        /// </summary>
        readonly bool HasTwin(int from, int to)
        {
            var toGroup = GroupId[to];
            var member = from;
            do
            {
                if (IsLive(member))
                {
                    foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(member))
                    {
                        var source = PreviousCorner(Triangles[triangleIndex], member);
                        if (GroupId[source] == toGroup && !HasDirectedEdge(member, source))
                        {
                            return true;
                        }
                    }
                }
                member = GroupNext[member];
            } while (member != from);
            return false;
        }

        /// <summary>
        /// Pairs every live member of mover's group with the single live member of target's group it shares an edge
        /// with. False when some member has no such partner or more than one, which makes mover a seam corner for
        /// this direction. The first pair is always (mover, target).
        /// </summary>
        public readonly bool TryPairGroups(int mover, int target, ref UnsafeList<int2> pairs)
        {
            pairs.Clear();
            if (!IsLive(mover) || !IsLive(target))
            {
                return false;
            }
            var member = mover;
            do
            {
                if (IsLive(member))
                {
                    var partner = -1;
                    var partnerCount = 0;
                    var candidate = target;
                    do
                    {
                        if (IsLive(candidate) && HasEdge(member, candidate))
                        {
                            partner = candidate;
                            partnerCount++;
                        }
                        candidate = GroupNext[candidate];
                    } while (candidate != target);
                    if (partnerCount != 1 || (member == mover && partner != target))
                    {
                        return false;
                    }
                    pairs.Add(new int2(member, partner));
                }
                member = GroupNext[member];
            } while (member != mover);
            return pairs.Length != 0;
        }

        static int NextCorner(int3 triangle, int vertex) => vertex == triangle.x ? triangle.y : vertex == triangle.y ? triangle.z : triangle.x;

        static int PreviousCorner(int3 triangle, int vertex) => vertex == triangle.x ? triangle.z : vertex == triangle.y ? triangle.x : triangle.y;
    }
}
