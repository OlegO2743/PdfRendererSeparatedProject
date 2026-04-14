using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using PdfCore;
using PdfCore.Parsing;

if (args.Length > 0 && string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
{
    InspectType1Font(args[1], args[2], args.Skip(3).ToArray());
    return;
}

if (args.Length > 0 && string.Equals(args[0], "glyph", StringComparison.OrdinalIgnoreCase))
{
    RenderType1Glyphs(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "images", StringComparison.OrdinalIgnoreCase))
{
    InspectImages(args[1], args.Skip(2).Select(int.Parse).ToArray());
    return;
}

if (args.Length > 0 && string.Equals(args[0], "decodeimage", StringComparison.OrdinalIgnoreCase))
{
    TryDecodeImages(args[1], args.Skip(2).Select(int.Parse).ToArray());
    return;
}

if (args.Length > 0 && string.Equals(args[0], "inspectjpx", StringComparison.OrdinalIgnoreCase))
{
    InspectJpxImages(args[1], args.Skip(2).Select(int.Parse).ToArray());
    return;
}

if (args.Length > 0 && string.Equals(args[0], "inspectjpxgeom", StringComparison.OrdinalIgnoreCase))
{
    int? tileIndex = null;
    var pageArgs = new List<int>();
    foreach (string arg in args.Skip(2))
    {
        if (arg.StartsWith("tile=", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(arg.AsSpan("tile=".Length), out int parsedTile))
        {
            tileIndex = parsedTile;
            continue;
        }

        pageArgs.Add(int.Parse(arg));
    }

    InspectJpxGeometry(args[1], pageArgs.ToArray(), tileIndex);
    return;
}

string pdfPath = args.Length > 0
    ? args[0]
    : @"C:\Users\Oleg Ogar\Downloads\book.pdf";

string outDir = args.Length > 1
    ? args[1]
    : @"D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_type1check\out";

int[] pages = args.Length > 2
    ? args.Skip(2).Select(int.Parse).ToArray()
    : [14, 19];

Directory.CreateDirectory(outDir);

var doc = SimplePdfParser.Parse(pdfPath);
Console.WriteLine($"Pages={doc.Pages.Count}");

foreach (int pageNumber in pages)
{
    if (pageNumber <= 0 || pageNumber > doc.Pages.Count)
        continue;

    using Bitmap bmp = SimplePdfRenderer.Render(doc.Pages[pageNumber - 1], 2.0f);
    string outPath = Path.Combine(outDir, $"page_{pageNumber}.png");
    bmp.Save(outPath, ImageFormat.Png);
    Console.WriteLine(outPath);
}

static void InspectType1Font(string pdfPath, string fontResourceName, string[] glyphNames)
{
    var doc = SimplePdfParser.Parse(pdfPath);
    var page = doc.Pages[18];
    if (!page.Resources.Fonts.TryGetValue(fontResourceName, out var font) || font.FontFileBytes == null)
        return;

    Assembly asm = typeof(SimplePdfParser).Assembly;
    Type? type1Type = asm.GetType("PdfCore.Text.PdfType1Font");
    if (type1Type == null)
        return;

    MethodInfo? tryCreate = type1Type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
    if (tryCreate == null)
        return;

    object?[] createArgs = [font.FontFileBytes, null];
    bool ok = (bool)(tryCreate.Invoke(null, createArgs) ?? false);
    Console.WriteLine($"TryCreate={ok}");
    if (!ok || createArgs[1] == null)
        return;

    object type1 = createArgs[1]!;
    FieldInfo? glyphMapField = type1Type.GetField("_glyphIdsByName", BindingFlags.NonPublic | BindingFlags.Instance);
    FieldInfo? charStringsField = type1Type.GetField("_charStrings", BindingFlags.NonPublic | BindingFlags.Instance);
    if (glyphMapField == null || charStringsField == null)
        return;

    var glyphMap = (System.Collections.IDictionary?)glyphMapField.GetValue(type1);
    var charStrings = (Array?)charStringsField.GetValue(type1);
    if (glyphMap == null || charStrings == null)
        return;

    foreach (string glyphName in glyphNames)
    {
        if (!glyphMap.Contains(glyphName))
            continue;

        int glyphId = (int)(glyphMap[glyphName] ?? -1);
        byte[] data = (byte[])charStrings.GetValue(glyphId)!;
        int code = -1;
        if (font.GlyphNameMap != null)
        {
            foreach (var pair in font.GlyphNameMap)
            {
                if (pair.Value == glyphName)
                {
                    code = pair.Key;
                    break;
                }
            }
        }
        int callOtherSubrCount = 0;
        int hvCount = 0;
        int vhCount = 0;
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 12 && data[i + 1] == 16)
                callOtherSubrCount++;
            if (data[i] == 30)
                vhCount++;
            if (data[i] == 31)
                hvCount++;
        }

        float width = code >= 0 ? font.GetGlyphWidth(code) : -1f;
        Console.WriteLine($"{glyphName}: code={code}, width={width}, id={glyphId}, len={data.Length}, callothersubr={callOtherSubrCount}, vh={vhCount}, hv={hvCount}");
    }
}

