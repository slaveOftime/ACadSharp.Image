namespace ACadSharp.Image.Rendering;

/// <summary>
/// A point in surface coordinates: pixels for the raster backend, drawing units for SVG. Y grows downward.
/// </summary>
internal readonly record struct SurfacePoint(double X, double Y);

/// <summary>
/// An axis-aligned rectangle in surface coordinates. <see cref="Y"/> is the top edge.
/// </summary>
internal readonly record struct SurfaceRect(double X, double Y, double Width, double Height);
