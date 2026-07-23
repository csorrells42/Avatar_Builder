using System;
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

	public string Status
	{
		get
		{
			if (!ModelExists)
			{
				return "OpenCV LBF facemark model missing";
			}
			return "OpenCV LBF facemark model ready";
		}
	}

	public static OpenCvFacemarkModelInfo Load()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "dependencies/vision/opencv/facemark".Replace('/', Path.DirectorySeparatorChar));
		return new OpenCvFacemarkModelInfo
		{
			ModelDirectory = text,
			ModelPath = Path.Combine(text, "lbfmodel.yaml")
		};
	}
}
