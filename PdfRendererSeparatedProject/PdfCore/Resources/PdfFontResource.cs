using System.Text;

namespace PdfCore.Resources;

public sealed class PdfFontResource
{
    public string ResourceName { get; init; } = string.Empty;
    public string BaseFontName { get; init; } = "Helvetica";
    public int FirstChar { get; init; }
    public float[]? Widths { get; init; }
    public float MissingWidth { get; init; } = 600f;
    public bool IsIdentityH { get; init; }
    public IReadOnlyDictionary<int, string>? ToUnicodeMap { get; init; }
    public IReadOnlyDictionary<int, string>? EncodingMap { get; init; }
    public IReadOnlyDictionary<int, string>? GlyphNameMap { get; init; }
    public IReadOnlyDictionary<int, float>? CidWidths { get; init; }
    public byte[]? FontFileBytes { get; init; }
    public string? FontFileSubtype { get; init; }
    public bool PreferCidGlyphCodesForRendering { get; init; }


    public float GetGlyphWidth(char ch)
        => GetGlyphWidth((int)ch);

    public float GetGlyphWidth(int code)
    {
        if (TryGetExplicitGlyphWidth(code, out float explicitWidth))
            return explicitWidth;

        if (code <= char.MaxValue &&
            ToUnicodeMap != null &&
            TryGetWidthForDecodedUnicodeChar((char)code, out float decodedWidth))
            return decodedWidth;

        if (code <= char.MaxValue &&
            EncodingMap != null &&
            TryGetWidthForDecodedEncodingChar((char)code, out decodedWidth))
            return decodedWidth;

        if (code <= char.MaxValue && PdfCore.Text.PdfStandardFontMetrics.TryGetWidth(BaseFontName, (char)code, out float stdWidth))
            return stdWidth;

        return MissingWidth > 0 ? MissingWidth : 600f;
    }

    private bool TryGetExplicitGlyphWidth(int code, out float width)
    {
        if (CidWidths != null && CidWidths.TryGetValue(code, out width))
            return true;

        if (Widths != null)
        {
            int index = code - FirstChar;
            if (index >= 0 && index < Widths.Length)
            {
                width = Widths[index];
                return true;
            }
        }

        width = 0f;
        return false;
    }

    private bool TryGetWidthForDecodedUnicodeChar(char ch, out float width)
    {
        if (ToUnicodeMap == null)
        {
            width = 0f;
            return false;
        }

        foreach (KeyValuePair<int, string> item in ToUnicodeMap)
        {
            if (item.Value.Length == 1 &&
                item.Value[0] == ch &&
                TryGetExplicitGlyphWidth(item.Key, out width))
            {
                return true;
            }
        }

        width = 0f;
        return false;
    }

    private bool TryGetWidthForDecodedEncodingChar(char ch, out float width)
    {
        if (EncodingMap == null)
        {
            width = 0f;
            return false;
        }

        foreach (KeyValuePair<int, string> item in EncodingMap)
        {
            if (item.Value.Length == 1 &&
                item.Value[0] == ch &&
                TryGetExplicitGlyphWidth(item.Key, out width))
            {
                return true;
            }
        }

        width = 0f;
        return false;
    }

    public string DecodeTextBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (IsIdentityH)
            return DecodeIdentityH(bytes);

        if (ToUnicodeMap != null)
        {
            var mapped = new StringBuilder();
            foreach (byte value in bytes)
            {
                if (ToUnicodeMap.TryGetValue(value, out string? text))
                    mapped.Append(text);
                else
                    mapped.Append((char)value);
            }

            return mapped.ToString();
        }

        if (EncodingMap != null)
        {
            var mapped = new StringBuilder();
            foreach (byte value in bytes)
            {
                if (EncodingMap.TryGetValue(value, out string? text))
                    mapped.Append(text);
                else
                    mapped.Append((char)value);
            }

            return mapped.ToString();
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private string DecodeIdentityH(byte[] bytes)
    {
        var decoded = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 2)
        {
            int code = bytes[i] << 8;
            if (i + 1 < bytes.Length)
                code |= bytes[i + 1];

            if (ToUnicodeMap != null && ToUnicodeMap.TryGetValue(code, out string? text))
            {
                decoded.Append(text);
                continue;
            }

            if (IsValidUnicodeScalar(code))
                decoded.Append(char.ConvertFromUtf32(code));
        }

        return decoded.ToString();
    }

    private static bool IsValidUnicodeScalar(int codePoint)
    {
        return codePoint >= 0 &&
               codePoint <= 0x10FFFF &&
               (codePoint < 0xD800 || codePoint > 0xDFFF);
    }

    //public float GetGlyphWidth(char ch)
    //{
    //    int code = ch;
    //    if (Widths == null)
    //        return MissingWidth;

    //    int index = code - FirstChar;
    //    if (index >= 0 && index < Widths.Length)
    //        return Widths[index];

    //    return MissingWidth;
    //}
}
