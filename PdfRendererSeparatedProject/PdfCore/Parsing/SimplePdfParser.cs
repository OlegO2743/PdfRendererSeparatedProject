using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfCore.Color;
using PdfCore.Resources;
using PdfCore.Text;

namespace PdfCore.Parsing;

public sealed record PdfParseProgress(string Message, int? Current, int? Total)
{
    public bool IsDeterminate => Current.HasValue && Total.HasValue && Total.Value > 0;

    public static PdfParseProgress Indeterminate(string message) => new(message, null, null);

    public static PdfParseProgress Determinate(string message, int current, int total) => new(message, current, total);
}

public static class SimplePdfParser
{
    public static SimplePdfDocument Parse(string filePath)
    {
        return Parse(filePath, null);
    }

    public static SimplePdfDocument Parse(string filePath, IProgress<PdfParseProgress>? progress)
    {
        progress?.Report(PdfParseProgress.Indeterminate("Чтение файла PDF"));
        byte[] data = File.ReadAllBytes(filePath);
        progress?.Report(PdfParseProgress.Indeterminate("Поиск страниц PDF"));
        return new ParseContext(data, progress).ParseDocument();
    }

    private sealed class ParseContext
    {
        private readonly byte[] _data;
        private readonly string _text;
        private readonly IProgress<PdfParseProgress>? _progress;
        private readonly Dictionary<string, PdfFormXObject> _formCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfImageXObject> _imageCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TopLevelObjectLocation> _topLevelObjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _compressedObjects = new(StringComparer.Ordinal);

        public ParseContext(byte[] data, IProgress<PdfParseProgress>? progress)
        {
            _data = data;
            _progress = progress;
            _text = Encoding.Latin1.GetString(data);
            IndexTopLevelObjects();
            ExtractCompressedObjects();
        }

        public SimplePdfDocument ParseDocument()
        {
            var doc = new SimplePdfDocument();

            List<PageParseInfo> pages = FindPagesFromPageTree();
            if (pages.Count == 0)
                pages.AddRange(FindAllPageObjectTexts().Select(text => new PageParseInfo(text, null, null)));

            int totalPages = pages.Count;
            for (int i = 0; i < totalPages; i++)
            {
                PageParseInfo pageInfo = pages[i];
                _progress?.Report(PdfParseProgress.Determinate(
                    $"Чтение страницы {i + 1} из {totalPages}",
                    i + 1,
                    totalPages));

                (float width, float height) = ParseMediaBox(pageInfo.ObjectText, pageInfo.InheritedMediaBoxOwnerText);
                string contentStream = ExtractContentsStream(pageInfo.ObjectText);
                PdfResourceSet resources = ParseResourcesFromOwnerText(pageInfo.ObjectText, pageInfo.InheritedResourcesDictionaryText);

                doc.Pages.Add(new SimplePdfPage
                {
                    WidthPt = width,
                    HeightPt = height,
                    ContentStream = contentStream,
                    Resources = resources
                });
            }

            if (doc.Pages.Count == 0)
                throw new InvalidOperationException("В документе не найдено ни одной страницы.");

            return doc;
        }

