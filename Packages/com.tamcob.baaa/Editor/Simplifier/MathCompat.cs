using Unity.Mathematics;

namespace BAAA.Simplifier
{
    /// <summary>
    /// Stand-in for Unity.Mathematics.Geometry.Plane, which is internal in Mathematics 1.2.x.
    /// Same normalization, except a zero length normal (zero area triangle) gives an all zero plane instead of NaN.
    /// </summary>
    internal struct QuadricPlane
    {
        public float4 NormalAndDistance;

        public QuadricPlane(float3 normal, float3 pointInPlane)
        {
            var coefficients = new float4(normal, -math.dot(normal, pointInPlane));
            var lengthSq = math.lengthsq(normal);
            NormalAndDistance = lengthSq > 1e-30f ? coefficients * math.rsqrt(lengthSq) : float4.zero;
        }

        public float3 Normal => NormalAndDistance.xyz;
    }

    /// <summary>
    /// Stand-in for Unity.Mathematics.Geometry.MinMaxAABB, which is internal in Mathematics 1.2.x.
    /// </summary>
    internal struct MinMaxBounds
    {
        public float3 Min;
        public float3 Max;

        public bool IsValid => math.all(Min <= Max);

        public float3 Extents => Max - Min;

        public void Encapsulate(float3 point)
        {
            Min = math.min(Min, point);
            Max = math.max(Max, point);
        }
    }
}
