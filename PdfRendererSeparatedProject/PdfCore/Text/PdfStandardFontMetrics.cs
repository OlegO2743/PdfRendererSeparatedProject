using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PdfCore.Text;

public static class PdfStandardFontMetrics
{
    public static bool TryGetWidth(string baseFontName, char ch, out float width)
    {
        width = baseFontName switch
        {
            "Courier" => 600f,
            "Courier-Bold" => 600f,
            _ => 0f
        };

        if (width > 0f)
            return true;

        // Временный phase 1 для Helvetica/Times:
        // ASCII letters/digits/space можно заполнить таблицей позже.
        return false;
    }
}


