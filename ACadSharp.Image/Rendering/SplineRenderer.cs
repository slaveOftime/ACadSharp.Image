using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace ACadSharp.Image.Rendering;

internal sealed class SplineRenderer(ImageConfiguration configuration)
{
    private readonly ImageConfiguration _configuration = configuration;

    public bool Draw(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (this.DrawBezierSpline(context, style, spline))
        {
            return true;
        }

        XY[] sampledVertices = this.SampleSpline(spline);
        if (sampledVertices.Length > 1)
        {
            PointF[] points = new PointF[sampledVertices.Length];
            for (int i = 0; i < sampledVertices.Length; i++)
            {
                points[i] = context.ToPixelPoint(sampledVertices[i]);
            }

            points = ClosePoints(points, spline.IsClosed || spline.IsPeriodic);
            context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, points));
            return true;
        }

        if (spline.TryPolygonalVertexes(this._configuration.ArcPrecision, out List<XYZ>? polygonalPoints) && polygonalPoints.Count > 1)
        {
            PointF[] points = new PointF[polygonalPoints.Count];
            for (int i = 0; i < polygonalPoints.Count; i++)
            {
                points[i] = context.ToPixelPoint(polygonalPoints[i].Convert<XY>());
            }

            points = ClosePoints(points, spline.IsClosed || spline.IsPeriodic);
            context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, points));
            return true;
        }

        this._configuration.Notify($"[{spline.SubclassMarker}] Could not approximate spline geometry.", NotificationType.Warning);
        return false;
    }

    private bool DrawBezierSpline(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (!TryGetBezierSegments(spline, out int segmentCount))
        {
            return false;
        }

        PathBuilder builder = new();
        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int index = segment * 3;
            builder.AddCubicBezier(
                context.ToPixelPoint(controlPoints[index]),
                context.ToPixelPoint(controlPoints[index + 1]),
                context.ToPixelPoint(controlPoints[index + 2]),
                context.ToPixelPoint(controlPoints[index + 3]));
        }

        IPath path = builder.Build();
        context.Canvas.Mutate(x => x.Draw(style.StrokeColor, style.StrokeWidth, path));
        return true;
    }

    private static bool TryGetBezierSegments(Spline spline, out int segmentCount)
    {
        segmentCount = 0;
        if (spline.Degree != 3 ||
            spline.Weights.Count != 0 ||
            spline.Knots.Count != spline.ControlPoints.Count + 4 ||
            spline.ControlPoints.Count < 4 ||
            (spline.ControlPoints.Count - 1) % 3 != 0)
        {
            return false;
        }

        segmentCount = (spline.ControlPoints.Count - 1) / 3;
        return HasKnotMultiplicity(spline.Knots, 0, 4) &&
            HasKnotMultiplicity(spline.Knots, spline.Knots.Count - 4, 4) &&
            HasBezierInternalKnots(spline.Knots, segmentCount);
    }

    private static bool HasBezierInternalKnots(IReadOnlyList<double> knots, int segmentCount)
    {
        int index = 4;
        for (int segment = 1; segment < segmentCount; segment++)
        {
            if (!HasKnotMultiplicity(knots, index, 3))
            {
                return false;
            }

            index += 3;
        }

        return index == knots.Count - 4;
    }

    private static bool HasKnotMultiplicity(IReadOnlyList<double> knots, int startIndex, int count)
    {
        if (startIndex < 0 || startIndex + count > knots.Count)
        {
            return false;
        }

        double value = knots[startIndex];
        for (int i = 1; i < count; i++)
        {
            if (Math.Abs(knots[startIndex + i] - value) > double.Epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private XY[] SampleSpline(Spline spline)
    {
        int degree = (int)spline.Degree;
        IReadOnlyList<double> knots = spline.Knots;
        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;
        IReadOnlyList<double> weights = spline.Weights;

        if (degree < 1 ||
            controlPoints.Count <= degree ||
            knots.Count != controlPoints.Count + degree + 1 ||
            (weights.Count != 0 && weights.Count != controlPoints.Count))
        {
            return [];
        }

        double start = knots[degree];
        double end = knots[controlPoints.Count];
        if (end <= start)
        {
            return [];
        }

        int knotSpans = 0;
        double previous = start;
        for (int i = degree + 1; i <= controlPoints.Count; i++)
        {
            double current = knots[i];
            if (current > previous)
            {
                knotSpans++;
                previous = current;
            }
        }

        int precision = Math.Max(this._configuration.ArcPrecision, knotSpans * 16);
        List<XY> vertices = new(precision + 1);
        for (int i = 0; i <= precision; i++)
        {
            double t = start + ((end - start) * i / precision);
            vertices.Add(EvaluateSplinePoint(degree, knots, controlPoints, weights, t));
        }

        return vertices.ToArray();
    }

    private static XY EvaluateSplinePoint(
        int degree,
        IReadOnlyList<double> knots,
        IReadOnlyList<XYZ> controlPoints,
        IReadOnlyList<double> weights,
        double t)
    {
        int span = FindKnotSpan(degree, knots, controlPoints.Count, t);
        SplinePoint[] points = new SplinePoint[degree + 1];

        for (int i = 0; i <= degree; i++)
        {
            int pointIndex = span - degree + i;
            XYZ point = controlPoints[pointIndex];
            double weight = weights.Count == 0 ? 1d : weights[pointIndex];
            points[i] = new SplinePoint(point.X * weight, point.Y * weight, point.Z * weight, weight);
        }

        for (int level = 1; level <= degree; level++)
        {
            for (int i = degree; i >= level; i--)
            {
                int knotIndex = span - degree + i;
                double denominator = knots[knotIndex + degree + 1 - level] - knots[knotIndex];
                double alpha = denominator == 0d ? 0d : (t - knots[knotIndex]) / denominator;
                points[i] = SplinePoint.Lerp(points[i - 1], points[i], alpha);
            }
        }

        SplinePoint result = points[degree];
        return result.Weight == 0d
            ? new XY(result.X, result.Y)
            : new XY(result.X / result.Weight, result.Y / result.Weight);
    }

    private static int FindKnotSpan(int degree, IReadOnlyList<double> knots, int controlPointCount, double t)
    {
        int maxSpan = controlPointCount - 1;
        if (t >= knots[controlPointCount])
        {
            return maxSpan;
        }

        int low = degree;
        int high = controlPointCount;
        int span = (low + high) / 2;
        while (t < knots[span] || t >= knots[span + 1])
        {
            if (t < knots[span])
            {
                high = span;
            }
            else
            {
                low = span;
            }

            span = (low + high) / 2;
        }

        return span;
    }

    private static PointF[] ClosePoints(PointF[] points, bool close)
    {
        if (!close || !ShouldClose(points))
        {
            return points;
        }

        PointF[] closedPoints = new PointF[points.Length + 1];
        Array.Copy(points, closedPoints, points.Length);
        closedPoints[^1] = points[0];
        return closedPoints;
    }

    private static bool ShouldClose(IReadOnlyList<PointF> points)
    {
        if (points.Count < 3)
        {
            return false;
        }

        float totalLength = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            totalLength += Distance(points[i - 1], points[i]);
        }

        float averageSegmentLength = totalLength / (points.Count - 1);
        float closingLength = Distance(points[^1], points[0]);
        return closingLength <= averageSegmentLength * 3f;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private readonly record struct SplinePoint(double X, double Y, double Z, double Weight)
    {
        public static SplinePoint Lerp(SplinePoint start, SplinePoint end, double amount)
        {
            double inverse = 1d - amount;
            return new SplinePoint(
                (start.X * inverse) + (end.X * amount),
                (start.Y * inverse) + (end.Y * amount),
                (start.Z * inverse) + (end.Z * amount),
                (start.Weight * inverse) + (end.Weight * amount));
        }
    }
}
