using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.IO;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Tests;

/// <summary>Drives splines through the dispatcher and checks which surface primitive they reach.</summary>
public sealed class SplineRendererTests
{
    private static ImageRenderContext Context(IDrawingSurface surface, ImageConfiguration configuration)
    {
        Layout layout = new("t") { PaperWidth = 100, PaperHeight = 100 };
        return new ImageRenderContext(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
    }

    // Degree 3, 6 control points, clamped uniform knots: not Bezier-form (interior knots have multiplicity 1).
    private static Spline ClampedUniformCubic()
    {
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 0d, 0d, 1d, 2d, 3d, 3d, 3d, 3d]);
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(1, 3, 0), new XYZ(3, 4, 0), new XYZ(5, 1, 0), new XYZ(7, 3, 0), new XYZ(9, 0, 0)]);
        return spline;
    }

    [Fact]
    public void NonBezierSplineIsSampledOnSurfacesWithoutCurves()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { ArcPrecision = 16 };
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = ClampedUniformCubic();

        dispatcher.Draw(Context(surface, configuration), spline);

        // 3 knot spans x 16 = 48 steps -> 49 points (ArcPrecision 16 is below that floor).
        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polylines);
        Assert.Equal(49, points.Count);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawCubicBezier", StringComparison.Ordinal));

        // Endpoints are the clamped control points; the midpoint is the de Boor evaluation at t = 1.5 (Y flipped by the context).
        Assert.Equal(0d, points[0].X, 9);
        Assert.Equal(100d, points[0].Y, 9);
        Assert.Equal(9d, points[^1].X, 9);
        XY mid = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, spline.Weights, 1.5);
        Assert.Equal(mid.X, points[24].X, 9);
        Assert.Equal(100d - mid.Y, points[24].Y, 9);
    }

    [Fact]
    public void RationalSplineIsSampledEvenOnCurveSurfaces()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = ClampedUniformCubic();
        spline.Weights.AddRange([1d, 2d, 1d, 2d, 1d, 1d]);

        dispatcher.Draw(Context(surface, configuration), spline);

        // Bezier conversion refuses rational splines, so the curve-capable surface still receives a polyline.
        Assert.Single(surface.Polylines);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawCubicBezier", StringComparison.Ordinal));
        // Weighting pulls the curve toward the heavier control points: the midpoint moves compared with the unweighted spline.
        XY weighted = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, spline.Weights, 1.5);
        XY unweighted = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, [], 1.5);
        Assert.NotEqual(unweighted.Y, weighted.Y);
    }

    [Fact]
    public void QuadraticSplineIsSampled()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new() { ArcPrecision = 8 };
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new() { Degree = 2 };
        spline.Knots.AddRange([0d, 0d, 0d, 1d, 2d, 2d, 2d]);
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(2, 4, 0), new XYZ(4, 0, 0), new XYZ(6, 4, 0)]);

        dispatcher.Draw(Context(surface, configuration), spline);

        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polylines);
        Assert.Equal(33, points.Count); // 2 spans x 16 = 32 steps
        Assert.Equal(6d, points[^1].X, 9);
    }

    [Fact]
    public void ClampedCubicIsConvertedToBezierOnCurveSurfaces()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(Context(surface, configuration), ClampedUniformCubic());

        // Knot insertion turns the clamped cubic into 3 Bezier segments: 10 control points.
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawCubicBezier n=10", StringComparison.Ordinal));
        Assert.Empty(surface.Polylines);
    }

    [Fact]
    public void EmptySplineWarnsAndDrawsNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new() { Degree = 3 }; // no knots, no control points: every fallback in SplineRenderer.Draw fails.

        dispatcher.Draw(Context(surface, configuration), spline);

        Assert.Empty(surface.Polylines);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("spline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InconsistentKnotSplineFallsBackToAcadSharpTessellation()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 1d, 1d]); // wrong knot count for 4 control points of degree 3
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(2, 0, 0), new XYZ(3, 1, 0)]);

        dispatcher.Draw(Context(surface, configuration), spline);

        // ACadSharp 3.7.1's TryPolygonalVertexes tessellates this malformed spline anyway (via its own
        // fallback), so the renderer's last-resort path succeeds and draws a polyline instead of warning.
        Assert.Single(surface.Polylines);
        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.Warning);
    }
}
