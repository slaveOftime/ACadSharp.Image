namespace ACadSharp.Image.Rendering;

/// <summary>
/// Geometry helpers shared by backends that need arcs as points or bulges as arcs.
/// </summary>
internal static class CurveTessellation
{
    /// <summary>
    /// Samples an elliptical arc into <paramref name="segments"/> + 1 points.
    /// </summary>
    /// <param name="center">Centre in surface units.</param>
    /// <param name="radiusX">Semi-axis along the rotated X axis.</param>
    /// <param name="radiusY">Semi-axis along the rotated Y axis.</param>
    /// <param name="rotation">Rotation of the X axis in radians (surface space).</param>
    /// <param name="startAngle">Start parameter in radians (surface space).</param>
    /// <param name="sweepAngle">Signed sweep in radians (surface space).</param>
    /// <param name="segments">Number of straight segments, at least 1.</param>
    public static IReadOnlyList<SurfacePoint> ArcPoints(SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle, int segments)
    {
        segments = Math.Max(1, segments);
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);
        SurfacePoint[] points = new SurfacePoint[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double angle = startAngle + (sweepAngle * i / segments);
            double x = radiusX * Math.Cos(angle);
            double y = radiusY * Math.Sin(angle);
            points[i] = new SurfacePoint(
                center.X + (x * cos) - (y * sin),
                center.Y + (x * sin) + (y * cos));
        }

        return points;
    }

    /// <summary>
    /// Converts a polyline bulge into arc parameters in surface space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bulge is tan(theta/4) where theta is the included angle. A positive bulge is a counter-clockwise arc in the drawing
    /// and still looks counter-clockwise on screen after the Y flip; but in surface coordinates (Y down) a visually
    /// counter-clockwise turn is a decreasing angle, so a positive bulge yields a negative sweep here.
    /// </para>
    /// <para>
    /// Callers must guard against <paramref name="bulge"/> being 0 and against coincident <paramref name="start"/>/<paramref name="end"/>
    /// points; either condition drives the chord length to 0 and yields NaN.
    /// </para>
    /// </remarks>
    public static void BulgeArc(SurfacePoint start, SurfacePoint end, double bulge, out SurfacePoint center, out double radius, out double startAngle, out double sweepAngle)
    {
        double chordX = end.X - start.X;
        double chordY = end.Y - start.Y;
        double chord = Math.Sqrt((chordX * chordX) + (chordY * chordY));
        double theta = 4d * Math.Atan(Math.Abs(bulge));
        radius = chord / (2d * Math.Sin(theta / 2d));

        // Distance from the chord midpoint to the centre, along the chord normal.
        double apothem = radius * Math.Cos(theta / 2d);
        double midX = (start.X + end.X) / 2d;
        double midY = (start.Y + end.Y) / 2d;
        double normalX = -chordY / chord;
        double normalY = chordX / chord;

        // The arc bulges toward +normal for a positive bulge, so the centre sits on the -normal side.
        double side = bulge > 0 ? -1d : 1d;
        center = new SurfacePoint(midX + (side * apothem * normalX), midY + (side * apothem * normalY));
        startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        sweepAngle = bulge > 0 ? -theta : theta;
    }

    /// <summary>
    /// Number of straight segments to use for an arc of <paramref name="sweepAngle"/> radians when a full turn uses <paramref name="fullCircleSegments"/>.
    /// </summary>
    public static int SegmentsForSweep(double sweepAngle, int fullCircleSegments)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (2d * Math.PI) * Math.Max(4, fullCircleSegments)));
    }
}
