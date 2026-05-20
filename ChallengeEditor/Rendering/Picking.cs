using System.Numerics;

namespace ChallengeEditor.Rendering;

/// Ray + intersection helpers for cursor-based picking and gizmo interaction.
public static class Picking
{
    /// Builds a world-space ray from a cursor position inside the viewport panel.
    /// `mouseLocal` is panel-relative pixels (top-left origin).
    public static (Vector3 origin, Vector3 dir) MouseRay(
        Vector2 mouseLocal, Vector2 panelSize, OrbitCamera cam)
    {
        if (panelSize.X <= 0 || panelSize.Y <= 0) return (cam.Position, Vector3.UnitZ);

        Vector2 ndc = new(
            (mouseLocal.X / panelSize.X) * 2f - 1f,
            1f - (mouseLocal.Y / panelSize.Y) * 2f);

        Matrix4x4 viewProj = cam.GetViewMatrix() * cam.GetProjectionMatrix(panelSize.X / panelSize.Y);
        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invVp)) return (cam.Position, Vector3.UnitZ);

        Vector4 nearH = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0f, 1f), invVp);
        Vector4 farH  = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 1f, 1f), invVp);
        Vector3 nearP = new(nearH.X / nearH.W, nearH.Y / nearH.W, nearH.Z / nearH.W);
        Vector3 farP  = new(farH.X  / farH.W,  farH.Y  / farH.W,  farH.Z  / farH.W);
        Vector3 dir = farP - nearP;
        if (dir.LengthSquared() < 1e-12f) return (nearP, Vector3.UnitZ);
        return (nearP, Vector3.Normalize(dir));
    }

    /// Oriented bounding box with full Euler rotation. Returns near intersection t along the ray, or false on miss.
    public static bool RayObb(Vector3 ro, Vector3 rd, Vector3 center, Vector3 halfExt, Vector3 rotationDegrees, out float t)
    {
        float pitch = rotationDegrees.X * MathF.PI / 180f;
        float yaw   = rotationDegrees.Y * MathF.PI / 180f;
        float roll  = rotationDegrees.Z * MathF.PI / 180f;
        Matrix4x4 rot = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        if (!Matrix4x4.Invert(rot, out Matrix4x4 invRot)) { t = 0; return false; }
        Vector3 localOrigin = Vector3.Transform(ro - center, invRot);
        Vector3 localDir    = Vector3.TransformNormal(rd, invRot);
        return RayAabb(localOrigin, localDir, -halfExt, halfExt, out t);
    }

    public static bool RayAabb(Vector3 ro, Vector3 rd, Vector3 mn, Vector3 mx, out float tNear)
    {
        tNear = 0f;
        float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
        if (!Slab(ro.X, rd.X, mn.X, mx.X, ref tmin, ref tmax)) return false;
        if (!Slab(ro.Y, rd.Y, mn.Y, mx.Y, ref tmin, ref tmax)) return false;
        if (!Slab(ro.Z, rd.Z, mn.Z, mx.Z, ref tmin, ref tmax)) return false;
        tNear = tmin >= 0 ? tmin : tmax;
        return tNear >= 0;
    }

    private static bool Slab(float ro, float rd, float mn, float mx, ref float tmin, ref float tmax)
    {
        if (MathF.Abs(rd) < 1e-6f) return ro >= mn && ro <= mx;
        float invD = 1f / rd;
        float t1 = (mn - ro) * invD;
        float t2 = (mx - ro) * invD;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tmin = MathF.Max(tmin, t1);
        tmax = MathF.Min(tmax, t2);
        return tmin <= tmax;
    }

    /// <summary>Möller–Trumbore ray–triangle, two-sided (matches mesh draw cull mode None).</summary>
    public static bool RayTriangleTwoSided(
        Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, float minT, out float t)
    {
        const float eps = 1e-7f;
        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < eps)
        {
            t = 0f;
            return false;
        }

        float invDet = 1f / det;
        Vector3 tvec = orig - v0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f)
        {
            t = 0f;
            return false;
        }

        Vector3 qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(dir, qvec) * invDet;
        if (v < 0f || u + v > 1f)
        {
            t = 0f;
            return false;
        }

        t = Vector3.Dot(e2, qvec) * invDet;
        return t >= minT;
    }

    /// <summary>Closest positive hit along <paramref name="rd"/> vs indexed triangles; updates <paramref name="bestT"/> / <paramref name="bestHit"/> when closer.</summary>
    public static void RayMeshTrianglesClosest(
        Vector3 ro, Vector3 rd,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float minT,
        ref float bestT,
        ref Vector3 bestHit)
    {
        if (positions.Length == 0 || indices.Length < 3)
            return;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length)
                continue;

            Vector3 v0 = positions[(int)i0];
            Vector3 v1 = positions[(int)i1];
            Vector3 v2 = positions[(int)i2];
            if (!RayTriangleTwoSided(ro, rd, v0, v1, v2, minT, out float triT))
                continue;
            if (triT >= bestT)
                continue;
            bestT = triT;
            bestHit = ro + rd * triT;
        }
    }

    public static bool RaySphere(Vector3 ro, Vector3 rd, Vector3 center, float radius, out float t)
    {
        Vector3 oc = ro - center;
        float b = Vector3.Dot(oc, rd);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;
        if (disc < 0f) { t = 0f; return false; }
        float sq = MathF.Sqrt(disc);
        float t0 = -b - sq;
        float t1 = -b + sq;
        t = t0 >= 0 ? t0 : t1;
        return t >= 0;
    }

    /// Closest-point parametric solution for two infinite lines.
    /// `t` is the parameter along (p1, d1); `s` along (p2, d2).
    /// Both directions assumed non-zero. Returns false on near-parallel.
    public static bool ClosestPointsOnLines(
        Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2,
        out float t, out float s)
    {
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float b = Vector3.Dot(d1, d2);
        float c = Vector3.Dot(d2, d2);
        float d = Vector3.Dot(d1, r);
        float e = Vector3.Dot(d2, r);
        float denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-6f) { t = 0; s = 0; return false; }
        t = (b * e - c * d) / denom;
        s = (a * e - b * d) / denom;
        return true;
    }

    /// Project a world-space point onto the panel coordinate system (px from top-left).
    /// Returns false if the point is behind the camera.
    public static bool WorldToPanel(Vector3 world, Vector2 panelSize, OrbitCamera cam, out Vector2 panelPx)
    {
        Matrix4x4 viewProj = cam.GetViewMatrix() * cam.GetProjectionMatrix(panelSize.X / panelSize.Y);
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
        if (clip.W <= 1e-6f) { panelPx = Vector2.Zero; return false; }
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        panelPx = new Vector2(
            (ndcX * 0.5f + 0.5f) * panelSize.X,
            (1f - (ndcY * 0.5f + 0.5f)) * panelSize.Y);
        return true;
    }

    /// Shortest pixel distance from `p` to the segment a→b (all in panel pixels).
    public static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.Distance(p, a);
        float u = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        Vector2 proj = a + ab * u;
        return Vector2.Distance(p, proj);
    }
}
