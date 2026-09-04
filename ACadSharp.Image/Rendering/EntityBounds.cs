using ACadSharp.Entities;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Bounds the renderer would actually draw, for page framing. ACadSharp's <c>GetBoundingBox</c> ignores a wipeout's
/// pixel vectors and a solid's extrusion normal, and throws for some malformed geometry; this helper applies the
/// renderer's own mapping for those and reports failure instead of throwing.
/// </summary>
internal static class EntityBounds
{
    /// <summary>
    /// Computes the bounds an entity would occupy as the renderer draws it.
    /// </summary>
    /// <param name="entity">The entity to bound.</param>
    /// <param name="bounds">The bounds, or <see langword="default"/> when the entity cannot contribute.</param>
    /// <returns>True when <paramref name="bounds"/> is valid.</returns>
    public static bool TryGet(Entity entity, out BoundingBox bounds)
    {
        bounds = default;
        switch (entity)
        {
            case Insert insert when insert.Block == null:
                return false;
            case Wipeout wipeout:
                return TryFromPoints(EntityRenderDispatcher.WipeoutWorldBoundary(wipeout), out bounds);
            case Solid solid when !OcsTransform.IsWorldPlane(solid.Normal):
                OcsTransform toWorld = OcsTransform.For(solid.Normal);
                return TryFromPoints([ToWorld(toWorld, solid.FirstCorner), ToWorld(toWorld, solid.SecondCorner), ToWorld(toWorld, solid.ThirdCorner), ToWorld(toWorld, solid.FourthCorner)], out bounds);
        }

        try
        {
            bounds = entity.GetBoundingBox();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ACadSharp throws for some malformed geometry (e.g. a bulge between coincident vertices).
            return false;
        }
    }

    /// <summary>
    /// Maps a solid corner through its OCS-to-world frame.
    /// </summary>
    /// <param name="toWorld">The OCS-to-world frame.</param>
    /// <param name="corner">The corner, in OCS coordinates.</param>
    /// <returns>The world point.</returns>
    private static XYZ ToWorld(OcsTransform toWorld, XYZ corner) => toWorld.ToWorld(corner.X, corner.Y, corner.Z);

    /// <summary>
    /// Builds the axis-aligned bounds enclosing a set of world points.
    /// </summary>
    /// <param name="points">The points to enclose.</param>
    /// <param name="bounds">The enclosing bounds, or <see langword="default"/> when <paramref name="points"/> is empty.</param>
    /// <returns>True when <paramref name="points"/> is non-empty.</returns>
    private static bool TryFromPoints(IReadOnlyList<XYZ> points, out BoundingBox bounds)
    {
        bounds = default;
        if (points.Count == 0)
        {
            return false;
        }

        double minX = points.Min(p => p.X);
        double minY = points.Min(p => p.Y);
        double minZ = points.Min(p => p.Z);
        double maxX = points.Max(p => p.X);
        double maxY = points.Max(p => p.Y);
        double maxZ = points.Max(p => p.Z);
        bounds = new BoundingBox(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        return true;
    }
}
