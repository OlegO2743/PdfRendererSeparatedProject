namespace PdfCore.Color;

public static class PdfColorSpaceFactory
{
    public static PdfColorSpace CreateDeviceSpaceByName(string? name)
    {
        return name switch
        {
            "/DeviceGray" => new PdfDeviceGrayColorSpace(),
            "/DeviceRGB" => new PdfDeviceRgbColorSpace(),
            "/DeviceCMYK" => new PdfDeviceCmykColorSpace(),
            _ => throw new NotSupportedException($"ColorSpace {name} пока не поддержан.")
        };
    }

    public static PdfColorSpace CreateDeviceSpaceByComponentCount(int components)
    {
        return components switch
        {
            1 => new PdfDeviceGrayColorSpace(),
            3 => new PdfDeviceRgbColorSpace(),
            4 => new PdfDeviceCmykColorSpace(),
            _ => throw new NotSupportedException($"Нельзя подобрать fallback ColorSpace для ICC profile с N={components}.")
        };
    }

    public static PdfColorSpace CreateFromIccProfile(PdfIccProfileObject profile)
    {
        PdfColorSpace? alternate = null;
        if (!string.IsNullOrWhiteSpace(profile.AlternateName))
            alternate = CreateDeviceSpaceByName(profile.AlternateName);

        return new PdfIccBasedColorSpace
        {
            N = profile.N,
            Alternate = alternate,
            ProfileBytes = profile.ProfileBytes,
            ProfileObjectNumber = profile.ObjectNumber
        };
    }

    public static bool IsDeviceColorSpaceName(string token)
    {
        return token == "/DeviceGray" || token == "/DeviceRGB" || token == "/DeviceCMYK";
    }
}
