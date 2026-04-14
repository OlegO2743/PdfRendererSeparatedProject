using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace PdfCore.Text;

internal sealed class PdfCffFont
{
    private readonly byte[][] _charStrings;
    private readonly byte[][] _globalSubrs;
    private readonly byte[][] _localSubrs;
    private readonly Dictionary<string, int> _glyphIdsByName;
    private readonly Dictionary<int, int> _glyphIdsByCid;
    private readonly float[] _fontMatrix;

    private PdfCffFont(
        byte[][] charStrings,
        byte[][] globalSubrs,
        byte[][] localSubrs,
        Dictionary<string, int> glyphIdsByName,
        Dictionary<int, int> glyphIdsByCid,
        float[] fontMatrix)
    {
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _localSubrs = localSubrs;
        _glyphIdsByName = glyphIdsByName;
        _glyphIdsByCid = glyphIdsByCid;
        _fontMatrix = fontMatrix;
    }

    public int UnitsPerEm => 1000;
    public float MatrixA => _fontMatrix[0];
    public float MatrixB => _fontMatrix[1];
    public float MatrixC => _fontMatrix[2];
    public float MatrixD => _fontMatrix[3];
    public float MatrixE => _fontMatrix[4];
    public float MatrixF => _fontMatrix[5];

    public static bool TryCreate(byte[] bytes, out PdfCffFont? font)
    {
        try
        {
            if (bytes.Length < 4)
            {
                font = null;
                return false;
            }

            int offset = bytes[2]; // header size
            _ = ReadIndex(bytes, ref offset); // Name INDEX
            byte[][] topDictIndex = ReadIndex(bytes, ref offset);
            byte[][] stringIndex = ReadIndex(bytes, ref offset);
            byte[][] globalSubrs = ReadIndex(bytes, ref offset);

            if (topDictIndex.Length == 0)
            {
                font = null;
                return false;
            }

            Dictionary<int, List<double>> topDict = ReadDict(topDictIndex[0], 0, topDictIndex[0].Length);
            if (!TryGetSingle(topDict, 17, out double charStringsOffsetValue))
            {
                font = null;
                return false;
            }

            int charStringsOffset = (int)charStringsOffsetValue;
            if (charStringsOffset <= 0 || charStringsOffset >= bytes.Length)
            {
                font = null;
                return false;
            }

            int charStringsReadOffset = charStringsOffset;
            byte[][] charStrings = ReadIndex(bytes, ref charStringsReadOffset);
            bool isCidKeyed = topDict.ContainsKey(1230); // ROS (12 30)
            Dictionary<string, int> glyphIdsByName = new(StringComparer.Ordinal);
            Dictionary<int, int> glyphIdsByCid = new();
            if (topDict.TryGetValue(15, out List<double>? charsetValues) && charsetValues.Count > 0)
            {
                int charsetOffset = (int)charsetValues[^1];
                if (isCidKeyed)
                    glyphIdsByCid = ReadCidCharset(bytes, charsetOffset, charStrings.Length);
                else
                    glyphIdsByName = ReadCharset(bytes, charsetOffset, charStrings.Length, stringIndex);
            }

            float[] fontMatrix = ReadFontMatrix(topDict);
            byte[][] localSubrs = Array.Empty<byte[]>();

            if (topDict.TryGetValue(18, out List<double>? privateValues) && privateValues.Count >= 2)
            {
                int privateSize = (int)privateValues[0];
                int privateOffset = (int)privateValues[1];
                if (privateSize > 0 &&
                    privateOffset >= 0 &&
                    privateOffset + privateSize <= bytes.Length)
                {
                    Dictionary<int, List<double>> privateDict = ReadDict(bytes, privateOffset, privateSize);
                    if (TryGetSingle(privateDict, 19, out double subrsRelativeOffsetValue))
                    {
                        int localSubrsOffset = privateOffset + (int)subrsRelativeOffsetValue;
                        if (localSubrsOffset >= 0 && localSubrsOffset < bytes.Length)
                        {
                            int localSubrsReadOffset = localSubrsOffset;
                            localSubrs = ReadIndex(bytes, ref localSubrsReadOffset);
                        }
                    }
                }
            }

            font = new PdfCffFont(charStrings, globalSubrs, localSubrs, glyphIdsByName, glyphIdsByCid, fontMatrix);
            return charStrings.Length > 0;
        }
        catch
        {
            font = null;
            return false;
        }
    }

