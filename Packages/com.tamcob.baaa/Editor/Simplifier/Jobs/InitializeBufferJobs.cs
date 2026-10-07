using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace BAAA.Simplifier
{

    [BurstCompile(CompileSynchronously = true)]
    struct InitializeVertexListJob<T> : IJob
           where T : unmanaged
    {
        [ReadOnly]
        public Mesh.MeshData MeshData;
        public NativeArrayOptions Options;
        public NativeList<T> Buffer;
        public void Execute()
        {
            Buffer.Clear();
            Buffer.Resize(MeshData.vertexCount, Options);
        }
    }
    [BurstCompile(CompileSynchronously = true)]
    struct InitializeSubMeshListJob<T> : IJob
           where T : unmanaged
    {
        [ReadOnly]
        public Mesh.MeshData MeshData;
        public NativeArrayOptions Options;
        public NativeList<T> Buffer;
        public void Execute()
        {
            Buffer.Clear();
            Buffer.Resize(MeshData.subMeshCount, Options);
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct InitializeVertexBitArrayJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData MeshData;
        public NativeBitArray Buffer;
        public void Execute()
        {
            Buffer.Resize(MeshData.vertexCount, NativeArrayOptions.UninitializedMemory);
            Buffer.Clear();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct InitializeTriangleListJob<T> : IJob
        where T : unmanaged
    {
        [ReadOnly]
        public Mesh.MeshData MeshData;
        public NativeArrayOptions Options;
        public NativeList<T> Buffer;
        public void Execute()
        {
            var triangleCount = MeshData.GetTriangleCount();
            Buffer.Clear();
            Buffer.Resize(triangleCount, Options);
        }
    }
    [BurstCompile(CompileSynchronously = true)]
    struct InitializeUnorderedDirtyVertexMergesJob : IJob
    {

        [ReadOnly]
        public NativeArray<int2> Edges;
        public NativeList<VertexMerge> UnorderedDirtyVertexMerges;

        public void Execute()
        {
            UnorderedDirtyVertexMerges.ResizeUninitialized(Edges.Length);
        }
    }
}

