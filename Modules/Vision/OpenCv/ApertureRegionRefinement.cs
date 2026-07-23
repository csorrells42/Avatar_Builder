using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record ApertureRegionRefinement(Rect Box, ApertureEstimate Estimate, double Score);
