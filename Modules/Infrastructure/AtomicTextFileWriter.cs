using System.IO;
using System.Text;

namespace AvatarBuilder.Modules.Infrastructure;

public static class AtomicTextFileWriter
{
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, contents, encoding);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
