using ArenaBuilder.Collision.Math;
using System.Numerics;

namespace ArenaBuilder.Collision.EdgeCodes;

/// <summary>
/// Extended edge cosine in [-1, +3]. RenderWare <c>meshbuilder::EdgeCosines</c> (<c>edgecosines.cpp</c>).
/// </summary>
public static class EdgeCosines
{
    public static float ComputeExtendedEdgeCosine(
        Vector3 triangleOneNormal,
        Vector3 triangleTwoNormal,
        Vector3 edgeDirectionInTriangleOne)
    {
        const float epsilon = -1e-6f;
        float cosTheta = Vector3.Dot(triangleOneNormal, triangleTwoNormal);
        float sinTheta = Vector3.Dot(edgeDirectionInTriangleOne, Vector3Extensions.Cross(triangleOneNormal, triangleTwoNormal));
        if (sinTheta > epsilon)
            return MathF.Max(cosTheta, -1f);

        return MathF.Min(2f - cosTheta, 3f);
    }
}