static void RenderType1Glyphs(string pdfPath, string fontResourceName, string glyphList, string? outDirArg)
{
    string outDir = outDirArg ?? @"D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_type1check\glyphs";
    Directory.CreateDirectory(outDir);

    var doc = SimplePdfParser.Parse(pdfPath);
    var page = doc.Pages[18];
    if (!page.Resources.Fonts.TryGetValue(fontResourceName, out var font) || font.FontFileBytes == null)
        return;

    Assembly asm = typeof(SimplePdfParser).Assembly;
    Type? type1Type = asm.GetType("PdfCore.Text.PdfType1Font");
    if (type1Type == null)
        return;

    MethodInfo? tryCreate = type1Type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
    MethodInfo? tryMapGlyphName = type1Type.GetMethod("TryMapGlyphName", BindingFlags.Public | BindingFlags.Instance);
    MethodInfo? addGlyphPath = type1Type.GetMethod("AddGlyphPath", BindingFlags.Public | BindingFlags.Instance);
    PropertyInfo? matrixA = type1Type.GetProperty("MatrixA");
    PropertyInfo? matrixB = type1Type.GetProperty("MatrixB");
    PropertyInfo? matrixC = type1Type.GetProperty("MatrixC");
    PropertyInfo? matrixD = type1Type.GetProperty("MatrixD");
    PropertyInfo? matrixE = type1Type.GetProperty("MatrixE");
    PropertyInfo? matrixF = type1Type.GetProperty("MatrixF");
    if (tryCreate == null || tryMapGlyphName == null || addGlyphPath == null ||
        matrixA == null || matrixB == null || matrixC == null || matrixD == null || matrixE == null || matrixF == null)
    {
        return;
    }

    object?[] createArgs = [font.FontFileBytes, null];
    bool ok = (bool)(tryCreate.Invoke(null, createArgs) ?? false);
    if (!ok || createArgs[1] == null)
        return;

    object type1 = createArgs[1]!;
    float a = Convert.ToSingle(matrixA.GetValue(type1));
    float b = Convert.ToSingle(matrixB.GetValue(type1));
    float c = Convert.ToSingle(matrixC.GetValue(type1));
    float d = Convert.ToSingle(matrixD.GetValue(type1));
    float e = Convert.ToSingle(matrixE.GetValue(type1));
    float f = Convert.ToSingle(matrixF.GetValue(type1));

    foreach (string rawGlyphName in glyphList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        object?[] mapArgs = [rawGlyphName, 0];
        bool mapped = (bool)(tryMapGlyphName.Invoke(type1, mapArgs) ?? false);
        if (!mapped)
            continue;

        int glyphId = (int)mapArgs[1]!;

        using var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Winding);
        using var transform = new System.Drawing.Drawing2D.Matrix(
            800f * a,
            800f * b,
            800f * c,
            800f * d,
            120f + 800f * e,
            900f + 800f * f);
        addGlyphPath.Invoke(type1, [path, glyphId, transform]);

        RectangleF bounds = path.GetBounds();
        using var bmp = new Bitmap((int)Math.Ceiling(bounds.Right + 120), (int)Math.Ceiling(bounds.Bottom + 120));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(Brushes.Black, path);
        g.DrawRectangle(Pens.Red, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        string outPath = Path.Combine(outDir, rawGlyphName + ".png");
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine(outPath);
    }
}

