using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Maps geometry through the transform of the block reference that placed it. A placement of <c>null</c> means the
/// entity is at top level and every map is the identity.
/// </summary>
/// <remarks>
/// Points and vectors map differently: a translation moves a point but must not change a direction, so a vector is
/// mapped by transforming its head and tail and subtracting. ACadSharp 3.7.1 gets this wrong in places of its own
/// (<c>Wipeout.ApplyTransform</c> transforms its U and V vectors as points), which is why the renderer maps from the
/// original entity through these helpers instead of trusting a transformed clone.
/// </remarks>
internal static class InsertPlacement
{
    /// <summary>Maps a world point through the placement.</summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="point">The world point.</param>
    /// <returns>The placed world point.</returns>
    internal static XYZ MapPoint(Transform? placement, XYZ point) => placement == null ? point : placement.ApplyTransform(point);

    /// <summary>Maps a world direction through the placement, keeping the linear part and dropping the translation.</summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="vector">The world direction.</param>
    /// <returns>The placed direction, scaled and rotated but not translated.</returns>
    internal static XYZ MapVector(Transform? placement, XYZ vector)
    {
        if (placement == null)
        {
            return vector;
        }

        return placement.ApplyTransform(vector) - placement.ApplyTransform(XYZ.Zero);
    }

    /// <summary>
    /// Maps a point stored in an entity's own object coordinate system: the OCS frame first (with the entity's
    /// elevation as the out-of-plane offset), then the placement.
    /// </summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="toWorld">The entity's OCS frame, or null when it lies in the world plane.</param>
    /// <param name="elevation">The entity's elevation along its own normal.</param>
    /// <param name="ocsPoint">The point in the entity's OCS.</param>
    /// <returns>The placed world point.</returns>
    internal static XYZ MapOcsPoint(Transform? placement, OcsTransform? toWorld, double elevation, XYZ ocsPoint)
    {
        XYZ world = toWorld != null ? toWorld.ToWorld(ocsPoint.X, ocsPoint.Y, elevation) : ocsPoint;
        return MapPoint(placement, world);
    }

    /// <summary>
    /// Whether the placement acts on the drawing plane as a similarity: one uniform scale and a rotation, optionally
    /// with a reflection. Geometry that has to be handed back to ACadSharp as an <c>Insert</c> can only be expressed
    /// when this holds, because an <c>Insert</c> has no way to represent the shear a non-uniform scale composed with
    /// a rotation produces.
    /// </summary>
    /// <param name="placement">The transform to test, or null at top level.</param>
    /// <param name="scale">Receives the uniform scale.</param>
    /// <param name="rotation">Receives the rotation of the mapped X axis, in radians.</param>
    /// <param name="mirrored">Receives whether the mapped Y axis lies clockwise from the mapped X axis.</param>
    /// <returns>True when the placement is a planar similarity.</returns>
    internal static bool TryGetPlanarSimilarity(Transform? placement, out double scale, out double rotation, out bool mirrored)
    {
        XYZ ex = MapVector(placement, XYZ.AxisX);
        XYZ ey = MapVector(placement, XYZ.AxisY);
        XY x = new(ex.X, ex.Y);
        XY y = new(ey.X, ey.Y);
        double lx = x.GetLength();
        double ly = y.GetLength();
        scale = lx;
        rotation = 0d;
        mirrored = false;
        if (lx < 1e-12 || ly < 1e-12 || !double.IsFinite(lx) || !double.IsFinite(ly))
        {
            return false;
        }

        // A similarity keeps both axes the same length and at right angles; the tolerances are relative so a drawing
        // in millimetres and one in metres are judged the same way.
        if (Math.Abs(lx - ly) > 1e-9 * lx || Math.Abs((x.X * y.X) + (x.Y * y.Y)) > 1e-9 * lx * ly)
        {
            return false;
        }

        rotation = Math.Atan2(x.Y, x.X);
        mirrored = (x.X * y.Y) - (x.Y * y.X) < 0d;
        return true;
    }
}
