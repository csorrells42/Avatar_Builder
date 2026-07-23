using System;
using System.Collections.Generic;
using System.Windows;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record ApertureEstimate(bool HasAperture, OpenCvSharp.Rect ApertureBox, IReadOnlyList<System.Windows.Point> Contour, double Confidence, double GlareRatio = 0.0, double ContrastScore = 0.0, double SharpnessScore = 0.0, double DarkCoverageRatio = 0.0, double? AverageOpeningRatio = null, int ProfileSampleCount = 0, double ProfileCoverageRatio = 0.0)
{
	public static ApertureEstimate None { get; } = new ApertureEstimate(HasAperture: false, default(OpenCvSharp.Rect), Array.Empty<System.Windows.Point>(), 0.0);

	public static ApertureEstimate FromDiagnostics(ApertureImageQuality imageQuality)
	{
		return new ApertureEstimate(HasAperture: false, default(OpenCvSharp.Rect), Array.Empty<System.Windows.Point>(), 0.0, imageQuality.GlareRatio, imageQuality.ContrastScore, imageQuality.SharpnessScore);
	}
}
