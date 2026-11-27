namespace AirPrinter.Services;

public static class MediaSizes
{
    public static readonly (string Name, double WidthMm, double HeightMm)[] Catalog =
    [
        ("iso_a4_210x297mm", 210.0, 297.0),
        ("na_letter_8.5x11in", 215.9, 279.4),
        ("iso_a5_148x210mm", 148.0, 210.0),
        ("iso_a6_105x148mm", 105.0, 148.0),
        ("na_legal_8.5x14in", 215.9, 355.6),
        ("na_index-4x6_4x6in", 101.6, 152.4),
    ];

    public static bool TryGet(string? keyword, out double widthMm, out double heightMm)
    {
        widthMm = 0;
        heightMm = 0;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        foreach ((string name, double width, double height) in Catalog)
        {
            if (string.Equals(name, keyword, StringComparison.OrdinalIgnoreCase))
            {
                widthMm = width;
                heightMm = height;
                return true;
            }
        }

        return false;
    }
}
