using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BAAA.Simplifier
{
    [BurstCompile(CompileSynchronously = true)]
    struct DisposeBlendShapesJob : IJob
    {
        public NativeList<BlendShapeData> BlendShapes;

        public void Execute()
        {
            for (int simplifiedBlendShapesIndex = 0; simplifiedBlendShapesIndex < BlendShapes.Length; simplifiedBlendShapesIndex++)
            {
                var blendShape = BlendShapes[simplifiedBlendShapesIndex];
                blendShape.Dispose();
            }
        }
    }
}


