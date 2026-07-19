using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using System.IO;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvYuNetModelInfo
{
    private const string RelativeModelDirectory = "dependencies/vision/opencv/yunet";
    private const string DefaultModelFileName = "face_detection_yunet_2023mar.onnx";

    public string ModelDirectory { get; init; } = "";

    public string ModelPath { get; init; } = "";

    public bool ModelExists => File.Exists(ModelPath);

    public bool IsReady => ModelExists;

    public string Status => ModelExists
        ? "OpenCV YuNet face detector model ready"
        : "OpenCV YuNet face detector model missing";

    public static OpenCvYuNetModelInfo Load()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            RelativeModelDirectory.Replace('/', Path.DirectorySeparatorChar));
        return new OpenCvYuNetModelInfo
        {
            ModelDirectory = directory,
            ModelPath = Path.Combine(directory, DefaultModelFileName)
        };
    }
}