        private List<PageParseInfo> FindPagesFromPageTree()
        {
            var pages = new List<PageParseInfo>();

            Match rootMatch = Regex.Match(_text, @"/Root\s+(\d+)\s+(\d+)\s+R\b");
            if (!rootMatch.Success)
                return pages;

            try
            {
                int catalogObj = int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int catalogGen = int.Parse(rootMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                string catalogText = FindObjectText(catalogObj, catalogGen);

                Match pagesMatch = Regex.Match(catalogText, @"/Pages\s+(\d+)\s+(\d+)\s+R\b");
                if (!pagesMatch.Success)
                    return pages;

                int pagesObj = int.Parse(pagesMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int pagesGen = int.Parse(pagesMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                TraversePageTreeNode(pagesObj, pagesGen, null, null, pages, visited);
            }
            catch (InvalidOperationException)
            {
                pages.Clear();
            }

            return pages;
        }

        private void TraversePageTreeNode(
            int objNum,
            int genNum,
            string? inheritedMediaBoxOwnerText,
            string? inheritedResourcesDictionaryText,
            List<PageParseInfo> pages,
            HashSet<string> visited)
        {
            string key = objNum.ToString(CultureInfo.InvariantCulture) + ":" + genNum.ToString(CultureInfo.InvariantCulture);
            if (!visited.Add(key))
                return;

            string objectText = FindObjectText(objNum, genNum);
            if (IsPageObject(objectText))
            {
                pages.Add(new PageParseInfo(objectText, inheritedMediaBoxOwnerText, inheritedResourcesDictionaryText));
                return;
            }

            if (!IsPagesObject(objectText))
                return;

            string? nextMediaBoxOwnerText = HasMediaBox(objectText)
                ? objectText
                : inheritedMediaBoxOwnerText;

            string? nextResourcesDictionaryText =
                ExtractResourcesDictionaryText(objectText) ?? inheritedResourcesDictionaryText;

            Match kidsMatch = Regex.Match(objectText, @"/Kids\s*\[(?<kids>.*?)\]", RegexOptions.Singleline);
            if (!kidsMatch.Success)
                return;

            foreach (Match kidMatch in Regex.Matches(kidsMatch.Groups["kids"].Value, @"(\d+)\s+(\d+)\s+R\b"))
            {
                int kidObj = int.Parse(kidMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int kidGen = int.Parse(kidMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                TraversePageTreeNode(kidObj, kidGen, nextMediaBoxOwnerText, nextResourcesDictionaryText, pages, visited);
            }
        }

        private IEnumerable<string> FindAllPageObjectTexts()
        {
            var pageTypeRegex = new Regex(@"/Type\s*/Page\b");
            var pagesTypeRegex = new Regex(@"/Type\s*/Pages\b");

            foreach (TopLevelObjectLocation location in _topLevelObjects.Values)
            {
                string objText = GetTopLevelObjectText(location);
                if (pageTypeRegex.IsMatch(objText) && !pagesTypeRegex.IsMatch(objText))
                    yield return objText;
            }

            foreach (string objText in _compressedObjects.Values)
            {
                if (pageTypeRegex.IsMatch(objText) && !pagesTypeRegex.IsMatch(objText))
                    yield return objText;
            }
        }

        private (float Width, float Height) ParseMediaBox(string ownerText, string? inheritedOwnerText)
        {
            Match m = MatchMediaBox(ownerText);
            if (!m.Success && inheritedOwnerText != null)
                m = MatchMediaBox(inheritedOwnerText);

            if (!m.Success)
                throw new InvalidOperationException("Не найден /MediaBox");

            float x0 = ParseFloat(m.Groups[1].Value);
            float y0 = ParseFloat(m.Groups[2].Value);
            float x1 = ParseFloat(m.Groups[3].Value);
            float y1 = ParseFloat(m.Groups[4].Value);

            return (x1 - x0, y1 - y0);
        }

        private static Match MatchMediaBox(string ownerText)
        {
            return Regex.Match(
                ownerText,
                @"/MediaBox\s*\[\s*([0-9\.\-]+)\s+([0-9\.\-]+)\s+([0-9\.\-]+)\s+([0-9\.\-]+)\s*\]",
                RegexOptions.Singleline);
        }

        private static bool HasMediaBox(string ownerText) => MatchMediaBox(ownerText).Success;

        private string ExtractContentsStream(string ownerText)
        {
            Match directMatch = Regex.Match(ownerText, @"/Contents\s+(\d+)\s+(\d+)\s+R");
            if (directMatch.Success)
            {
                int obj = int.Parse(directMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int gen = int.Parse(directMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                return ReadSingleStreamObject(obj, gen);
            }

            Match arrayMatch = Regex.Match(ownerText, @"/Contents\s*\[(?<items>.*?)\]", RegexOptions.Singleline);
            if (arrayMatch.Success)
            {
                var sb = new StringBuilder();
                MatchCollection refs = Regex.Matches(arrayMatch.Groups["items"].Value, @"(\d+)\s+(\d+)\s+R");
                foreach (Match m in refs)
                {
                    int obj = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int gen = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    sb.AppendLine(ReadSingleStreamObject(obj, gen));
                }

                return sb.ToString();
            }

            return string.Empty;
        }

        private string ReadSingleStreamObject(int objNum, int genNum)
        {
            ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);
            if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            return Encoding.Latin1.GetString(streamBytes);
        }

        private PdfResourceSet ParseResourcesFromOwnerText(string ownerText, string? inheritedResourcesDictionaryText = null)
        {
            string? resourcesDict = ExtractResourcesDictionaryText(ownerText) ?? inheritedResourcesDictionaryText;
            if (string.IsNullOrEmpty(resourcesDict))
                return new PdfResourceSet();

            return ParseResourcesFromDictionaryText(resourcesDict);
        }

        private PdfResourceSet ParseResourcesFromDictionaryText(string resourcesDictText)
        {
            var resources = new PdfResourceSet();

            string? fontDict = ExtractNamedDictionary(resourcesDictText, "/Font");
            if (!string.IsNullOrEmpty(fontDict))
            {
                foreach (Match match in Regex.Matches(fontDict, @"/(\w+)\s+(\d+)\s+(\d+)\s+R"))
                {
                    string resourceName = "/" + match.Groups[1].Value;
                    int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    string fontObjectText = FindObjectText(objNum, genNum);
                    string descendantFontObjectText = string.Empty;
                    Match descendantMatch = Regex.Match(fontObjectText, @"/DescendantFonts\s*\[\s*(\d+)\s+(\d+)\s+R\s*\]", RegexOptions.Singleline);
                    if (descendantMatch.Success)
                    {
                        int descendantObj = int.Parse(descendantMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        int descendantGen = int.Parse(descendantMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                        descendantFontObjectText = FindObjectText(descendantObj, descendantGen);
                    }

                    string baseFontName = Regex.Match(fontObjectText, @"/BaseFont\s*/([^\s/<>\[\]\(\)]+)").Groups[1].Value;
                    if (string.IsNullOrEmpty(baseFontName))
                        baseFontName = Regex.Match(descendantFontObjectText, @"/BaseFont\s*/([^\s/<>\[\]\(\)]+)").Groups[1].Value;
                    if (string.IsNullOrEmpty(baseFontName))
                        baseFontName = "Helvetica";

                    bool isIdentityH = Regex.IsMatch(fontObjectText, @"/Encoding\s*/Identity-H\b");
                    IReadOnlyDictionary<int, string>? toUnicodeMap = ParseToUnicodeMap(fontObjectText);
                    IReadOnlyDictionary<int, string>? encodingMap = ParseEncodingMap(fontObjectText);
                    IReadOnlyDictionary<int, string>? glyphNameMap = ParseEncodingGlyphNameMap(fontObjectText);
                    (byte[]? fontFileBytes, string? fontFileSubtype) = ParseEmbeddedFontFile(fontObjectText, descendantFontObjectText);
                    IReadOnlyDictionary<int, string>? embeddedType1GlyphNameMap = ParseEmbeddedType1GlyphNameMap(fontFileBytes);
                    glyphNameMap = MergeGlyphNameMaps(glyphNameMap, embeddedType1GlyphNameMap);
                    encodingMap = MergeEncodingMaps(encodingMap, BuildUnicodeEncodingMap(glyphNameMap));
                    IReadOnlyDictionary<int, float>? cidWidths = ParseCidWidths(descendantFontObjectText);

                    int firstChar = 0;
                    Match firstCharMatch = Regex.Match(fontObjectText, @"/FirstChar\s+(\d+)");
                    if (firstCharMatch.Success)
                        firstChar = int.Parse(firstCharMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                    float missingWidth = 600f;
                    Match missingMatch = Regex.Match(fontObjectText + " " + descendantFontObjectText, @"/MissingWidth\s+([0-9\.\-]+)");
                    if (missingMatch.Success)
                        missingWidth = ParseFloat(missingMatch.Groups[1].Value);
                    else
                    {
                        Match defaultWidthMatch = Regex.Match(descendantFontObjectText, @"/DW\s+([0-9\.\-]+)");
                        if (defaultWidthMatch.Success)
                            missingWidth = ParseFloat(defaultWidthMatch.Groups[1].Value);
                    }

                    float[]? widths = null;
                    Match widthsRefMatch = Regex.Match(fontObjectText, @"/Widths\s+(\d+)\s+(\d+)\s+R");
                    if (widthsRefMatch.Success)
                    {
                        int widthsObj = int.Parse(widthsRefMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        int widthsGen = int.Parse(widthsRefMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                        string widthsText = FindObjectText(widthsObj, widthsGen);
                        widths = ParseNumericArray(widthsText);
                    }
                    else
                    {
                        Match widthsInlineMatch = Regex.Match(fontObjectText, @"/Widths\s*\[(?<w>.*?)\]", RegexOptions.Singleline);
                        if (widthsInlineMatch.Success)
                            widths = ParseNumericArray(widthsInlineMatch.Groups["w"].Value);
                    }

                    resources.Fonts[resourceName] = new PdfFontResource
                    {
                        ResourceName = resourceName,
                        BaseFontName = baseFontName,
                        FirstChar = firstChar,
                        Widths = widths,
                        MissingWidth = missingWidth,
                        IsIdentityH = isIdentityH,
                        ToUnicodeMap = toUnicodeMap,
                        EncodingMap = encodingMap,
                        GlyphNameMap = glyphNameMap,
                        CidWidths = cidWidths,
                        FontFileBytes = fontFileBytes,
                        FontFileSubtype = fontFileSubtype,
                        PreferCidGlyphCodesForRendering = ShouldPreferCidGlyphCodesForRendering(
                            baseFontName,
                            descendantFontObjectText,
                            fontFileBytes,
                            fontFileSubtype)
                    };
                }
            }

            string? colorSpaceDict = ExtractNamedDictionary(resourcesDictText, "/ColorSpace");
            if (!string.IsNullOrEmpty(colorSpaceDict))
                ParseColorSpaceResources(colorSpaceDict, resources);

            string? xObjectDict = ExtractNamedDictionary(resourcesDictText, "/XObject");
            if (!string.IsNullOrEmpty(xObjectDict))
            {
                foreach (Match match in Regex.Matches(xObjectDict, @"/(\w+)\s+(\d+)\s+(\d+)\s+R"))
                {
                    string resourceName = "/" + match.Groups[1].Value;
                    int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

                    string objectText = FindObjectText(objNum, genNum);
                    if (Regex.IsMatch(objectText, @"/Subtype\s*/Form\b"))
                    {
                        PdfFormXObject? form = ParseFormXObject(objNum, genNum, resourceName);
                        if (form != null)
                            resources.Forms[resourceName] = form;
                    }
                    else if (Regex.IsMatch(objectText, @"/Subtype\s*/Image\b"))
                    {
                        PdfImageXObject? image = ParseImageXObject(objNum, genNum, resourceName, resources);
                        if (image != null)
                            resources.Images[resourceName] = image;
                    }
                }
            }

            string? patternDict = ExtractNamedDictionary(resourcesDictText, "/Pattern");
            if (!string.IsNullOrEmpty(patternDict))
            {
                foreach (Match match in Regex.Matches(patternDict, @"/(\w+)\s+(\d+)\s+(\d+)\s+R"))
                {
                    string resourceName = "/" + match.Groups[1].Value;
                    int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    PdfTilingPattern? pattern = ParseTilingPattern(objNum, genNum, resourceName);
                    if (pattern != null)
                        resources.Patterns[resourceName] = pattern;
                }
            }

            return resources;
        }

        private void ParseColorSpaceResources(string colorSpaceDictText, PdfResourceSet resources)
        {
            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s*/(DeviceGray|DeviceRGB|DeviceCMYK)\b"))
            {
                string resourceName = "/" + match.Groups[1].Value;
                string deviceName = "/" + match.Groups[2].Value;
                resources.ColorSpaces[resourceName] = PdfColorSpaceFactory.CreateDeviceSpaceByName(deviceName);
            }

            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s*\[\s*/Pattern(?:\s*/(?<base>DeviceGray|DeviceRGB|DeviceCMYK))?\s*\]", RegexOptions.Singleline))
            {
                string resourceName = "/" + match.Groups[1].Value;
                PdfColorSpace? baseColorSpace = null;
                if (match.Groups["base"].Success)
                    baseColorSpace = PdfColorSpaceFactory.CreateDeviceSpaceByName("/" + match.Groups["base"].Value);

                resources.ColorSpaces[resourceName] = new PdfPatternColorSpace { BaseColorSpace = baseColorSpace };
            }

            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s*\[\s*/ICCBased\s+(\d+)\s+(\d+)\s+R\s*\]", RegexOptions.Singleline))
            {
                string resourceName = "/" + match.Groups[1].Value;
                int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                PdfIccProfileObject profile = ParseIccProfileObject(objNum, genNum);
                resources.ColorSpaces[resourceName] = PdfColorSpaceFactory.CreateFromIccProfile(profile);
            }

            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s*\[\s*/(CalRGB|Lab)\b", RegexOptions.Singleline))
            {
                string resourceName = "/" + match.Groups[1].Value;
                resources.ColorSpaces[resourceName] = new PdfDeviceRgbColorSpace();
            }

            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s*\[\s*/CalGray\b", RegexOptions.Singleline))
            {
                string resourceName = "/" + match.Groups[1].Value;
                resources.ColorSpaces[resourceName] = new PdfDeviceGrayColorSpace();
            }

            foreach (Match match in Regex.Matches(colorSpaceDictText, @"/(\w+)\s+(\d+)\s+(\d+)\s+R\b"))
            {
                string resourceName = "/" + match.Groups[1].Value;
                int objNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                PdfColorSpace? colorSpace = ParseColorSpaceDefinitionObject(objNum, genNum);
                if (colorSpace != null)
                    resources.ColorSpaces[resourceName] = colorSpace;
            }
        }

        private PdfFormXObject? ParseFormXObject(int objNum, int genNum, string resourceName)
        {
            string key = objNum.ToString(CultureInfo.InvariantCulture) + ":" + genNum.ToString(CultureInfo.InvariantCulture);
            if (_formCache.TryGetValue(key, out PdfFormXObject? cached))
                return cached;

            string objectText = FindObjectText(objNum, genNum);
            if (!Regex.IsMatch(objectText, @"/Subtype\s*/Form\b"))
                return null;

            ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);
            if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            float[] bbox = ParseArrayFromOwnerText(header, "/BBox", new float[] { 0f, 0f, 0f, 0f });
            float[] matrix = ParseArrayFromOwnerText(header, "/Matrix", new float[] { 1f, 0f, 0f, 1f, 0f, 0f });

            PdfResourceSet formResources = ParseResourcesFromOwnerText(header);

            var form = new PdfFormXObject
            {
                ResourceName = resourceName,
                ContentStream = Encoding.Latin1.GetString(streamBytes),
                BBox = bbox,
                MatrixValues = matrix,
                Resources = formResources
            };

            _formCache[key] = form;
            return form;
        }

        private PdfTilingPattern? ParseTilingPattern(int objNum, int genNum, string resourceName)
        {
            ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);
            if (!Regex.IsMatch(header, @"/PatternType\s+1\b"))
                return null;

            if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            return new PdfTilingPattern
            {
                ResourceName = resourceName,
                PaintType = ParseOptionalInt(header, "/PaintType", 1),
                TilingType = ParseOptionalInt(header, "/TilingType", 1),
                BBox = ParseArrayFromOwnerText(header, "/BBox", new[] { 0f, 0f, 0f, 0f }),
                MatrixValues = ParseArrayFromOwnerText(header, "/Matrix", new[] { 1f, 0f, 0f, 1f, 0f, 0f }),
                XStep = ParseOptionalFloat(header, "/XStep", 0f),
                YStep = ParseOptionalFloat(header, "/YStep", 0f),
                ContentStream = Encoding.Latin1.GetString(streamBytes),
                Resources = ParseResourcesFromOwnerText(header)
            };
        }

        private PdfImageXObject? ParseImageXObject(int objNum, int genNum, string resourceName, PdfResourceSet ownerResources)
        {
            string key = objNum.ToString(CultureInfo.InvariantCulture) + ":" + genNum.ToString(CultureInfo.InvariantCulture);
            if (_imageCache.TryGetValue(key, out PdfImageXObject? cached))
                return cached;

            string objectText = FindObjectText(objNum, genNum);
            if (!Regex.IsMatch(objectText, @"/Subtype\s*/Image\b"))
                return null;

            ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);

            int width = ParseRequiredInt(header, "/Width");
            int height = ParseRequiredInt(header, "/Height");
            int bitsPerComponent = ParseOptionalInt(header, "/BitsPerComponent", 8);
            PdfColorSpace colorSpace = ParseColorSpace(header, ownerResources);
            bool isImageMask = Regex.IsMatch(header, @"/ImageMask\s+true\b");
            bool decodeInverted = Regex.IsMatch(header, @"/Decode\s*\[\s*1\s+0\s*\]", RegexOptions.Singleline);
            int ccittK = ParseOptionalSignedInt(header, "/K", 0);

            streamBytes = DecodeImageStreamFilters(
                header,
                streamBytes,
                width,
                height,
                colorSpace.Components,
                bitsPerComponent,
                out string filter);

            PdfImageXObject? softMask = null;
            Match softMaskMatch = Regex.Match(header, @"/SMask\s+(\d+)\s+(\d+)\s+R\b");
            if (softMaskMatch.Success)
            {
                int smaskObj = int.Parse(softMaskMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int smaskGen = int.Parse(softMaskMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                softMask = ParseImageXObject(smaskObj, smaskGen, resourceName + ":SMask", ownerResources);
            }

            var image = new PdfImageXObject
            {
                ResourceName = resourceName,
                Width = width,
                Height = height,
                BitsPerComponent = bitsPerComponent,
                ColorSpace = colorSpace,
                Filter = filter,
                ImageBytes = streamBytes,
                SoftMask = softMask,
                IsImageMask = isImageMask,
                DecodeInverted = decodeInverted,
                CcittK = ccittK
            };

            _imageCache[key] = image;
            return image;
        }

        private PdfColorSpace ParseColorSpace(string source, PdfResourceSet? ownerResources = null)
        {
            Match directDeviceMatch = Regex.Match(source, @"/ColorSpace\s*/(DeviceGray|DeviceRGB|DeviceCMYK)\b");
            if (directDeviceMatch.Success)
            {
                string name = "/" + directDeviceMatch.Groups[1].Value;
                return PdfColorSpaceFactory.CreateDeviceSpaceByName(name);
            }

            Match iccMatch = Regex.Match(source, @"/ColorSpace\s*\[\s*/ICCBased\s+(\d+)\s+(\d+)\s+R\s*\]", RegexOptions.Singleline);
            if (iccMatch.Success)
            {
                int objNum = int.Parse(iccMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(iccMatch.Groups[2].Value, CultureInfo.InvariantCulture);

                PdfIccProfileObject profile = ParseIccProfileObject(objNum, genNum);
                return PdfColorSpaceFactory.CreateFromIccProfile(profile);
            }

            if (Regex.IsMatch(source, @"/ColorSpace\s*\[\s*/(CalRGB|Lab)\b", RegexOptions.Singleline))
                return new PdfDeviceRgbColorSpace();

            if (Regex.IsMatch(source, @"/ColorSpace\s*\[\s*/CalGray\b", RegexOptions.Singleline))
                return new PdfDeviceGrayColorSpace();

            PdfIndexedColorSpace? indexed = ParseIndexedColorSpace(source, ownerResources);
            if (indexed != null)
                return indexed;

            Match indirectMatch = Regex.Match(source, @"/ColorSpace\s+(\d+)\s+(\d+)\s+R\b");
            if (indirectMatch.Success)
            {
                int objNum = int.Parse(indirectMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(indirectMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                PdfColorSpace? colorSpace = ParseColorSpaceDefinitionObject(objNum, genNum);
                if (colorSpace != null)
                    return colorSpace;
            }

            Match namedMatch = Regex.Match(source, @"/ColorSpace\s*/([^/\s<>\[\]\(\)]+)");
            if (namedMatch.Success)
            {
                string name = "/" + namedMatch.Groups[1].Value;

                if (PdfColorSpaceFactory.IsDeviceColorSpaceName(name))
                    return PdfColorSpaceFactory.CreateDeviceSpaceByName(name);

                if (ownerResources != null)
                {
                    if (ownerResources.ColorSpaces.TryGetValue(name, out PdfColorSpace? cs) && cs != null)
                        return cs;
                }

                throw new NotSupportedException($"ColorSpace resource {name} не найден в /Resources.");
            }

            return new PdfDeviceRgbColorSpace();
        }

        private PdfColorSpace? ParseColorSpaceDefinitionObject(int objNum, int genNum)
        {
            string objectText = FindObjectText(objNum, genNum);

            Match directDeviceMatch = Regex.Match(objectText, @"^\s*/(DeviceGray|DeviceRGB|DeviceCMYK)\b");
            if (directDeviceMatch.Success)
                return PdfColorSpaceFactory.CreateDeviceSpaceByName("/" + directDeviceMatch.Groups[1].Value);

            Match patternMatch = Regex.Match(objectText, @"\[\s*/Pattern(?:\s*/(?<base>DeviceGray|DeviceRGB|DeviceCMYK))?\s*\]", RegexOptions.Singleline);
            if (patternMatch.Success)
            {
                PdfColorSpace? baseColorSpace = null;
                if (patternMatch.Groups["base"].Success)
                    baseColorSpace = PdfColorSpaceFactory.CreateDeviceSpaceByName("/" + patternMatch.Groups["base"].Value);

                return new PdfPatternColorSpace { BaseColorSpace = baseColorSpace };
            }

            PdfIndexedColorSpace? indexed = ParseIndexedColorSpace(objectText, null);
            if (indexed != null)
                return indexed;

            if (Regex.IsMatch(objectText, @"\[\s*/(CalRGB|Lab)\b", RegexOptions.Singleline))
                return new PdfDeviceRgbColorSpace();

            if (Regex.IsMatch(objectText, @"\[\s*/CalGray\b", RegexOptions.Singleline))
                return new PdfDeviceGrayColorSpace();

            Match iccMatch = Regex.Match(objectText, @"\[\s*/ICCBased\s+(\d+)\s+(\d+)\s+R\s*\]", RegexOptions.Singleline);
            if (iccMatch.Success)
            {
                int profileObj = int.Parse(iccMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int profileGen = int.Parse(iccMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                return PdfColorSpaceFactory.CreateFromIccProfile(ParseIccProfileObject(profileObj, profileGen));
            }

            return null;
        }

        private PdfIndexedColorSpace? ParseIndexedColorSpace(string colorSpaceText, PdfResourceSet? ownerResources)
        {
            Match startMatch = Regex.Match(colorSpaceText, @"\[\s*/Indexed\b", RegexOptions.Singleline);
            if (!startMatch.Success)
                return null;

            int arrayStart = colorSpaceText.LastIndexOf('[', startMatch.Index + startMatch.Length - 1);
            if (arrayStart < 0)
                return null;

            int index = arrayStart + 1;
            string indexedToken = ReadPdfToken(colorSpaceText, ref index);
            if (!string.Equals(indexedToken, "/Indexed", StringComparison.Ordinal))
                return null;

            if (!TryResolveIndexedBaseColorSpace(colorSpaceText, ref index, ownerResources, out PdfColorSpace? baseColorSpace) ||
                baseColorSpace == null)
            {
                return null;
            }

            string highValueToken = ReadPdfToken(colorSpaceText, ref index);
            if (!int.TryParse(highValueToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int highValue))
                return null;

            if (!TryReadIndexedLookupBytes(colorSpaceText, ref index, out byte[] lookup))
                return null;

            return new PdfIndexedColorSpace
            {
                BaseColorSpace = baseColorSpace,
                HighValue = highValue,
                Lookup = lookup
            };
        }

        private bool TryResolveIndexedBaseColorSpace(
            string text,
            ref int index,
            PdfResourceSet? ownerResources,
            out PdfColorSpace? baseColorSpace)
        {
            baseColorSpace = null;
            SkipPdfWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == '/')
            {
                string name = ReadPdfToken(text, ref index);
                if (PdfColorSpaceFactory.IsDeviceColorSpaceName(name))
                {
                    baseColorSpace = PdfColorSpaceFactory.CreateDeviceSpaceByName(name);
                    return true;
                }

                if (ownerResources != null &&
                    ownerResources.ColorSpaces.TryGetValue(name, out PdfColorSpace? resourceColorSpace) &&
                    resourceColorSpace != null)
                {
                    baseColorSpace = resourceColorSpace;
                    return true;
                }

                return false;
            }

            if (TryReadIndirectReference(text, ref index, out int objNum, out int genNum))
            {
                baseColorSpace = ParseColorSpaceDefinitionObject(objNum, genNum);
                return baseColorSpace != null;
            }

            return false;
        }

        private bool TryReadIndexedLookupBytes(string text, ref int index, out byte[] lookup)
        {
            lookup = Array.Empty<byte>();
            SkipPdfWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == '<' && (index + 1 >= text.Length || text[index + 1] != '<'))
            {
                string hex = ReadPdfHexString(text, ref index);
                lookup = HexStringToBytes(hex);
                return true;
            }

            if (text[index] == '(')
            {
                lookup = ReadPdfLiteralStringBytes(text, ref index);
                return true;
            }

            if (TryReadIndirectReference(text, ref index, out int objNum, out int genNum))
            {
                byte[]? indirectLookup = TryReadLookupBytesFromObject(objNum, genNum);
                if (indirectLookup != null)
                {
                    lookup = indirectLookup;
                    return true;
                }
            }

            return false;
        }

        private byte[]? TryReadLookupBytesFromObject(int objNum, int genNum)
        {
            string objectText = FindObjectText(objNum, genNum);
            int index = 0;
            SkipPdfWhitespaceAndComments(objectText, ref index);
            if (index >= objectText.Length)
                return null;

            if (objectText[index] == '<' && (index + 1 >= objectText.Length || objectText[index + 1] != '<'))
            {
                string hex = ReadPdfHexString(objectText, ref index);
                return HexStringToBytes(hex);
            }

            if (objectText[index] == '(')
                return ReadPdfLiteralStringBytes(objectText, ref index);

            if (objectText.Contains("stream", StringComparison.Ordinal))
            {
                ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);
                if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                    streamBytes = DecompressFlate(streamBytes);

                return streamBytes;
            }

            return null;
        }

        private static void SkipPdfWhitespaceAndComments(string text, ref int index)
        {
            while (index < text.Length)
            {
                char ch = text[index];
                if (char.IsWhiteSpace(ch))
                {
                    index++;
                    continue;
                }

                if (ch == '%')
                {
                    index++;
                    while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                        index++;

                    continue;
                }

                break;
            }
        }

        private static string ReadPdfToken(string text, ref int index)
        {
            SkipPdfWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
                return string.Empty;

            int start = index;
            char ch = text[index];

            if (ch == '/')
            {
                index++;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && !"[]<>()/%".Contains(text[index]))
                    index++;

                return text.Substring(start, index - start);
            }

            while (index < text.Length && !char.IsWhiteSpace(text[index]) && !"[]<>()/%".Contains(text[index]))
                index++;

            return text.Substring(start, index - start);
        }

        private static bool TryReadIndirectReference(string text, ref int index, out int objNum, out int genNum)
        {
            objNum = 0;
            genNum = 0;

            int savedIndex = index;
            string objToken = ReadPdfToken(text, ref index);
            if (!int.TryParse(objToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out objNum))
            {
                index = savedIndex;
                return false;
            }

            string genToken = ReadPdfToken(text, ref index);
            if (!int.TryParse(genToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out genNum))
            {
                index = savedIndex;
                return false;
            }

            string rToken = ReadPdfToken(text, ref index);
            if (!string.Equals(rToken, "R", StringComparison.Ordinal))
            {
                index = savedIndex;
                return false;
            }

            return true;
        }

        private static string ReadPdfHexString(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '<')
                return string.Empty;

            index++;
            int start = index;
            while (index < text.Length && text[index] != '>')
                index++;

            string result = text.Substring(start, Math.Max(0, index - start));
            if (index < text.Length && text[index] == '>')
                index++;

            return result;
        }

        private static byte[] ReadPdfLiteralStringBytes(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '(')
                return Array.Empty<byte>();

            var bytes = new List<byte>();
            int depth = 1;
            index++;

            while (index < text.Length && depth > 0)
            {
                char ch = text[index++];
                if (ch == '\\')
                {
                    if (index >= text.Length)
                        break;

                    char escaped = text[index++];
                    switch (escaped)
                    {
                        case 'n':
                            bytes.Add((byte)'\n');
                            break;
                        case 'r':
                            bytes.Add((byte)'\r');
                            break;
                        case 't':
                            bytes.Add((byte)'\t');
                            break;
                        case 'b':
                            bytes.Add(0x08);
                            break;
                        case 'f':
                            bytes.Add(0x0C);
                            break;
                        case '(':
                        case ')':
                        case '\\':
                            bytes.Add((byte)escaped);
                            break;
                        case '\r':
                            if (index < text.Length && text[index] == '\n')
                                index++;
                            break;
                        case '\n':
                            break;
                        default:
                            if (escaped >= '0' && escaped <= '7')
                            {
                                int value = escaped - '0';
                                int octalDigits = 1;
                                while (octalDigits < 3 &&
                                       index < text.Length &&
                                       text[index] >= '0' &&
                                       text[index] <= '7')
                                {
                                    value = (value * 8) + (text[index] - '0');
                                    index++;
                                    octalDigits++;
                                }

                                bytes.Add((byte)value);
                            }
                            else
                            {
                                bytes.Add((byte)escaped);
                            }

                            break;
                    }

                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    bytes.Add((byte)ch);
                    continue;
                }

                if (ch == ')')
                {
                    depth--;
                    if (depth > 0)
                        bytes.Add((byte)ch);

                    continue;
                }

                bytes.Add((byte)ch);
            }

            return bytes.ToArray();
        }

        private byte[] ApplyDecodeParms(
            string header,
            byte[] decoded,
            int width,
            int height,
            int colorComponents,
            int bitsPerComponent)
        {
            int predictor = ParseOptionalInt(header, "/Predictor", 1);
            if (predictor <= 1)
                return decoded;

            int columns = ParseOptionalInt(header, "/Columns", width);
            int colors = ParseOptionalInt(header, "/Colors", Math.Max(1, colorComponents));
            int bits = ParseOptionalInt(header, "/BitsPerComponent", bitsPerComponent);

            if (bits != 8)
                return decoded;

            if (predictor == 2)
                return DecodeTiffPredictor(decoded, columns, height, colors);

            if (predictor >= 10 && predictor <= 15)
                return DecodePngPredictor(decoded, columns, height, colors);

            return decoded;
        }

        private static byte[] DecodeTiffPredictor(byte[] data, int columns, int height, int colors)
        {
            int rowLength = columns * colors;
            if (rowLength <= 0 || data.Length < rowLength * height)
                return data;

            byte[] output = new byte[rowLength * height];
            Buffer.BlockCopy(data, 0, output, 0, output.Length);

            for (int row = 0; row < height; row++)
            {
                int rowStart = row * rowLength;
                for (int x = colors; x < rowLength; x++)
                    output[rowStart + x] = (byte)(output[rowStart + x] + output[rowStart + x - colors]);
            }

            return output;
        }

        private static byte[] DecodePngPredictor(byte[] data, int columns, int height, int colors)
        {
            int rowLength = columns * colors;
            int encodedRowLength = rowLength + 1;
            if (rowLength <= 0 || data.Length < encodedRowLength * height)
                return data;

            byte[] output = new byte[rowLength * height];
            int src = 0;
            int dst = 0;

            for (int row = 0; row < height; row++)
            {
                int filter = data[src++];
                int rowStart = dst;

                for (int x = 0; x < rowLength; x++, src++, dst++)
                {
                    int raw = data[src];
                    int left = x >= colors ? output[dst - colors] : 0;
                    int up = row > 0 ? output[dst - rowLength] : 0;
                    int upLeft = row > 0 && x >= colors ? output[dst - rowLength - colors] : 0;

                    output[dst] = filter switch
                    {
                        0 => (byte)raw,
                        1 => (byte)(raw + left),
                        2 => (byte)(raw + up),
                        3 => (byte)(raw + ((left + up) / 2)),
                        4 => (byte)(raw + Paeth(left, up, upLeft)),
                        _ => (byte)raw
                    };
                }

                if (dst - rowStart != rowLength)
                    return data;
            }

            return output;
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            int p = left + up - upLeft;
            int pa = Math.Abs(p - left);
            int pb = Math.Abs(p - up);
            int pc = Math.Abs(p - upLeft);

            if (pa <= pb && pa <= pc)
                return left;
            return pb <= pc ? up : upLeft;
        }

        private PdfIccProfileObject ParseIccProfileObject(int objNum, int genNum)
        {
            ExtractStreamObject(objNum, genNum, out string header, out byte[] streamBytes);

            if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            int n = ParseRequiredInt(header, "/N");
            string? alternateName = null;
            Match altMatch = Regex.Match(header, @"/Alternate\s*/([^/\s<>\[\]\(\)]+)");
            if (altMatch.Success)
                alternateName = "/" + altMatch.Groups[1].Value;

            return new PdfIccProfileObject
            {
                ObjectNumber = objNum,
                N = n,
                AlternateName = alternateName,
                ProfileBytes = streamBytes
            };
        }

        private IReadOnlyDictionary<int, string>? ParseToUnicodeMap(string fontObjectText)
        {
            Match toUnicodeMatch = Regex.Match(fontObjectText, @"/ToUnicode\s+(\d+)\s+(\d+)\s+R\b");
            if (!toUnicodeMatch.Success)
                return null;

            int objNum = int.Parse(toUnicodeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int genNum = int.Parse(toUnicodeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            string cmapText = ReadSingleStreamObject(objNum, genNum);

            var map = new Dictionary<int, string>();
            foreach (Match block in Regex.Matches(cmapText, @"beginbfchar(?<body>.*?)endbfchar", RegexOptions.Singleline))
            {
                foreach (Match item in Regex.Matches(block.Groups["body"].Value, @"<(?<src>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
                {
                    int src = HexToInt(item.Groups["src"].Value);
                    map[src] = DecodeUnicodeHex(item.Groups["dst"].Value);
                }
            }

            foreach (Match block in Regex.Matches(cmapText, @"beginbfrange(?<body>.*?)endbfrange", RegexOptions.Singleline))
            {
                foreach (Match item in Regex.Matches(block.Groups["body"].Value, @"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
                {
                    int start = HexToInt(item.Groups["start"].Value);
                    int end = HexToInt(item.Groups["end"].Value);
                    int dstStart = HexToInt(item.Groups["dst"].Value);

                    for (int code = start; code <= end && code - start < 65536; code++)
                    {
                        int dst = dstStart + (code - start);
                        if (TryConvertUnicodeScalar(dst, out string? mappedText))
                            map[code] = mappedText;
                    }
                }

                foreach (Match item in Regex.Matches(block.Groups["body"].Value, @"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*\[(?<items>.*?)\]", RegexOptions.Singleline))
                {
                    int code = HexToInt(item.Groups["start"].Value);
                    foreach (Match dst in Regex.Matches(item.Groups["items"].Value, @"<(?<dst>[0-9A-Fa-f]+)>"))
                        map[code++] = DecodeUnicodeHex(dst.Groups["dst"].Value);
                }
            }

            return map.Count == 0 ? null : map;
        }

        private IReadOnlyDictionary<int, string>? ParseEncodingMap(string fontObjectText)
        {
            string? encodingText = ExtractEncodingText(fontObjectText);
            if (string.IsNullOrWhiteSpace(encodingText))
                return null;

            bool isWinAnsi = Regex.IsMatch(encodingText, @"/WinAnsiEncoding\b");
            var map = new Dictionary<int, string>();

            int differencesIndex = encodingText.IndexOf("/Differences", StringComparison.Ordinal);
            if (differencesIndex >= 0)
            {
                int arrayStart = encodingText.IndexOf('[', differencesIndex);
                if (arrayStart >= 0)
                    ParseEncodingDifferences(ExtractBalancedArray(encodingText, arrayStart), map);
            }

            if (isWinAnsi)
            {
                foreach (KeyValuePair<int, string> item in PdfGlyphNames.CreateWinAnsiEncoding())
                    map.TryAdd(item.Key, item.Value);
            }

            return map.Count == 0 ? null : map;
        }

        private IReadOnlyDictionary<int, string>? ParseEncodingGlyphNameMap(string fontObjectText)
        {
            string? encodingText = ExtractEncodingText(fontObjectText);
            if (string.IsNullOrWhiteSpace(encodingText))
                return null;

            var map = new Dictionary<int, string>();
            int differencesIndex = encodingText.IndexOf("/Differences", StringComparison.Ordinal);
            if (differencesIndex >= 0)
            {
                int arrayStart = encodingText.IndexOf('[', differencesIndex);
                if (arrayStart >= 0)
                    ParseEncodingGlyphNameDifferences(ExtractBalancedArray(encodingText, arrayStart), map);
            }

            return map.Count == 0 ? null : map;
        }

        private static IReadOnlyDictionary<int, string>? MergeEncodingMaps(
            IReadOnlyDictionary<int, string>? primary,
            IReadOnlyDictionary<int, string>? secondary)
        {
            if (primary == null || primary.Count == 0)
                return secondary;

            if (secondary == null || secondary.Count == 0)
                return primary;

            var merged = new Dictionary<int, string>(primary);
            foreach (KeyValuePair<int, string> item in secondary)
                merged.TryAdd(item.Key, item.Value);

            return merged;
        }

        private static IReadOnlyDictionary<int, string>? MergeGlyphNameMaps(
            IReadOnlyDictionary<int, string>? primary,
            IReadOnlyDictionary<int, string>? secondary)
        {
            if (primary == null || primary.Count == 0)
                return secondary;

            if (secondary == null || secondary.Count == 0)
                return primary;

            var merged = new Dictionary<int, string>(primary);
            foreach (KeyValuePair<int, string> item in secondary)
                merged.TryAdd(item.Key, item.Value);

            return merged;
        }

        private static IReadOnlyDictionary<int, string>? BuildUnicodeEncodingMap(IReadOnlyDictionary<int, string>? glyphNameMap)
        {
            if (glyphNameMap == null || glyphNameMap.Count == 0)
                return null;

            var map = new Dictionary<int, string>();
            foreach (KeyValuePair<int, string> item in glyphNameMap)
            {
                if (PdfGlyphNames.TryGetUnicode(item.Value, out string? mappedText))
                    map[item.Key] = mappedText;
            }

            return map.Count == 0 ? null : map;
        }

        private string? ExtractEncodingText(string fontObjectText)
        {
            int nameIndex = fontObjectText.IndexOf("/Encoding", StringComparison.Ordinal);
            if (nameIndex < 0)
                return null;

            int scan = nameIndex + "/Encoding".Length;
            while (scan < fontObjectText.Length && char.IsWhiteSpace(fontObjectText[scan]))
                scan++;

            if (scan >= fontObjectText.Length)
                return null;

            Match refMatch = Regex.Match(fontObjectText.Substring(scan), @"^(\d+)\s+(\d+)\s+R\b");
            if (refMatch.Success)
            {
                int objNum = int.Parse(refMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(refMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                return FindObjectText(objNum, genNum);
            }

            if (scan + 1 < fontObjectText.Length && fontObjectText[scan] == '<' && fontObjectText[scan + 1] == '<')
                return ExtractBalancedDictionary(fontObjectText, scan);

            Match nameMatch = Regex.Match(fontObjectText.Substring(scan), @"^/[^\s/<>\[\]\(\)]+");
            return nameMatch.Success ? nameMatch.Value : null;
        }

        private static void ParseEncodingDifferences(string differencesArrayText, Dictionary<int, string> map)
        {
            MatchCollection tokens = Regex.Matches(differencesArrayText, @"[+\-]?\d+|/[^\s<>\[\]\(\)/]+");
            int code = 0;
            bool hasCode = false;

            foreach (Match token in tokens)
            {
                string value = token.Value;
                if (value.Length == 0 || value == "[" || value == "]")
                    continue;

                if (value[0] != '/')
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCode))
                    {
                        code = parsedCode;
                        hasCode = true;
                    }

                    continue;
                }

                if (!hasCode)
                    continue;

                string glyphName = value[1..];
                if (PdfGlyphNames.TryGetUnicode(glyphName, out string? mappedText))
                    map[code] = mappedText;

                code++;
            }
        }

        private static void ParseEncodingGlyphNameDifferences(string differencesArrayText, Dictionary<int, string> map)
        {
            MatchCollection tokens = Regex.Matches(differencesArrayText, @"[+\-]?\d+|/[^\s<>\[\]\(\)/]+");
            int code = 0;
            bool hasCode = false;

            foreach (Match token in tokens)
            {
                string value = token.Value;
                if (value.Length == 0 || value == "[" || value == "]")
                    continue;

                if (value[0] != '/')
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCode))
                    {
                        code = parsedCode;
                        hasCode = true;
                    }

                    continue;
                }

                if (!hasCode)
                    continue;

                map[code] = value[1..];
                code++;
            }
        }

        private (byte[]? Bytes, string? Subtype) ParseEmbeddedFontFile(string fontObjectText, string descendantFontObjectText)
        {
            Match descriptorMatch = Regex.Match(fontObjectText + " " + descendantFontObjectText, @"/FontDescriptor\s+(\d+)\s+(\d+)\s+R\b");
            if (!descriptorMatch.Success)
                return (null, null);

            int descriptorObj = int.Parse(descriptorMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int descriptorGen = int.Parse(descriptorMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            string descriptorText = FindObjectText(descriptorObj, descriptorGen);

            Match fontFileMatch = Regex.Match(descriptorText, @"/FontFile(?:2|3)?\s+(\d+)\s+(\d+)\s+R\b");
            if (!fontFileMatch.Success)
                return (null, null);

            int fontFileObj = int.Parse(fontFileMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int fontFileGen = int.Parse(fontFileMatch.Groups[2].Value, CultureInfo.InvariantCulture);

            ExtractStreamObject(fontFileObj, fontFileGen, out string header, out byte[] streamBytes);
            if (Regex.IsMatch(header, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            string? subtype = null;
            Match subtypeMatch = Regex.Match(header, @"/Subtype\s*/([^/\s<>\[\]\(\)]+)");
            if (subtypeMatch.Success)
                subtype = "/" + subtypeMatch.Groups[1].Value;
            else if (LooksLikeEmbeddedType1Font(streamBytes))
                subtype = "/Type1";

            return (streamBytes, subtype);
        }

        private static IReadOnlyDictionary<int, string>? ParseEmbeddedType1GlyphNameMap(byte[]? fontBytes)
        {
            if (!LooksLikeEmbeddedType1Font(fontBytes))
                return null;

            string fontText = Encoding.Latin1.GetString(fontBytes!);
            int clearTextLength = fontText.IndexOf("currentfile eexec", StringComparison.Ordinal);
            if (clearTextLength < 0)
                clearTextLength = fontText.IndexOf("\neexec", StringComparison.Ordinal);
            if (clearTextLength < 0)
                clearTextLength = fontText.IndexOf("\reexec", StringComparison.Ordinal);
            if (clearTextLength < 0)
                clearTextLength = Math.Min(fontText.Length, 65536);

            string clearText = fontText.Substring(0, clearTextLength);
            MatchCollection dupMatches = Regex.Matches(clearText, @"dup\s+(?<code>\d+)\s+/(?<name>[^\s<>\[\]\(\)/]+)\s+put", RegexOptions.Singleline);
            if (dupMatches.Count == 0)
                return null;

            var map = new Dictionary<int, string>();
            foreach (Match match in dupMatches)
            {
                if (int.TryParse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    map[code] = match.Groups["name"].Value;
            }

            return map.Count == 0 ? null : map;
        }

        private IReadOnlyDictionary<int, float>? ParseCidWidths(string descendantFontObjectText)
        {
            Match widthNameMatch = Regex.Match(descendantFontObjectText, @"/W\b");
            if (!widthNameMatch.Success)
                return null;

            int arrayStart = descendantFontObjectText.IndexOf('[', widthNameMatch.Index);
            if (arrayStart < 0)
                return null;

            string widthsArrayText = ExtractBalancedArray(descendantFontObjectText, arrayStart);
            MatchCollection tokens = Regex.Matches(widthsArrayText, @"\[|\]|[+\-]?(?:\d+\.\d+|\d+|\.\d+)");
            if (tokens.Count < 2)
                return null;

            var widths = new Dictionary<int, float>();
            int i = 1; // skip outer [

            while (i < tokens.Count - 1)
            {
                if (tokens[i].Value == "]")
                    break;

                if (!TryParseIntToken(tokens[i].Value, out int firstCode))
                {
                    i++;
                    continue;
                }

                i++;
                if (i >= tokens.Count - 1)
                    break;

                if (tokens[i].Value == "[")
                {
                    i++;
                    int code = firstCode;
                    while (i < tokens.Count && tokens[i].Value != "]")
                    {
                        if (TryParseFloatToken(tokens[i].Value, out float width))
                            widths[code++] = width;
                        i++;
                    }

                    if (i < tokens.Count && tokens[i].Value == "]")
                        i++;

                    continue;
                }

                if (!TryParseIntToken(tokens[i].Value, out int lastCode))
                    break;

                i++;
                if (i >= tokens.Count || !TryParseFloatToken(tokens[i].Value, out float rangeWidth))
                    break;

                i++;
                if (lastCode < firstCode)
                    continue;

                for (int code = firstCode; code <= lastCode && code - firstCode < 65536; code++)
                    widths[code] = rangeWidth;
            }

            return widths.Count == 0 ? null : widths;
        }

        private static bool TryParseIntToken(string token, out int value)
        {
            value = 0;
            if (token == "[" || token == "]")
                return false;

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return false;

            value = (int)parsed;
            return true;
        }

        private static bool TryParseFloatToken(string token, out float value)
        {
            value = 0f;
            return token != "[" &&
                   token != "]" &&
                   float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static int HexToInt(string hex)
        {
            return int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static byte[] HexStringToBytes(string hex)
        {
            var clean = new StringBuilder(hex.Length);
            foreach (char ch in hex)
            {
                if (!char.IsWhiteSpace(ch))
                    clean.Append(ch);
            }

            if (clean.Length % 2 == 1)
                clean.Append('0');

            byte[] bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(clean.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            return bytes;
        }

        private static string DecodeUnicodeHex(string hex)
        {
            if (hex.Length % 2 == 1)
                hex += "0";

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        private static bool TryConvertUnicodeScalar(int codePoint, out string text)
        {
            if (codePoint < 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                text = string.Empty;
                return false;
            }

            text = char.ConvertFromUtf32(codePoint);
            return true;
        }

        private static bool LooksLikeEmbeddedType1Font(byte[]? fontBytes)
        {
            if (fontBytes == null || fontBytes.Length == 0)
                return false;

            int sampleLength = Math.Min(fontBytes.Length, 512);
            string head = Encoding.Latin1.GetString(fontBytes, 0, sampleLength);

            return head.Contains("%!PS-AdobeFont-", StringComparison.Ordinal) ||
                   head.Contains("%!FontType1-", StringComparison.Ordinal) ||
                   head.Contains("/FontType 1", StringComparison.Ordinal);
        }

        private static bool ShouldPreferCidGlyphCodesForRendering(
            string baseFontName,
            string descendantFontObjectText,
            byte[]? fontFileBytes,
            string? fontFileSubtype)
        {
            string fontName = StripSubsetPrefix(baseFontName);
            bool barcodeFont =
                fontName.Contains("Code128", StringComparison.OrdinalIgnoreCase) ||
                fontName.Contains("Code39", StringComparison.OrdinalIgnoreCase) ||
                fontName.Contains("Barcode", StringComparison.OrdinalIgnoreCase);

            bool embeddedIdentityCidFont =
                Regex.IsMatch(descendantFontObjectText, @"/Subtype\s*/CIDFontType2\b") &&
                Regex.IsMatch(descendantFontObjectText, @"/CIDToGIDMap\s*/Identity\b");

            bool hasEmbeddedTrueTypeFont =
                fontFileBytes != null &&
                fontFileBytes.Length > 0 &&
                !string.Equals(fontFileSubtype, "/Type1C", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fontFileSubtype, "/CIDFontType0C", StringComparison.OrdinalIgnoreCase);

            return barcodeFont || (embeddedIdentityCidFont && hasEmbeddedTrueTypeFont);
        }

        private static string StripSubsetPrefix(string fontName)
        {
            int plus = fontName.IndexOf('+');
            return plus >= 0 && plus + 1 < fontName.Length ? fontName[(plus + 1)..] : fontName;
        }

        private int ParseRequiredInt(string source, string name)
        {
            Match m = Regex.Match(source, Regex.Escape(name) + @"\s+(\d+)");
            if (!m.Success)
                throw new InvalidOperationException("Не найдено обязательное целое значение " + name);
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private int ParseOptionalInt(string source, string name, int fallback)
        {
            Match m = Regex.Match(source, Regex.Escape(name) + @"\s+(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        }

        private int ParseOptionalSignedInt(string source, string name, int fallback)
        {
            Match m = Regex.Match(source, Regex.Escape(name) + @"\s+([+\-]?\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        }

        private float ParseOptionalFloat(string source, string name, float fallback)
        {
            Match m = Regex.Match(source, Regex.Escape(name) + @"\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))");
            return m.Success ? ParseFloat(m.Groups[1].Value) : fallback;
        }

        private float[] ParseArrayFromOwnerText(string ownerText, string name, float[] fallback)
        {
            Match m = Regex.Match(ownerText, Regex.Escape(name) + @"\s*\[(?<vals>.*?)\]", RegexOptions.Singleline);
            if (!m.Success)
                return fallback;

            float[] values = ParseNumericArray(m.Groups["vals"].Value);
            return values.Length == 0 ? fallback : values;
        }

        private float[] ParseNumericArray(string text)
        {
            MatchCollection matches = Regex.Matches(text, @"[+\-]?(?:\d+\.\d+|\d+|\.\d+)");
            var values = new List<float>(matches.Count);
            foreach (Match match in matches)
                values.Add(ParseFloat(match.Value));
            return values.ToArray();
        }

        private string? ExtractResourcesDictionaryText(string ownerText)
        {
            int nameIndex = ownerText.IndexOf("/Resources", StringComparison.Ordinal);
            if (nameIndex < 0)
                return null;

            int scan = nameIndex + "/Resources".Length;
            while (scan < ownerText.Length && char.IsWhiteSpace(ownerText[scan]))
                scan++;

            if (scan + 1 < ownerText.Length && ownerText[scan] == '<' && ownerText[scan + 1] == '<')
                return ExtractBalancedDictionary(ownerText, scan);

            Match refMatch = Regex.Match(ownerText.Substring(scan), @"^(\d+)\s+(\d+)\s+R");
            if (!refMatch.Success)
                return null;

            int objNum = int.Parse(refMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int genNum = int.Parse(refMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            string objectText = FindObjectText(objNum, genNum);

            int dictStart = objectText.IndexOf("<<", StringComparison.Ordinal);
            if (dictStart < 0)
                return null;

            return ExtractBalancedDictionary(objectText, dictStart);
        }

        private string? ExtractNamedDictionary(string source, string dictionaryName)
        {
            int nameIndex = source.IndexOf(dictionaryName, StringComparison.Ordinal);
            if (nameIndex < 0)
                return null;

            int scan = nameIndex + dictionaryName.Length;
            while (scan < source.Length && char.IsWhiteSpace(source[scan]))
                scan++;

            if (scan + 1 < source.Length && source[scan] == '<' && source[scan + 1] == '<')
                return ExtractBalancedDictionary(source, scan);

            Match refMatch = Regex.Match(source.Substring(scan), @"^(\d+)\s+(\d+)\s+R\b");
            if (!refMatch.Success)
                return null;

            int objNum = int.Parse(refMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int genNum = int.Parse(refMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            string objectText = FindObjectText(objNum, genNum);

            int dictStart = objectText.IndexOf("<<", StringComparison.Ordinal);
            return dictStart < 0 ? null : ExtractBalancedDictionary(objectText, dictStart);
        }

        private string ExtractBalancedDictionary(string source, int dictStart)
        {
            int depth = 0;
            for (int i = dictStart; i < source.Length - 1; i++)
            {
                if (source[i] == '<' && source[i + 1] == '<')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (source[i] == '>' && source[i + 1] == '>')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                        return source.Substring(dictStart, i - dictStart + 1);
                }
            }

            throw new InvalidOperationException("Не удалось дочитать PDF dictionary");
        }

        private string ExtractBalancedArray(string source, int arrayStart)
        {
            int depth = 0;
            for (int i = arrayStart; i < source.Length; i++)
            {
                if (source[i] == '[')
                {
                    depth++;
                    continue;
                }

                if (source[i] == ']')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(arrayStart, i - arrayStart + 1);
                }
            }

            throw new InvalidOperationException("Could not read PDF array");
        }

        private string FindObjectText(int objNum, int genNum)
        {
            string key = MakeObjectKey(objNum, genNum);

            if (_compressedObjects.TryGetValue(key, out string? compressedObjectText))
                return compressedObjectText;

            if (_topLevelObjects.TryGetValue(key, out TopLevelObjectLocation location))
                return GetTopLevelObjectText(location);
            var objRegex = new Regex(@"(?:^|[\r\n])\s*" + objNum.ToString(CultureInfo.InvariantCulture) + @"\s+" + genNum.ToString(CultureInfo.InvariantCulture) + @"\s+obj\b");
            Match objMatch = objRegex.Match(_text);
            if (!objMatch.Success)
                throw new InvalidOperationException("Не найден объект " + objNum + " " + genNum + " obj");

            int objBodyStart = objMatch.Index + objMatch.Length;
            Match endObjMatch = new Regex(@"(?:^|[\r\n])\s*endobj\b").Match(_text, objBodyStart);
            if (!endObjMatch.Success)
                throw new InvalidOperationException("Не найден endobj для объекта " + objNum + " " + genNum);

            return _text.Substring(objBodyStart, endObjMatch.Index - objBodyStart);
        }

        private void ExtractStreamObject(int objNum, int genNum, out string headerText, out byte[] streamBytes)
        {
            if (_topLevelObjects.TryGetValue(MakeObjectKey(objNum, genNum), out TopLevelObjectLocation indexedLocation))
            {
                int indexedObjStart = indexedLocation.BodyStart;
                int indexedStreamKeywordPos = _text.IndexOf("stream", indexedObjStart, StringComparison.Ordinal);
                if (indexedStreamKeywordPos < 0 || indexedStreamKeywordPos > indexedLocation.EndObjIndex)
                    throw new InvalidOperationException("Ð’ Ð¾Ð±ÑŠÐµÐºÑ‚Ðµ " + objNum + " " + genNum + " Ð½ÐµÑ‚ stream");

                headerText = _text.Substring(indexedObjStart, indexedStreamKeywordPos - indexedObjStart);

                int indexedStreamLength;
                Match indexedRefLengthMatch = Regex.Match(headerText, @"/Length\s+(\d+)\s+(\d+)\s+R\b");
                if (indexedRefLengthMatch.Success)
                {
                    int lenObj = int.Parse(indexedRefLengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    int lenGen = int.Parse(indexedRefLengthMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                    string lenText = FindObjectText(lenObj, lenGen);
                    Match valueMatch = Regex.Match(lenText, @"([0-9]+)");
                    if (!valueMatch.Success)
                        throw new InvalidOperationException("ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°Ñ‚ÑŒ ÐºÐ¾ÑÐ²ÐµÐ½Ð½Ñ‹Ð¹ /Length");

                    indexedStreamLength = int.Parse(valueMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                else
                {
                    Match indexedDirectLengthMatch = Regex.Match(headerText, @"/Length\s+(\d+)\b");
                    if (!indexedDirectLengthMatch.Success)
                        throw new InvalidOperationException("ÐŸÐ¾Ð´Ð´ÐµÑ€Ð¶Ð¸Ð²Ð°ÐµÑ‚ÑÑ Ñ‚Ð¾Ð»ÑŒÐºÐ¾ Ð¿Ñ€ÑÐ¼Ð¾Ð¹ Ð¸Ð»Ð¸ ÐºÐ¾ÑÐ²ÐµÐ½Ð½Ñ‹Ð¹ /Length");

                    indexedStreamLength = int.Parse(indexedDirectLengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                int indexedStreamDataStart = indexedStreamKeywordPos + "stream".Length;
                if (indexedStreamDataStart < _data.Length && _data[indexedStreamDataStart] == (byte)'\r')
                    indexedStreamDataStart++;
                if (indexedStreamDataStart < _data.Length && _data[indexedStreamDataStart] == (byte)'\n')
                    indexedStreamDataStart++;

                if (indexedStreamDataStart + indexedStreamLength > _data.Length)
                    throw new InvalidOperationException("ÐÐµÐºÐ¾Ñ€Ñ€ÐµÐºÑ‚Ð½Ð°Ñ Ð´Ð»Ð¸Ð½Ð° stream");

                streamBytes = new byte[indexedStreamLength];
                Buffer.BlockCopy(_data, indexedStreamDataStart, streamBytes, 0, indexedStreamLength);
                return;
            }
            var objRegex = new Regex(@"(?:^|[\r\n])\s*" + objNum.ToString(CultureInfo.InvariantCulture) + @"\s+" + genNum.ToString(CultureInfo.InvariantCulture) + @"\s+obj\b");
            Match objMatch = objRegex.Match(_text);
            if (!objMatch.Success)
                throw new InvalidOperationException("Не найден объект " + objNum + " " + genNum + " obj");

            int objStart = objMatch.Index + objMatch.Length;
            int streamKeywordPos = _text.IndexOf("stream", objStart, StringComparison.Ordinal);
            if (streamKeywordPos < 0)
                throw new InvalidOperationException("В объекте " + objNum + " " + genNum + " нет stream");

            headerText = _text.Substring(objStart, streamKeywordPos - objStart);

            int streamLength;
            Match refLengthMatch = Regex.Match(headerText, @"/Length\s+(\d+)\s+(\d+)\s+R\b");
            if (refLengthMatch.Success)
            {
                int lenObj = int.Parse(refLengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int lenGen = int.Parse(refLengthMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                string lenText = FindObjectText(lenObj, lenGen);
                Match valueMatch = Regex.Match(lenText, @"([0-9]+)");
                if (!valueMatch.Success)
                    throw new InvalidOperationException("Не удалось прочитать косвенный /Length");

                streamLength = int.Parse(valueMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                Match directLengthMatch = Regex.Match(headerText, @"/Length\s+(\d+)\b");
                if (!directLengthMatch.Success)
                    throw new InvalidOperationException("Поддерживается только прямой или косвенный /Length");

                streamLength = int.Parse(directLengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            int streamDataStart = streamKeywordPos + "stream".Length;
            if (streamDataStart < _data.Length && _data[streamDataStart] == (byte)'\r')
                streamDataStart++;
            if (streamDataStart < _data.Length && _data[streamDataStart] == (byte)'\n')
                streamDataStart++;

            if (streamDataStart + streamLength > _data.Length)
                throw new InvalidOperationException("Некорректная длина stream");

            streamBytes = new byte[streamLength];
            Buffer.BlockCopy(_data, streamDataStart, streamBytes, 0, streamLength);
        }

        private void IndexTopLevelObjects()
        {
            var objHeaderRegex = new Regex(@"(?:^|[\r\n])\s*(\d+)\s+(\d+)\s+obj\b");
            var endObjRegex = new Regex(@"(?:^|[\r\n])\s*endobj\b");

            Match match = objHeaderRegex.Match(_text);
            while (match.Success)
            {
                int objNum = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int genNum = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int bodyStart = match.Index + match.Length;

                Match endObjMatch = endObjRegex.Match(_text, bodyStart);
                if (!endObjMatch.Success)
                    break;

                _topLevelObjects[MakeObjectKey(objNum, genNum)] = new TopLevelObjectLocation(
                    objNum,
                    genNum,
                    match.Index,
                    bodyStart,
                    endObjMatch.Index);

                match = match.NextMatch();
            }
        }

        private void ExtractCompressedObjects()
        {
            foreach (TopLevelObjectLocation location in _topLevelObjects.Values)
            {
                string objectText = GetTopLevelObjectText(location);
                if (!Regex.IsMatch(objectText, @"/Type\s*/ObjStm\b"))
                    continue;

                try
                {
                    ExtractCompressedObjectsFromObjectStream(location.ObjectNumber, location.GenerationNumber);
                }
                catch
                {
                    // Leave the rest of the document readable even if one object stream is malformed.
                }
            }
        }

        private void ExtractCompressedObjectsFromObjectStream(int objNum, int genNum)
        {
            ExtractStreamObject(objNum, genNum, out string headerText, out byte[] streamBytes);
            if (Regex.IsMatch(headerText, @"/Filter\s*/FlateDecode\b"))
                streamBytes = DecompressFlate(streamBytes);

            int objectCount = ParseRequiredInt(headerText, "/N");
            int firstOffset = ParseRequiredInt(headerText, "/First");
            if (objectCount <= 0 || firstOffset < 0 || firstOffset > streamBytes.Length)
                return;

            string streamText = Encoding.Latin1.GetString(streamBytes);
            string headerSection = streamText.Substring(0, Math.Min(firstOffset, streamText.Length));
            MatchCollection headerNumbers = Regex.Matches(headerSection, @"[+\-]?\d+");
            if (headerNumbers.Count < objectCount * 2)
                return;

            var objectNumbers = new int[objectCount];
            var offsets = new int[objectCount];

            for (int i = 0; i < objectCount; i++)
            {
                objectNumbers[i] = int.Parse(headerNumbers[i * 2].Value, CultureInfo.InvariantCulture);
                offsets[i] = int.Parse(headerNumbers[(i * 2) + 1].Value, CultureInfo.InvariantCulture);
            }

            for (int i = 0; i < objectCount; i++)
            {
                int start = firstOffset + offsets[i];
                int end = i + 1 < objectCount
                    ? firstOffset + offsets[i + 1]
                    : streamText.Length;

                if (start < firstOffset || start >= streamText.Length || end <= start)
                    continue;

                string embeddedObjectText = streamText.Substring(start, end - start).Trim();
                if (embeddedObjectText.Length == 0)
                    continue;

                _compressedObjects[MakeObjectKey(objectNumbers[i], 0)] = embeddedObjectText;
            }
        }

        private string GetTopLevelObjectText(TopLevelObjectLocation location)
        {
            return _text.Substring(location.BodyStart, location.EndObjIndex - location.BodyStart);
        }

        private static string MakeObjectKey(int objNum, int genNum)
        {
            return objNum.ToString(CultureInfo.InvariantCulture) + ":" + genNum.ToString(CultureInfo.InvariantCulture);
        }

        private static byte[] DecompressFlate(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }

        private byte[] DecodeImageStreamFilters(
            string header,
            byte[] streamBytes,
            int width,
            int height,
            int components,
            int bitsPerComponent,
            out string finalFilter)
        {
            finalFilter = string.Empty;
            byte[] decoded = streamBytes;
            List<string> filters = ParseFilterNames(header);

            if (filters.Count == 0)
            {
                finalFilter = GuessImageFilterFromBytes(decoded);
                return decoded;
            }

            foreach (string filter in filters)
            {
                switch (filter)
                {
                    case "/ASCII85Decode":
                        decoded = DecodeAscii85(decoded);
                        break;

                    case "/ASCIIHexDecode":
                        decoded = DecodeAsciiHex(decoded);
                        break;

                    case "/FlateDecode":
                        decoded = DecompressFlate(decoded);
                        decoded = ApplyDecodeParms(header, decoded, width, height, components, bitsPerComponent);
                        break;

                    case "/DCTDecode":
                    case "/JPXDecode":
                    case "/CCITTFaxDecode":
                        finalFilter = filter;
                        return decoded;

                    default:
                        finalFilter = filter;
                        return decoded;
                }
            }

            finalFilter = GuessImageFilterFromBytes(decoded);
            return decoded;
        }

        private static List<string> ParseFilterNames(string header)
        {
            var filters = new List<string>();
            int filterIndex = header.IndexOf("/Filter", StringComparison.Ordinal);
            if (filterIndex < 0)
                return filters;

            int index = filterIndex + "/Filter".Length;
            SkipPdfWhiteSpace(header, ref index);
            if (index >= header.Length)
                return filters;

            if (header[index] == '[')
            {
                int closeIndex = header.IndexOf(']', index + 1);
                if (closeIndex < 0)
                    closeIndex = header.Length;

                string arrayText = header.Substring(index + 1, closeIndex - index - 1);
                foreach (Match match in Regex.Matches(arrayText, @"/([^/\s<>\[\]\(\)]+)"))
                    filters.Add(NormalizeFilterName("/" + match.Groups[1].Value));

                return filters;
            }

            if (header[index] != '/')
                return filters;

            index++;
            int start = index;
            while (index < header.Length && !IsPdfDelimiterOrWhiteSpace(header[index]))
                index++;

            if (index > start)
                filters.Add(NormalizeFilterName("/" + header.Substring(start, index - start)));

            return filters;
        }

        private static string NormalizeFilterName(string filter)
        {
            return filter switch
            {
                "/Fl" => "/FlateDecode",
                "/A85" => "/ASCII85Decode",
                "/AHx" => "/ASCIIHexDecode",
                "/DCT" => "/DCTDecode",
                "/CCF" => "/CCITTFaxDecode",
                "/LZW" => "/LZWDecode",
                "/RL" => "/RunLengthDecode",
                _ => filter
            };
        }

        private static string GuessImageFilterFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "/DCTDecode";

            if (bytes.Length >= 12 &&
                bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x0C &&
                bytes[4] == 0x6A && bytes[5] == 0x50 && bytes[6] == 0x20 && bytes[7] == 0x20)
            {
                return "/JPXDecode";
            }

            return string.Empty;
        }

        private static byte[] DecodeAsciiHex(byte[] encoded)
        {
            using var output = new MemoryStream(encoded.Length / 2);
            int high = -1;

            foreach (byte b in encoded)
            {
                char ch = (char)b;
                if (ch == '>')
                    break;

                if (char.IsWhiteSpace(ch))
                    continue;

                int value = HexDigitValue(ch);
                if (value < 0)
                    continue;

                if (high < 0)
                {
                    high = value;
                    continue;
                }

                output.WriteByte((byte)((high << 4) | value));
                high = -1;
            }

            if (high >= 0)
                output.WriteByte((byte)(high << 4));

            return output.ToArray();
        }

        private static byte[] DecodeAscii85(byte[] encoded)
        {
            using var output = new MemoryStream(encoded.Length);
            var group = new int[5];
            int count = 0;

            for (int i = 0; i < encoded.Length; i++)
            {
                char ch = (char)encoded[i];
                if (char.IsWhiteSpace(ch))
                    continue;

                if (ch == '<' && i + 1 < encoded.Length && (char)encoded[i + 1] == '~')
                {
                    i++;
                    continue;
                }

                if (ch == '~')
                    break;

                if (ch == 'z' && count == 0)
                {
                    output.WriteByte(0);
                    output.WriteByte(0);
                    output.WriteByte(0);
                    output.WriteByte(0);
                    continue;
                }

                if (ch < '!' || ch > 'u')
                    continue;

                group[count++] = ch - '!';
                if (count != 5)
                    continue;

                WriteAscii85Group(output, group, 4);
                count = 0;
            }

            if (count > 1)
            {
                for (int i = count; i < 5; i++)
                    group[i] = 84;

                WriteAscii85Group(output, group, count - 1);
            }

            return output.ToArray();
        }

        private static void WriteAscii85Group(Stream output, int[] group, int bytesToWrite)
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
                value = (value * 85u) + (uint)group[i];

            byte b0 = (byte)(value >> 24);
            byte b1 = (byte)(value >> 16);
            byte b2 = (byte)(value >> 8);
            byte b3 = (byte)value;

            if (bytesToWrite >= 1)
                output.WriteByte(b0);
            if (bytesToWrite >= 2)
                output.WriteByte(b1);
            if (bytesToWrite >= 3)
                output.WriteByte(b2);
            if (bytesToWrite >= 4)
                output.WriteByte(b3);
        }

        private static int HexDigitValue(char ch)
        {
            if (ch >= '0' && ch <= '9')
                return ch - '0';
            if (ch >= 'A' && ch <= 'F')
                return ch - 'A' + 10;
            if (ch >= 'a' && ch <= 'f')
                return ch - 'a' + 10;
            return -1;
        }

        private static void SkipPdfWhiteSpace(string source, ref int index)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
                index++;
        }

        private static bool IsPdfDelimiterOrWhiteSpace(char ch)
        {
            return char.IsWhiteSpace(ch) ||
                   ch == '/' ||
                   ch == '<' ||
                   ch == '>' ||
                   ch == '[' ||
                   ch == ']' ||
                   ch == '(' ||
                   ch == ')' ||
                   ch == '{' ||
                   ch == '}';
        }

        private static float ParseFloat(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static bool IsPageObject(string objectText)
        {
            return Regex.IsMatch(objectText, @"/Type\s*/Page\b") &&
                   !Regex.IsMatch(objectText, @"/Type\s*/Pages\b");
        }

        private static bool IsPagesObject(string objectText)
        {
            return Regex.IsMatch(objectText, @"/Type\s*/Pages\b");
        }

        private sealed record TopLevelObjectLocation(
            int ObjectNumber,
            int GenerationNumber,
            int HeaderIndex,
            int BodyStart,
            int EndObjIndex);

        private sealed record PageParseInfo(
            string ObjectText,
            string? InheritedMediaBoxOwnerText,
            string? InheritedResourcesDictionaryText);
    }
}