static void InspectImages(string pdfPath, int[] pages)
{
    var doc = SimplePdfParser.Parse(pdfPath);
    Console.WriteLine($"Pages={doc.Pages.Count}");

    foreach (int pageNumber in pages)
    {
        if (pageNumber <= 0 || pageNumber > doc.Pages.Count)
            continue;

        var page = doc.Pages[pageNumber - 1];
        Console.WriteLine($"Page {pageNumber}: images={page.Resources.Images.Count}, forms={page.Resources.Forms.Count}");

        foreach (var pair in page.Resources.Images)
        {
            var image = pair.Value;
            string firstBytes = string.Join(" ", image.ImageBytes.Take(12).Select(b => b.ToString("X2")));
            Console.WriteLine(
                $"  {pair.Key}: {image.Width}x{image.Height}, bpc={image.BitsPerComponent}, cs={image.ColorSpace.GetType().Name}, filter='{image.Filter}', bytes={image.ImageBytes.Length}, head={firstBytes}");
        }

        foreach (var pair in page.Resources.Forms)
        {
            var form = pair.Value;
            Console.WriteLine($"  form {pair.Key}: images={form.Resources.Images.Count}, forms={form.Resources.Forms.Count}, stream={form.ContentStream.Length}");
            foreach (var imagePair in form.Resources.Images)
            {
                var image = imagePair.Value;
                string firstBytes = string.Join(" ", image.ImageBytes.Take(12).Select(b => b.ToString("X2")));
                Console.WriteLine(
                    $"    {imagePair.Key}: {image.Width}x{image.Height}, bpc={image.BitsPerComponent}, cs={image.ColorSpace.GetType().Name}, filter='{image.Filter}', bytes={image.ImageBytes.Length}, head={firstBytes}");
            }
        }
    }
}

static void TryDecodeImages(string pdfPath, int[] pages)
{
    var doc = SimplePdfParser.Parse(pdfPath);
    foreach (int pageNumber in pages)
    {
        if (pageNumber <= 0 || pageNumber > doc.Pages.Count)
            continue;

        var page = doc.Pages[pageNumber - 1];
        Console.WriteLine($"Page {pageNumber}");
        foreach (var pair in page.Resources.Images)
        {
            var image = pair.Value;
            Console.Write($"  {pair.Key} {image.Filter}: ");
            try
            {
                using var bitmap = image.CreateBitmap();
                Console.WriteLine($"System.Drawing ok {bitmap.Width}x{bitmap.Height}");
            }
            catch (Exception ex)
            {
                Console.Write($"System.Drawing fail {ex.GetType().Name}: {ex.Message}; ");
                Console.WriteLine();
                Console.WriteLine(ex);
                try
                {
                    using var stream = new MemoryStream(image.ImageBytes);
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    Console.WriteLine($"WIC ok {frame.PixelWidth}x{frame.PixelHeight} {decoder.GetType().Name}");
                }
                catch (Exception wicEx)
                {
                    Console.WriteLine($"WIC fail {wicEx.GetType().Name}: {wicEx.Message}");
                }
            }
        }
    }
}

static void InspectJpxImages(string pdfPath, int[] pages)
{
    var doc = SimplePdfParser.Parse(pdfPath);
    foreach (int pageNumber in pages)
    {
        if (pageNumber <= 0 || pageNumber > doc.Pages.Count)
            continue;

        var page = doc.Pages[pageNumber - 1];
        Console.WriteLine($"Page {pageNumber}");
        foreach (var pair in page.Resources.Images)
        {
            var image = pair.Value;
            if (!string.Equals(image.Filter, "/JPXDecode", StringComparison.Ordinal))
                continue;

            Console.WriteLine($"  {pair.Key}: {image.Width}x{image.Height}, bytes={image.ImageBytes.Length}");
            try
            {
                Console.WriteLine("    info: " + PdfCore.Images.Jpeg2000.Jpeg2000Decoder.Describe(image.ImageBytes, parsePackets: false));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    info failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                Console.WriteLine("    " + PdfCore.Images.Jpeg2000.Jpeg2000Decoder.Describe(image.ImageBytes, parsePackets: true));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    describe failed: {ex.GetType().Name}: {ex.Message}");
            }

            DumpJp2Boxes(image.ImageBytes, "    ");
            int codestreamOffset = FindJp2CodestreamOffset(image.ImageBytes);
            if (codestreamOffset >= 0)
            {
                DumpCodestreamMarkers(image.ImageBytes, codestreamOffset, "    ");
                DumpTilePartBoundaries(image.ImageBytes, codestreamOffset, "    ");
            }
        }
    }
}

