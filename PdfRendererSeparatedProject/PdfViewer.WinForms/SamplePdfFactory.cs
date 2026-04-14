
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
namespace PdfViewer.WinForms;

public static class SamplePdfFactory
{
    public static void CreateGraphicsDemoPdf(string filePath)
    {
        string contentStream =
@"BT
/F1 18 Tf
1 0 0 1 20 210 Tm
(q / Q / cm / clip / Bezier demo) Tj
ET

0 0 0 RG
2 w
40 40 m
90 170 210 -10 260 90 c
S

0 0 0 RG
1 w
80 30 140 90 re
S

q
80 30 140 90 re
W
n
0 0.7 1 rg
20 10 260 140 re
f
Q

BT
/F1 12 Tf
1 0 0 1 165 115 Tm
(Translated block) Tj
ET

1 0.5 0 rg
220 20 70 120 re
f
";
        CreateSimplePagePdf(
            filePath,
            360,
            240,
            contentStream,
            new[] { ("F1", "Helvetica") });
    }

    public static void CreateFullDemoPdf(string filePath)
    {
        string contentStream =
@"BT
/F1 18 Tf
1 0 0 1 20 250 Tm
(Full text state + graphics demo) Tj
ET

BT
/F1 12 Tf
1 0 0 1 20 225 Tm
(Default spacing) Tj
ET

BT
/F1 12 Tf
2 Tc
1 0 0 1 20 205 Tm
(Character spacing via Tc) Tj
ET

BT
/F1 12 Tf
0 Tc
120 Tz
1 0 0 1 20 185 Tm
(Horizontal scaling via Tz) Tj
ET

BT
/F1 14 Tf
1 0 0 1 20 155 Tm
(Base) Tj
ET

BT
/F1 10 Tf
4 Ts
1 0 0 1 58 155 Tm
(super) Tj
ET

0 0 1 RG
2 w
20 70 140 50 re
S

0 1 1 rg
180 70 90 50 re
f
";
        CreateSimplePagePdf(
            filePath,
            360,
            280,
            contentStream,
            new[] { ("F1", "Helvetica") });
    }

