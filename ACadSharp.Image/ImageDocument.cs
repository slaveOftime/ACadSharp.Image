namespace ACadSharp.Image;

public sealed class ImageDocument
{
    public IList<ImagePage> Pages { get; } = new List<ImagePage>();
}
