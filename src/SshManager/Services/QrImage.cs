using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace SshManager.Services;

/// <summary>QR code as a bitmap: black modules on white with the standard quiet zone, sharp when scaled up.</summary>
public static class QrImage
{
    private const int PixelsPerModule = 8;

    public static BitmapSource Render(string text)
    {
        using var generator = new QRCodeGenerator();
        QRCodeData data;
        try
        {
            data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L); // long configs: less error correction
        }
        using (data)
        {
            var matrix = data.ModuleMatrix; // includes the quiet zone
            var size = matrix.Count * PixelsPerModule;
            var pixels = new byte[size * size];
            for (var y = 0; y < size; y++)
            {
                var row = matrix[y / PixelsPerModule];
                for (var x = 0; x < size; x++) pixels[y * size + x] = row[x / PixelsPerModule] ? (byte)0 : (byte)255;
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
