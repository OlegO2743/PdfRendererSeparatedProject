using System.Drawing.Imaging;
using System.IO;
using PdfCore;
using PdfCore.Color;
using PdfCore.Images.Jpeg2000;
using PdfCore.Parsing;
using PdfCore.Resources;

if (string.Equals(Environment.GetEnvironmentVariable("PDF_COLOR_MODE"), "icc", StringComparison.OrdinalIgnoreCase))
    PdfColorManagementSettings.Mode = PdfColorManagementMode.ExperimentalPhase2Icc;

if (args.Length >= 1 && string.Equals(args[0], "fonts", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage fonts <pdf-path> <page-number-1-based>");
        return 1;
    }

    string fontsPdfPath = args[1];
    if (!File.Exists(fontsPdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + fontsPdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int fontsPageNumber) || fontsPageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    SimplePdfDocument fontsDocument = SimplePdfParser.Parse(fontsPdfPath);
    if (fontsPageNumber > fontsDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {fontsDocument.Pages.Count} pages.");
        return 6;
    }

    SimplePdfPage fontsPage = fontsDocument.Pages[fontsPageNumber - 1];
    Console.WriteLine($"Page={fontsPageNumber} Fonts={fontsPage.Resources.Fonts.Count}");
    foreach ((string name, PdfFontResource font) in fontsPage.Resources.Fonts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"{name} BaseFont={font.BaseFontName} IdentityH={font.IsIdentityH} Widths={(font.Widths?.Length ?? 0)} " +
            $"CidWidths={(font.CidWidths?.Count ?? 0)} ToUnicode={(font.ToUnicodeMap?.Count ?? 0)} " +
            $"Encoding={(font.EncodingMap?.Count ?? 0)} GlyphNames={(font.GlyphNameMap?.Count ?? 0)} " +
            $"FontBytes={(font.FontFileBytes?.Length ?? 0)} FontSubtype={font.FontFileSubtype ?? "-"} PreferCid={font.PreferCidGlyphCodesForRendering}");

        if (font.ToUnicodeMap != null && font.ToUnicodeMap.Count > 0)
        {
            int[] sampleCodes = [0x0003, 0x0013, 0x0031, 0x0038, 0x0044, 0x004F, 0x0052, 0x0057];
            string samples = string.Join(
                ", ",
                sampleCodes
                    .Where(font.ToUnicodeMap.ContainsKey)
                    .Select(code => $"{code:X4}=>{EscapeSample(font.ToUnicodeMap[code])}"));
            if (!string.IsNullOrEmpty(samples))
                Console.WriteLine("  SampleToUnicode: " + samples);
        }
    }

    return 0;
}

