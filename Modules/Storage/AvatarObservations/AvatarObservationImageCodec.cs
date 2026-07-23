using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarObservationImageCodec
{
	public static string WriteObject(string storageRoot, BitmapSource bitmap)
	{
		string imageObjectFolder = AvatarStorageLayout.GetImageObjectFolder(storageRoot);
		Directory.CreateDirectory(imageObjectFolder);
		string text = Path.Combine(imageObjectFolder, $".{Guid.NewGuid():N}.tmp");
		try
		{
			JpegBitmapEncoder jpegBitmapEncoder = new JpegBitmapEncoder
			{
				QualityLevel = 95
			};
			jpegBitmapEncoder.Frames.Add(BitmapFrame.Create(bitmap));
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan))
			{
				jpegBitmapEncoder.Save(fileStream);
				fileStream.Flush(flushToDisk: true);
			}
			return AvatarStorageLayout.PromoteContentAddressedObject(text, imageObjectFolder, ".jpg");
		}
		catch
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			throw;
		}
	}
}
