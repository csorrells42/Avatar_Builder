using System.IO;
using System.Text;
using System.Text.Json;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Write(string folder, FaceReconstructionWorkItem workItem, string fileName = ThreeDdfaOnnxReconstructionSpec.WorkItemFileName)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(workItem, JsonOptions), Encoding.UTF8);
        return path;
    }

    public FaceReconstructionWorkItem Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<FaceReconstructionWorkItem>(text, JsonOptions)
            ?? throw new InvalidDataException($"Could not read face reconstruction work item: {path}");
    }
}
