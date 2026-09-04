using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ACadSharp.Image.Tests;

public sealed class InsertPlacementTests
{
    [Fact]
    public void MapPointWithoutAPlacementReturnsThePointUnchanged()
    {
        Assert.Equal(new XYZ(3, 4, 5), InsertPlacement.MapPoint(null, new XYZ(3, 4, 5)));
    }

    [Fact]
    public void MapVectorDropsTheTranslation()
    {
        Transform placement = Transform.CreateTranslation(new XYZ(100, 200, 300));

        Assert.Equal(new XYZ(1, 0, 0), InsertPlacement.MapVector(placement, new XYZ(1, 0, 0)));
    }

    [Fact]
    public void MapVectorKeepsTheLinearPart()
    {
        Transform placement = PlacementOf(new XYZ(100, 200, 0), 0d, 2, 3);

        XYZ mapped = InsertPlacement.MapVector(placement, new XYZ(1, 1, 0));

        Assert.Equal(2d, mapped.X, 9);
        Assert.Equal(3d, mapped.Y, 9);
    }

    /// <summary>
    /// A placement built the way production builds one: from a real block reference. Constructing a
    /// <c>Transform</c> directly would depend on an argument order these tests should not be pinning.
    /// </summary>
    private static Transform PlacementOf(XYZ insertPoint, double rotation, double xScale, double yScale)
        => new Insert(new BlockRecord("PLACEMENT"))
        {
            InsertPoint = insertPoint,
            Rotation = rotation,
            XScale = xScale,
            YScale = yScale,
            ZScale = Math.Abs(xScale),
        }.GetTransform();

    [Fact]
    public void MapOcsPointAppliesTheOcsBeforeThePlacement()
    {
        // Normal (0,0,-1) mirrors X going from OCS to world: (4,1) becomes (-4,1); the placement then adds (10,0).
        Transform placement = Transform.CreateTranslation(new XYZ(10, 0, 0));

        XYZ mapped = InsertPlacement.MapOcsPoint(placement, OcsTransform.For(new XYZ(0, 0, -1)), 0d, new XYZ(4, 1, 0));

        Assert.Equal(6d, mapped.X, 9);
        Assert.Equal(1d, mapped.Y, 9);
    }

    [Fact]
    public void MapOcsPointUsesTheElevationForTheOutOfPlaneOffset()
    {
        XYZ mapped = InsertPlacement.MapOcsPoint(null, OcsTransform.For(new XYZ(0, 0, -1)), 7d, new XYZ(1, 2, 0));

        Assert.Equal(-7d, mapped.Z, 9);
    }

    [Fact]
    public void MapOcsPointWithoutAnOcsIsAPlainPointMap()
    {
        Assert.Equal(new XYZ(1, 2, 0), InsertPlacement.MapOcsPoint(null, null, 0d, new XYZ(1, 2, 0)));
    }

    [Fact]
    public void ANullPlacementIsAUnitSimilarity()
    {
        Assert.True(InsertPlacement.TryGetPlanarSimilarity(null, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(1d, scale, 9);
        Assert.Equal(0d, rotation, 9);
        Assert.False(mirrored);
    }

    [Fact]
    public void AUniformlyScaledRotationIsASimilarity()
    {
        Transform placement = PlacementOf(new XYZ(5, 5, 0), Math.PI / 2, 3, 3);

        Assert.True(InsertPlacement.TryGetPlanarSimilarity(placement, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(3d, scale, 9);
        Assert.Equal(Math.PI / 2, rotation, 9);
        Assert.False(mirrored);
    }

    [Fact]
    public void AMirroredPlacementIsASimilarityAndSaysSo()
    {
        Transform placement = PlacementOf(XYZ.Zero, 0d, -2, 2);

        Assert.True(InsertPlacement.TryGetPlanarSimilarity(placement, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(2d, scale, 9);
        Assert.True(mirrored);
    }

    [Fact]
    public void ANonUniformScaleIsNotASimilarity()
    {
        Transform placement = PlacementOf(XYZ.Zero, 0d, 2, 5);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }

    [Fact]
    public void ANonUniformScaleUnderRotationIsRejectedOnAxisLength()
    {
        // Insert.GetTransform() scales in local axes and then applies a pure rotation, which always keeps two
        // originally-orthogonal axes orthogonal: turning a 3:1 scale 45 degrees gives mapped axis lengths 3 and 1
        // (unequal), not equal-length-but-skewed axes. This is caught by the length check, not the orthogonality
        // check; see AShearedPlacementWithEqualLengthAxesIsNotASimilarity for a case that reaches the latter.
        Transform placement = PlacementOf(XYZ.Zero, Math.PI / 4, 3, 1);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }

    [Fact]
    public void AShearedPlacementWithEqualLengthAxesIsNotASimilarity()
    {
        // A placement built from Insert.GetTransform() can never produce equal-length, non-orthogonal mapped axes
        // (see ANonUniformScaleUnderRotationIsRejectedOnAxisLength), so the orthogonality branch of
        // TryGetPlanarSimilarity is unreached by any placement the renderer builds today; it exists for the
        // arrow-block task, which composes placements, and a composition of two rotate+scale maps can shear. This
        // test drives that branch directly with a hand-built shear: X maps to (1,0,0) and Y maps to (cos 60, sin
        // 60, 0), both unit length with a 60 degree angle between them, so the rejection can only come from the
        // orthogonality term.
        Matrix4 matrix = new(
            1d, Math.Cos(Math.PI / 3d), 0d, 0d,
            0d, Math.Sin(Math.PI / 3d), 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, 0d, 0d, 1d);
        Transform placement = new(matrix);

        // Guard the test's own premise so it can never silently degrade into exercising the length check instead.
        XYZ ex = InsertPlacement.MapVector(placement, XYZ.AxisX);
        XYZ ey = InsertPlacement.MapVector(placement, XYZ.AxisY);
        Assert.Equal(new XY(ex.X, ex.Y).GetLength(), new XY(ey.X, ey.Y).GetLength(), 9);
        Assert.NotEqual(0d, (ex.X * ey.X) + (ex.Y * ey.Y), 6);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }

    [Fact]
    public void APlacementSeenEdgeOnIsNotASimilarity()
    {
        // Rotating a quarter turn about X flattens the Y axis onto Z, so nothing is left in the drawing plane.
        Transform placement = Transform.CreateRotation(XYZ.AxisX, Math.PI / 2);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }
}
