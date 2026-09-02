using ACadSharp.Entities;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Converts clamped, non-rational cubic B-splines into a chain of cubic Bezier segments by Boehm knot insertion.
/// </summary>
internal static class SplineBezierConverter
{
    private const double KnotTolerance = 1e-9;

    public static bool TryConvert(Spline spline, out List<XYZ> bezierControlPoints)
    {
        bezierControlPoints = new List<XYZ>();
        const int degree = 3;
        IReadOnlyList<double> knots = spline.Knots;
        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;

        if (spline.Degree != degree ||
            spline.Weights.Any(w => Math.Abs(w - 1d) > 1e-12) ||
            controlPoints.Count < degree + 1 ||
            knots.Count != controlPoints.Count + degree + 1 ||
            !HasMultiplicity(knots, 0, degree + 1) ||
            !HasMultiplicity(knots, knots.Count - (degree + 1), degree + 1))
        {
            return false;
        }

        List<double> k = new(knots);
        List<XYZ> p = new(controlPoints);

        int index = degree + 1;
        while (index < k.Count - (degree + 1))
        {
            double u = k[index];
            int multiplicity = 1;
            while (index + multiplicity < k.Count && Math.Abs(k[index + multiplicity] - u) <= KnotTolerance)
            {
                multiplicity++;
            }

            for (int m = multiplicity; m < degree; m++)
            {
                InsertKnot(k, p, u, degree);
            }

            index += degree;
        }

        if ((p.Count - 1) % degree != 0)
        {
            return false;
        }

        bezierControlPoints = p;
        return true;
    }

    /// <summary>
    /// Boehm's algorithm: inserts <paramref name="u"/> once, updating knots and control points in place.
    /// </summary>
    public static void InsertKnot(List<double> knots, List<XYZ> points, double u, int degree)
    {
        int span = FindSpan(knots, points.Count, degree, u);
        List<XYZ> updated = new(points.Count + 1);
        for (int i = 0; i <= span - degree; i++)
        {
            updated.Add(points[i]);
        }

        for (int i = span - degree + 1; i <= span; i++)
        {
            double denominator = knots[i + degree] - knots[i];
            double alpha = denominator <= KnotTolerance ? 0d : (u - knots[i]) / denominator;
            XYZ a = points[i - 1];
            XYZ b = points[i];
            updated.Add(new XYZ(
                ((1d - alpha) * a.X) + (alpha * b.X),
                ((1d - alpha) * a.Y) + (alpha * b.Y),
                ((1d - alpha) * a.Z) + (alpha * b.Z)));
        }

        for (int i = span; i < points.Count; i++)
        {
            updated.Add(points[i]);
        }

        points.Clear();
        points.AddRange(updated);
        knots.Insert(span + 1, u);
    }

    private static int FindSpan(List<double> knots, int pointCount, int degree, double u)
    {
        int last = pointCount - 1;
        if (u >= knots[pointCount])
        {
            return last;
        }

        int span = degree;
        while (span < last && u >= knots[span + 1])
        {
            span++;
        }

        return span;
    }

    private static bool HasMultiplicity(IReadOnlyList<double> knots, int start, int count)
    {
        if (start < 0 || start + count > knots.Count)
        {
            return false;
        }

        for (int i = 1; i < count; i++)
        {
            if (Math.Abs(knots[start + i] - knots[start]) > KnotTolerance)
            {
                return false;
            }
        }

        return true;
    }
}