    public bool TryMapCharacterCode(int code, out int glyphId)
    {
        if (_glyphIdsByCid.TryGetValue(code, out glyphId))
            return glyphId > 0 && glyphId < _charStrings.Length;

        // Simple embedded Type1C subset fonts in PDFs normally keep code 1 -> GID 1.
        // When this is not true, callers fall back to the regular text path.
        glyphId = code;
        return glyphId > 0 && glyphId < _charStrings.Length;
    }

    public bool TryMapGlyphName(string glyphName, out int glyphId)
        => _glyphIdsByName.TryGetValue(glyphName, out glyphId) &&
           glyphId > 0 &&
           glyphId < _charStrings.Length;

    public void AddGlyphPath(GraphicsPath target, int glyphId, Matrix transform)
    {
        if (glyphId <= 0 || glyphId >= _charStrings.Length)
            return;

        using var glyphPath = new GraphicsPath(FillMode.Winding);
        var interpreter = new Type2Interpreter(glyphPath, _localSubrs, _globalSubrs);
        interpreter.Execute(_charStrings[glyphId], 0);

        if (glyphPath.PointCount == 0)
            return;

        glyphPath.Transform(transform);
        target.AddPath(glyphPath, false);
    }

    private static byte[][] ReadIndex(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length)
            return Array.Empty<byte[]>();

        int count = ReadUInt16(data, offset);
        offset += 2;
        if (count == 0)
            return Array.Empty<byte[]>();

        if (offset >= data.Length)
            return Array.Empty<byte[]>();

        int offSize = data[offset++];
        if (offSize < 1 || offSize > 4)
            return Array.Empty<byte[]>();

        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            if (offset + offSize > data.Length)
                return Array.Empty<byte[]>();

