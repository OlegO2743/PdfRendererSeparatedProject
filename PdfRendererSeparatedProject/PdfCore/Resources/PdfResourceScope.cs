using PdfCore.Color;

namespace PdfCore.Resources;

public sealed class PdfResourceScope
{
    public PdfResourceScope(PdfResourceSet localResources, PdfResourceScope? parent = null)
    {
        LocalResources = localResources;
        Parent = parent;
    }

    public PdfResourceSet LocalResources { get; }
    public PdfResourceScope? Parent { get; }

    public bool TryGetFont(string resourceName, out PdfFontResource? font)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.Fonts.TryGetValue(key, out font))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetFont(resourceName, out font);

        font = null;
        return false;
    }

    public bool TryGetForm(string resourceName, out PdfFormXObject? form)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.Forms.TryGetValue(key, out form))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetForm(resourceName, out form);

        form = null;
        return false;
    }

    public bool TryGetImage(string resourceName, out PdfImageXObject? image)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.Images.TryGetValue(key, out image))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetImage(resourceName, out image);

        image = null;
        return false;
    }

    public bool TryGetColorSpace(string resourceName, out PdfColorSpace? colorSpace)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.ColorSpaces.TryGetValue(key, out colorSpace))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetColorSpace(resourceName, out colorSpace);

        colorSpace = null;
        return false;
    }

    public bool TryGetPattern(string resourceName, out PdfTilingPattern? pattern)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.Patterns.TryGetValue(key, out pattern))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetPattern(resourceName, out pattern);

        pattern = null;
        return false;
    }

    public bool TryGetExtGraphicsState(string resourceName, out PdfExtGraphicsState? extGraphicsState)
    {
        foreach (string key in CandidateKeys(resourceName))
        {
            if (LocalResources.ExtGraphicsStates.TryGetValue(key, out extGraphicsState))
                return true;
        }

        if (Parent != null)
            return Parent.TryGetExtGraphicsState(resourceName, out extGraphicsState);

        extGraphicsState = null;
        return false;
    }

    private static IEnumerable<string> CandidateKeys(string name)
    {
        yield return name;
        if (name.StartsWith("/"))
            yield return name[1..];
        else
            yield return "/" + name;
    }
}
