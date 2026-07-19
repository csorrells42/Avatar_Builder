using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using System.IO;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvFacemarkModelInfo
{
    private const string RelativeModelDirectory = "dependencies/vision/opencv/facemark";
    private const string DefaultModelFileName = "lbfmodel.yaml";

    public string ModelDirectory { get; init; } = "";

    public string ModelPath { get; init; } = "";

    public bool ModelExists => File.Exists(ModelPath);

    public bool IsReady => ModelExists;

    public string Status => ModelExists
        ? "OpenCV LBF facemark model ready"
        : "OpenCV LBF facemark model missing";

    public static OpenCvFacemarkModelInfo Load()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            RelativeModelDirectory.Replace('/', Path.DirectorySeparatorChar));
        return new OpenCvFacemarkModelInfo
        {
            ModelDirectory = directory,
            ModelPath = Path.Combine(directory, DefaultModelFileName)
        };
    }
}