            offsets[i] = ReadOffset(data, offset, offSize);
            offset += offSize;
        }

        int dataStart = offset;
        var items = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int start = dataStart + offsets[i] - 1;
            int end = dataStart + offsets[i + 1] - 1;
            if (start < dataStart || end < start || end > data.Length)
                items[i] = Array.Empty<byte>();
            else
            {
                int length = end - start;
                items[i] = new byte[length];
                Array.Copy(data, start, items[i], 0, length);
            }
        }

        offset = dataStart + offsets[count] - 1;
        return items;
    }

    private static Dictionary<int, List<double>> ReadDict(byte[] data, int start, int length)
    {
        var dict = new Dictionary<int, List<double>>();
        var stack = new List<double>();
        int pos = start;
        int end = Math.Min(data.Length, start + length);

        while (pos < end)
        {
            byte b = data[pos++];
            if (IsDictNumberStart(b))
            {
                stack.Add(ReadDictNumber(data, ref pos, b));
                continue;
            }

            int op = b;
            if (b == 12 && pos < end)
                op = 1200 + data[pos++];

            dict[op] = new List<double>(stack);
            stack.Clear();
        }

        return dict;
    }

    private static Dictionary<string, int> ReadCharset(
        byte[] data,
        int charsetOffset,
        int glyphCount,
        byte[][] stringIndex)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (glyphCount <= 1 || charsetOffset <= 2 || charsetOffset >= data.Length)
            return map;

        int pos = charsetOffset;
        int format = data[pos++];
        int gid = 1;

        if (format == 0)
        {
            while (gid < glyphCount && pos + 1 < data.Length)
                AddSid(map, gid++, ReadUInt16(data, Advance(ref pos, 2)), stringIndex);
        }
        else if (format == 1)
        {
            while (gid < glyphCount && pos + 2 < data.Length)
            {
                int firstSid = ReadUInt16(data, pos);
                pos += 2;
                int left = data[pos++];
                for (int i = 0; i <= left && gid < glyphCount; i++)
                    AddSid(map, gid++, firstSid + i, stringIndex);
            }
        }
        else if (format == 2)
        {
            while (gid < glyphCount && pos + 3 < data.Length)
            {
                int firstSid = ReadUInt16(data, pos);
                pos += 2;
                int left = ReadUInt16(data, pos);
                pos += 2;
                for (int i = 0; i <= left && gid < glyphCount; i++)
                    AddSid(map, gid++, firstSid + i, stringIndex);
            }
        }

        return map;

        static int Advance(ref int position, int count)
        {
            int old = position;
            position += count;
            return old;
        }
    }

    private static Dictionary<int, int> ReadCidCharset(
        byte[] data,
        int charsetOffset,
        int glyphCount)
    {
        var map = new Dictionary<int, int>();
        if (glyphCount <= 1 || charsetOffset <= 2 || charsetOffset >= data.Length)
            return map;

        int pos = charsetOffset;
        int format = data[pos++];
        int gid = 1;

        if (format == 0)
        {
            while (gid < glyphCount && pos + 1 < data.Length)
                map[ReadUInt16(data, Advance(ref pos, 2))] = gid++;
        }
        else if (format == 1)
        {
            while (gid < glyphCount && pos + 2 < data.Length)
            {
                int firstCid = ReadUInt16(data, pos);
                pos += 2;
                int left = data[pos++];
                for (int i = 0; i <= left && gid < glyphCount; i++)
                    map[firstCid + i] = gid++;
            }
        }
        else if (format == 2)
        {
            while (gid < glyphCount && pos + 3 < data.Length)
            {
                int firstCid = ReadUInt16(data, pos);
                pos += 2;
                int left = ReadUInt16(data, pos);
                pos += 2;
                for (int i = 0; i <= left && gid < glyphCount; i++)
                    map[firstCid + i] = gid++;
            }
        }

        return map;

        static int Advance(ref int position, int count)
        {
            int old = position;
            position += count;
            return old;
        }
    }

    private static void AddSid(Dictionary<string, int> map, int glyphId, int sid, byte[][] stringIndex)
    {
        string? name = ResolveSid(sid, stringIndex);
        if (!string.IsNullOrEmpty(name))
            map[name] = glyphId;
    }

    private static string? ResolveSid(int sid, byte[][] stringIndex)
    {
        if (sid >= 0 && sid < StandardStrings.Length)
            return StandardStrings[sid];

        int stringIndexOffset = sid - StandardStrings.Length;
        if (stringIndexOffset >= 0 && stringIndexOffset < stringIndex.Length)
            return Encoding.ASCII.GetString(stringIndex[stringIndexOffset]);

        return null;
    }

    private static bool TryGetSingle(Dictionary<int, List<double>> dict, int op, out double value)
    {
        if (dict.TryGetValue(op, out List<double>? values) && values.Count > 0)
        {
            value = values[^1];
            return true;
        }

        value = 0;
        return false;
    }

    private static float[] ReadFontMatrix(Dictionary<int, List<double>> topDict)
    {
        if (topDict.TryGetValue(1207, out List<double>? values) && values.Count >= 6)
        {
            return
            [
                (float)values[^6],
                (float)values[^5],
                (float)values[^4],
                (float)values[^3],
                (float)values[^2],
                (float)values[^1]
            ];
        }

        return [0.001f, 0f, 0f, 0.001f, 0f, 0f];
    }

    private static bool IsDictNumberStart(byte b)
        => b == 28 || b == 29 || b == 30 || b == 255 || b >= 32;

    private static double ReadDictNumber(byte[] data, ref int pos, byte first)
    {
        if (first >= 32 && first <= 246)
            return first - 139;

        if (first >= 247 && first <= 250 && pos < data.Length)
            return (first - 247) * 256 + data[pos++] + 108;

        if (first >= 251 && first <= 254 && pos < data.Length)
            return -((first - 251) * 256 + data[pos++] + 108);

        if (first == 28 && pos + 1 < data.Length)
        {
            short value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos, 2));
            pos += 2;
            return value;
        }

        if (first == 29 && pos + 3 < data.Length)
        {
            int value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
            pos += 4;
            return value;
        }

        if (first == 30)
            return ReadRealNumber(data, ref pos);

        if (first == 255 && pos + 3 < data.Length)
        {
            int value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
            pos += 4;
            return value / 65536.0;
        }

        return 0;
    }

    private static double ReadRealNumber(byte[] data, ref int pos)
    {
        var chars = new List<char>();
        while (pos < data.Length)
        {
            byte b = data[pos++];
            if (!AppendRealNibble(chars, b >> 4) || !AppendRealNibble(chars, b & 0x0F))
                break;
        }

        return double.TryParse(
            new string(chars.ToArray()),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : 0;
    }

    private static bool AppendRealNibble(List<char> chars, int nibble)
    {
        if (nibble <= 9)
            chars.Add((char)('0' + nibble));
        else if (nibble == 0xA)
            chars.Add('.');
        else if (nibble == 0xB)
            chars.Add('E');
        else if (nibble == 0xC)
        {
            chars.Add('E');
            chars.Add('-');
        }
        else if (nibble == 0xE)
            chars.Add('-');
        else if (nibble == 0xF)
            return false;

        return true;
    }

    private static int ReadOffset(byte[] data, int offset, int size)
    {
        int value = 0;
        for (int i = 0; i < size; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }

    private static int ReadUInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static readonly string?[] StandardStrings = CreateStandardStrings();

    private static string?[] CreateStandardStrings()
    {
        return
        [
            ".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
            "quoteright", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period",
            "slash", "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D",
            "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T",
            "U", "V", "W", "X", "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum",
            "underscore", "quoteleft", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k",
            "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
            "braceleft", "bar", "braceright", "asciitilde", "exclamdown", "cent", "sterling",
            "fraction", "yen", "florin", "section", "currency", "quotesingle", "quotedblleft",
            "guillemotleft", "guilsinglleft", "guilsinglright", "fi", "fl", "endash", "dagger",
            "daggerdbl", "periodcentered", "paragraph", "bullet", "quotesinglbase", "quotedblbase",
            "quotedblright", "guillemotright", "ellipsis", "perthousand", "questiondown", "grave",
            "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis", "ring",
            "cedilla", "hungarumlaut", "ogonek", "caron", "emdash", "AE", "ordfeminine", "Lslash",
            "Oslash", "OE", "ordmasculine", "ae", "dotlessi", "lslash", "oslash", "oe", "germandbls",
            "onesuperior", "logicalnot", "mu", "trademark", "Eth", "onehalf", "plusminus", "Thorn",
            "onequarter", "divide", "brokenbar", "degree", "thorn", "threequarters", "twosuperior", "registered",
            "minus", "eth", "multiply", "threesuperior", "copyright", "Aacute", "Acircumflex", "Adieresis",
            "Agrave", "Aring", "Atilde", "Ccedilla", "Eacute", "Ecircumflex", "Edieresis", "Egrave",
            "Iacute", "Icircumflex", "Idieresis", "Igrave", "Ntilde", "Oacute", "Ocircumflex", "Odieresis",
            "Ograve", "Otilde", "Scaron", "Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute",
            "Ydieresis", "Zcaron", "aacute", "acircumflex", "adieresis", "agrave", "aring", "atilde",
            "ccedilla", "eacute", "ecircumflex", "edieresis", "egrave", "iacute", "icircumflex", "idieresis",
            "igrave", "ntilde", "oacute", "ocircumflex", "odieresis", "ograve", "otilde", "scaron",
            "uacute", "ucircumflex", "udieresis", "ugrave", "yacute", "ydieresis", "zcaron", "exclamsmall",
            "Hungarumlautsmall", "dollaroldstyle", "dollarsuperior", "ampersandsmall", "Acutesmall", "parenleftsuperior", "parenrightsuperior", "twodotenleader",
            "onedotenleader", "zerooldstyle", "oneoldstyle", "twooldstyle", "threeoldstyle", "fouroldstyle", "fiveoldstyle", "sixoldstyle",
            "sevenoldstyle", "eightoldstyle", "nineoldstyle", "commasuperior", "threequartersemdash", "periodsuperior", "questionsmall", "asuperior",
            "bsuperior", "centsuperior", "dsuperior", "esuperior", "isuperior", "lsuperior", "msuperior", "nsuperior",
            "osuperior", "rsuperior", "ssuperior", "tsuperior", "ff", "ffi", "ffl", "parenleftinferior",
            "parenrightinferior", "Circumflexsmall", "hyphensuperior", "Gravesmall", "Asmall", "Bsmall", "Csmall", "Dsmall",
            "Esmall", "Fsmall", "Gsmall", "Hsmall", "Ismall", "Jsmall", "Ksmall", "Lsmall",
            "Msmall", "Nsmall", "Osmall", "Psmall", "Qsmall", "Rsmall", "Ssmall", "Tsmall",
            "Usmall", "Vsmall", "Wsmall", "Xsmall", "Ysmall", "Zsmall", "colonmonetary", "onefitted",
            "rupiah", "Tildesmall", "exclamdownsmall", "centoldstyle", "Lslashsmall", "Scaronsmall", "Zcaronsmall", "Dieresissmall",
            "Brevesmall", "Caronsmall", "Dotaccentsmall", "Macronsmall", "figuredash", "hypheninferior", "Ogoneksmall", "Ringsmall",
            "Cedillasmall", "questiondownsmall", "oneeighth", "threeeighths", "fiveeighths", "seveneighths", "onethird", "twothirds",
            "zerosuperior", "foursuperior", "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior", "ninesuperior", "zeroinferior",
            "oneinferior", "twoinferior", "threeinferior", "fourinferior", "fiveinferior", "sixinferior", "seveninferior", "eightinferior",
            "nineinferior", "centinferior", "dollarinferior", "periodinferior", "commainferior", "Agravesmall", "Aacutesmall", "Acircumflexsmall",
            "Atildesmall", "Adieresissmall", "Aringsmall", "AEsmall", "Ccedillasmall", "Egravesmall", "Eacutesmall", "Ecircumflexsmall",
            "Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall", "Idieresissmall", "Ethsmall", "Ntildesmall", "Ogravesmall",
            "Oacutesmall", "Ocircumflexsmall", "Otildesmall", "Odieresissmall", "OEsmall", "Oslashsmall", "Ugravesmall", "Uacutesmall",
            "Ucircumflexsmall", "Udieresissmall", "Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", "001.001", "001.002",
            "001.003", "Black", "Bold", "Book", "Light", "Medium", "Regular", "Roman", "Semibold",
        ];
    }

    private sealed class Type2Interpreter
    {
        private readonly GraphicsPath _path;
        private readonly byte[][] _localSubrs;
        private readonly byte[][] _globalSubrs;
        private readonly int _localBias;
        private readonly int _globalBias;
        private readonly List<float> _stack = new();

        private float _x;
        private float _y;
        private int _stemCount;

        public Type2Interpreter(GraphicsPath path, byte[][] localSubrs, byte[][] globalSubrs)
        {
            _path = path;
            _localSubrs = localSubrs;
            _globalSubrs = globalSubrs;
            _localBias = GetSubrBias(localSubrs.Length);
            _globalBias = GetSubrBias(globalSubrs.Length);
        }

        public bool Execute(byte[] data, int depth)
        {
            if (depth > 12)
                return true;

            int pos = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                if (b == 28 || b == 255 || b >= 32)
                {
                    _stack.Add(ReadCharStringNumber(data, ref pos, b));
                    continue;
                }

                int op = b;
                if (b == 12 && pos < data.Length)
                    op = 1200 + data[pos++];

                if (!ExecuteOperator(op, data, ref pos, depth))
                    return false;
            }

            return true;
        }

        private bool ExecuteOperator(int op, byte[] data, ref int pos, int depth)
        {
            switch (op)
            {
                case 1:  // hstem
                case 3:  // vstem
                case 18: // hstemhm
                case 23: // vstemhm
                    _stemCount += _stack.Count / 2;
                    _stack.Clear();
                    return true;

                case 4: // vmoveto
                    RemoveWidthIfPresent(1);
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

                case 10: // callsubr
                    CallSubr(_localSubrs, _localBias, depth);
                    return true;

                case 11: // return
                    _stack.Clear();
                    return false;

                case 14: // endchar
                    _stack.Clear();
                    return false;

                case 19: // hintmask
                case 20: // cntrmask
                    _stemCount += _stack.Count / 2;
                    _stack.Clear();
                    pos = Math.Min(data.Length, pos + ((_stemCount + 7) / 8));
                    return true;

                case 21: // rmoveto
                    RemoveWidthIfPresent(2);
                    if (_stack.Count >= 2)
                        MoveTo(_stack[^2], _stack[^1]);
                    _stack.Clear();
                    return true;

                case 22: // hmoveto
                    RemoveWidthIfPresent(1);
                    MoveTo(PopOrZero(), 0f);
                    _stack.Clear();
                    return true;

                case 24: // rcurveline
                    CurveLine();
                    return true;

                case 25: // rlinecurve
                    LineCurve();
                    return true;

                case 26: // vvcurveto
                    VvCurveTo();
                    return true;

                case 27: // hhcurveto
                    HhCurveTo();
                    return true;

                case 29: // callgsubr
                    CallSubr(_globalSubrs, _globalBias, depth);
                    return true;

                case 30: // vhcurveto
                    VhHvCurveTo(verticalFirst: true);
                    return true;

                case 31: // hvcurveto
                    VhHvCurveTo(verticalFirst: false);
                    return true;

                case 1234: // hflex
                    HFlex();
                    return true;

                case 1235: // flex
                    Flex();
                    return true;

                case 1236: // hflex1
                    HFlex1();
                    return true;

                case 1237: // flex1
                    Flex1();
                    return true;

                default:
                    _stack.Clear();
                    return true;
            }
        }

        private void MoveTo(float dx, float dy)
        {
            _x += dx;
            _y += dy;
            _path.StartFigure();
        }

        private void LineTo(float dx, float dy)
        {
            PointF start = new(_x, _y);
            _x += dx;
            _y += dy;
            _path.AddLine(start, new PointF(_x, _y));
        }

        private void CurveTo(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
        {
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

        private void CurveLine()
        {
            int i = 0;
            for (; i + 7 < _stack.Count; i += 6)
                CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);

            if (i + 1 < _stack.Count)
                LineTo(_stack[i], _stack[i + 1]);

            _stack.Clear();
        }

        private void LineCurve()
        {
            int curveStart = Math.Max(0, _stack.Count - 6);
            for (int i = 0; i + 1 < curveStart; i += 2)
                LineTo(_stack[i], _stack[i + 1]);

            if (curveStart + 5 < _stack.Count)
                CurveTo(
                    _stack[curveStart],
                    _stack[curveStart + 1],
                    _stack[curveStart + 2],
                    _stack[curveStart + 3],
                    _stack[curveStart + 4],
                    _stack[curveStart + 5]);

            _stack.Clear();
        }

        private void VvCurveTo()
        {
            int i = 0;
            float dx1 = 0f;
            if ((_stack.Count & 1) != 0)
                dx1 = _stack[i++];

            for (; i + 3 < _stack.Count; i += 4)
            {
                CurveTo(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0f, _stack[i + 3]);
                dx1 = 0f;
            }

            _stack.Clear();
        }

        private void HhCurveTo()
        {
            int i = 0;
            float dy1 = 0f;
            if ((_stack.Count & 1) != 0)
                dy1 = _stack[i++];

            for (; i + 3 < _stack.Count; i += 4)
            {
                CurveTo(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0f);
                dy1 = 0f;
            }

            _stack.Clear();
        }

        private void VhHvCurveTo(bool verticalFirst)
        {
            int i = 0;
            bool vertical = verticalFirst;
            while (i + 3 < _stack.Count)
            {
                int remainingAfterFour = _stack.Count - (i + 4);
                if (vertical)
                {
                    float dy1 = _stack[i++];
                    float dx2 = _stack[i++];
                    float dy2 = _stack[i++];
                    float dx3 = _stack[i++];
                    float dy3 = remainingAfterFour == 1 ? _stack[i++] : 0f;
                    CurveTo(0f, dy1, dx2, dy2, dx3, dy3);
                }
                else
                {
                    float dx1 = _stack[i++];
                    float dx2 = _stack[i++];
                    float dy2 = _stack[i++];
                    float dy3 = _stack[i++];
                    float dx3 = remainingAfterFour == 1 ? _stack[i++] : 0f;
                    CurveTo(dx1, 0f, dx2, dy2, dx3, dy3);
                }

                vertical = !vertical;
            }

            _stack.Clear();
        }

        private void HFlex()
        {
            if (_stack.Count < 7)
            {
                _stack.Clear();
                return;
            }

            CurveTo(_stack[0], 0f, _stack[1], _stack[2], _stack[3], 0f);
            CurveTo(_stack[4], 0f, _stack[5], -_stack[2], _stack[6], 0f);
            _stack.Clear();
        }

        private void Flex()
        {
            if (_stack.Count < 12)
            {
                _stack.Clear();
                return;
            }

            CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
            CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
            _stack.Clear();
        }

        private void HFlex1()
        {
            if (_stack.Count < 9)
            {
                _stack.Clear();
                return;
            }

            CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0f);
            CurveTo(_stack[5], 0f, _stack[6], _stack[7], _stack[8], 0f);
            _stack.Clear();
        }

        private void Flex1()
        {
            if (_stack.Count < 11)
            {
                _stack.Clear();
                return;
            }

            float dx = 0f;
            float dy = 0f;
            for (int i = 0; i < 10; i += 2)
            {
                dx += _stack[i];
                dy += _stack[i + 1];
            }

            float dx6 = Math.Abs(dx) > Math.Abs(dy) ? _stack[10] : 0f;
            float dy6 = Math.Abs(dx) > Math.Abs(dy) ? 0f : _stack[10];
            CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
            CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], dx6, dy6);
            _stack.Clear();
        }

        private void CallSubr(byte[][] subrs, int bias, int depth)
        {
            if (_stack.Count == 0)
                return;

            int index = (int)_stack[^1] + bias;
            _stack.RemoveAt(_stack.Count - 1);
            if (index < 0 || index >= subrs.Length)
                return;

            Execute(subrs[index], depth + 1);
        }

        private void RemoveWidthIfPresent(int expectedArgs)
        {
            if (_stack.Count > expectedArgs)
                _stack.RemoveAt(0);
        }

        private float PopOrZero()
        {
            if (_stack.Count == 0)
                return 0f;

            float value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        private static float ReadCharStringNumber(byte[] data, ref int pos, byte first)
        {
            if (first >= 32 && first <= 246)
                return first - 139;

            if (first >= 247 && first <= 250 && pos < data.Length)
                return (first - 247) * 256 + data[pos++] + 108;

            if (first >= 251 && first <= 254 && pos < data.Length)
                return -((first - 251) * 256 + data[pos++] + 108);

            if (first == 28 && pos + 1 < data.Length)
            {
                short value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos, 2));
                pos += 2;
                return value;
            }

            if (first == 255 && pos + 3 < data.Length)
            {
                int value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
                pos += 4;
                return value / 65536f;
            }

            return 0f;
        }

        private static int GetSubrBias(int count)
            => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
    }
}
