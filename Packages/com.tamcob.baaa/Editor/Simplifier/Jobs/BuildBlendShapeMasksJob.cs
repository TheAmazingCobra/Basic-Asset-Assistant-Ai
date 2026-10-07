using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BAAA.Simplifier
{
    /// <summary>
    /// For each vertex, flags the blend shapes whose last frame moves that vertex further than
    /// <see cref="MeshSimplifierOptions.BlendShapeMaskThreshold"/>. The stretch rule only has to look at
    /// flagged shapes, because two unflagged deltas differ by at most the slack. Empty when the rule is off.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BuildBlendShapeMasksJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;
        [ReadOnly]
        public NativeList<BlendShapeData> BlendShapes;
        public bool Enabled;
        public NativeList<ulong> Masks;

        public void Execute()
        {
            Masks.Clear();
            var shapeCount = BlendShapes.Length;
            if (!Enabled || shapeCount == 0)
            {
                return;
            }
            var vertexCount = Mesh.vertexCount;
            var words = (shapeCount + 63) >> 6;
            Masks.Resize(vertexCount * words, NativeArrayOptions.ClearMemory);
            var thresholdSq = MeshSimplifierOptions.BlendShapeMaskThreshold * MeshSimplifierOptions.BlendShapeMaskThreshold;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var frames = BlendShapes[shapeIndex].Frames;
                if (frames.Length == 0)
                {
                    continue;
                }
                var deltas = frames[frames.Length - 1].DeltaVertices;
                var word = shapeIndex >> 6;
                var bit = 1ul << (shapeIndex & 63);
                var count = math.min(vertexCount, deltas.Length);
                for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
                {
                    if (math.lengthsq(deltas[vertexIndex]) > thresholdSq)
                    {
                        Masks[vertexIndex * words + word] |= bit;
                    }
                }
            }
        }
    }
}
