using System.IO;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarObservationImageCodec
{
    public static string WriteObject(string storageRoot, BitmapSource bitmap)
    {
        var objectFolder = AvatarStorageLayout.GetImageObjectFolder(storageRoot);
        Directory.CreateDirectory(objectFolder);
        var temporaryPath = Path.Combine(objectFolder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1 << 20,
                       FileOptions.SequentialScan))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            return AvatarStorageLayout.PromoteContentAddressedObject(temporaryPath, objectFolder, ".jpg");
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }

            throw;
        }
    }
}
