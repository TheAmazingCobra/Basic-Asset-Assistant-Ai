using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Groups the vertices in use by position. Vertices closer than <see cref="Tolerance"/> (exact copies in
    /// practice) share a group. GroupNext links each group into a ring in index order and GroupId is the smallest
    /// index of the group. Unused vertices stay alone.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BuildPositionGroupsJob : IJob
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeBitArray VertexIsDiscardedBits;
        public float Tolerance;
        public NativeList<int> GroupNext;
        public NativeList<int> GroupId;

        public void Execute()
        {
            var vertexCount = VertexPositionBuffer.Length;
            GroupNext.Clear();
            GroupId.Clear();
            GroupNext.Resize(vertexCount, NativeArrayOptions.UninitializedMemory);
            GroupId.Resize(vertexCount, NativeArrayOptions.UninitializedMemory);
            if (vertexCount == 0)
            {
                return;
            }

            var parent = new NativeArray<int>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                parent[vertex] = vertex;
            }

            var kdTree = new UnsafeKdTree(Allocator.Temp);
            kdTree.Initialize(VertexPositionBuffer);
            var near = new UnsafeList<int>(8, Allocator.Temp);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (VertexIsDiscardedBits.IsSet(vertex))
                {
                    continue;
                }
                kdTree.QueryPointsInSphere(VertexPositionBuffer, VertexPositionBuffer[vertex], Tolerance, ref near);
                foreach (var other in near)
                {
                    if (other != vertex && !VertexIsDiscardedBits.IsSet(other))
                    {
                        Union(ref parent, vertex, other);
                    }
                }
                near.Clear();
            }
            near.Dispose();
            kdTree.Dispose();

            // roots are the smallest index of their group, so walking up from 0 meets each root before its members
            var last = new NativeArray<int>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                var root = Find(ref parent, vertex);
                GroupId[vertex] = root;
                if (root == vertex)
                {
                    GroupNext[vertex] = vertex;
                    last[vertex] = vertex;
                }
                else
                {
                    GroupNext[last[root]] = vertex;
                    GroupNext[vertex] = root;
                    last[root] = vertex;
                }
            }
            last.Dispose();
            parent.Dispose();
        }

        static int Find(ref NativeArray<int> parent, int vertex)
        {
            var root = vertex;
            while (parent[root] != root)
            {
                root = parent[root];
            }
            while (parent[vertex] != root)
            {
                var next = parent[vertex];
                parent[vertex] = root;
                vertex = next;
            }
            return root;
        }

        static void Union(ref NativeArray<int> parent, int vertexA, int vertexB)
        {
            var rootA = Find(ref parent, vertexA);
            var rootB = Find(ref parent, vertexB);
            if (rootA == rootB)
            {
                return;
            }
            if (rootA < rootB)
            {
                parent[rootB] = rootA;
            }
            else
            {
                parent[rootA] = rootB;
            }
        }
    }
}