if (args.Length >= 1 && string.Equals(args[0], "content-dump", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage content-dump <pdf-path> <page-number-1-based> [output-txt]");
        return 1;
    }

    string contentPdfPath = args[1];
    if (!File.Exists(contentPdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + contentPdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int contentPageNumber) || contentPageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    SimplePdfDocument contentDocument = SimplePdfParser.Parse(contentPdfPath);
    if (contentPageNumber > contentDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {contentDocument.Pages.Count} pages.");
        return 6;
    }

    string contentOutputPath = args.Length >= 4
        ? args[3]
        : Path.Combine(AppContext.BaseDirectory, $"page_{contentPageNumber}_content.txt");

    SimplePdfPage contentPage = contentDocument.Pages[contentPageNumber - 1];
    Directory.CreateDirectory(Path.GetDirectoryName(contentOutputPath)!);
    File.WriteAllText(contentOutputPath, contentPage.ContentStream);

    Console.WriteLine("Saved content stream: " + contentOutputPath);
    Console.WriteLine($"Page={contentPageNumber} Length={contentPage.ContentStream.Length}");
    return 0;
}

if (args.Length >= 1 && string.Equals(args[0], "images", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage images <pdf-path> <page-number-1-based>");
        return 1;
    }

    string inspectPdfPath = args[1];
    if (!File.Exists(inspectPdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + inspectPdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int inspectPageNumber) || inspectPageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    SimplePdfDocument inspectDocument = SimplePdfParser.Parse(inspectPdfPath);
    if (inspectPageNumber > inspectDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {inspectDocument.Pages.Count} pages.");
        return 6;
    }

    SimplePdfPage inspectPage = inspectDocument.Pages[inspectPageNumber - 1];
    Console.WriteLine($"Page={inspectPageNumber} Images={inspectPage.Resources.Images.Count}");
    foreach ((string name, PdfImageXObject image) in inspectPage.Resources.Images.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        string extra = image.ColorSpace is PdfIccBasedColorSpace icc
            ? $" N={icc.N} ProfileBytes={icc.ProfileBytes.Length} ProfileObj={icc.ProfileObjectNumber?.ToString() ?? "-"} Alternate={icc.Alternate?.Name ?? "-"}"
            : string.Empty;
        Console.WriteLine(
            $"{name} Filter={image.Filter} Size={image.Width}x{image.Height} Bpc={image.BitsPerComponent} ColorSpace={image.ColorSpace}{extra}");
    }

    return 0;
}

if (args.Length >= 1 && string.Equals(args[0], "image-raw", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage image-raw <pdf-path> <page-number-1-based> <resource-name> [output-bin]");
        return 1;
    }

    string rawPdfPath = args[1];
    if (!File.Exists(rawPdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + rawPdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int rawPageNumber) || rawPageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    string rawResourceName = args[3];
    SimplePdfDocument rawDocument = SimplePdfParser.Parse(rawPdfPath);
    if (rawPageNumber > rawDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {rawDocument.Pages.Count} pages.");
        return 6;
    }

    SimplePdfPage rawPage = rawDocument.Pages[rawPageNumber - 1];
    if (!rawPage.Resources.Images.TryGetValue(rawResourceName, out PdfImageXObject? rawImageObject))
    {
        Console.Error.WriteLine($"Image resource not found on page {rawPageNumber}: {rawResourceName}");
        return 7;
    }

    string rawExtension = string.Equals(rawImageObject.Filter, "/JPXDecode", StringComparison.Ordinal)
        ? ".jpx"
        : ".bin";
    string rawOutputPath = args.Length >= 5
        ? args[4]
        : Path.Combine(AppContext.BaseDirectory, $"page_{rawPageNumber}_{rawResourceName.TrimStart('/')}{rawExtension}");

    Directory.CreateDirectory(Path.GetDirectoryName(rawOutputPath)!);
    File.WriteAllBytes(rawOutputPath, rawImageObject.ImageBytes);

    Console.WriteLine("Saved raw: " + rawOutputPath);
    Console.WriteLine(
        $"Page={rawPageNumber} Resource={rawResourceName} Filter={rawImageObject.Filter} Bytes={rawImageObject.ImageBytes.Length}");
    if (string.Equals(rawImageObject.Filter, "/JPXDecode", StringComparison.Ordinal))
        Console.WriteLine(Jpeg2000Decoder.Describe(rawImageObject.ImageBytes, parsePackets: false));
    return 0;
}

if (args.Length >= 1 && string.Equals(args[0], "image-decoded", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage image-decoded <pdf-path> <page-number-1-based> <resource-name> [output-png]");
        return 1;
    }

    string decodedPdfPath = args[1];
    if (!File.Exists(decodedPdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + decodedPdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int decodedPageNumber) || decodedPageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    string decodedResourceName = args[3];
    string decodedOutputPath = args.Length >= 5
        ? args[4]
        : Path.Combine(AppContext.BaseDirectory, $"page_{decodedPageNumber}_{decodedResourceName.TrimStart('/')}_decoded.png");

    SimplePdfDocument decodedDocument = SimplePdfParser.Parse(decodedPdfPath);
    if (decodedPageNumber > decodedDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {decodedDocument.Pages.Count} pages.");
        return 6;
    }

    SimplePdfPage decodedPage = decodedDocument.Pages[decodedPageNumber - 1];
    if (!decodedPage.Resources.Images.TryGetValue(decodedResourceName, out PdfImageXObject? decodedImageObject))
    {
        Console.Error.WriteLine($"Image resource not found on page {decodedPageNumber}: {decodedResourceName}");
        return 7;
    }

    using Bitmap decodedBitmap = decodedImageObject.Filter switch
    {
        "/JPXDecode" => Jpeg2000Decoder.Decode(decodedImageObject.ImageBytes),
        "/DCTDecode" => LoadJpegBitmap(decodedImageObject.ImageBytes),
        _ => throw new NotSupportedException($"image-decoded supports only /JPXDecode and /DCTDecode, got {decodedImageObject.Filter}.")
    };

    Directory.CreateDirectory(Path.GetDirectoryName(decodedOutputPath)!);
    decodedBitmap.Save(decodedOutputPath, ImageFormat.Png);

    Console.WriteLine("Saved decoded-only: " + decodedOutputPath);
    Console.WriteLine(
        $"Page={decodedPageNumber} Resource={decodedResourceName} Filter={decodedImageObject.Filter} Size={decodedBitmap.Width}x{decodedBitmap.Height}");
    if (string.Equals(decodedImageObject.Filter, "/JPXDecode", StringComparison.Ordinal))
        Console.WriteLine(Jpeg2000Decoder.Describe(decodedImageObject.ImageBytes, parsePackets: false));
    return 0;
}

if (args.Length >= 1 && string.Equals(args[0], "image", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: _diag_renderpage image <pdf-path> <page-number-1-based> <resource-name> [output-png]");
        return 1;
    }

    string imagePdfPath = args[1];
    if (!File.Exists(imagePdfPath))
    {
        Console.Error.WriteLine("PDF file not found: " + imagePdfPath);
        return 2;
    }

    if (!int.TryParse(args[2], out int imagePageNumber) || imagePageNumber <= 0)
    {
        Console.Error.WriteLine("Page number must be a positive 1-based integer.");
        return 3;
    }

    string resourceName = args[3];
    string imageOutputPath = args.Length >= 5
        ? args[4]
        : Path.Combine(AppContext.BaseDirectory, $"page_{imagePageNumber}_{resourceName.TrimStart('/')}.png");

    SimplePdfDocument imageDocument = SimplePdfParser.Parse(imagePdfPath);
    if (imagePageNumber > imageDocument.Pages.Count)
    {
        Console.Error.WriteLine($"Document has only {imageDocument.Pages.Count} pages.");
        return 6;
    }

    SimplePdfPage imagePage = imageDocument.Pages[imagePageNumber - 1];
    if (!imagePage.Resources.Images.TryGetValue(resourceName, out PdfImageXObject? imageObject))
    {
        Console.Error.WriteLine($"Image resource not found on page {imagePageNumber}: {resourceName}");
        return 7;
    }

    using var bitmap = imageObject.CreateBitmap();
    Directory.CreateDirectory(Path.GetDirectoryName(imageOutputPath)!);
    bitmap.Save(imageOutputPath, ImageFormat.Png);

    Console.WriteLine("Saved: " + imageOutputPath);
    Console.WriteLine(
        $"Page={imagePageNumber} Resource={resourceName} Filter={imageObject.Filter} Size={bitmap.Width}x{bitmap.Height}");
    if (string.Equals(imageObject.Filter, "/JPXDecode", StringComparison.Ordinal))
        Console.WriteLine(Jpeg2000Decoder.Describe(imageObject.ImageBytes, parsePackets: false));
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: _diag_renderpage <pdf-path> <page-number-1-based> [zoom] [output-png]");
    return 1;
}

string pdfPath = args[0];
if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine("PDF file not found: " + pdfPath);
    return 2;
}

if (!int.TryParse(args[1], out int pageNumber) || pageNumber <= 0)
{
    Console.Error.WriteLine("Page number must be a positive 1-based integer.");
    return 3;
}

float zoom = 1f;
if (args.Length >= 3 &&
    !float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoom))
{
    Console.Error.WriteLine("Zoom must be a floating-point number.");
    return 4;
}

if (zoom <= 0f)
{
    Console.Error.WriteLine("Zoom must be > 0.");
    return 5;
}

string outputPath = args.Length >= 4
    ? args[3]
    : Path.Combine(
        AppContext.BaseDirectory,
        $"page_{pageNumber}_z{zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_')}.png");

SimplePdfDocument document = SimplePdfParser.Parse(pdfPath);
if (pageNumber > document.Pages.Count)
{
    Console.Error.WriteLine($"Document has only {document.Pages.Count} pages.");
    return 6;
}

SimplePdfPage page = document.Pages[pageNumber - 1];
PdfRenderResult render = SimplePdfRenderer.RenderWithObjects(page, zoom);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
render.Bitmap.Save(outputPath, ImageFormat.Png);

Console.WriteLine("Saved: " + outputPath);
Console.WriteLine($"Page={pageNumber} Zoom={zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)} Size={render.Bitmap.Width}x{render.Bitmap.Height} Objects={render.Objects.Count}");
return 0;

static Bitmap LoadJpegBitmap(byte[] bytes)
{
    using var ms = new MemoryStream(bytes);
    using var bitmap = new Bitmap(ms);
    return new Bitmap(bitmap);
}

static string EscapeSample(string text)
{
    if (string.IsNullOrEmpty(text))
        return "<empty>";

    return text
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace(" ", "<space>", StringComparison.Ordinal);
}
