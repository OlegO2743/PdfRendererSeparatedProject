using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfCore.Text;

internal sealed class PdfType1Font
{
    private readonly byte[][] _subrs;
    private readonly byte[][] _charStrings;
    private readonly Dictionary<string, int> _glyphIdsByName;
    private readonly float[] _fontMatrix;
    private readonly object _pathCacheLock = new();
    private readonly Dictionary<int, Type1GlyphData> _glyphPathCache = new();

    private PdfType1Font(
        byte[][] subrs,
        byte[][] charStrings,
        Dictionary<string, int> glyphIdsByName,
        float[] fontMatrix)
    {
        _subrs = subrs;
        _charStrings = charStrings;
        _glyphIdsByName = glyphIdsByName;
        _fontMatrix = fontMatrix;
    }

    public float MatrixA => _fontMatrix[0];
    public float MatrixB => _fontMatrix[1];
    public float MatrixC => _fontMatrix[2];
    public float MatrixD => _fontMatrix[3];
    public float MatrixE => _fontMatrix[4];
    public float MatrixF => _fontMatrix[5];

    public static bool TryCreate(byte[] bytes, out PdfType1Font? font)
    {
        try
        {
            if (!LooksLikeType1(bytes))
            {
                font = null;
                return false;
            }

            byte[] clearText = ExtractClearText(bytes);
            float[] fontMatrix = ParseFontMatrix(clearText);
            byte[] privateSection = ExtractDecryptedPrivateSection(bytes);
            if (privateSection.Length == 0)
            {
                font = null;
                return false;
            }

            int lenIV = ParseLenIV(privateSection);
            byte[][] subrs = ParseSubrs(privateSection, lenIV);
            (byte[][] charStrings, Dictionary<string, int> glyphIdsByName) = ParseCharStrings(privateSection, lenIV);
            if (charStrings.Length == 0 || glyphIdsByName.Count == 0)
            {
                font = null;
                return false;
            }

            font = new PdfType1Font(subrs, charStrings, glyphIdsByName, fontMatrix);
            return true;
        }
        catch
        {
            font = null;
            return false;
        }
    }

    public bool TryMapGlyphName(string glyphName, out int glyphId)
    {
        glyphId = 0;
        if (string.IsNullOrWhiteSpace(glyphName))
            return false;

        int suffix = glyphName.IndexOf('.');
        if (suffix > 0)
            glyphName = glyphName[..suffix];

        return _glyphIdsByName.TryGetValue(glyphName, out glyphId) &&
               glyphId >= 0 &&
               glyphId < _charStrings.Length;
    }

    public void AddGlyphPath(GraphicsPath target, int glyphId, Matrix transform)
    {
        Type1GlyphData? data = GetGlyphData(glyphId);
        using GraphicsPath? glyphPath = data?.CloneHintedPath(transform);
        if (glyphPath == null || glyphPath.PointCount == 0)
            return;

        glyphPath.Transform(transform);
        target.AddPath(glyphPath, false);
    }

    public float GetAdvanceWidth(int glyphId)
    {
        Type1GlyphData? data = GetGlyphData(glyphId);
        return data?.Width ?? 0f;
    }

    public RectangleF? GetGlyphBounds(int glyphId)
    {
        Type1GlyphData? data = GetGlyphData(glyphId);
        if (data == null || data.Path.PointCount == 0)
            return null;

        return data.Path.GetBounds();
    }

    private GraphicsPath? GetGlyphPath(int glyphId)
        => GetGlyphData(glyphId)?.ClonePath();

    private Type1GlyphData? GetGlyphData(int glyphId)
    {
        if (glyphId < 0 || glyphId >= _charStrings.Length)
            return null;

        lock (_pathCacheLock)
        {
            if (_glyphPathCache.TryGetValue(glyphId, out Type1GlyphData? cached))
                return cached;
        }

        var built = new GraphicsPath(FillMode.Winding);
        var interpreter = new Type1Interpreter(built, _subrs);
        interpreter.Execute(_charStrings[glyphId], 0);
        var data = new Type1GlyphData(
            (GraphicsPath)built.Clone(),
            interpreter.Width,
            interpreter.SideBearingX,
            interpreter.SideBearingY,
            interpreter.HorizontalStems,
            interpreter.VerticalStems);
        built.Dispose();

        lock (_pathCacheLock)
        {
            if (!_glyphPathCache.ContainsKey(glyphId))
                _glyphPathCache[glyphId] = data;

            return _glyphPathCache[glyphId];
        }
    }