    public static void CreateFormXObjectDemoPdf(string filePath)
    {
        byte[] pageBytes = CompressZlib(Encoding.ASCII.GetBytes(
@"BT
/F1 18 Tf
1 0 0 1 20 220 Tm
(Form XObject demo) Tj
ET

q
1 0 0 1 20 40 cm
/Fm1 Do
Q

q
1 0 0 1 180 40 cm
/Fm1 Do
Q

q
0.866 0.5 -0.5 0.866 260 165 cm
/Fm2 Do
Q
"));

        byte[] form1Bytes = CompressZlib(Encoding.ASCII.GetBytes(
@"0 0 1 RG
1.5 w
0 0 120 70 re
S

BT
/F1 12 Tf
1 0 0 1 10 52 Tm
(Form 1 block) Tj
ET

q
1 0 0 1 20 12 cm
/FmInner Do
Q
"));

        byte[] formInnerBytes = CompressZlib(Encoding.ASCII.GetBytes(
@"1 0 0 rg
0 0 0 RG
1 w
0 0 70 24 re
B

BT
/F1 10 Tf
1 0 0 1 8 8 Tm
(Inner) Tj
ET
"));

        byte[] form2Bytes = CompressZlib(Encoding.ASCII.GetBytes(
@"0 0.6 0 RG
2 w
0 0 90 30 re
S

BT
/F1 12 Tf
1 0 0 1 8 10 Tm
(Rotated form) Tj
ET
"));

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0L };

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int fontObj = 4;
        const int pageContentsObj = 5;
        const int form1Obj = 6;
        const int form2Obj = 7;
        const int formInnerObj = 8;
        const int objectCount = 8;

        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, $@"{catalogObj} 0 obj
<< /Type /Catalog /Pages {pagesObj} 0 R >>
endobj
");

        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, $@"{pagesObj} 0 obj
<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>
endobj
");

        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms, $@"{pageObj} 0 obj
<< /Type /Page
   /Parent {pagesObj} 0 R
   /MediaBox [0 0 360 260]
   /Resources <<
      /Font << /F1 {fontObj} 0 R >>
      /XObject << /Fm1 {form1Obj} 0 R /Fm2 {form2Obj} 0 R >>
   >>
   /Contents {pageContentsObj} 0 R
>>
endobj
");

        SetObjectOffset(offsets, fontObj, ms.Position);
        WriteAscii(ms, $@"{fontObj} 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
");

        SetObjectOffset(offsets, pageContentsObj, ms.Position);
        WriteAscii(ms, $@"{pageContentsObj} 0 obj
<< /Length {pageBytes.Length} /Filter /FlateDecode >>
stream
");
        ms.Write(pageBytes, 0, pageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, form1Obj, ms.Position);
        WriteAscii(ms, $@"{form1Obj} 0 obj
<< /Type /XObject
   /Subtype /Form
   /BBox [0 0 120 70]
   /Matrix [1 0 0 1 0 0]
   /Resources <<
      /Font << /F1 {fontObj} 0 R >>
      /XObject << /FmInner {formInnerObj} 0 R >>
   >>
   /Length {form1Bytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(form1Bytes, 0, form1Bytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, form2Obj, ms.Position);
        WriteAscii(ms, $@"{form2Obj} 0 obj
<< /Type /XObject
   /Subtype /Form
   /BBox [0 0 90 30]
   /Matrix [1 0 0 1 0 0]
   /Resources <<
      /Font << /F1 {fontObj} 0 R >>
   >>
   /Length {form2Bytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(form2Bytes, 0, form2Bytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, formInnerObj, ms.Position);
        WriteAscii(ms, $@"{formInnerObj} 0 obj
<< /Type /XObject
   /Subtype /Form
   /BBox [0 0 70 24]
   /Matrix [1 0 0 1 0 0]
   /Resources <<
      /Font << /F1 {fontObj} 0 R >>
   >>
   /Length {formInnerBytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(formInnerBytes, 0, formInnerBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    public static void CreateImageXObjectDemoPdf(string filePath)
    {
        byte[] rgbImage = BuildDemoRgbImage(48, 36);
        byte[] imageBytes = CompressZlib(rgbImage);

        string pageContent =
@"BT
/F1 18 Tf
1 0 0 1 20 250 Tm
(Image XObject / Do demo) Tj
ET

BT
/F1 12 Tf
1 0 0 1 20 230 Tm
(Direct image placement, rotated image, and image inside a form.) Tj
ET

q
120 0 0 90 25 70 cm
/Im1 Do
Q

q
0 78 -78 0 285 150 cm
/Im1 Do
Q

q
1 0 0 1 180 55 cm
/FmImg Do
Q
";

        string formContent =
@"0 0 1 RG
1.8 w
0 0 140 92 re
S

BT
/F1 12 Tf
1 0 0 1 10 76 Tm
(Image inside Form XObject) Tj
ET

q
96 0 0 68 18 10 cm
/ImForm Do
Q
";

        CreateImageXObjectPdf(filePath, 360, 280, pageContent, formContent, imageBytes, 48, 36, "/DeviceRGB");
    }

    public static void CreateContentsArrayDemoPdf(string filePath)
    {
        string stream1 =
@"BT
/F1 18 Tf
1 0 0 1 20 220 Tm
(Contents array demo) Tj
ET
";

        string stream2 =
@"0 0 1 RG
2 w
20 80 220 100 re
S
";

        string stream3 =
@"BT
/F1 12 Tf
1 0 0 1 30 155 Tm
(Text from stream 3 inside the rectangle.) Tj
ET
";

        CreateContentsArrayPagePdf(
            filePath,
            300,
            250,
            new[] { stream1, stream2, stream3 },
            new[] { ("F1", "Helvetica") });
    }

    public static void CreatePaintOperatorsDemoPdf(string filePath)
    {
        string contentStream =
@"BT
/F1 18 Tf
1 0 0 1 20 220 Tm
(Paint operators demo) Tj
ET

1 0 0 rg
0 0 1 RG
2 w
20 140 60 40 re
B

0 1 0 rg
0 0 0 RG
2 w
100 140 60 40 re
B*

1 0.5 0 rg
0 0 0 RG
2 w
180 140 m
240 140 l
240 180 l
180 180 l
b

0.8 0 0.8 rg
0 0 0 RG
2 w
260 140 m
320 140 l
320 180 l
260 180 l
b*

0 0 0 RG
2 w
20 70 m
80 70 l
80 110 l
20 110 l
s

0 0.7 1 rg
100 70 m
160 70 l
160 110 l
100 110 l
f*
";
        CreateSimplePagePdf(
            filePath,
            360,
            260,
            contentStream,
            new[] { ("F1", "Helvetica") });
    }

    public static void CreateCmykDemoPdf(string filePath)
    {
        byte[] cmykImage = BuildDemoCmykImage(48, 36);
        byte[] imageBytes = CompressZlib(cmykImage);

        string pageContent =
@"BT
/F1 18 Tf
1 0 0 1 20 250 Tm
(CMYK image + K/k operators demo) Tj
ET

0 1 1 0 k
BT
/F1 12 Tf
1 0 0 1 20 225 Tm
(Fill color via k) Tj
ET

1 0 0 0 K
0 0 0 0 k
2 w
20 140 110 60 re
B

q
120 0 0 90 180 90 cm
/Im1 Do
Q
";

        CreateImageOnlyPagePdf(filePath, 360, 280, pageContent, imageBytes, 48, 36, "/DeviceCMYK");
    }

    private static void CreateSimplePagePdf(
        string filePath,
        int widthPt,
        int heightPt,
        string contentStream,
        IReadOnlyList<(string ResourceName, string BaseFontName)> fonts)
    {
        byte[] compressedStream = CompressZlib(Encoding.ASCII.GetBytes(contentStream));

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0L };

        int catalogObj = 1;
        int pagesObj = 2;
        int pageObj = 3;
        int firstFontObj = 4;
        int contentsObj = firstFontObj + fonts.Count;
        int objectCount = contentsObj;

        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, $@"{catalogObj} 0 obj
<< /Type /Catalog /Pages {pagesObj} 0 R >>
endobj
");

        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, $@"{pagesObj} 0 obj
<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>
endobj
");

        var fontDictBuilder = new StringBuilder();
        for (int i = 0; i < fonts.Count; i++)
        {
            int objNum = firstFontObj + i;
            fontDictBuilder.Append('/').Append(fonts[i].ResourceName).Append(' ').Append(objNum).Append(" 0 R ");
        }

        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms, $@"{pageObj} 0 obj
<< /Type /Page
   /Parent {pagesObj} 0 R
   /MediaBox [0 0 {widthPt} {heightPt}]
   /Resources << /Font << {fontDictBuilder}>> >>
   /Contents {contentsObj} 0 R
>>
endobj
");

        for (int i = 0; i < fonts.Count; i++)
        {
            int objNum = firstFontObj + i;
            SetObjectOffset(offsets, objNum, ms.Position);
            WriteAscii(ms, $@"{objNum} 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /{fonts[i].BaseFontName} >>
endobj
");
        }

        SetObjectOffset(offsets, contentsObj, ms.Position);
        WriteAscii(ms, $@"{contentsObj} 0 obj
<< /Length {compressedStream.Length} /Filter /FlateDecode >>
stream
");
        ms.Write(compressedStream, 0, compressedStream.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private static void CreateContentsArrayPagePdf(
        string filePath,
        int widthPt,
        int heightPt,
        IReadOnlyList<string> contentStreams,
        IReadOnlyList<(string ResourceName, string BaseFontName)> fonts)
    {
        if (contentStreams.Count == 0)
            throw new ArgumentException("Должен быть хотя бы один content stream.", nameof(contentStreams));

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0L };

        int catalogObj = 1;
        int pagesObj = 2;
        int pageObj = 3;
        int firstFontObj = 4;
        int firstContentObj = firstFontObj + fonts.Count;
        int objectCount = firstContentObj + contentStreams.Count - 1;

        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, $@"{catalogObj} 0 obj
<< /Type /Catalog /Pages {pagesObj} 0 R >>
endobj
");

        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, $@"{pagesObj} 0 obj
<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>
endobj
");

        var fontDictBuilder = new StringBuilder();
        for (int i = 0; i < fonts.Count; i++)
        {
            int objNum = firstFontObj + i;
            fontDictBuilder.Append('/').Append(fonts[i].ResourceName).Append(' ').Append(objNum).Append(" 0 R ");
        }

        var contentsArrayBuilder = new StringBuilder();
        for (int i = 0; i < contentStreams.Count; i++)
        {
            int objNum = firstContentObj + i;
            contentsArrayBuilder.Append(objNum).Append(" 0 R ");
        }

        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms, $@"{pageObj} 0 obj
<< /Type /Page
   /Parent {pagesObj} 0 R
   /MediaBox [0 0 {widthPt} {heightPt}]
   /Resources << /Font << {fontDictBuilder}>> >>
   /Contents [{contentsArrayBuilder}]
>>
endobj
");

        for (int i = 0; i < fonts.Count; i++)
        {
            int objNum = firstFontObj + i;
            SetObjectOffset(offsets, objNum, ms.Position);
            WriteAscii(ms, $@"{objNum} 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /{fonts[i].BaseFontName} >>
endobj
");
        }

        for (int i = 0; i < contentStreams.Count; i++)
        {
            int objNum = firstContentObj + i;
            byte[] streamBytes = CompressZlib(Encoding.ASCII.GetBytes(contentStreams[i]));

            SetObjectOffset(offsets, objNum, ms.Position);
            WriteAscii(ms, $@"{objNum} 0 obj
<< /Length {streamBytes.Length} /Filter /FlateDecode >>
stream
");
            ms.Write(streamBytes, 0, streamBytes.Length);
            WriteAscii(ms, "\nendstream\nendobj\n");
        }

        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private static void CreateImageOnlyPagePdf(
        string filePath,
        int widthPt,
        int heightPt,
        string pageContent,
        byte[] imageBytes,
        int imageWidth,
        int imageHeight,
        string colorSpace)
    {
        byte[] pageBytes = CompressZlib(Encoding.ASCII.GetBytes(pageContent));

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0L };

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int fontObj = 4;
        const int contentsObj = 5;
        const int imageObj = 6;
        const int objectCount = 6;

        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, @"1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
");

        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, @"2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
");

        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms, $@"3 0 obj
<< /Type /Page
   /Parent 2 0 R
   /MediaBox [0 0 {widthPt} {heightPt}]
   /Resources <<
      /Font << /F1 4 0 R >>
      /XObject << /Im1 6 0 R >>
   >>
   /Contents 5 0 R
>>
endobj
");

        SetObjectOffset(offsets, fontObj, ms.Position);
        WriteAscii(ms, @"4 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
");

        SetObjectOffset(offsets, contentsObj, ms.Position);
        WriteAscii(ms, $@"5 0 obj
<< /Length {pageBytes.Length} /Filter /FlateDecode >>
stream
");
        ms.Write(pageBytes, 0, pageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, imageObj, ms.Position);
        WriteAscii(ms, $@"6 0 obj
<< /Type /XObject
   /Subtype /Image
   /Width {imageWidth}
   /Height {imageHeight}
   /ColorSpace {colorSpace}
   /BitsPerComponent 8
   /Length {imageBytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(imageBytes, 0, imageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private static void CreateImageXObjectPdf(
        string filePath,
        int widthPt,
        int heightPt,
        string pageContent,
        string formContent,
        byte[] imageBytes,
        int imageWidth,
        int imageHeight,
        string colorSpace)
    {
        byte[] pageBytes = CompressZlib(Encoding.ASCII.GetBytes(pageContent));
        byte[] formBytes = CompressZlib(Encoding.ASCII.GetBytes(formContent));

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0L };

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int helveticaObj = 4;
        const int pageContentsObj = 5;
        const int formObj = 6;
        const int imageObjPage = 7;
        const int imageObjForm = 8;
        const int objectCount = 8;

        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, @"1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
");

        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, @"2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
");

        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms, $@"3 0 obj
<< /Type /Page
   /Parent 2 0 R
   /MediaBox [0 0 {widthPt} {heightPt}]
   /Resources <<
      /Font << /F1 4 0 R >>
      /XObject << /Im1 7 0 R /FmImg 6 0 R >>
   >>
   /Contents 5 0 R
>>
endobj
");

        SetObjectOffset(offsets, helveticaObj, ms.Position);
        WriteAscii(ms, @"4 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
");

        SetObjectOffset(offsets, pageContentsObj, ms.Position);
        WriteAscii(ms, $@"5 0 obj
<< /Length {pageBytes.Length} /Filter /FlateDecode >>
stream
");
        ms.Write(pageBytes, 0, pageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, formObj, ms.Position);
        WriteAscii(ms, $@"6 0 obj
<< /Type /XObject
   /Subtype /Form
   /BBox [0 0 130 90]
   /Matrix [1 0 0 1 0 0]
   /Resources <<
      /Font << /F1 4 0 R >>
      /XObject << /ImForm 8 0 R >>
   >>
   /Length {formBytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(formBytes, 0, formBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, imageObjPage, ms.Position);
        WriteAscii(ms, $@"7 0 obj
<< /Type /XObject
   /Subtype /Image
   /Width {imageWidth}
   /Height {imageHeight}
   /ColorSpace {colorSpace}
   /BitsPerComponent 8
   /Length {imageBytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(imageBytes, 0, imageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        SetObjectOffset(offsets, imageObjForm, ms.Position);
        WriteAscii(ms, $@"8 0 obj
<< /Type /XObject
   /Subtype /Image
   /Width {imageWidth}
   /Height {imageHeight}
   /ColorSpace {colorSpace}
   /BitsPerComponent 8
   /Length {imageBytes.Length}
   /Filter /FlateDecode
>>
stream
");
        ms.Write(imageBytes, 0, imageBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private static byte[] BuildDemoRgbImage(int width, int height)
    {
        byte[] data = new byte[width * height * 3];
        int i = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 255 / Math.Max(1, width - 1));
                byte g = (byte)(y * 255 / Math.Max(1, height - 1));
                bool checker = ((x / 6) + (y / 6)) % 2 == 0;
                byte b = checker ? (byte)220 : (byte)60;

                data[i++] = r;
                data[i++] = g;
                data[i++] = b;
            }
        }

        return data;
    }

    private static byte[] BuildDemoCmykImage(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        int i = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte c = (byte)(x * 255 / Math.Max(1, width - 1));
                byte m = (byte)(y * 255 / Math.Max(1, height - 1));
                byte yv = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
                byte k = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 20 : 80);

                data[i++] = c;
                data[i++] = m;
                data[i++] = yv;
                data[i++] = k;
            }
        }

        return data;
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(input, 0, input.Length);
        }
        return output.ToArray();
    }

    private static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void SetObjectOffset(List<long> offsets, int objectNumber, long position)
    {
        while (offsets.Count <= objectNumber)
            offsets.Add(0);

        offsets[objectNumber] = position;
    }

    private static void WriteXrefAndTrailer(Stream ms, List<long> offsets, int objectCount, int catalogObj)
    {
        while (offsets.Count <= objectCount)
            offsets.Add(0);

        for (int i = 1; i <= objectCount; i++)
        {
            if (offsets[i] <= 0)
                throw new InvalidOperationException($"Не задан offset для объекта {i}");
        }

        long xrefPosition = ms.Position;

        WriteAscii(ms, "xref\n");
        WriteAscii(ms, $"0 {objectCount + 1}\n");
        WriteAscii(ms, "0000000000 65535 f \n");

        for (int i = 1; i <= objectCount; i++)
            WriteAscii(ms, offsets[i].ToString("0000000000") + " 00000 n \n");

        WriteAscii(ms,
$@"trailer
<< /Size {objectCount + 1} /Root {catalogObj} 0 R >>
startxref
{xrefPosition}
%%EOF");
    }

    // New demos added after the stable baseline, without changing old ones.
    public static void CreateResourceColorSpaceDemoPdf(string filePath)
    {
        byte[] cmykRaw = BuildResourceColorSpaceDemoCmykImage(48, 36);
        byte[] cmykCompressed = CompressZlib(cmykRaw);
        byte[] iccProfileBytes = Encoding.ASCII.GetBytes("FAKE_ICC_PROFILE_PHASE1");

        string pageContent =
@"BT
/F1 18 Tf
1 0 0 1 20 250 Tm
(Resource ColorSpace demo) Tj
ET

BT
/F1 12 Tf
1 0 0 1 20 230 Tm
(/CS1 = DeviceCMYK, /CS2 = ICCBased -> Alternate DeviceCMYK) Tj
ET

q
120 0 0 90 25 80 cm
/Im1 Do
Q

q
120 0 0 90 205 80 cm
/Im2 Do
Q

BT
/F1 10 Tf
1 0 0 1 45 65 Tm
(/Im1 uses /CS1) Tj
ET

BT
/F1 10 Tf
1 0 0 1 225 65 Tm
(/Im2 uses /CS2) Tj
ET
";

        CreateTwoImageResourceColorSpacePage(filePath, pageContent, cmykCompressed, 48, 36, iccProfileBytes, false);
    }

    public static void CreateIccBasedPhase2DemoPdf(string filePath)
    {
        byte[] cmykRaw = BuildResourceColorSpaceDemoCmykImage(48, 36);
        byte[] cmykCompressed = CompressZlib(cmykRaw);
        byte[] iccProfileBytes = LoadDefaultCmykIccProfile();

        string pageContent =
@"BT
/F1 18 Tf
1 0 0 1 20 250 Tm
(ICCBased phase 2 demo) Tj
ET

BT
/F1 12 Tf
1 0 0 1 20 230 Tm
(/CS1 = DeviceCMYK, /CS2 = ICCBased with embedded profile) Tj
ET

q
120 0 0 90 25 80 cm
/Im1 Do
Q

q
120 0 0 90 205 80 cm
/Im2 Do
Q

BT
/F1 10 Tf
1 0 0 1 40 65 Tm
(/Im1 DeviceCMYK) Tj
ET

BT
/F1 10 Tf
1 0 0 1 220 65 Tm
(/Im2 ICCBased phase 2) Tj
ET
";

        CreateTwoImageResourceColorSpacePage(filePath, pageContent, cmykCompressed, 48, 36, iccProfileBytes, true);
    }

    private static void CreateTwoImageResourceColorSpacePage(string filePath, string pageContent, byte[] compressedImage, int imageWidth, int imageHeight, byte[] iccProfileBytes, bool includePhase2Caption)
    {
        byte[] pageBytes = CompressZlib(Encoding.ASCII.GetBytes(pageContent));

        using MemoryStream ms = new();
        List<long> offsets = new() { 0L };

        const int catalogObj = 1, pagesObj = 2, pageObj = 3, fontObj = 4, contentsObj = 5, image1Obj = 6, image2Obj = 7, iccObj = 8, objectCount = 8;
        WriteAscii(ms, "%PDF-1.4\n");

        SetObjectOffset(offsets, catalogObj, ms.Position);
        WriteAscii(ms, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        SetObjectOffset(offsets, pagesObj, ms.Position);
        WriteAscii(ms, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        SetObjectOffset(offsets, pageObj, ms.Position);
        WriteAscii(ms,
@"3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 360 280]
   /Resources <<
      /Font << /F1 4 0 R >>
      /ColorSpace <<
         /CS1 /DeviceCMYK
         /CS2 [/ICCBased 8 0 R]
      >>
      /XObject << /Im1 6 0 R /Im2 7 0 R >>
   >>
   /Contents 5 0 R
>>
endobj
");
        SetObjectOffset(offsets, fontObj, ms.Position);
        WriteAscii(ms, "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        SetObjectOffset(offsets, contentsObj, ms.Position);
        WriteAscii(ms, $"5 0 obj\n<< /Length {pageBytes.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(pageBytes);
        WriteAscii(ms, "\nendstream\nendobj\n");
        SetObjectOffset(offsets, image1Obj, ms.Position);
        WriteAscii(ms, $"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {imageWidth} /Height {imageHeight} /ColorSpace /CS1 /BitsPerComponent 8 /Length {compressedImage.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(compressedImage);
        WriteAscii(ms, "\nendstream\nendobj\n");
        SetObjectOffset(offsets, image2Obj, ms.Position);
        WriteAscii(ms, $"7 0 obj\n<< /Type /XObject /Subtype /Image /Width {imageWidth} /Height {imageHeight} /ColorSpace /CS2 /BitsPerComponent 8 /Length {compressedImage.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(compressedImage);
        WriteAscii(ms, "\nendstream\nendobj\n");
        SetObjectOffset(offsets, iccObj, ms.Position);
        WriteAscii(ms, $"8 0 obj\n<< /N 4 /Alternate /DeviceCMYK /Length {iccProfileBytes.Length} >>\nstream\n");
        ms.Write(iccProfileBytes);
        WriteAscii(ms, "\nendstream\nendobj\n");
        WriteXrefAndTrailer(ms, offsets, objectCount, catalogObj);
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private static byte[] LoadDefaultCmykIccProfile()
    {
        string[] projectCandidates =
        {
        Path.Combine(AppContext.BaseDirectory, "Assets", "icc", "default_cmyk.icc"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "icc", "default_cmyk.icm"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "icc", "default_cmyk.icc"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "icc", "default_cmyk.icm")
    };

        foreach (string candidate in projectCandidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return File.ReadAllBytes(full);
        }

        string windowsColorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "spool", "drivers", "color");

        if (Directory.Exists(windowsColorDir))
        {
            string[] preferredPatterns =
            {
            "*cmyk*.icc",
            "*CMYK*.icc",
            "*cmyk*.icm",
            "*CMYK*.icm"
        };

            foreach (string pattern in preferredPatterns)
            {
                string[] files = Directory.GetFiles(windowsColorDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                    return File.ReadAllBytes(files[0]);
            }

            string[] anyProfiles = Directory.GetFiles(windowsColorDir, "*.icc", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(windowsColorDir, "*.icm", SearchOption.TopDirectoryOnly))
                .ToArray();

            if (anyProfiles.Length > 0)
                return File.ReadAllBytes(anyProfiles[0]);
        }

        throw new FileNotFoundException(
            "Не найден ICC profile для phase 2 demo. " +
            "Положи CMYK ICC/ICM файл в Assets\\icc\\default_cmyk.icc " +
            "или используй установленный системный профиль Windows.");
    }

    private static byte[] BuildResourceColorSpaceDemoCmykImage(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float xf = x / (float)Math.Max(1, width - 1);
                float yf = y / (float)Math.Max(1, height - 1);
                byte c = (byte)(xf * 220);
                byte m = (byte)(yf * 220);
                byte yy = ((x / 6) + (y / 6)) % 2 == 0 ? (byte)180 : (byte)40;
                byte k = (byte)(30 + yf * 80);
                data[i++] = c;
                data[i++] = m;
                data[i++] = yy;
                data[i++] = k;
            }
        }
        return data;
    }

}