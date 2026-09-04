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
    public void ANonUniformScaleUnderRotationIsNotASimilarityEvenWhenTheAxesMatchInLength()
    {
        // A 3:1 scale turned 45 degrees leaves both mapped axes the same length but no longer at right angles, so a
        // check that only compared lengths would wrongly call this a similarity.
        Transform placement = PlacementOf(XYZ.Zero, Math.PI / 4, 3, 1);

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