    private static bool LooksLikeType1(byte[] bytes)
    {
        int sampleLength = Math.Min(bytes.Length, 512);
        string head = Encoding.Latin1.GetString(bytes, 0, sampleLength);
        return head.Contains("%!PS-AdobeFont-", StringComparison.Ordinal) ||
               head.Contains("%!FontType1-", StringComparison.Ordinal) ||
               head.Contains("/FontType 1", StringComparison.Ordinal);
    }

    private static byte[] ExtractClearText(byte[] bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);
        int eexecIndex = text.IndexOf("currentfile eexec", StringComparison.Ordinal);
        if (eexecIndex < 0)
            eexecIndex = text.IndexOf("\neexec", StringComparison.Ordinal);
        if (eexecIndex < 0)
            eexecIndex = text.IndexOf("\reexec", StringComparison.Ordinal);
        if (eexecIndex < 0)
            eexecIndex = Math.Min(text.Length, 65536);

        return bytes[..Math.Min(bytes.Length, eexecIndex)];
    }

    private static float[] ParseFontMatrix(byte[] clearTextBytes)
    {
        string clearText = Encoding.Latin1.GetString(clearTextBytes);
        Match match = Regex.Match(
            clearText,
            @"/FontMatrix\s*\[\s*([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s+([+\-]?(?:\d+\.\d+|\d+|\.\d+))\s*\]",
            RegexOptions.Singleline);
        if (!match.Success)
            return [0.001f, 0f, 0f, 0.001f, 0f, 0f];

        return
        [
            ParseFloat(match.Groups[1].Value),
            ParseFloat(match.Groups[2].Value),
            ParseFloat(match.Groups[3].Value),
            ParseFloat(match.Groups[4].Value),
            ParseFloat(match.Groups[5].Value),
            ParseFloat(match.Groups[6].Value)
        ];
    }

    private static byte[] ExtractDecryptedPrivateSection(byte[] bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);
        int eexecIndex = text.IndexOf("currentfile eexec", StringComparison.Ordinal);
        int tokenLength = "currentfile eexec".Length;
        if (eexecIndex < 0)
        {
            eexecIndex = text.IndexOf("\neexec", StringComparison.Ordinal);
            tokenLength = 6;
        }

        if (eexecIndex < 0)
        {
            eexecIndex = text.IndexOf("\reexec", StringComparison.Ordinal);
            tokenLength = 6;
        }

        if (eexecIndex < 0)
            return Array.Empty<byte>();

        int start = Math.Min(bytes.Length, eexecIndex + tokenLength);
        while (start < bytes.Length && IsWhitespace(bytes[start]))
            start++;

        byte[] encrypted = IsHexEncoded(bytes, start)
            ? DecodeHexSection(bytes, start)
            : bytes[start..];

        if (encrypted.Length == 0)
            return Array.Empty<byte>();

        byte[] decrypted = Decrypt(encrypted, 55665, 4);
        int clearToMarkIndex = IndexOfAscii(decrypted, "cleartomark", 0);
        if (clearToMarkIndex > 0)
            decrypted = decrypted[..clearToMarkIndex];

        return decrypted;
    }

    private static bool IsHexEncoded(byte[] bytes, int start)
    {
        int checkedChars = 0;
        int hexChars = 0;
        for (int i = start; i < bytes.Length && checkedChars < 128; i++)
        {
            byte b = bytes[i];
            if (IsWhitespace(b))
                continue;

            checkedChars++;
            if (IsHexDigit(b))
                hexChars++;
        }

        return checkedChars > 16 && hexChars >= checkedChars - 2;
    }

    private static byte[] DecodeHexSection(byte[] bytes, int start)
    {
        var decoded = new List<byte>(bytes.Length / 2);
        int? highNibble = null;

        for (int i = start; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (IsWhitespace(b))
                continue;

            int nibble = HexValue(b);
            if (nibble < 0)
            {
                if (highNibble.HasValue)
                    highNibble = null;
                break;
            }

            if (!highNibble.HasValue)
            {
                highNibble = nibble;
            }
            else
            {
                decoded.Add((byte)((highNibble.Value << 4) | nibble));
                highNibble = null;
            }
        }

        return decoded.ToArray();
    }

    private static int ParseLenIV(byte[] privateSection)
    {
        string text = Encoding.Latin1.GetString(privateSection, 0, Math.Min(privateSection.Length, 65536));
        Match match = Regex.Match(text, @"/lenIV\s+([+\-]?\d+)");
        if (!match.Success)
            return 4;

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lenIV)
            ? lenIV
            : 4;
    }

    private static byte[][] ParseSubrs(byte[] privateSection, int lenIV)
    {
        int subrsIndex = IndexOfAscii(privateSection, "/Subrs", 0);
        if (subrsIndex < 0)
            return Array.Empty<byte[]>();

        int pos = subrsIndex + "/Subrs".Length;
        if (!TryReadInt(privateSection, ref pos, out int count) || count <= 0)
            return Array.Empty<byte[]>();

        var subrs = new byte[count][];
        int searchPos = pos;
        int charStringsIndex = IndexOfAscii(privateSection, "/CharStrings", searchPos);

        while (true)
        {
            int dupIndex = IndexOfAscii(privateSection, "dup", searchPos);
            if (dupIndex < 0 || (charStringsIndex >= 0 && dupIndex > charStringsIndex))
                break;

            pos = dupIndex + 3;
            if (!TryReadInt(privateSection, ref pos, out int index) ||
                !TryReadInt(privateSection, ref pos, out int length) ||
                !MoveToBinaryData(privateSection, ref pos))
            {
                searchPos = dupIndex + 3;
                continue;
            }

            if (index < 0 || index >= count || pos + length > privateSection.Length)
                break;

            byte[] encrypted = new byte[length];
            Buffer.BlockCopy(privateSection, pos, encrypted, 0, length);
            subrs[index] = DecryptCharString(encrypted, lenIV);
            searchPos = pos + length;
        }

        for (int i = 0; i < subrs.Length; i++)
            subrs[i] ??= Array.Empty<byte>();

        return subrs;
    }

    private static (byte[][] CharStrings, Dictionary<string, int> GlyphIdsByName) ParseCharStrings(byte[] privateSection, int lenIV)
    {
        int charStringsIndex = IndexOfAscii(privateSection, "/CharStrings", 0);
        if (charStringsIndex < 0)
            return (Array.Empty<byte[]>(), new Dictionary<string, int>(StringComparer.Ordinal));

        var charStrings = new List<byte[]>();
        var glyphIdsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        int searchPos = charStringsIndex + "/CharStrings".Length;

        while (true)
        {
            int nameIndex = IndexOfByte(privateSection, (byte)'/', searchPos);
            if (nameIndex < 0)
                break;

            int endIndex = IndexOfAscii(privateSection, "end", searchPos);
            if (endIndex >= 0 && endIndex < nameIndex)
                break;

            int pos = nameIndex + 1;
            string glyphName = ReadToken(privateSection, ref pos);
            if (string.IsNullOrEmpty(glyphName) ||
                !TryReadInt(privateSection, ref pos, out int length) ||
                !MoveToBinaryData(privateSection, ref pos) ||
                pos + length > privateSection.Length)
            {
                searchPos = nameIndex + 1;
                continue;
            }

            byte[] encrypted = new byte[length];
            Buffer.BlockCopy(privateSection, pos, encrypted, 0, length);
            byte[] decrypted = DecryptCharString(encrypted, lenIV);
            glyphIdsByName[glyphName] = charStrings.Count;
            charStrings.Add(decrypted);
            searchPos = pos + length;
        }

        return (charStrings.ToArray(), glyphIdsByName);
    }

    private static byte[] DecryptCharString(byte[] encrypted, int lenIV)
    {
        int discard = Math.Max(0, lenIV);
        return Decrypt(encrypted, 4330, discard);
    }

    private static byte[] Decrypt(byte[] encrypted, int key, int discardBytes)
    {
        const int c1 = 52845;
        const int c2 = 22719;

        var decrypted = new byte[encrypted.Length];
        int r = key;
        for (int i = 0; i < encrypted.Length; i++)
        {
            int cipher = encrypted[i];
            byte plain = (byte)(cipher ^ (r >> 8));
            r = ((cipher + r) * c1 + c2) & 0xFFFF;
            decrypted[i] = plain;
        }

        if (discardBytes <= 0)
            return decrypted;

        if (discardBytes >= decrypted.Length)
            return Array.Empty<byte>();

        return decrypted[discardBytes..];
    }

    private static bool MoveToBinaryData(byte[] data, ref int pos)
    {
        SkipWhitespace(data, ref pos);
        string token = ReadToken(data, ref pos);
        if (!string.Equals(token, "RD", StringComparison.Ordinal) &&
            !string.Equals(token, "-|", StringComparison.Ordinal))
        {
            return false;
        }

        if (pos < data.Length && IsWhitespace(data[pos]))
            pos++;

        return pos < data.Length;
    }

    private static bool TryReadInt(byte[] data, ref int pos, out int value)
    {
        SkipWhitespace(data, ref pos);
        int start = pos;
        if (pos < data.Length && (data[pos] == (byte)'+' || data[pos] == (byte)'-'))
            pos++;

        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
            pos++;

        if (start == pos || (start + 1 == pos && (data[start] == (byte)'+' || data[start] == (byte)'-')))
        {
            value = 0;
            return false;
        }

        value = int.Parse(Encoding.ASCII.GetString(data, start, pos - start), CultureInfo.InvariantCulture);
        return true;
    }

    private static string ReadToken(byte[] data, ref int pos)
    {
        SkipWhitespace(data, ref pos);
        int start = pos;
        while (pos < data.Length && !IsWhitespace(data[pos]) && !IsDelimiter(data[pos]))
            pos++;

        return pos > start
            ? Encoding.ASCII.GetString(data, start, pos - start)
            : string.Empty;
    }

    private static int IndexOfAscii(byte[] data, string token, int start)
    {
        byte[] needle = Encoding.ASCII.GetBytes(token);
        for (int i = Math.Max(0, start); i <= data.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    private static void ApplyStemHints(
        PointF[] points,
        Matrix transform,
        IReadOnlyList<StemHint> horizontalStems,
        IReadOnlyList<StemHint> verticalStems)
    {
        if (points.Length == 0 || (horizontalStems.Count == 0 && verticalStems.Count == 0))
            return;

        float[] elements = transform.Elements;
        float a = elements[0];
        float b = elements[1];
        float c = elements[2];
        float d = elements[3];
        float tx = elements[4];
        float ty = elements[5];

        // This lightweight hint pass is intentionally limited to non-skewed text.
        // It improves common horizontal PDF text without corrupting rotated labels.
        if (MathF.Abs(b) > 0.001f || MathF.Abs(c) > 0.001f)
            return;

        ApplyStemHintsToAxis(points, verticalStems, isX: true, a, tx);
        ApplyStemHintsToAxis(points, horizontalStems, isX: false, d, ty);
    }

    private static void ApplyStemHintsToAxis(
        PointF[] points,
        IReadOnlyList<StemHint> stems,
        bool isX,
        float scale,
        float offset)
    {
        if (stems.Count == 0 || MathF.Abs(scale) < 0.0001f)
            return;

        float tolerance = Math.Clamp(0.20f / MathF.Abs(scale), 1.0f, 6.0f);
        var edges = new List<(float Original, float Adjusted)>(stems.Count * 2);

        foreach (StemHint stem in stems)
        {
            if (MathF.Abs(stem.Width) < 0.01f)
                continue;

            float start = stem.Position;
            float end = stem.Position + stem.Width;
            float screenStart = start * scale + offset;
            float screenEnd = end * scale + offset;
            float snappedStart = SnapStemEdge(screenStart);
            float snappedEnd = SnapStemEdge(screenEnd);
            float screenWidth = screenEnd - screenStart;

            if (MathF.Abs(snappedEnd - snappedStart) < 0.75f && MathF.Abs(screenWidth) >= 0.45f)
                snappedEnd = snappedStart + MathF.Sign(screenWidth == 0f ? scale : screenWidth);

            edges.Add((start, (snappedStart - offset) / scale));
            edges.Add((end, (snappedEnd - offset) / scale));
        }

        if (edges.Count == 0)
            return;

        for (int i = 0; i < points.Length; i++)
        {
            float value = isX ? points[i].X : points[i].Y;
            float bestDistance = tolerance;
            float adjusted = value;

            foreach ((float original, float edgeAdjusted) in edges)
            {
                float distance = MathF.Abs(value - original);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    adjusted = edgeAdjusted;
                }
            }

            if (adjusted.Equals(value))
                continue;

            if (isX)
                points[i].X = adjusted;
            else
                points[i].Y = adjusted;
        }
    }

    private static float SnapStemEdge(float value)
        => MathF.Round(value);

    private static int IndexOfByte(byte[] data, byte value, int start)
    {
        for (int i = Math.Max(0, start); i < data.Length; i++)
        {
            if (data[i] == value)
                return i;
        }

        return -1;
    }

    private static void SkipWhitespace(byte[] data, ref int pos)
    {
        while (pos < data.Length)
        {
            if (IsWhitespace(data[pos]))
            {
                pos++;
                continue;
            }

            if (data[pos] == (byte)'%')
            {
                while (pos < data.Length && data[pos] != (byte)'\n' && data[pos] != (byte)'\r')
                    pos++;
                continue;
            }

            break;
        }
    }

    private static bool IsWhitespace(byte b)
        => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b)
        => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsHexDigit(byte b)
        => (b >= (byte)'0' && b <= (byte)'9') ||
           (b >= (byte)'A' && b <= (byte)'F') ||
           (b >= (byte)'a' && b <= (byte)'f');

    private static int HexValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
            return b - (byte)'0';
        if (b >= (byte)'A' && b <= (byte)'F')
            return b - (byte)'A' + 10;
        if (b >= (byte)'a' && b <= (byte)'f')
            return b - (byte)'a' + 10;
        return -1;
    }

    private static float ParseFloat(string text)
        => float.Parse(text, CultureInfo.InvariantCulture);

    private sealed class Type1Interpreter
    {
        private readonly GraphicsPath _path;
        private readonly byte[][] _subrs;
        private readonly List<float> _stack = new();
        private readonly Stack<float> _postScriptStack = new();
        private readonly List<PointF> _flexPoints = new();
        private readonly List<StemHint> _horizontalStems = new();
        private readonly List<StemHint> _verticalStems = new();

        private float _x;
        private float _y;
        private bool _figureOpen;
        private bool _inFlex;

        public float Width { get; private set; }
        public float SideBearingX { get; private set; }
        public float SideBearingY { get; private set; }
        public IReadOnlyList<StemHint> HorizontalStems => _horizontalStems;
        public IReadOnlyList<StemHint> VerticalStems => _verticalStems;

        public Type1Interpreter(GraphicsPath path, byte[][] subrs)
        {
            _path = path;
            _subrs = subrs;
        }

        public bool Execute(byte[] data, int depth)
        {
            if (depth > 12)
                return true;

            int pos = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                if (b == 255 || b >= 32)
                {
                    _stack.Add(ReadNumber(data, ref pos, b));
                    continue;
                }

                int op = b;
                if (b == 12 && pos < data.Length)
                    op = 1200 + data[pos++];

                if (!ExecuteOperator(op, depth))
                    return false;
            }

            return true;
        }

        private bool ExecuteOperator(int op, int depth)
        {
            switch (op)
            {
                case 1: // hstem
                    AddStemHints(_horizontalStems);
                    _stack.Clear();
                    return true;

                case 3: // vstem
                    AddStemHints(_verticalStems);
                    _stack.Clear();
                    return true;

                case 1201: // vstem3
                    AddStemHints(_verticalStems);
                    _stack.Clear();
                    return true;

                case 1202: // hstem3
                    AddStemHints(_horizontalStems);
                    _stack.Clear();
                    return true;

                case 1200: // dotsection
                    _stack.Clear();
                    return true;

                case 4: // vmoveto
                    MoveTo(0f, PopOrZero());
                    _stack.Clear();
                    return true;

                case 5: // rlineto
                    for (int i = 0; i + 1 < _stack.Count; i += 2)
                        LineTo(_stack[i], _stack[i + 1]);
                    _stack.Clear();
                    return true;

                case 6: // hlineto
                    HvlLineTo(horizontalFirst: true);
                    return true;

                case 7: // vlineto
                    HvlLineTo(horizontalFirst: false);
                    return true;

                case 8: // rrcurveto
                    for (int i = 0; i + 5 < _stack.Count; i += 6)
                        CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                    _stack.Clear();
                    return true;

                case 9: // closepath
                    CloseFigure();
                    _stack.Clear();
                    return true;

                case 10: // callsubr
                    CallSubr(depth);
                    return true;

                case 11: // return
                    _stack.Clear();
                    return false;

                case 13: // hsbw
                    Hsbw();
                    return true;

                case 14: // endchar
                    CloseFigure();
                    _stack.Clear();
                    return false;

                case 21: // rmoveto
                    if (_stack.Count >= 2)
                        MoveTo(_stack[^2], _stack[^1]);
                    _stack.Clear();
                    return true;

                case 22: // hmoveto
                    MoveTo(PopOrZero(), 0f);
                    _stack.Clear();
                    return true;

                case 30: // vhcurveto
                    VhHvCurveTo(verticalFirst: true);
                    return true;

                case 31: // hvcurveto
                    VhHvCurveTo(verticalFirst: false);
                    return true;

                case 1207: // sbw
                    Sbw();
                    return true;

                case 1212: // div
                    DivideTopTwo();
                    return true;

                case 1216: // callothersubr
                    CallOtherSubr();
                    return true;

                case 1217: // pop
                    if (_postScriptStack.Count > 0)
                        _stack.Add(_postScriptStack.Pop());
                    return true;

                case 1233: // setcurrentpoint
                    SetCurrentPoint();
                    return true;

                default:
                    _stack.Clear();
                    return true;
            }
        }

        private void Hsbw()
        {
            if (_stack.Count >= 2)
            {
                SideBearingX = _stack[^2];
                SideBearingY = 0f;
                Width = _stack[^1];
                _x = SideBearingX;
                _y = 0f;
            }

            _stack.Clear();
            _figureOpen = false;
        }

        private void AddStemHints(List<StemHint> target)
        {
            for (int i = 0; i + 1 < _stack.Count; i += 2)
            {
                float position = _stack[i];
                float width = _stack[i + 1];
                if (MathF.Abs(width) >= 0.01f)
                    target.Add(new StemHint(position, width));
            }
        }

        private void Sbw()
        {
            if (_stack.Count >= 4)
            {
                SideBearingX = _stack[^4];
                SideBearingY = _stack[^3];
                Width = _stack[^2];
                _x = SideBearingX;
                _y = SideBearingY;
            }

            _stack.Clear();
            _figureOpen = false;
        }

        private void SetCurrentPoint()
        {
            if (_stack.Count >= 2)
            {
                _x = _stack[^2];
                _y = _stack[^1];
            }

            _stack.Clear();
        }

        private void DivideTopTwo()
        {
            if (_stack.Count < 2)
                return;

            float denominator = _stack[^1];
            float numerator = _stack[^2];
            _stack.RemoveRange(_stack.Count - 2, 2);
            _stack.Add(Math.Abs(denominator) < 0.0001f ? 0f : numerator / denominator);
        }

        private void CallOtherSubr()
        {
            if (_stack.Count < 2)
            {
                _stack.Clear();
                return;
            }

            int othersubrNumber = (int)_stack[^1];
            int argCount = (int)_stack[^2];
            _stack.RemoveRange(_stack.Count - 2, 2);

            var args = new List<float>(argCount);
            for (int i = 0; i < argCount && _stack.Count > 0; i++)
            {
                args.Add(_stack[^1]);
                _stack.RemoveAt(_stack.Count - 1);
            }

            args.Reverse();

            switch (othersubrNumber)
            {
                case 0:
                    EndFlex();
                    break;

                case 1:
                    BeginFlex();
                    break;

                case 2:
                    AddFlexPoint(args);
                    break;

                case 3:
                    if (args.Count > 0)
                        _postScriptStack.Push(args[0]);
                    break;

                default:
                    break;
            }

            _stack.Clear();
        }

        private void BeginFlex()
        {
            _inFlex = true;
            _flexPoints.Clear();
        }

        private void AddFlexPoint(List<float> args)
        {
            if (!_inFlex)
                return;

            if (args.Count >= 2)
            {
                _x += args[^2];
                _y += args[^1];
            }

            _flexPoints.Add(new PointF(_x, _y));
        }

        private void EndFlex()
        {
            if (!_inFlex)
                return;

            _inFlex = false;
            if (_flexPoints.Count >= 7)
            {
                PointF p0 = _flexPoints[0];
                PointF p1 = _flexPoints[1];
                PointF p2 = _flexPoints[2];
                PointF p3 = _flexPoints[3];
                PointF p4 = _flexPoints[4];
                PointF p5 = _flexPoints[5];
                PointF p6 = _flexPoints[6];

                EnsureFigure();
                _path.AddBezier(p0, p1, p2, p3);
                _path.AddBezier(p3, p4, p5, p6);

                _x = p6.X;
                _y = p6.Y;

                // pop pop setcurrentpoint expects x then y on the operand stack.
                _postScriptStack.Push(_y);
                _postScriptStack.Push(_x);
            }

            _flexPoints.Clear();
        }

        private void MoveTo(float dx, float dy)
        {
            if (_inFlex)
            {
                _x += dx;
                _y += dy;
                return;
            }

            CloseFigure();
            _x += dx;
            _y += dy;
            _path.StartFigure();
            _figureOpen = true;
        }

        private void EnsureFigure()
        {
            if (_figureOpen)
                return;

            _path.StartFigure();
            _figureOpen = true;
        }

        private void CloseFigure()
        {
            if (_figureOpen)
            {
                _path.CloseFigure();
                _figureOpen = false;
            }
        }

        private void LineTo(float dx, float dy)
        {
            EnsureFigure();
            PointF start = new(_x, _y);
            _x += dx;
            _y += dy;
            _path.AddLine(start, new PointF(_x, _y));
        }

        private void CurveTo(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
        {
            EnsureFigure();
            PointF p0 = new(_x, _y);
            PointF p1 = new(_x + dx1, _y + dy1);
            PointF p2 = new(p1.X + dx2, p1.Y + dy2);
            PointF p3 = new(p2.X + dx3, p2.Y + dy3);
            _path.AddBezier(p0, p1, p2, p3);
            _x = p3.X;
            _y = p3.Y;
        }

        private void HvlLineTo(bool horizontalFirst)
        {
            bool horizontal = horizontalFirst;
            foreach (float value in _stack)
            {
                if (horizontal)
                    LineTo(value, 0f);
                else
                    LineTo(0f, value);
                horizontal = !horizontal;
            }

            _stack.Clear();
        }

        private void VhHvCurveTo(bool verticalFirst)
        {
            int i = 0;
            while (i + 3 < _stack.Count)
            {
                if (verticalFirst)
                {
                    float dy1 = _stack[i++];
                    float dx2 = _stack[i++];
                    float dy2 = _stack[i++];
                    float dx3 = _stack[i++];
                    CurveTo(0f, dy1, dx2, dy2, dx3, 0f);
                }
                else
                {
                    float dx1 = _stack[i++];
                    float dx2 = _stack[i++];
                    float dy2 = _stack[i++];
                    float dy3 = _stack[i++];
                    CurveTo(dx1, 0f, dx2, dy2, 0f, dy3);
                }

                // Type 1 charstrings use 4 operands per hvcurveto/vhcurveto segment.
                // If a producer stacked multiple segments before a single operator,
                // continue alternating conservatively rather than dropping the rest.
                verticalFirst = !verticalFirst;
            }

            _stack.Clear();
        }

        private void CallSubr(int depth)
        {
            if (_stack.Count == 0)
                return;

            int index = (int)_stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (index < 0 || index >= _subrs.Length || _subrs[index].Length == 0)
                return;

            Execute(_subrs[index], depth + 1);
        }

        private float PopOrZero()
        {
            if (_stack.Count == 0)
                return 0f;

            float value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        private static float ReadNumber(byte[] data, ref int pos, byte first)
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
    }

    private sealed class Type1GlyphData
    {
        public Type1GlyphData(
            GraphicsPath path,
            float width,
            float sideBearingX,
            float sideBearingY,
            IReadOnlyList<StemHint> horizontalStems,
            IReadOnlyList<StemHint> verticalStems)
        {
            Path = path;
            Width = width;
            SideBearingX = sideBearingX;
            SideBearingY = sideBearingY;
            HorizontalStems = horizontalStems.ToArray();
            VerticalStems = verticalStems.ToArray();
        }

        public GraphicsPath Path { get; }
        public float Width { get; }
        public float SideBearingX { get; }
        public float SideBearingY { get; }
        public IReadOnlyList<StemHint> HorizontalStems { get; }
        public IReadOnlyList<StemHint> VerticalStems { get; }

        public GraphicsPath ClonePath() => (GraphicsPath)Path.Clone();

        public GraphicsPath CloneHintedPath(Matrix transform)
        {
            if (Path.PointCount == 0)
                return (GraphicsPath)Path.Clone();

            PointF[] points = Path.PathPoints;
            byte[] types = Path.PathTypes;
            ApplyStemHints(points, transform, HorizontalStems, VerticalStems);
            return new GraphicsPath(points, types, Path.FillMode);
        }
    }

    private readonly record struct StemHint(float Position, float Width);
}
