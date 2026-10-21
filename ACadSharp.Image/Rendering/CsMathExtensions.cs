using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Vector helpers that CSMath 1.x exposed as instance methods and CSMath 2.x removed.
/// </summary>
internal static class CsMathExtensions
{
    /// <summary>Euclidean length of a 2D vector.</summary>
    public static double GetLength(this XY vector) => Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));

    /// <summary>Euclidean length of a 3D vector.</summary>
    public static double GetLength(this XYZ vector) =>
        Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z));

    /// <summary>Whether both components of a 2D vector are exactly zero.</summary>
    public static bool IsZero(this XY vector) => vector.X == 0d && vector.Y == 0d;
}
