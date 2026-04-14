using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Drawing.Imaging;
using PdfCore.Parsing;
using PdfCore;
using PdfCore.Color;
using System.Reflection;
using PdfCore.Text;
using PdfCore.Resources;

class P
{
    static void Main()
    {
        var cli = Environment.GetCommandLineArgs();
        if (cli.Length > 3 && string.Equals(cli[1], "pagefonts", StringComparison.OrdinalIgnoreCase))
        {
            DumpPageFonts(cli[2], int.Parse(cli[3]));
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "type1coverage", StringComparison.OrdinalIgnoreCase))
        {
            CheckType1Coverage(cli[2], int.Parse(cli[3]));
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "inspectfont", StringComparison.OrdinalIgnoreCase))
        {
            if (cli.Length > 4 && int.TryParse(cli[3], out int inspectPageNumber))
                InspectFont(cli[2], inspectPageNumber, cli[4], cli.Skip(5).ToArray());
            else
                InspectFont(cli[2], 19, cli[3], cli.Skip(4).ToArray());
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "dumpcontent", StringComparison.OrdinalIgnoreCase))
        {
            DumpContent(cli[2], int.Parse(cli[3]));
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "inspectobjects", StringComparison.OrdinalIgnoreCase))
        {
            InspectObjects(cli[2], int.Parse(cli[3]), cli.Skip(4).ToArray());
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "inspectrawobjects", StringComparison.OrdinalIgnoreCase))
        {
            InspectRawObjects(cli[2], int.Parse(cli[3]), cli.Skip(4).ToArray());
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "inspectassets", StringComparison.OrdinalIgnoreCase))
        {
            InspectAssets(cli[2], int.Parse(cli[3]));
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "inspectxobjects", StringComparison.OrdinalIgnoreCase))
        {
            InspectXObjects(cli[2], int.Parse(cli[3]));
            return;
        }
        if (cli.Length > 4 && string.Equals(cli[1], "savexobjects", StringComparison.OrdinalIgnoreCase))
        {
            SaveXObjects(cli[2], int.Parse(cli[3]), cli[4]);
            return;
        }
        if (cli.Length > 4 && string.Equals(cli[1], "saverender", StringComparison.OrdinalIgnoreCase))
        {
            SaveRender(cli[2], int.Parse(cli[3]), cli[4], cli.Skip(5).ToArray());
            return;
        }
        if (cli.Length > 3 && string.Equals(cli[1], "traceops", StringComparison.OrdinalIgnoreCase))
        {
            TraceOps(cli[2], int.Parse(cli[3]), cli.Skip(4).ToArray());
            return;
        }

        string pdfPath = @"C:\Users\Oleg Ogar\Downloads\book.pdf";
        int pageNumber = 1;
        if (cli.Length > 1 && int.TryParse(cli[1], out int requestedPage))
            pageNumber = Math.Max(1, requestedPage);

        DumpPageFonts(pdfPath, pageNumber);
    }

    private static string Escape(string text)
        => text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void DumpPageFonts(string pdfPath, int pageNumber)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];

        Console.WriteLine("Pages=" + doc.Pages.Count);
        Console.WriteLine($"Page={pageIndex + 1}");
        Console.WriteLine($"Size={page.WidthPt}x{page.HeightPt}");
        Console.WriteLine("Fonts on page:");

        foreach (var pair in page.Resources.Fonts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var font = pair.Value;
            Console.WriteLine($"[{pair.Key}] BaseFont={font.BaseFontName}");
            Console.WriteLine($"  IdentityH={font.IsIdentityH}");
            Console.WriteLine($"  ToUnicode={(font.ToUnicodeMap != null ? font.ToUnicodeMap.Count.ToString() : "null")}");
            Console.WriteLine($"  EncodingMap={(font.EncodingMap != null ? font.EncodingMap.Count.ToString() : "null")}");
            Console.WriteLine($"  GlyphNameMap={(font.GlyphNameMap != null ? font.GlyphNameMap.Count.ToString() : "null")}");
            Console.WriteLine($"  FontFileBytes={(font.FontFileBytes != null ? font.FontFileBytes.Length.ToString() : "null")}");
            Console.WriteLine($"  FontFileSubtype={font.FontFileSubtype ?? "null"}");
            Console.WriteLine($"  PreferCidGlyphCodes={font.PreferCidGlyphCodesForRendering}");
            if (font.FontFileBytes != null && font.FontFileBytes.Length > 0)
            {
                int count = Math.Min(16, font.FontFileBytes.Length);
                Console.WriteLine($"  FontFileHead={BitConverter.ToString(font.FontFileBytes, 0, count)}");

                string fontText = Encoding.Latin1.GetString(font.FontFileBytes);
                int encodingIndex = fontText.IndexOf("/Encoding", StringComparison.Ordinal);
                if (encodingIndex >= 0)
                {
                    MatchCollection dupMatches = Regex.Matches(fontText, @"dup\s+(\d+)\s+/([^\s]+)\s+put");
                    if (dupMatches.Count > 0)
                    {
                        Console.WriteLine("  EncodingDups(sample)=" + string.Join(
                            ", ",
                            dupMatches.Cast<Match>()
                                .Where(m =>
                                {
                                    int code = int.Parse(m.Groups[1].Value);
                                    return code <= 5 || code == 40 || code == 41 || code == 61;
                                })
                                .Take(24)
                                .Select(m => $"{m.Groups[1].Value}:{m.Groups[2].Value}")));
                    }
                }
            }

            if (font.EncodingMap != null)
            {
                foreach (var enc in font.EncodingMap.OrderBy(e => e.Key).Take(16))
                    Console.WriteLine($"    enc {enc.Key} => {Escape(enc.Value)}");
            }

            if (font.GlyphNameMap != null)
            {
                Console.WriteLine("  glyph names without unicode mapping:");
                foreach (var glyph in font.GlyphNameMap
                             .OrderBy(e => e.Key)
                             .Where(e => font.EncodingMap == null || !font.EncodingMap.ContainsKey(e.Key))
                             .Take(48))
                {
                    Console.WriteLine($"    glyph {glyph.Key} => {glyph.Value}");
                }
            }

            if (font.ToUnicodeMap != null)
            {
                foreach (var uni in font.ToUnicodeMap.OrderBy(e => e.Key).Take(16))
                    Console.WriteLine($"    uni {uni.Key} => {Escape(uni.Value)}");
            }
        }

        Console.WriteLine("Content head:");
        Console.WriteLine(page.ContentStream[..Math.Min(12000, page.ContentStream.Length)]);
    }

    private static void DumpContent(string pdfPath, int pageNumber)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        Console.WriteLine(doc.Pages[pageIndex].ContentStream);
    }

    private static void InspectObjects(string pdfPath, int pageNumber, string[] extraArgs)
    {
        float zoom = 1f;
        foreach (string arg in extraArgs)
        {
            if (arg.StartsWith("zoom=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(arg.AsSpan("zoom=".Length), System.Globalization.CultureInfo.InvariantCulture, out float parsedZoom))
            {
                zoom = parsedZoom;
            }
        }

        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];
        PdfRenderResult result = SimplePdfRenderer.RenderWithObjects(page, zoom);

        Console.WriteLine($"Page={pageNumber}, Zoom={zoom}, Objects={result.Objects.Count}");
        for (int i = 0; i < result.Objects.Count; i++)
        {
            PdfRenderObject obj = result.Objects[i];
            RectangleF b = obj.Bounds;
            string preview = (obj.Content ?? string.Empty)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            if (preview.Length > 120)
                preview = preview[..120] + "...";

            Console.WriteLine(
                $"{i:D4} {obj.Kind,-10} x={b.X,8:0.##} y={b.Y,8:0.##} w={b.Width,8:0.##} h={b.Height,8:0.##} text={preview}");
        }

        result.Bitmap.Dispose();
    }

    private static void InspectRawObjects(string pdfPath, int pageNumber, string[] extraArgs)
    {
        float zoom = 1f;
        foreach (string arg in extraArgs)
        {
            if (arg.StartsWith("zoom=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(arg.AsSpan("zoom=".Length), System.Globalization.CultureInfo.InvariantCulture, out float parsedZoom))
            {
                zoom = parsedZoom;
            }
        }

        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];

        PdfRenderResult result = SimplePdfRenderer.RenderWithObjects(page, zoom, mergeObjects: false);
        var objects = result.Objects;
        Console.WriteLine($"Page={pageNumber}, Zoom={zoom}, RawObjects={objects.Count}");
        for (int i = 0; i < objects.Count; i++)
        {
            PdfRenderObject obj = objects[i];
            RectangleF b = obj.Bounds;
            string preview = (obj.Content ?? string.Empty)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            if (preview.Length > 120)
                preview = preview[..120] + "...";

            Console.WriteLine(
                $"{i:D4} {obj.Kind,-10} x={b.X,8:0.##} y={b.Y,8:0.##} w={b.Width,8:0.##} h={b.Height,8:0.##} text={preview}");
        }

        result.Bitmap.Dispose();
    }

    private static void InspectAssets(string pdfPath, int pageNumber)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];

        Console.WriteLine($"Page={pageNumber}");
        Console.WriteLine($"Size={page.WidthPt}x{page.HeightPt}");
        Console.WriteLine("Content Do refs:");
        DumpDoReferences(page.ContentStream, "  ");
        Console.WriteLine();
        Console.WriteLine("Resource tree:");

        var visitedForms = new HashSet<string>(StringComparer.Ordinal);
        DumpResourceSet("PAGE", page.Resources, "  ", visitedForms);
    }

    private static void DumpDoReferences(string contentStream, string indent)
    {
        foreach (Match match in Regex.Matches(contentStream, @"/([^\s<>\[\]\(\)]+)\s+Do\b"))
        {
            Console.WriteLine($"{indent}/{match.Groups[1].Value} Do");
        }
    }

    private static void DumpResourceSet(
        string ownerName,
        PdfResourceSet resources,
        string indent,
        HashSet<string> visitedForms)
    {
        Console.WriteLine($"{indent}{ownerName}: fonts={resources.Fonts.Count}, forms={resources.Forms.Count}, images={resources.Images.Count}, patterns={resources.Patterns.Count}, colorspaces={resources.ColorSpaces.Count}");

        if (resources.Images.Count > 0)
        {
            Console.WriteLine($"{indent}  Images:");
            foreach (var pair in resources.Images.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                PdfImageXObject image = pair.Value;
                Console.WriteLine(
                    $"{indent}    {pair.Key} {image.Width}x{image.Height} bpc={image.BitsPerComponent} filter={image.Filter} cs={DescribeColorSpace(image.ColorSpace)} bytes={image.ImageBytes.Length} smask={(image.SoftMask != null ? "yes" : "no")} imagemask={image.IsImageMask}");
            }
        }

        if (resources.Forms.Count > 0)
        {
            Console.WriteLine($"{indent}  Forms:");
            foreach (var pair in resources.Forms.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                PdfFormXObject form = pair.Value;
                string key = form.ResourceName + "|" + form.ContentStream.Length.ToString();
                Console.WriteLine($"{indent}    {pair.Key} bbox=[{string.Join(", ", form.BBox)}] matrix=[{string.Join(", ", form.MatrixValues)}] contentLen={form.ContentStream.Length}");
                Console.WriteLine($"{indent}      Content Do refs:");
                DumpDoReferences(form.ContentStream, indent + "        ");

                if (visitedForms.Add(key))
                    DumpResourceSet(pair.Key, form.Resources, indent + "      ", visitedForms);
                else
                    Console.WriteLine($"{indent}      (form already visited)");
            }
        }
    }

    private static string DescribeColorSpace(PdfColorSpace colorSpace)
        => colorSpace switch
        {
            PdfIndexedColorSpace indexed => $"Indexed({DescribeColorSpace(indexed.BaseColorSpace)},high={indexed.HighValue})",
            PdfIccBasedColorSpace icc => $"ICCBased(n={icc.N},alt={DescribeColorSpace(icc.GetFallback())})",
            _ => colorSpace.GetType().Name
        };

    private static void InspectXObjects(string pdfPath, int pageNumber)
    {
        byte[] bytes = File.ReadAllBytes(pdfPath);
        Type parserType = typeof(SimplePdfParser);
        Type? parseContextType = parserType.GetNestedType("ParseContext", BindingFlags.NonPublic);
        if (parseContextType == null)
            return;

        ConstructorInfo? ctor = parseContextType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(byte[]), typeof(IProgress<PdfParseProgress>) },
            modifiers: null);
        if (ctor == null)
            return;

        object parseContext = ctor.Invoke(new object?[] { bytes, null });

        MethodInfo? findPages = parseContextType.GetMethod("FindPagesFromPageTree", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? extractResourcesDictionaryText = parseContextType.GetMethod("ExtractResourcesDictionaryText", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? extractNamedDictionary = parseContextType.GetMethod("ExtractNamedDictionary", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? findObjectText = parseContextType.GetMethod("FindObjectText", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? extractStreamObject = parseContextType.GetMethod("ExtractStreamObject", BindingFlags.Instance | BindingFlags.NonPublic);
        if (findPages == null || extractResourcesDictionaryText == null || extractNamedDictionary == null || findObjectText == null || extractStreamObject == null)
            return;

        var pageInfos = ((System.Collections.IEnumerable)(findPages.Invoke(parseContext, null) ?? Array.Empty<object>()))
            .Cast<object>()
            .ToList();

        int pageIndex = Math.Clamp(pageNumber - 1, 0, pageInfos.Count - 1);
        object pageInfo = pageInfos[pageIndex];
        Type pageInfoType = pageInfo.GetType();

        string ownerText = pageInfoType.GetProperty("ObjectText")?.GetValue(pageInfo) as string ?? string.Empty;
        string? inheritedResourcesDictionaryText = pageInfoType.GetProperty("InheritedResourcesDictionaryText")?.GetValue(pageInfo) as string;
        string? resourcesDictText = extractResourcesDictionaryText.Invoke(parseContext, new object?[] { ownerText }) as string
            ?? inheritedResourcesDictionaryText;

        Console.WriteLine($"Page={pageNumber}");
        if (string.IsNullOrWhiteSpace(resourcesDictText))
        {
            Console.WriteLine("No /Resources dictionary.");
            return;
        }

        string? colorSpaceDictText = extractNamedDictionary.Invoke(parseContext, new object?[] { resourcesDictText, "/ColorSpace" }) as string;
        string? xObjectDictText = extractNamedDictionary.Invoke(parseContext, new object?[] { resourcesDictText, "/XObject" }) as string;

        Console.WriteLine("Raw /ColorSpace dictionary:");
        Console.WriteLine(string.IsNullOrWhiteSpace(colorSpaceDictText) ? "  <none>" : "  " + CompressWhitespace(colorSpaceDictText));
        Console.WriteLine();

        Console.WriteLine("Raw /XObject dictionary:");
        Console.WriteLine(string.IsNullOrWhiteSpace(xObjectDictText) ? "  <none>" : "  " + CompressWhitespace(xObjectDictText));
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(xObjectDictText))
            return;

        foreach (Match match in Regex.Matches(xObjectDictText, @"/(\w+)\s+(\d+)\s+(\d+)\s+R"))
        {
            string resourceName = "/" + match.Groups[1].Value;
            int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            object?[] extractArgs = { objNum, genNum, null, null };
            extractStreamObject.Invoke(parseContext, extractArgs);
            string headerText = extractArgs[2] as string ?? string.Empty;
            byte[] imageBytes = extractArgs[3] as byte[] ?? Array.Empty<byte>();

            Console.WriteLine($"{resourceName} => {objNum} {genNum} obj");
            Console.WriteLine($"  header: {CompressWhitespace(headerText)}");
            Console.WriteLine($"  streamBytes={imageBytes.Length}");

            Match colorSpaceMatch = Regex.Match(
                headerText,
                @"/ColorSpace\s+(?<value>(\[[^\]]*\])|(\d+\s+\d+\s+R)|(/[^\s<>\[\]\(\)]+))",
                RegexOptions.Singleline);

            if (colorSpaceMatch.Success)
            {
                string colorSpaceValue = colorSpaceMatch.Groups["value"].Value;
                Console.WriteLine($"  colorSpaceRef: {CompressWhitespace(colorSpaceValue)}");

                if (colorSpaceValue.StartsWith("/", StringComparison.Ordinal) && !PdfColorSpaceFactory.IsDeviceColorSpaceName(colorSpaceValue) && !string.IsNullOrWhiteSpace(colorSpaceDictText))
                {
                    Match namedCs = Regex.Match(colorSpaceDictText, Regex.Escape(colorSpaceValue) + @"\s+(?<def>(\[[^\]]*\])|(\d+\s+\d+\s+R)|(/[^\s<>\[\]\(\)]+))", RegexOptions.Singleline);
                    if (namedCs.Success)
                    {
                        string namedDef = namedCs.Groups["def"].Value;
                        Console.WriteLine($"  colorSpaceDef: {CompressWhitespace(namedDef)}");
                        DumpIndirectColorSpaceIfNeeded(parseContext, findObjectText, namedDef);
                    }
                }
                else
                {
                    DumpIndirectColorSpaceIfNeeded(parseContext, findObjectText, colorSpaceValue);
                }
            }

            Console.WriteLine();
        }
    }

    private static void DumpIndirectColorSpaceIfNeeded(object parseContext, MethodInfo findObjectText, string value)
    {
        Match indirect = Regex.Match(value, @"(\d+)\s+(\d+)\s+R\b");
        if (!indirect.Success)
            return;

        int csObj = int.Parse(indirect.Groups[1].Value, CultureInfo.InvariantCulture);
        int csGen = int.Parse(indirect.Groups[2].Value, CultureInfo.InvariantCulture);
        string csObjectText = findObjectText.Invoke(parseContext, new object?[] { csObj, csGen }) as string ?? string.Empty;
        Console.WriteLine($"  colorSpaceObj {csObj} {csGen}: {CompressWhitespace(csObjectText)}");
    }

    private static string CompressWhitespace(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static void SaveXObjects(string pdfPath, int pageNumber, string outputDir)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];

        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Page={pageNumber}, output={outputDir}");
        foreach (var pair in page.Resources.Images.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string safeName = pair.Key.TrimStart('/');
            string pngPath = Path.Combine(outputDir, $"{pageNumber:D3}_{safeName}.png");
            string txtPath = Path.Combine(outputDir, $"{pageNumber:D3}_{safeName}.txt");
            try
            {
                using Bitmap bitmap = pair.Value.CreateBitmap();
                bitmap.Save(pngPath, ImageFormat.Png);

                File.WriteAllText(
                    txtPath,
                    $"name={pair.Key}{Environment.NewLine}" +
                    $"size={pair.Value.Width}x{pair.Value.Height}{Environment.NewLine}" +
                    $"bpc={pair.Value.BitsPerComponent}{Environment.NewLine}" +
                    $"filter={pair.Value.Filter}{Environment.NewLine}" +
                    $"colorspace={DescribeColorSpace(pair.Value.ColorSpace)}{Environment.NewLine}" +
                    $"bytes={pair.Value.ImageBytes.Length}{Environment.NewLine}" +
                    $"output={pngPath}{Environment.NewLine}");

                Console.WriteLine($"  saved {pair.Key} -> {pngPath}");
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    txtPath,
                    $"name={pair.Key}{Environment.NewLine}" +
                    $"size={pair.Value.Width}x{pair.Value.Height}{Environment.NewLine}" +
                    $"bpc={pair.Value.BitsPerComponent}{Environment.NewLine}" +
                    $"filter={pair.Value.Filter}{Environment.NewLine}" +
                    $"colorspace={DescribeColorSpace(pair.Value.ColorSpace)}{Environment.NewLine}" +
                    $"bytes={pair.Value.ImageBytes.Length}{Environment.NewLine}" +
                    $"error={ex.GetType().FullName}{Environment.NewLine}" +
                    $"message={ex.Message}{Environment.NewLine}");

                Console.WriteLine($"  failed {pair.Key}: {ex.Message}");
            }
        }
    }

    private static void SaveRender(string pdfPath, int pageNumber, string outputPath, string[] extraArgs)
    {
        float zoom = 1f;
        foreach (string arg in extraArgs)
        {
            if (arg.StartsWith("zoom=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(arg.AsSpan("zoom=".Length), CultureInfo.InvariantCulture, out float parsedZoom))
            {
                zoom = parsedZoom;
            }
        }

        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];
        PdfRenderResult result = SimplePdfRenderer.RenderWithObjects(page, zoom, mergeObjects: false);
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            result.Bitmap.Save(outputPath, ImageFormat.Png);
            Console.WriteLine($"saved render page={pageNumber} zoom={zoom} -> {outputPath}");
            Console.WriteLine($"objects={result.Objects.Count}");
        }
        finally
        {
            result.Bitmap.Dispose();
        }
    }

    private static void TraceOps(string pdfPath, int pageNumber, string[] filters)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];

        MethodInfo? tokenize = typeof(SimplePdfRenderer).GetMethod("Tokenize", BindingFlags.NonPublic | BindingFlags.Static);
        if (tokenize == null)
        {
            Console.WriteLine("Tokenize() not found.");
            return;
        }

        var filterSet = new HashSet<string>(
            filters.Where(f => !string.IsNullOrWhiteSpace(f)).Select(NormalizeResourceName),
            StringComparer.Ordinal);

        Console.WriteLine($"Page={pageNumber}");
        TraceContentStream(
            tokenize,
            page.ContentStream,
            page.Resources,
            path: $"page[{pageNumber}]",
            indent: "",
            filterSet,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static void TraceContentStream(
        MethodInfo tokenize,
        string contentStream,
        PdfResourceSet resources,
        string path,
        string indent,
        HashSet<string> filterSet,
        HashSet<string> visitedForms)
    {
        Console.WriteLine($"{indent}{path}");

        object tokensObj = tokenize.Invoke(null, new object[] { contentStream })!;
        var operands = new List<object>();
        int opIndex = 0;

        foreach (object token in ((System.Collections.IEnumerable)tokensObj).Cast<object>())
        {
            if (token is string op && IsOperatorToken(op))
            {
                bool shouldPrint = ShouldTraceOperator(op);
                string line = string.Empty;

                switch (op)
                {
                    case "q":
                    case "Q":
                    case "W":
                    case "W*":
                    case "n":
                    case "S":
                    case "s":
                    case "f":
                    case "f*":
                    case "B":
                    case "B*":
                    case "b":
                    case "b*":
                    case "BT":
                    case "ET":
                        line = $"{indent}  {opIndex:D4} {op}";
                        break;

                    case "w":
                    case "J":
                    case "j":
                    case "M":
                    case "i":
                    case "ri":
                    case "g":
                    case "G":
                    case "rg":
                    case "RG":
                    case "k":
                    case "K":
                    case "cs":
                    case "CS":
                    case "sc":
                    case "SC":
                    case "scn":
                    case "SCN":
                    case "sh":
                    case "gs":
                    case "m":
                    case "l":
                    case "c":
                    case "v":
                    case "y":
                    case "h":
                    case "d":
                        line = operands.Count > 0
                            ? $"{indent}  {opIndex:D4} {op} [{FormatTail(operands, operands.Count)}]"
                            : $"{indent}  {opIndex:D4} {op}";
                        break;

                    case "cm":
                        if (operands.Count >= 6)
                        {
                            line = $"{indent}  {opIndex:D4} cm [{FormatTail(operands, 6)}]";
                        }
                        else
                        {
                            line = $"{indent}  {opIndex:D4} cm";
                        }
                        break;

                    case "re":
                        if (operands.Count >= 4)
                        {
                            line = $"{indent}  {opIndex:D4} re [{FormatTail(operands, 4)}]";
                        }
                        else
                        {
                            line = $"{indent}  {opIndex:D4} re";
                        }
                        break;

                    case "Do":
                        {
                            string resourceName = operands.Count > 0
                                ? NormalizeResourceName(FormatOperand(operands[^1]))
                                : "<missing>";
                            bool matchesFilter = filterSet.Count == 0 || filterSet.Contains(resourceName);
                            shouldPrint = matchesFilter || shouldPrint;

                            if (TryGetForm(resources, resourceName, out PdfFormXObject? form))
                            {
                                line = $"{indent}  {opIndex:D4} Do {resourceName} FORM bbox=[{string.Join(", ", form.BBox.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))}] len={form.ContentStream.Length}";
                                if (shouldPrint)
                                    Console.WriteLine(line);

                                string visitedKey = form.ResourceName + "|" + form.ContentStream.Length.ToString(CultureInfo.InvariantCulture);
                                if (visitedForms.Add(visitedKey))
                                {
                                    TraceContentStream(
                                        tokenize,
                                        form.ContentStream,
                                        form.Resources,
                                        path: $"{path}/{resourceName}",
                                        indent: indent + "    ",
                                        filterSet,
                                        visitedForms);
                                }
                                else if (shouldPrint)
                                {
                                    Console.WriteLine($"{indent}    {resourceName} already visited");
                                }

                                operands.Clear();
                                opIndex++;
                                continue;
                            }

                            if (TryGetImage(resources, resourceName, out PdfImageXObject? image))
                            {
                                line = $"{indent}  {opIndex:D4} Do {resourceName} IMAGE {image.Width}x{image.Height} filter={image.Filter} cs={DescribeColorSpace(image.ColorSpace)}";
                            }
                            else
                            {
                                line = $"{indent}  {opIndex:D4} Do {resourceName} <unresolved>";
                            }
                            break;
                        }
                }

                if (shouldPrint && !string.IsNullOrEmpty(line))
                    Console.WriteLine(line);

                operands.Clear();
                opIndex++;
                continue;
            }

            operands.Add(token);
        }
    }

    private static bool ShouldTraceOperator(string op)
        => op is
            "q" or "Q" or
            "cm" or "re" or
            "m" or "l" or "c" or "v" or "y" or "h" or
            "w" or "J" or "j" or "M" or "d" or "i" or "ri" or
            "W" or "W*" or "n" or
            "S" or "s" or "f" or "f*" or "B" or "B*" or "b" or "b*" or
            "g" or "G" or "rg" or "RG" or "k" or "K" or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN" or "sh" or "gs" or
            "Do" or "BT" or "ET";

    private static bool TryGetForm(PdfResourceSet resources, string resourceName, out PdfFormXObject? form)
    {
        if (resources.Forms.TryGetValue(resourceName, out form))
            return true;
        if (!resourceName.StartsWith("/", StringComparison.Ordinal) && resources.Forms.TryGetValue("/" + resourceName, out form))
            return true;
        if (resourceName.StartsWith("/", StringComparison.Ordinal) && resources.Forms.TryGetValue(resourceName[1..], out form))
            return true;
        form = null;
        return false;
    }

    private static bool TryGetImage(PdfResourceSet resources, string resourceName, out PdfImageXObject? image)
    {
        if (resources.Images.TryGetValue(resourceName, out image))
            return true;
        if (!resourceName.StartsWith("/", StringComparison.Ordinal) && resources.Images.TryGetValue("/" + resourceName, out image))
            return true;
        if (resourceName.StartsWith("/", StringComparison.Ordinal) && resources.Images.TryGetValue(resourceName[1..], out image))
            return true;
        image = null;
        return false;
    }

    private static string NormalizeResourceName(string name)
        => name.StartsWith("/", StringComparison.Ordinal) ? name : "/" + name;

    private static string FormatTail(List<object> operands, int count)
        => string.Join(", ", operands.Skip(Math.Max(0, operands.Count - count)).Select(FormatOperand));

    private static string FormatOperand(object value)
    {
        if (value is string s)
            return s;

        if (value is List<object> list)
            return "[" + string.Join(" ", list.Select(FormatOperand)) + "]";

        Type type = value.GetType();
        if (string.Equals(type.Name, "PdfStringToken", StringComparison.Ordinal))
        {
            PropertyInfo? valueProperty = type.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object? raw = valueProperty?.GetValue(value);
            if (raw is string tokenValue)
                return Escape(tokenValue);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void InspectFont(string pdfPath, int pageNumber, string fontResourceName, string[] glyphNames)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];
        if (!page.Resources.Fonts.TryGetValue(fontResourceName, out var font) || font.FontFileBytes == null)
            return;

        Assembly asm = typeof(SimplePdfParser).Assembly;
        Type? type1Type = asm.GetType("PdfCore.Text.PdfType1Font");
        if (type1Type == null)
            return;

        MethodInfo? tryCreate = type1Type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? tryMapGlyphName = type1Type.GetMethod("TryMapGlyphName", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? getAdvanceWidth = type1Type.GetMethod("GetAdvanceWidth", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? getGlyphData = type1Type.GetMethod("GetGlyphData", BindingFlags.NonPublic | BindingFlags.Instance);
        if (tryCreate == null || tryMapGlyphName == null || getAdvanceWidth == null)
        {
            return;
        }

        object?[] createArgs = [font.FontFileBytes, null];
        bool created = (bool)(tryCreate.Invoke(null, createArgs) ?? false);
        if (!created || createArgs[1] == null)
            return;

        object type1 = createArgs[1]!;
        Console.WriteLine(
            $"FontMatrix=[{type1Type.GetProperty("MatrixA")?.GetValue(type1)}, {type1Type.GetProperty("MatrixB")?.GetValue(type1)}, {type1Type.GetProperty("MatrixC")?.GetValue(type1)}, {type1Type.GetProperty("MatrixD")?.GetValue(type1)}, {type1Type.GetProperty("MatrixE")?.GetValue(type1)}, {type1Type.GetProperty("MatrixF")?.GetValue(type1)}]");
        byte[][]? charStrings = type1Type.GetField("_charStrings", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(type1) as byte[][];
        byte[][]? subrs = type1Type.GetField("_subrs", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(type1) as byte[][];
        foreach (string glyphName in glyphNames)
        {
            object?[] mapArgs = [glyphName, 0];
            bool mapped = (bool)(tryMapGlyphName.Invoke(type1, mapArgs) ?? false);
            if (!mapped)
                continue;
            int glyphId = (int)mapArgs[1]!;

            int pdfCode = -1;
            if (font.GlyphNameMap != null)
            {
                foreach (var pair in font.GlyphNameMap)
                {
                    if (pair.Value == glyphName)
                    {
                        pdfCode = pair.Key;
                        break;
                    }
                }
            }

            float pdfWidth = pdfCode >= 0 ? font.GetGlyphWidth(pdfCode) : -1f;
            float fontWidth = Convert.ToSingle(getAdvanceWidth.Invoke(type1, [glyphId]));
            string extra = "";
            object? data = getGlyphData?.Invoke(type1, [glyphId]);
            if (data != null)
            {
                Type dataType = data.GetType();
                var path = dataType.GetProperty("Path")?.GetValue(data) as System.Drawing.Drawing2D.GraphicsPath;
                float sbx = Convert.ToSingle(dataType.GetProperty("SideBearingX")?.GetValue(data) ?? 0f);
                float sby = Convert.ToSingle(dataType.GetProperty("SideBearingY")?.GetValue(data) ?? 0f);
                if (path != null && path.PointCount > 0)
                {
                    var b = path.GetBounds();
                    extra = $" sb=({sbx},{sby}) bounds=({b.X},{b.Y},{b.Width},{b.Height}) points={path.PointCount}";
                }
                else
                {
                    extra = $" sb=({sbx},{sby}) bounds=empty";
                }
            }

            Console.WriteLine($"{glyphName}: code={pdfCode} pdfWidth={pdfWidth} fontWidth={fontWidth} glyphId={glyphId}{extra}");
            if (charStrings != null && glyphId >= 0 && glyphId < charStrings.Length)
                Console.WriteLine("  ops=" + DisassembleType1(charStrings[glyphId], subrs, 0));
        }
    }

    private static string DisassembleType1(byte[] data, byte[][]? subrs, int depth)
    {
        if (depth > 3)
            return "...";

        var parts = new List<string>();
        var stack = new List<float>();
        int pos = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            if (b == 255 || b >= 32)
            {
                float number = ReadType1Number(data, ref pos, b);
                stack.Add(number);
                parts.Add(FormatType1Number(number));
                continue;
            }

            int op = b;
            if (b == 12 && pos < data.Length)
                op = 1200 + data[pos++];

            string opName = Type1OperatorName(op);
            parts.Add(opName);
            if (op == 10 && stack.Count > 0 && subrs != null)
            {
                int subrIndex = (int)stack[^1];
                if (subrIndex >= 0 && subrIndex < subrs.Length && subrs[subrIndex].Length > 0)
                    parts.Add("{subr" + subrIndex + ":" + DisassembleType1(subrs[subrIndex], subrs, depth + 1) + "}");
            }

            stack.Clear();
        }

        return string.Join(" ", parts);
    }

    private static float ReadType1Number(byte[] data, ref int pos, byte first)
    {
        if (first >= 32 && first <= 246)
            return first - 139;

        if (first >= 247 && first <= 250 && pos < data.Length)
            return (first - 247) * 256 + data[pos++] + 108;

        if (first >= 251 && first <= 254 && pos < data.Length)
            return -((first - 251) * 256 + data[pos++] + 108);

        if (first == 255 && pos + 3 < data.Length)
        {
            int value =
                (data[pos] << 24) |
                (data[pos + 1] << 16) |
                (data[pos + 2] << 8) |
                data[pos + 3];
            pos += 4;
            return value;
        }

        return 0f;
    }

    private static string FormatType1Number(float value)
        => Math.Abs(value - MathF.Round(value)) < 0.0001f
            ? ((int)MathF.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Type1OperatorName(int op)
        => op switch
        {
            1 => "hstem",
            3 => "vstem",
            4 => "vmoveto",
            5 => "rlineto",
            6 => "hlineto",
            7 => "vlineto",
            8 => "rrcurveto",
            9 => "closepath",
            10 => "callsubr",
            11 => "return",
            13 => "hsbw",
            14 => "endchar",
            21 => "rmoveto",
            22 => "hmoveto",
            30 => "vhcurveto",
            31 => "hvcurveto",
            1200 => "dotsection",
            1201 => "vstem3",
            1202 => "hstem3",
            1206 => "seac",
            1207 => "sbw",
            1212 => "div",
            1216 => "callothersubr",
            1217 => "pop",
            1233 => "setcurrentpoint",
            _ => "op" + op.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    private static void CheckType1Coverage(string pdfPath, int pageNumber)
    {
        var doc = SimplePdfParser.Parse(pdfPath);
        int pageIndex = Math.Clamp(pageNumber - 1, 0, doc.Pages.Count - 1);
        var page = doc.Pages[pageIndex];
        var resources = page.Resources;

        Assembly asm = typeof(SimplePdfParser).Assembly;
        Type? rendererType = asm.GetType("PdfCore.SimplePdfRenderer");
        Type? type1Type = asm.GetType("PdfCore.Text.PdfType1Font");
        if (rendererType == null || type1Type == null)
            return;

        MethodInfo? tokenize = rendererType.GetMethod("Tokenize", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? tryCreate = type1Type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? tryMapGlyphName = type1Type.GetMethod("TryMapGlyphName", BindingFlags.Public | BindingFlags.Instance);
        if (tokenize == null || tryCreate == null || tryMapGlyphName == null)
            return;

        var type1ByFont = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in resources.Fonts)
        {
            PdfFontResource font = kv.Value;
            if (!string.Equals(font.FontFileSubtype, "/Type1", StringComparison.OrdinalIgnoreCase) ||
                font.FontFileBytes == null ||
                font.FontFileBytes.Length == 0)
            {
                continue;
            }

            string normalized = StripSubsetPrefix(font.BaseFontName);
            if (!normalized.StartsWith("CM", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            object?[] createArgs = [font.FontFileBytes, null];
            bool created = (bool)(tryCreate.Invoke(null, createArgs) ?? false);
            if (created && createArgs[1] != null)
                type1ByFont[kv.Key] = createArgs[1]!;
        }

        if (type1ByFont.Count == 0)
        {
            Console.WriteLine("No CM/MSBM Type1 fonts on page.");
            return;
        }

        object tokensObj = tokenize.Invoke(null, [page.ContentStream])!;
        var tokens = ((System.Collections.IEnumerable)tokensObj).Cast<object>().ToList();

        string currentFont = "/F1";
        var operands = new List<object>();
        var misses = new List<string>();
        var stats = new Dictionary<string, (int strings, int bytes, int misses)>(StringComparer.Ordinal);

        foreach (object token in tokens)
        {
            if (token is string op &&
                IsOperatorToken(op))
            {
                if (op == "Tf")
                {
                    if (operands.Count >= 2)
                    {
                        object fontObj = operands[^2];
                        if (fontObj is string fontName && fontName.StartsWith("/", StringComparison.Ordinal))
                            currentFont = fontName;
                    }
                }
                else if (op == "Tj")
                {
                    if (operands.Count > 0)
                        CheckTextOperand(operands[^1], currentFont);
                }
                else if (op == "TJ")
                {
                    if (operands.Count > 0 && operands[^1] is List<object> arr)
                    {
                        foreach (object item in arr)
                            CheckTextOperand(item, currentFont);
                    }
                }

                operands.Clear();
                continue;
            }

            operands.Add(token);
        }

        foreach (var kv in stats.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"{kv.Key}: strings={kv.Value.strings} bytes={kv.Value.bytes} misses={kv.Value.misses}");

        if (misses.Count == 0)
        {
            Console.WriteLine("No Type1 glyph mapping misses on page.");
            return;
        }

        Console.WriteLine("Sample misses:");
        foreach (string line in misses.Take(80))
            Console.WriteLine("  " + line);

        void CheckTextOperand(object operand, string fontName)
        {
            if (!type1ByFont.TryGetValue(fontName, out object? type1))
                return;

            if (!resources.Fonts.TryGetValue(fontName, out PdfFontResource? font) || font.GlyphNameMap == null)
                return;

            byte[]? bytes = TryGetPdfStringBytes(operand);
            if (bytes == null || bytes.Length == 0)
                return;

            if (!stats.TryGetValue(fontName, out var s))
                s = (0, 0, 0);
            s.strings++;
            s.bytes += bytes.Length;

            for (int i = 0; i < bytes.Length; i++)
            {
                int code = bytes[i];
                if (!font.GlyphNameMap.TryGetValue(code, out string? glyphName) ||
                    string.IsNullOrWhiteSpace(glyphName))
                {
                    s.misses++;
                    misses.Add($"{fontName} code={code} no glyph name");
                    continue;
                }

                object?[] mapArgs = [glyphName, 0];
                bool mapped = (bool)(tryMapGlyphName.Invoke(type1, mapArgs) ?? false);
                if (!mapped)
                {
                    s.misses++;
                    misses.Add($"{fontName} code={code} glyph={glyphName} not in Type1 charstrings");
                }
            }

            stats[fontName] = s;
        }
    }

    private static bool IsOperatorToken(string token)
    {
        return token == "q" || token == "Q" || token == "cm" ||
               token == "BT" || token == "ET" || token == "Tf" || token == "Tm" || token == "Td" || token == "TD" || token == "T*" ||
               token == "Tc" || token == "Tw" || token == "Tz" || token == "TL" || token == "Ts" ||
               token == "Tj" || token == "TJ" || token == "'" || token == "\"" ||
               token == "m" || token == "l" || token == "c" || token == "v" || token == "y" || token == "h" || token == "re" ||
               token == "S" || token == "s" ||
               token == "f" || token == "f*" ||
               token == "B" || token == "B*" ||
               token == "b" || token == "b*" ||
               token == "n" ||
               token == "w" || token == "J" || token == "j" || token == "M" || token == "d" || token == "i" || token == "ri" || token == "gs" ||
               token == "G" || token == "g" ||
               token == "CS" || token == "cs" || token == "SC" || token == "sc" || token == "SCN" || token == "scn" ||
               token == "RG" || token == "rg" || token == "K" || token == "k" ||
               token == "W" || token == "W*" ||
               token == "Do";
    }

    private static byte[]? TryGetPdfStringBytes(object operand)
    {
        Type t = operand.GetType();
        if (t.Name != "PdfStringToken")
            return null;

        PropertyInfo? bytesProp = t.GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance);
        return bytesProp?.GetValue(operand) as byte[];
    }

    private static string StripSubsetPrefix(string fontName)
    {
        int plus = fontName.IndexOf('+');
        return plus >= 0 && plus + 1 < fontName.Length ? fontName[(plus + 1)..] : fontName;
    }
}