static void InspectJpxGeometry(string pdfPath, int[] pages, int? tileIndex)
{
    var doc = SimplePdfParser.Parse(pdfPath);
    foreach (int pageNumber in pages)
    {
        if (pageNumber <= 0 || pageNumber > doc.Pages.Count)
            continue;

        var page = doc.Pages[pageNumber - 1];
        Console.WriteLine($"Page {pageNumber}");
        foreach (var pair in page.Resources.Images)
        {
            var image = pair.Value;
            if (!string.Equals(image.Filter, "/JPXDecode", StringComparison.Ordinal))
                continue;

            Console.WriteLine($"  {pair.Key}: {image.Width}x{image.Height}, bytes={image.ImageBytes.Length}");
            try
            {
                Console.WriteLine(PdfCore.Images.Jpeg2000.Jpeg2000Decoder.DescribeGeometry(image.ImageBytes, tileIndex ?? 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine("    geometry failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}

static void DumpJp2Boxes(byte[] data, string indent)
{
    int offset = 0;
    int guard = 0;
    while (offset + 8 <= data.Length && guard++ < 64)
    {
        long length = ReadUInt32BE(data, offset);
        string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
        int header = 8;
        if (length == 1 && offset + 16 <= data.Length)
        {
            length = (long)ReadUInt64BE(data, offset + 8);
            header = 16;
        }
        else if (length == 0)
        {
            length = data.Length - offset;
        }

        if (length < header || offset + length > data.Length)
        {
            Console.WriteLine($"{indent}box @{offset}: {type}, invalid length={length}");
            break;
        }

        Console.WriteLine($"{indent}box @{offset}: {type}, len={length}");
        if (type == "ihdr" && length >= header + 14)
        {
            uint height = ReadUInt32BE(data, offset + header);
            uint width = ReadUInt32BE(data, offset + header + 4);
            ushort comps = ReadUInt16BE(data, offset + header + 8);
            byte bpc = data[offset + header + 10];
            Console.WriteLine($"{indent}  ihdr {width}x{height}, comps={comps}, bpcByte={bpc}");
        }

        if (type == "jp2c")
            return;

        offset += (int)length;
    }
}

static int FindJp2CodestreamOffset(byte[] data)
{
    int offset = 0;
    int guard = 0;
    while (offset + 8 <= data.Length && guard++ < 64)
    {
        long length = ReadUInt32BE(data, offset);
        string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
        int header = 8;
        if (length == 1 && offset + 16 <= data.Length)
        {
            length = (long)ReadUInt64BE(data, offset + 8);
            header = 16;
        }
        else if (length == 0)
        {
            length = data.Length - offset;
        }

        if (length < header || offset + length > data.Length)
            return -1;

        if (type == "jp2c")
            return offset + header;

        offset += (int)length;
    }

    return data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F ? 0 : -1;
}

static void DumpCodestreamMarkers(byte[] data, int offset, string indent)
{
    int i = offset;
    int guard = 0;
    while (i + 1 < data.Length && guard++ < 96)
    {
        if (data[i] != 0xFF)
        {
            i++;
            continue;
        }

        while (i < data.Length && data[i] == 0xFF)
            i++;
        if (i >= data.Length)
            return;

        byte marker = data[i++];
        if (marker == 0x00)
            continue;

        string name = Jpeg2000MarkerName(marker);
        if (marker == 0x4F || marker == 0x93 || marker == 0xD9)
        {
            Console.WriteLine($"{indent}{i - 2:X6}: FF{marker:X2} {name}");
            if (marker == 0x93)
                return;
            continue;
        }

        if (i + 2 > data.Length)
            return;

        ushort length = ReadUInt16BE(data, i);
        Console.WriteLine($"{indent}{i - 2:X6}: FF{marker:X2} {name}, len={length}");

        if (marker == 0x51 && i + length <= data.Length)
        {
            ushort rsiz = ReadUInt16BE(data, i + 2);
            uint xsiz = ReadUInt32BE(data, i + 4);
            uint ysiz = ReadUInt32BE(data, i + 8);
            uint xosiz = ReadUInt32BE(data, i + 12);
            uint yosiz = ReadUInt32BE(data, i + 16);
            uint xtsiz = ReadUInt32BE(data, i + 20);
            uint ytsiz = ReadUInt32BE(data, i + 24);
            ushort comps = ReadUInt16BE(data, i + 36);
            Console.WriteLine($"{indent}  SIZ rsiz={rsiz}, image={xsiz - xosiz}x{ysiz - yosiz}, tile={xtsiz}x{ytsiz}, comps={comps}");
        }
        else if (marker == 0x52 && i + length <= data.Length)
        {
            byte progression = data[i + 3];
            ushort layers = ReadUInt16BE(data, i + 4);
            byte mct = data[i + 6];
            byte levels = data[i + 7];
            byte cbw = data[i + 8];
            byte cbh = data[i + 9];
            byte style = data[i + 10];
            byte transform = data[i + 11];
            Console.WriteLine($"{indent}  COD prog={progression}, layers={layers}, mct={mct}, levels={levels}, cb=2^{cbw + 2}x2^{cbh + 2}, style=0x{style:X2}, transform={transform}");
        }

        i += length;
    }
}

static void DumpTilePartBoundaries(byte[] data, int offset, string indent)
{
    int end = data.Length;
    int i = offset;
    int guard = 0;
    while (i + 1 < end && guard++ < 128)
    {
        int markerStart = FindNextMarker(data, i, end);
        if (markerStart < 0)
            return;

        byte marker = data[markerStart + 1];
        i = markerStart + 2;
        if (marker == 0xD9)
        {
            Console.WriteLine($"{indent}tile-scan EOC @{markerStart:X6}");
            return;
        }

        if (marker != 0x90)
        {
            if (marker == 0x4F || marker == 0x93)
                continue;

            if (i + 2 > end)
                return;

            ushort len = ReadUInt16BE(data, i);
            i += len;
            continue;
        }

        if (i + 10 > end)
            return;

        ushort lenSot = ReadUInt16BE(data, i);
        ushort tile = ReadUInt16BE(data, i + 2);
        uint psot = ReadUInt32BE(data, i + 4);
        byte part = data[i + 8];
        byte parts = data[i + 9];
        int afterSot = i + lenSot;
        int sod = FindNextExactMarker(data, afterSot, end, 0x93);
        int dataStart = sod >= 0 ? sod + 2 : -1;
        int declaredEnd = psot == 0 ? -1 : markerStart + checked((int)psot);
        int nextBoundary = dataStart >= 0 ? FindNextTileBoundary(data, dataStart, end) : -1;
        string declaredBytes = declaredEnd >= 0 && declaredEnd + 1 < end
            ? $"{data[declaredEnd]:X2} {data[declaredEnd + 1]:X2}"
            : "--";
        string preview = dataStart >= 0 && dataStart < end
            ? BitConverter.ToString(data, dataStart, Math.Min(16, end - dataStart))
            : string.Empty;
        Console.WriteLine($"{indent}tile-scan SOT @{markerStart:X6}: tile={tile}, psot={psot}, part={part}/{parts}, sod={sod:X6}, data={dataStart:X6}, declaredEnd={declaredEnd:X6}, bytes={declaredBytes}, nextBoundary={nextBoundary:X6}, preview={preview}");

        if (declaredEnd > markerStart && declaredEnd <= end)
            i = declaredEnd;
        else if (nextBoundary >= 0)
            i = nextBoundary;
        else
            return;
    }
}

static int FindNextMarker(byte[] data, int start, int end)
{
    for (int i = start; i + 1 < end; i++)
    {
        if (data[i] != 0xFF)
            continue;

        int markerOffset = i + 1;
        while (markerOffset < end && data[markerOffset] == 0xFF)
            markerOffset++;

        if (markerOffset >= end)
            return -1;

        if (data[markerOffset] == 0x00)
        {
            i = markerOffset;
            continue;
        }

        return i;
    }

    return -1;
}

static int FindNextExactMarker(byte[] data, int start, int end, byte marker)
{
    for (int i = start; i + 1 < end; i++)
    {
        if (data[i] == 0xFF && data[i + 1] == marker)
            return i;
    }

    return -1;
}

static int FindNextTileBoundary(byte[] data, int start, int end)
{
    for (int i = start; i + 1 < end; i++)
    {
        if (data[i] != 0xFF)
            continue;

        int markerOffset = i + 1;
        while (markerOffset < end && data[markerOffset] == 0xFF)
            markerOffset++;

        if (markerOffset >= end)
            return -1;

        byte marker = data[markerOffset];
        if (marker == 0x00)
        {
            i = markerOffset;
            continue;
        }

        if (marker == 0x90 || marker == 0xD9)
            return i;
    }

    return -1;
}

static string Jpeg2000MarkerName(byte marker) => marker switch
{
    0x4F => "SOC",
    0x51 => "SIZ",
    0x52 => "COD",
    0x53 => "COC",
    0x5C => "QCD",
    0x5D => "QCC",
    0x5E => "RGN",
    0x5F => "POC",
    0x55 => "TLM",
    0x57 => "PLM",
    0x58 => "PLT",
    0x60 => "PPM",
    0x61 => "PPT",
    0x64 => "CME",
    0x90 => "SOT",
    0x92 => "SOP",
    0x93 => "SOD",
    0xD9 => "EOC",
    _ => "?"
};

static ushort ReadUInt16BE(byte[] data, int offset)
    => (ushort)((data[offset] << 8) | data[offset + 1]);

static uint ReadUInt32BE(byte[] data, int offset)
    => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

static ulong ReadUInt64BE(byte[] data, int offset)
    => ((ulong)ReadUInt32BE(data, offset) << 32) | ReadUInt32BE(data, offset + 4);
