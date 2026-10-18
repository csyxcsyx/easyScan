using System.IO;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.Services;

public static class BitmapHelper
{
    public static BitmapSource FromBytes(byte[] bytes, int decodeWidth = 0)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
