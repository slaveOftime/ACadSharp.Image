using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Maps object coordinate system (OCS) points of a planar entity into world coordinates.
/// </summary>
/// <remarks>
/// <para>
/// Polylines and hatches store their vertices in the plane defined by their extrusion <c>Normal</c> (the OCS) and
/// ACadSharp 3.7.1 returns them raw: neither <c>IPolyline.GetPoints</c> nor <c>Hatch.BoundaryPath.GetPoints</c> nor
/// <c>Hatch.ExplodePattern</c> applies the normal. The renderer projects world XY onto the page, so those points must
/// be brought into world space first.
/// </para>
/// <para>
/// The frame follows the AutoCAD arbitrary axis algorithm (DXF reference, "Arbitrary Axis Algorithm"): when the normal
/// is within 1/64 of the world Z axis the OCS X axis is <c>Wy x N</c>, otherwise <c>Wz x N</c>; the OCS Y axis is
/// <c>N x Ax</c>. It is written out here rather than taken from <c>CSMath.Matrix3.ArbitraryAxis</c>, whose result is
/// not orthonormal for tilted normals in the pinned version. A <c>(0,0,-1)</c> normal, the common case, mirrors X.
/// </para>
/// </remarks>
internal sealed class OcsTransform
{
    private const double AxisThreshold = 1d / 64d;

    private OcsTransform(XYZ axisX, XYZ axisY, XYZ normal)
    {
        this.AxisX = axisX;
        this.AxisY = axisY;
        this.Normal = normal;
    }

    /// <summary>OCS X axis in world coordinates.</summary>
    public XYZ AxisX { get; }

    /// <summary>OCS Y axis in world coordinates.</summary>
    public XYZ AxisY { get; }

    /// <summary>OCS Z axis (the unit normal) in world coordinates.</summary>
    public XYZ Normal { get; }

    /// <summary>
    /// True when an entity's extrusion is the world Z axis, so its OCS coordinates are already world coordinates.
    /// </summary>
    /// <param name="normal">The entity's extrusion direction.</param>
    /// <returns>True for the default normal.</returns>
    public static bool IsWorldPlane(XYZ normal)
    {
        return Math.Abs(normal.X) < 1e-9 && Math.Abs(normal.Y) < 1e-9 && Math.Abs(normal.Z - 1d) < 1e-9;
    }

    /// <summary>
    /// Builds the OCS-to-world frame for an extrusion normal.
    /// </summary>
    /// <param name="normal">The entity's extrusion direction; it need not be unit length.</param>
    /// <returns>The frame, or the identity frame when the normal is degenerate (zero or non-finite).</returns>
    public static OcsTransform For(XYZ normal)
    {
        double length = Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y) + (normal.Z * normal.Z));
        if (!double.IsFinite(length) || length < 1e-12)
        {
            return new OcsTransform(XYZ.AxisX, XYZ.AxisY, XYZ.AxisZ);
        }

        XYZ n = new(normal.X / length, normal.Y / length, normal.Z / length);
        XYZ axisX = Math.Abs(n.X) < AxisThreshold && Math.Abs(n.Y) < AxisThreshold
            ? Normalize(new XYZ(n.Z, 0d, -n.X))    // Wy x N
            : Normalize(new XYZ(-n.Y, n.X, 0d));   // Wz x N
        XYZ axisY = Cross(n, axisX);
        return new OcsTransform(axisX, axisY, n);
    }

    /// <summary>
    /// Transforms an OCS point into world coordinates and drops Z, which the page projection ignores.
    /// </summary>
    /// <param name="x">OCS X.</param>
    /// <param name="y">OCS Y.</param>
    /// <param name="elevation">OCS Z; the entity's <c>Elevation</c>.</param>
    /// <returns>The world XY of the point.</returns>
    public XY ToWorldXY(double x, double y, double elevation)
    {
        return new XY(
            (x * this.AxisX.X) + (y * this.AxisY.X) + (elevation * this.Normal.X),
            (x * this.AxisX.Y) + (y * this.AxisY.Y) + (elevation * this.Normal.Y));
    }

    /// <summary>
    /// Transforms an OCS point into world coordinates.
    /// </summary>
    /// <param name="x">OCS X.</param>
    /// <param name="y">OCS Y.</param>
    /// <param name="elevation">OCS Z; the entity's <c>Elevation</c>.</param>
    /// <returns>The world point.</returns>
    public XYZ ToWorld(double x, double y, double elevation)
    {
        return new XYZ(
            (x * this.AxisX.X) + (y * this.AxisY.X) + (elevation * this.Normal.X),
            (x * this.AxisX.Y) + (y * this.AxisY.Y) + (elevation * this.Normal.Y),
            (x * this.AxisX.Z) + (y * this.AxisY.Z) + (elevation * this.Normal.Z));
    }

    private static XYZ Normalize(XYZ v)
    {
        double length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        return new XYZ(v.X / length, v.Y / length, v.Z / length);
    }

    private static XYZ Cross(XYZ a, XYZ b)
    {
        return new XYZ((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
    }
}
