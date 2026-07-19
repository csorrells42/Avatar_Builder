using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FileName { get; }

    public PersonalFaceModelStore(string fileName = "personal_face_model.json")
    {
        FileName = fileName;
    }

    public string Write(string folder, PersonalFaceModel model)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        return path;
    }

    public PersonalFaceModel Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var model = ReadFile(path);
        return model ?? throw new FileNotFoundException("Personal face model file was not found.", path);
    }

    public PersonalFaceModel? TryRead(string folder)
    {
        return ReadFile(Path.Combine(folder, FileName));
    }

    public static string WriteFile(string path, PersonalFaceModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        return path;
    }

    public static PersonalFaceModel? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<PersonalFaceModel>(json, JsonOptions);
    }
}
