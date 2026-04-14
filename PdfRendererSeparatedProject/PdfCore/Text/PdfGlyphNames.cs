using System.Globalization;
using System.Text;

namespace PdfCore.Text;

public static class PdfGlyphNames
{
    private static readonly IReadOnlyDictionary<string, string> Names = CreateNames();

    public static bool TryGetUnicode(string glyphName, out string text)
    {
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(glyphName))
            return false;

        string normalized = DecodeGlyphName(glyphName);
        int suffix = normalized.IndexOf('.');
        if (suffix > 0)
            normalized = normalized[..suffix];

        if (Names.TryGetValue(normalized, out string? mapped))
        {
            text = mapped;
            return true;
        }

        if (TryDecodeUniName(normalized, out text))
            return true;

        if (normalized.Length == 1)
        {
            text = normalized;
            return true;
        }

        return false;
    }

    public static Dictionary<int, string> CreateWinAnsiEncoding()
    {
        var map = new Dictionary<int, string>();
        for (int i = 32; i <= 126; i++)
            map[i] = ((char)i).ToString();

        map[0x80] = "\u20AC";
        map[0x82] = "\u201A";
        map[0x83] = "\u0192";
        map[0x84] = "\u201E";
        map[0x85] = "\u2026";
        map[0x86] = "\u2020";
        map[0x87] = "\u2021";
        map[0x88] = "\u02C6";
        map[0x89] = "\u2030";
        map[0x8A] = "\u0160";
        map[0x8B] = "\u2039";
        map[0x8C] = "\u0152";
        map[0x8E] = "\u017D";
        map[0x91] = "\u2018";
        map[0x92] = "\u2019";
        map[0x93] = "\u201C";
        map[0x94] = "\u201D";
        map[0x95] = "\u2022";
        map[0x96] = "\u2013";
        map[0x97] = "\u2014";
        map[0x98] = "\u02DC";
        map[0x99] = "\u2122";
        map[0x9A] = "\u0161";
        map[0x9B] = "\u203A";
        map[0x9C] = "\u0153";
        map[0x9E] = "\u017E";
        map[0x9F] = "\u0178";

        for (int i = 0xA0; i <= 0xFF; i++)
            map[i] = Encoding.Latin1.GetString(new[] { (byte)i });

        return map;
    }

    private static string DecodeGlyphName(string name)
    {
        var decoded = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '#' &&
                i + 2 < name.Length &&
                int.TryParse(name.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            {
                decoded.Append((char)value);
                i += 2;
                continue;
            }

            decoded.Append(name[i]);
        }

        return decoded.ToString();
    }

    private static bool TryDecodeUniName(string name, out string text)
    {
        text = string.Empty;

        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var decoded = new StringBuilder();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint) ||
                    !IsValidUnicodeScalar(codePoint))
                {
                    return false;
                }

                decoded.Append(char.ConvertFromUtf32(codePoint));
            }

            text = decoded.ToString();
            return text.Length > 0;
        }

        if (name.StartsWith("u", StringComparison.Ordinal) &&
            name.Length >= 5 &&
            name.Length <= 7 &&
            int.TryParse(name[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int singleCodePoint) &&
            IsValidUnicodeScalar(singleCodePoint))
        {
            text = char.ConvertFromUtf32(singleCodePoint);
            return true;
        }

        return false;
    }

    private static bool IsValidUnicodeScalar(int codePoint)
    {
        return codePoint >= 0 &&
               codePoint <= 0x10FFFF &&
               (codePoint < 0xD800 || codePoint > 0xDFFF);
    }

    private static IReadOnlyDictionary<string, string> CreateNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["space"] = " ",
            ["nbspace"] = "\u00A0",
            ["exclam"] = "!",
            ["quotedbl"] = "\"",
            ["numbersign"] = "#",
            ["dollar"] = "$",
            ["percent"] = "%",
            ["ampersand"] = "&",
            ["quotesingle"] = "'",
            ["parenleft"] = "(",
            ["parenright"] = ")",
            ["asterisk"] = "*",
            ["plus"] = "+",
            ["comma"] = ",",
            ["hyphen"] = "-",
            ["minus"] = "-",
            ["period"] = ".",
            ["slash"] = "/",
            ["colon"] = ":",
            ["semicolon"] = ";",
            ["less"] = "<",
            ["equal"] = "=",
            ["greater"] = ">",
            ["question"] = "?",
            ["at"] = "@",
            ["bracketleft"] = "[",
            ["backslash"] = "\\",
            ["bracketright"] = "]",
            ["asciicircum"] = "^",
            ["underscore"] = "_",
            ["quoteleft"] = "\u2018",
            ["quoteright"] = "\u2019",
            ["braceleft"] = "{",
            ["bar"] = "|",
            ["braceright"] = "}",
            ["asciitilde"] = "~",
            ["bullet"] = "\u2022",
            ["ellipsis"] = "\u2026",
            ["endash"] = "\u2013",
            ["emdash"] = "\u2014",
            ["quotedblleft"] = "\u201C",
            ["quotedblright"] = "\u201D",
            ["quotedblbase"] = "\u201E",
            ["guillemotleft"] = "\u00AB",
            ["guillemotright"] = "\u00BB",
            ["guilsinglleft"] = "\u2039",
            ["guilsinglright"] = "\u203A",
            ["periodcentered"] = "\u00B7",
            ["dotmath"] = ".",
            ["degree"] = "\u00B0",
            ["multiply"] = "\u00D7",
            ["divide"] = "\u00F7",
            ["plusminus"] = "\u00B1",
            ["lessequal"] = "\u2264",
            ["greaterequal"] = "\u2265",
            ["summation"] = "\u2211",
            ["Sigma"] = "\u03A3",
            ["nu"] = "\u03BD",
            ["rho"] = "\u03C1",
            ["ordmasculine"] = "\u00BA",
            ["parenlefttp"] = "(",
            ["parenleftex"] = "(",
            ["parenleftbt"] = "(",
            ["parenrighttp"] = ")",
            ["parenrightex"] = ")",
            ["parenrightbt"] = ")",
            ["onesans"] = "1",
            ["foursans"] = "4",
            ["fiveoclock"] = "\u25F4",
            ["tenoclock"] = "\u25F7",
            ["fi"] = "fi",
            ["fl"] = "fl",
            ["ff"] = "ff",
            ["ffi"] = "ffi",
            ["ffl"] = "ffl",
            ["Aogonek"] = "\u0104",
            ["aogonek"] = "\u0105",
            ["Cacute"] = "\u0106",
            ["cacute"] = "\u0107",
            ["Eogonek"] = "\u0118",
            ["eogonek"] = "\u0119",
            ["Lslash"] = "\u0141",
            ["lslash"] = "\u0142",
            ["Nacute"] = "\u0143",
            ["nacute"] = "\u0144",
            ["Oacute"] = "\u00D3",
            ["oacute"] = "\u00F3",
            ["Sacute"] = "\u015A",
            ["sacute"] = "\u015B",
            ["Zacute"] = "\u0179",
            ["zacute"] = "\u017A",
            ["Zdot"] = "\u017B",
            ["Zdotaccent"] = "\u017B",
            ["zdot"] = "\u017C",
            ["zdotaccent"] = "\u017C",
            ["adieresis"] = "\u00E4",
            ["udieresis"] = "\u00FC"
        };

        for (char c = 'A'; c <= 'Z'; c++)
            names[c.ToString()] = c.ToString();
        for (char c = 'a'; c <= 'z'; c++)
            names[c.ToString()] = c.ToString();

        string[] digits =
        {
            "zero",
            "one",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight",
            "nine"
        };

        for (int i = 0; i < digits.Length; i++)
            names[digits[i]] = i.ToString(CultureInfo.InvariantCulture);

        AddGreekAndMathMappings(names);
        AddLatinExtendedMappings(names);
        AddCommonBracketVariants(names);
        AddCyrillicAfiiMappings(names);

        return names;
    }

    private static void AddGreekAndMathMappings(Dictionary<string, string> names)
    {
        string[] upperGreekNames =
        {
            "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta",
            "Iota", "Kappa", "Lambda", "Mu", "Nu", "Xi", "Omicron", "Pi",
            "Rho", "Sigma", "Tau", "Upsilon", "Phi", "Chi", "Psi", "Omega"
        };

        string[] upperGreekValues =
        {
            "\u0391", "\u0392", "\u0393", "\u0394", "\u0395", "\u0396", "\u0397", "\u0398",
            "\u0399", "\u039A", "\u039B", "\u039C", "\u039D", "\u039E", "\u039F", "\u03A0",
            "\u03A1", "\u03A3", "\u03A4", "\u03A5", "\u03A6", "\u03A7", "\u03A8", "\u03A9"
        };

        for (int i = 0; i < upperGreekNames.Length; i++)
            names[upperGreekNames[i]] = upperGreekValues[i];

        names["alpha"] = "\u03B1";
        names["beta"] = "\u03B2";
        names["gamma"] = "\u03B3";
        names["delta"] = "\u03B4";
        names["epsilon"] = "\u03B5";
        names["varepsilon"] = "\u03B5";
        names["zeta"] = "\u03B6";
        names["eta"] = "\u03B7";
        names["theta"] = "\u03B8";
        names["vartheta"] = "\u03D1";
        names["iota"] = "\u03B9";
        names["kappa"] = "\u03BA";
        names["lambda"] = "\u03BB";
        names["mu"] = "\u03BC";
        names["xi"] = "\u03BE";
        names["omicron"] = "\u03BF";
        names["pi"] = "\u03C0";
        names["varpi"] = "\u03D6";
        names["sigma"] = "\u03C3";
        names["varsigma"] = "\u03C2";
        names["tau"] = "\u03C4";
        names["upsilon"] = "\u03C5";
        names["phi"] = "\u03C6";
        names["varphi"] = "\u03D5";
        names["chi"] = "\u03C7";
        names["psi"] = "\u03C8";
        names["omega"] = "\u03C9";

        names["partialdiff"] = "\u2202";
        names["gradient"] = "\u2207";
        names["product"] = "\u220F";
        names["integral"] = "\u222B";
        names["radical"] = "\u221A";
        names["radicalex"] = "\u221A";
        names["infinity"] = "\u221E";
        names["approxequal"] = "\u2248";
        names["notequal"] = "\u2260";
        names["equivalence"] = "\u2261";
        names["proportional"] = "\u221D";
        names["similar"] = "\u223C";
        names["intersection"] = "\u2229";
        names["union"] = "\u222A";
        names["element"] = "\u2208";
        names["notelement"] = "\u2209";
        names["propersubset"] = "\u2282";
        names["propersuperset"] = "\u2283";
        names["reflexsubset"] = "\u2286";
        names["reflexsuperset"] = "\u2287";
        names["logicaland"] = "\u2227";
        names["logicalor"] = "\u2228";
        names["therefore"] = "\u2234";
        names["angle"] = "\u2220";
        names["perpendicular"] = "\u27C2";
        names["emptyset"] = "\u2205";
        names["lozenge"] = "\u25CA";
        names["asteriskmath"] = "\u2217";
        names["circleplus"] = "\u2295";
        names["circlemultiply"] = "\u2297";
        names["lessmuch"] = "\u226A";
        names["muchless"] = "\u226A";
        names["greatermuch"] = "\u226B";
        names["muchgreater"] = "\u226B";
        names["arrowdblboth"] = "\u21D4";
        names["negationslash"] = "\u0338";
        names["openbullet"] = "\u25E6";
        names["prime"] = "\u2032";
        names["dprime"] = "\u2033";
        names["trprime"] = "\u2034";
        names["bardbl"] = "\u2016";
        names["vextendsingle"] = "|";
        names["braceex"] = "{";
        names["integraldisplay"] = "\u222B";
        names["radicalbig"] = "\u221A";
        names["radicalBig"] = "\u221A";
        names["radicalbigg"] = "\u221A";
        names["radicalBigg"] = "\u221A";
        names["planckover2pi1"] = "\u0127";
        names["phi1"] = "\u03D5";
        names["dotaccent"] = "\u02D9";
        names["dieresis"] = "\u00A8";
        names["arrowleft"] = "\u2190";
        names["arrowright"] = "\u2192";
        names["arrowup"] = "\u2191";
        names["arrowdown"] = "\u2193";
        names["arrowdblleft"] = "\u21D0";
        names["arrowdblright"] = "\u21D2";
        names["arrowdblup"] = "\u21D1";
        names["arrowdbldown"] = "\u21D3";
    }

    private static void AddLatinExtendedMappings(Dictionary<string, string> names)
    {
        names["Agrave"] = "\u00C0";
        names["Aacute"] = "\u00C1";
        names["Acircumflex"] = "\u00C2";
        names["Atilde"] = "\u00C3";
        names["Adieresis"] = "\u00C4";
        names["Aring"] = "\u00C5";
        names["AE"] = "\u00C6";
        names["Ccedilla"] = "\u00C7";
        names["Egrave"] = "\u00C8";
        names["Eacute"] = "\u00C9";
        names["Ecircumflex"] = "\u00CA";
        names["Edieresis"] = "\u00CB";
        names["Igrave"] = "\u00CC";
        names["Iacute"] = "\u00CD";
        names["Icircumflex"] = "\u00CE";
        names["Idieresis"] = "\u00CF";
        names["Eth"] = "\u00D0";
        names["Ntilde"] = "\u00D1";
        names["Ograve"] = "\u00D2";
        names["Ocircumflex"] = "\u00D4";
        names["Otilde"] = "\u00D5";
        names["Odieresis"] = "\u00D6";
        names["Oslash"] = "\u00D8";
        names["Ugrave"] = "\u00D9";
        names["Uacute"] = "\u00DA";
        names["Ucircumflex"] = "\u00DB";
        names["Udieresis"] = "\u00DC";
        names["Yacute"] = "\u00DD";
        names["Thorn"] = "\u00DE";
        names["germandbls"] = "\u00DF";
        names["agrave"] = "\u00E0";
        names["aacute"] = "\u00E1";
        names["acircumflex"] = "\u00E2";
        names["atilde"] = "\u00E3";
        names["aring"] = "\u00E5";
        names["ae"] = "\u00E6";
        names["ccedilla"] = "\u00E7";
        names["egrave"] = "\u00E8";
        names["eacute"] = "\u00E9";
        names["ecircumflex"] = "\u00EA";
        names["edieresis"] = "\u00EB";
        names["igrave"] = "\u00EC";
        names["iacute"] = "\u00ED";
        names["icircumflex"] = "\u00EE";
        names["idieresis"] = "\u00EF";
        names["eth"] = "\u00F0";
        names["ntilde"] = "\u00F1";
        names["ograve"] = "\u00F2";
        names["ocircumflex"] = "\u00F4";
        names["otilde"] = "\u00F5";
        names["odieresis"] = "\u00F6";
        names["oslash"] = "\u00F8";
        names["ugrave"] = "\u00F9";
        names["uacute"] = "\u00FA";
        names["ucircumflex"] = "\u00FB";
        names["yacute"] = "\u00FD";
        names["thorn"] = "\u00FE";
        names["ydieresis"] = "\u00FF";

        names["Abreve"] = "\u0102";
        names["abreve"] = "\u0103";
        names["Ccaron"] = "\u010C";
        names["ccaron"] = "\u010D";
        names["Dcaron"] = "\u010E";
        names["dcaron"] = "\u010F";
        names["Dcroat"] = "\u0110";
        names["dcroat"] = "\u0111";
        names["Ecaron"] = "\u011A";
        names["ecaron"] = "\u011B";
        names["Gbreve"] = "\u011E";
        names["gbreve"] = "\u011F";
        names["Idotaccent"] = "\u0130";
        names["dotlessi"] = "\u0131";
        names["Lacute"] = "\u0139";
        names["lacute"] = "\u013A";
        names["Lcaron"] = "\u013D";
        names["lcaron"] = "\u013E";
        names["Ncaron"] = "\u0147";
        names["ncaron"] = "\u0148";
        names["OE"] = "\u0152";
        names["oe"] = "\u0153";
        names["Racute"] = "\u0154";
        names["racute"] = "\u0155";
        names["Rcaron"] = "\u0158";
        names["rcaron"] = "\u0159";
        names["Scaron"] = "\u0160";
        names["scaron"] = "\u0161";
        names["Tcaron"] = "\u0164";
        names["tcaron"] = "\u0165";
        names["Uring"] = "\u016E";
        names["uring"] = "\u016F";
        names["Uhungarumlaut"] = "\u0170";
        names["uhungarumlaut"] = "\u0171";
        names["Ydieresis"] = "\u0178";
        names["Zcaron"] = "\u017D";
        names["zcaron"] = "\u017E";
        names["Scommaaccent"] = "\u0218";
        names["scommaaccent"] = "\u0219";
        names["Tcommaaccent"] = "\u021A";
        names["tcommaaccent"] = "\u021B";
    }

    private static void AddCommonBracketVariants(Dictionary<string, string> names)
    {
        string[] leftParens = { "parenleftbig", "parenleftBig", "parenleftbigg", "parenleftBigg" };
        string[] rightParens = { "parenrightbig", "parenrightBig", "parenrightbigg", "parenrightBigg" };
        string[] leftBrackets = { "bracketlefttp", "bracketleftex", "bracketleftbt", "bracketleftbig", "bracketleftBig", "bracketleftbigg", "bracketleftBigg" };
        string[] rightBrackets = { "bracketrighttp", "bracketrightex", "bracketrightbt", "bracketrightbig", "bracketrightBig", "bracketrightbigg", "bracketrightBigg" };
        string[] leftBraces = { "bracelefttp", "braceleftmid", "braceleftbt", "braceleftex", "braceleftbig", "braceleftBig", "braceleftbigg", "braceleftBigg" };
        string[] rightBraces = { "bracerighttp", "bracerightmid", "bracerightbt", "bracerightex", "bracerightbig", "bracerightBig", "bracerightbigg", "bracerightBigg" };

        foreach (string name in leftParens)
            names[name] = "(";
        foreach (string name in rightParens)
            names[name] = ")";
        foreach (string name in leftBrackets)
            names[name] = "[";
        foreach (string name in rightBrackets)
            names[name] = "]";
        foreach (string name in leftBraces)
            names[name] = "{";
        foreach (string name in rightBraces)
            names[name] = "}";
    }

    private static void AddCyrillicAfiiMappings(Dictionary<string, string> names)
    {
        string[] upper =
        {
            "\u0410", // А
            "\u0411", // Б
            "\u0412", // В
            "\u0413", // Г
            "\u0414", // Д
            "\u0415", // Е
            "\u0401", // Ё
            "\u0416", // Ж
            "\u0417", // З
            "\u0418", // И
            "\u0419", // Й
            "\u041A", // К
            "\u041B", // Л
            "\u041C", // М
            "\u041D", // Н
            "\u041E", // О
            "\u041F", // П
            "\u0420", // Р
            "\u0421", // С
            "\u0422", // Т
            "\u0423", // У
            "\u0424", // Ф
            "\u0425", // Х
            "\u0426", // Ц
            "\u0427", // Ч
            "\u0428", // Ш
            "\u0429", // Щ
            "\u042A", // Ъ
            "\u042B", // Ы
            "\u042C", // Ь
            "\u042D", // Э
            "\u042E", // Ю
            "\u042F"  // Я
        };

        string[] lower =
        {
            "\u0430", // а
            "\u0431", // б
            "\u0432", // в
            "\u0433", // г
            "\u0434", // д
            "\u0435", // е
            "\u0451", // ё
            "\u0436", // ж
            "\u0437", // з
            "\u0438", // и
            "\u0439", // й
            "\u043A", // к
            "\u043B", // л
            "\u043C", // м
            "\u043D", // н
            "\u043E", // о
            "\u043F", // п
            "\u0440", // р
            "\u0441", // с
            "\u0442", // т
            "\u0443", // у
            "\u0444", // ф
            "\u0445", // х
            "\u0446", // ц
            "\u0447", // ч
            "\u0448", // ш
            "\u0449", // щ
            "\u044A", // ъ
            "\u044B", // ы
            "\u044C", // ь
            "\u044D", // э
            "\u044E", // ю
            "\u044F"  // я
        };

        for (int i = 0; i < upper.Length; i++)
            names[$"afii{10017 + i}"] = upper[i];

        for (int i = 0; i < lower.Length; i++)
            names[$"afii{10065 + i}"] = lower[i];
    }
}
