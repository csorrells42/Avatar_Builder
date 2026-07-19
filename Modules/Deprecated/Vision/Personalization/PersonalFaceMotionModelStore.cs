using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMotionModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FileName { get; }

    public PersonalFaceMotionModelStore(string fileName = "personal_face_motion_model.json")
    {
        FileName = fileName;
    }

    public string Write(string folder, PersonalFaceMotionModel model)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        return path;
    }

    public static string WriteFile(string path, PersonalFaceMotionModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        return path;
    }
}
