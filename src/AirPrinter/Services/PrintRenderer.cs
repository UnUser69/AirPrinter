using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace AirPrinter.Services;

public sealed class DocumentFormatException : Exception
{
    public DocumentFormatException(string message) : base(message)
    {
    }
}

public sealed class PrintJobRequest
{
    public required string PrinterName { get; init; }
    public required string Format { get; init; }
    public required byte[] Document { get; init; }
    public int Copies { get; init; } = 1;
    public bool Duplex { get; init; }
    public bool ShortEdge { get; init; }
    public string? Media { get; init; }
}

public interface IJobPrinter
{
    void Print(PrintJobRequest request, CancellationToken cancellationToken);
}

public sealed class PrintRenderer : IJobPrinter
{
    public void Print(PrintJobRequest request, CancellationToken cancellationToken)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                PrintOnSta(request, cancellationToken);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
        {
            throw error;
        }
    }

    public static string ResolveFormat(string? declared, byte[] document)
    {
        string format = (declared ?? "").Trim().ToLowerInvariant();
        if (format == "image/jpg")
        {
            format = "image/jpeg";
        }

        if (format is "application/pdf" or "image/jpeg" or "image/png" or "image/urf" or "image/pwg-raster")
        {
            return format;
        }

        if (format is "" or "application/octet-stream")
        {
            if (IsPdf(document))
            {
                return "application/pdf";
            }

            if (IsJpeg(document))
            {
                return "image/jpeg";
            }

            if (IsPng(document))
            {
                return "image/png";
            }

            if (IsUrf(document))
            {
                return "image/urf";
            }

            if (IsPwg(document))
            {
                return "image/pwg-raster";
            }
        }

        throw new DocumentFormatException(string.IsNullOrWhiteSpace(format)
            ? "Формат документа не распознан."
            : "Формат не поддерживается: " + format);
    }

    public static Bitmap DecodeJpeg(byte[] data) => DecodeBitmap(data, 40_000_000);

    public static Bitmap DecodePng(byte[] data) => DecodeBitmap(data, 40_000_000);

    public static IReadOnlyList<Bitmap> RenderPdf(byte[] data)
    {
        var pages = new List<Bitmap>();
        try
        {
            using IDocReader reader = DocLib.Instance.GetDocReader(data, new PageDimensions(300.0 / 72.0));
            int count = reader.GetPageCount();
            if (count <= 0)
            {
                throw new InvalidDataException("PDF не содержит страниц.");
            }

            if (count > 40)
            {
                throw new InvalidDataException("PDF длиннее 40 страниц.");
            }

            for (int i = 0; i < count; i++)
            {
                using IPageReader page = reader.GetPageReader(i);
                pages.Add(BitmapFromBgra(page.GetImage(), page.GetPageWidth(), page.GetPageHeight()));
            }

            return pages;
        }
        catch
        {
            foreach (Bitmap page in pages)
            {
                page.Dispose();
            }

            throw;
        }
    }

    public static IReadOnlyList<Bitmap> DecodeUrf(byte[] data)
    {
        var pages = new List<Bitmap>();
        try
        {
            foreach (Bitmap page in DecodeUrfPages(data))
            {
                pages.Add(page);
            }

            return pages;
        }
        catch
        {
            foreach (Bitmap page in pages)
            {
                page.Dispose();
            }

            throw;
        }
    }

    private static void PrintOnSta(PrintJobRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string format = ResolveFormat(request.Format, request.Document);
        List<Bitmap> pages = format switch
        {
            "application/pdf" => RenderPdf(request.Document).ToList(),
            "image/jpeg" or "image/png" => [DecodeBitmap(request.Document, 40_000_000)],
            "image/urf" => DecodeUrf(request.Document).ToList(),
            "image/pwg-raster" => DecodePwg(request.Document).ToList(),
            _ => throw new DocumentFormatException("Формат не поддерживается: " + format),
        };

        try
        {
            if (pages.Count == 0)
            {
                throw new InvalidDataException("В документе нет страниц.");
            }

            int copies = Math.Clamp(request.Copies, 1, 99);
            for (int copy = 0; copy < copies; copy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrintBitmaps(request, pages, cancellationToken);
            }
        }
        finally
        {
            foreach (Bitmap page in pages)
            {
                page.Dispose();
            }
        }
    }

    private static void PrintBitmaps(PrintJobRequest request, IReadOnlyList<Bitmap> pages, CancellationToken cancellationToken)
    {
        int index = 0;
        bool customMargins = true;
        using var document = new PrintDocument();
        document.PrintController = new StandardPrintController();
        document.DocumentName = "AirPrint";
        document.PrinterSettings.PrinterName = request.PrinterName;
        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException($"Принтер «{request.PrinterName}» недоступен.");
        }

        document.PrinterSettings.Copies = 1;
        if (request.Duplex && document.PrinterSettings.CanDuplex)
        {
            document.PrinterSettings.Duplex = request.ShortEdge ? Duplex.Horizontal : Duplex.Vertical;
        }
        else
        {
            document.PrinterSettings.Duplex = Duplex.Simplex;
        }

        document.DefaultPageSettings.Landscape = pages[0].Width > pages[0].Height;
        ApplyMedia(document, request.Media);
        document.DefaultPageSettings.Margins = new Margins(25, 25, 25, 25);
        document.PrintPage += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bitmap page = pages[index];
            Rectangle bounds = args.MarginBounds;
            args.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            args.Graphics.DrawImage(page, bounds);
            index++;
            args.HasMorePages = index < pages.Count;
        };

        try
        {
            document.Print();
        }
        catch (Exception ex) when (customMargins && ex is not OperationCanceledException and not InvalidPrinterException)
        {
            index = 0;
            customMargins = false;
            document.DefaultPageSettings.Margins = new Margins(100, 100, 100, 100);
            document.Print();
        }
    }

    private static IEnumerable<Bitmap> DecodeUrfPages(byte[] data)
    {
        if (!IsUrf(data) || data.Length < 12 || data[7] != 0)
        {
            throw new InvalidDataException("Это не файл Apple Raster (URF).");
        }

        int offset = 8;
        int pageCount = ReadInt32(data, ref offset);
        if (pageCount < 0 || pageCount > 200)
        {
            throw new InvalidDataException("Некорректное число страниц URF.");
        }

        int produced = 0;
        int limit = pageCount == 0 ? 200 : pageCount;
        while (produced < limit && offset + 32 <= data.Length)
        {
            byte bpp = data[offset];
            int width = ReadInt32(data, offset + 12);
            int height = ReadInt32(data, offset + 16);
            int dpi = ReadInt32(data, offset + 20);
            offset += 32;
            if (width <= 0 || height <= 0 || width > 20000 || height > 30000)
            {
                throw new InvalidDataException("Некорректный размер страницы URF.");
            }

            if ((long)width * height * 4 > 200_000_000)
            {
                throw new InvalidDataException("Страница URF слишком большая.");
            }

            int pixelBytes = bpp switch
            {
                8 => 1,
                24 => 3,
                _ => throw new InvalidDataException("URF с глубиной " + bpp + " не поддерживается."),
            };

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            if (dpi > 0)
            {
                bitmap.SetResolution(dpi, dpi);
            }

            var rect = new Rectangle(0, 0, width, height);
            BitmapData locked = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int row = 0;
                var line = new byte[width * pixelBytes];
                var rowBytes = new byte[width * 3];
                while (row < height)
                {
                    if (offset >= data.Length)
                    {
                        throw new InvalidDataException("URF оборван.");
                    }

                    int repeats = data[offset++] + 1;
                    DecodeLine(data, ref offset, line, width, pixelBytes);
                    for (int repeat = 0; repeat < repeats && row < height; repeat++)
                    {
                        WriteRgbRow(locked, row, line, width, pixelBytes, rowBytes);
                        row++;
                    }
                }
            }
            catch
            {
                bitmap.UnlockBits(locked);
                bitmap.Dispose();
                throw;
            }

            bitmap.UnlockBits(locked);
            produced++;
            yield return bitmap;
        }

        if (produced == 0)
        {
            throw new InvalidDataException("URF не содержит страниц.");
        }
    }

    private static void DecodeLine(byte[] data, ref int offset, byte[] line, int width, int pixelBytes)
    {
        int pixel = 0;
        while (pixel < width)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("URF оборван.");
            }

            sbyte code = (sbyte)data[offset++];
            if (code == -128)
            {
                for (int i = pixel * pixelBytes; i < line.Length; i++)
                {
                    line[i] = 0xFF;
                }

                return;
            }

            if (code >= 0)
            {
                int count = code + 1;
                if (offset + pixelBytes > data.Length)
                {
                    throw new InvalidDataException("URF оборван.");
                }

                for (int i = 0; i < count && pixel < width; i++)
                {
                    Buffer.BlockCopy(data, offset, line, pixel * pixelBytes, pixelBytes);
                    pixel++;
                }

                offset += pixelBytes;
                continue;
            }

            int literal = (-code) + 1;
            for (int i = 0; i < literal && pixel < width; i++)
            {
                if (offset + pixelBytes > data.Length)
                {
                    throw new InvalidDataException("URF оборван.");
                }

                Buffer.BlockCopy(data, offset, line, pixel * pixelBytes, pixelBytes);
                offset += pixelBytes;
                pixel++;
            }
        }
    }

    private static void WriteRgbRow(BitmapData locked, int row, byte[] line, int width, int pixelBytes, byte[] rowBytes)
    {
        int stride = locked.Stride;
        IntPtr start = locked.Scan0 + row * stride;
        int count = Math.Min(width * 3, stride);
        if (pixelBytes == 3)
        {
            for (int i = 0; i < width; i++)
            {
                rowBytes[i * 3] = line[i * 3 + 2];
                rowBytes[i * 3 + 1] = line[i * 3 + 1];
                rowBytes[i * 3 + 2] = line[i * 3];
            }
        }
        else
        {
            for (int i = 0; i < width; i++)
            {
                byte gray = line[i];
                rowBytes[i * 3] = gray;
                rowBytes[i * 3 + 1] = gray;
                rowBytes[i * 3 + 2] = gray;
            }
        }

        Marshal.Copy(rowBytes, 0, start, count);
    }

    private static Bitmap BitmapFromBgra(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Пустая страница PDF.");
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData locked = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                int source = y * sourceStride;
                if (source + sourceStride > bgra.Length)
                {
                    break;
                }

                Marshal.Copy(bgra, source, locked.Scan0 + y * locked.Stride, sourceStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return bitmap;
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        int value = ReadInt32(data, offset);
        offset += 4;
        return value;
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            throw new InvalidDataException("URF оборван.");
        }

        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static bool IsPdf(byte[] data)
    {
        return data.Length >= 5 && data[0] == (byte)'%' && data[1] == (byte)'P' && data[2] == (byte)'D' && data[3] == (byte)'F';
    }

    private static bool IsJpeg(byte[] data)
    {
        return data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;
    }

    private static bool IsPng(byte[] data)
    {
        return data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
    }

    private static bool IsUrf(byte[] data)
    {
        return data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 7) == "UNIRAST";
    }

    private static bool IsPwg(byte[] data)
    {
        return data.Length >= 4 && data[0] == (byte)'R' && data[1] == (byte)'a' && data[2] == (byte)'S' && data[3] == (byte)'2';
    }

    private static Bitmap DecodeBitmap(byte[] data, int maxPixels)
    {
        using var stream = new MemoryStream(data);
        using Image image = Image.FromStream(stream, false, true);
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > maxPixels)
        {
            throw new InvalidDataException("Изображение слишком большое.");
        }

        return new Bitmap(image);
    }

    public static IReadOnlyList<Bitmap> DecodePwg(byte[] data)
    {
        if (!IsPwg(data))
        {
            throw new InvalidDataException("Это не PWG Raster.");
        }

        var pages = new List<Bitmap>();
        try
        {
            int offset = 4;
            while (pages.Count < 40 && offset + 1796 <= data.Length)
            {
                int width = ReadInt32(data, offset + 372);
                int height = ReadInt32(data, offset + 376);
                int bpp = ReadInt32(data, offset + 388);
                int colorSpace = ReadInt32(data, offset + 400);
                offset += 1796;
                if (width <= 0 || height <= 0)
                {
                    break;
                }

                if (width > 20000 || height > 30000 || (long)width * height * 4 > 200_000_000)
                {
                    throw new InvalidDataException("Страница PWG слишком большая.");
                }

                int pixelBytes = bpp switch
                {
                    8 => 1,
                    24 => 3,
                    _ => throw new InvalidDataException("PWG с глубиной " + bpp + " не поддерживается."),
                };
                Bitmap bitmap = RasterPage(data, ref offset, width, height, pixelBytes, colorSpace == 3 && pixelBytes == 1);
                pages.Add(bitmap);
            }

            if (pages.Count == 0)
            {
                throw new InvalidDataException("PWG не содержит страниц.");
            }

            return pages;
        }
        catch
        {
            foreach (Bitmap page in pages)
            {
                page.Dispose();
            }

            throw;
        }
    }

    private static Bitmap RasterPage(byte[] data, ref int offset, int width, int height, int pixelBytes, bool invertGray)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData locked = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int row = 0;
            var line = new byte[width * pixelBytes];
            var rowBytes = new byte[width * 3];
            while (row < height)
            {
                if (offset >= data.Length)
                {
                    throw new InvalidDataException("Растр оборван.");
                }

                int repeats = data[offset++] + 1;
                DecodeLine(data, ref offset, line, width, pixelBytes);
                if (invertGray)
                {
                    for (int i = 0; i < line.Length; i++)
                    {
                        line[i] = (byte)(255 - line[i]);
                    }
                }

                for (int repeat = 0; repeat < repeats && row < height; repeat++)
                {
                    WriteRgbRow(locked, row, line, width, pixelBytes, rowBytes);
                    row++;
                }
            }
        }
        catch
        {
            bitmap.UnlockBits(locked);
            bitmap.Dispose();
            throw;
        }

        bitmap.UnlockBits(locked);
        return bitmap;
    }

    private static void ApplyMedia(PrintDocument document, string? media)
    {
        if (!MediaSizes.TryGet(media, out double widthMm, out double heightMm))
        {
            return;
        }

        int width = (int)Math.Round(widthMm / 25.4 * 100.0);
        int height = (int)Math.Round(heightMm / 25.4 * 100.0);
        foreach (PaperSize size in document.PrinterSettings.PaperSizes)
        {
            bool same = Math.Abs(size.Width - width) <= 15 && Math.Abs(size.Height - height) <= 15;
            bool swapped = Math.Abs(size.Width - height) <= 15 && Math.Abs(size.Height - width) <= 15;
            if (same || swapped)
            {
                document.DefaultPageSettings.PaperSize = size;
                document.DefaultPageSettings.Landscape = swapped && !same;
                return;
            }
        }
    }
}
