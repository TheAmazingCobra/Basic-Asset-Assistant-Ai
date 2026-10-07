using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Copies the caller's per vertex lock flags into the simplifier. An empty source means nothing is locked,
    /// and then the lock bits stay empty so every lock check is skipped.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct CopyLockedVerticesJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;
        [ReadOnly]
        public NativeBitArray Source;
        public NativeBitArray VertexIsLockedBits;

        public void Execute()
        {
            if (Source.Length == 0)
            {
                VertexIsLockedBits.Resize(0);
                return;
            }
            var vertexCount = Mesh.vertexCount;
            VertexIsLockedBits.Resize(vertexCount);
            VertexIsLockedBits.Clear();
            var count = math.min(vertexCount, Source.Length);
            for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
            {
                if (Source.IsSet(vertexIndex))
                {
                    VertexIsLockedBits.Set(vertexIndex, true);
                }
            }
        }
    }

    /// <summary>
    /// Vertices that no triangle uses are dropped at load. Locked ones are put back before writing, so every
    /// locked vertex is in the output.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct KeepLockedVerticesJob : IJob
    {
        [ReadOnly]
        public NativeBitArray VertexIsLockedBits;
        public NativeBitArray DiscardedVertex;

        public void Execute()
        {
            var count = math.min(VertexIsLockedBits.Length, DiscardedVertex.Length);
            for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
            {
                if (VertexIsLockedBits.IsSet(vertexIndex))
                {
                    DiscardedVertex.Set(vertexIndex, false);
                }
            }
        }
    }
}
