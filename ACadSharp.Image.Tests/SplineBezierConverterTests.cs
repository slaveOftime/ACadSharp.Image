using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class SplineBezierConverterTests
{
    private static Spline ClampedUniformCubic()
    {
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 0d, 0d, 1d, 2d, 3d, 3d, 3d, 3d]);
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0), new XYZ(1, 3, 0), new XYZ(3, 4, 0), new XYZ(5, 1, 0), new XYZ(7, 3, 0), new XYZ(9, 0, 0),
        ]);
        return spline;
    }

    [Fact]
    public void ConvertsClampedCubicIntoBezierChain()
    {
        Spline spline = ClampedUniformCubic();

        Assert.True(SplineBezierConverter.TryConvert(spline, out List<XYZ> bezier));

        // 3 knot spans -> 3 segments -> 10 control points.
        Assert.Equal(10, bezier.Count);
        Assert.Equal(spline.ControlPoints[0], bezier[0]);
        Assert.Equal(spline.ControlPoints[^1], bezier[^1]);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(1.7)]
    [InlineData(2.9)]
    public void BezierChainMatchesDeBoorEvaluation(double t)
    {
        Spline spline = ClampedUniformCubic();
        Assert.True(SplineBezierConverter.TryConvert(spline, out List<XYZ> bezier));

        XY expected = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, spline.Weights, t);

        // Segment index and local parameter for uniform interior knots 0,1,2,3.
        int segment = Math.Min(2, (int)Math.Floor(t));
        double u = t - segment;
        XYZ p0 = bezier[segment * 3];
        XYZ p1 = bezier[(segment * 3) + 1];
        XYZ p2 = bezier[(segment * 3) + 2];
        XYZ p3 = bezier[(segment * 3) + 3];
        double v = 1 - u;
        double x = (v * v * v * p0.X) + (3 * v * v * u * p1.X) + (3 * v * u * u * p2.X) + (u * u * u * p3.X);
        double y = (v * v * v * p0.Y) + (3 * v * v * u * p1.Y) + (3 * v * u * u * p2.Y) + (u * u * u * p3.Y);

        Assert.Equal(expected.X, x, 9);
        Assert.Equal(expected.Y, y, 9);
    }

    [Fact]
    public void RejectsRationalUnclampedOrNonCubic()
    {
        Spline rational = ClampedUniformCubic();
        rational.Weights.AddRange(Enumerable.Repeat(2d, 6));
        Assert.False(SplineBezierConverter.TryConvert(rational, out _));

        Spline quadratic = new() { Degree = 2 };
        quadratic.Knots.AddRange([0d, 0d, 0d, 1d, 1d, 1d]);
        quadratic.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(2, 0, 0)]);
        Assert.False(SplineBezierConverter.TryConvert(quadratic, out _));

        Spline unclamped = ClampedUniformCubic();
        unclamped.Knots[0] = -1d;
        Assert.False(SplineBezierConverter.TryConvert(unclamped, out _));
    }

    [Fact]
    public void RejectsInteriorKnotMultiplicityAboveDegree()
    {
        // A multiplicity-4 interior knot breaks the curve into two independent splines; it is not a Bezier chain.
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 0d, 0d, 1d, 1d, 1d, 1d, 2d, 2d, 2d, 2d]);
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(2, 1, 0), new XYZ(3, 0, 0),
            new XYZ(4, 0, 0), new XYZ(5, -1, 0), new XYZ(6, -1, 0), new XYZ(7, 0, 0),
        ]);

        Assert.False(SplineBezierConverter.TryConvert(spline, out _));
    }
}
