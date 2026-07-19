using System.Globalization;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarCaptureQualityAnalyzer
{
    private const double MinimumCollectScorePercent = 62d;
    private const double MinimumCollectCameraModeScorePercent = 60d;
    private const double MinimumAvatarScorePercent = 80d;
    private const double MinimumAvatarCameraModeScorePercent = 84d;
    private const double MinimumAvatarEyeScorePercent = 72d;
    private const double MinimumAvatarFaceScaleScorePercent = 70d;
    private const double MinimumAvatarStabilityScorePercent = 70d;

    public AvatarCaptureQualityAssessment Analyze(AvatarCaptureQualityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var issues = new List<string>();
        var suggestions = new List<string>();

        if (!input.Metrics.HasFace || !input.LandmarkFrame.HasFace)
        {
            issues.Add("no face landmark lock");
            suggestions.Add("Keep the face visible and let the tracker lock before collecting measurements.");
            return BuildAssessment(
                "no-face",
                0d,
                false,
                false,
                "no face landmark lock",
                0d,
                0d,
                0d,
                0d,
                0d,
                0d,
                CalculateStorageScore(input, issues, suggestions),
                null,
                null,
                issues,
                suggestions);
        }

        var cameraModeScore = CalculateCameraModeScore(input, issues, suggestions);
        var faceScale = CalculateFaceScaleScore(input, issues, suggestions);
        var eyeScore = CalculateEyeEvidenceScore(input, issues, suggestions);
        var mouthScore = CalculateMouthEvidenceScore(input, issues, suggestions);
        var stabilityScore = CalculateStabilityScore(input, issues, suggestions);
        var glassesScore = CalculateGlassesRiskScore(input, issues, suggestions);
        var storageScore = CalculateStorageScore(input, issues, suggestions);

        var score = Round(
            cameraModeScore * 0.14d
            + faceScale.ScorePercent * 0.18d
            + eyeScore * 0.26d
            + mouthScore * 0.14d
            + stabilityScore * 0.16d
            + glassesScore * 0.06d
            + storageScore * 0.06d);

        var subjectGateOpen = input.SubjectConfirmed;
        var modelAccepting = input.AvatarCaptureRequested && input.CaptureGateAccepted;
        if (!subjectGateOpen)
        {
            issues.Add("subject confirmation is off");
            suggestions.Add("Only enable collection when the enrolled subject is in front of the webcam.");
        }
        else if (!input.AvatarCaptureRequested)
        {
            issues.Add("avatar capture is stopped");
            suggestions.Add("Click Start Avatar Capture when the selected user is ready to collect 3DDFA samples.");
        }
        else if (!modelAccepting)
        {
            issues.Add("avatar capture is waiting");
            if (!string.IsNullOrWhiteSpace(input.CaptureGateReason))
            {
                suggestions.Add(input.CaptureGateReason);
            }
        }

        var canCollect = subjectGateOpen
            && modelAccepting
            && score >= MinimumCollectScorePercent
            && cameraModeScore >= MinimumCollectCameraModeScorePercent
            && storageScore >= 35d;
        var avatarReady = canCollect
            && score >= MinimumAvatarScorePercent
            && cameraModeScore >= MinimumAvatarCameraModeScorePercent
            && eyeScore >= MinimumAvatarEyeScorePercent
            && faceScale.ScorePercent >= MinimumAvatarFaceScaleScorePercent
            && stabilityScore >= MinimumAvatarStabilityScorePercent;
        var label = score switch
        {
            >= MinimumAvatarScorePercent when avatarReady => "avatar-grade",
            >= 72d => "strong",
            >= MinimumCollectScorePercent => "usable",
            >= 38d => "limited",
            _ => "low"
        };
        var reason = BuildPrimaryReason(canCollect, avatarReady, score, issues);
        return BuildAssessment(
            label,
            score,
            canCollect,
            avatarReady,
            reason,
            cameraModeScore,
            faceScale.ScorePercent,
            eyeScore,
            mouthScore,
            stabilityScore,
            glassesScore,
            storageScore,
            faceScale.FaceWidthPercent,
            faceScale.FaceHeightPercent,
            issues,
            suggestions);
    }

    private static double CalculateCameraModeScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        if (input.IsAutoCameraMode || input.VideoWidth is not int width || input.VideoHeight is not int height)
        {
            issues.Add("camera mode is auto or unknown");
            suggestions.Add("Use the explicit 3840x2160 30 fps mode when practical.");
            return 58d;
        }

        var pixelScore = (width, height) switch
        {
            (>= 3840, >= 2160) => 100d,
            (>= 2560, >= 1440) => 84d,
            (>= 1920, >= 1080) => 68d,
            (>= 1280, >= 720) => 45d,
            _ => 25d
        };
        if (width < 1920 || height < 1080)
        {
            issues.Add($"camera mode is low resolution: {width}x{height}");
            suggestions.Add("Switch to 4K or at least 1080p before collecting long-term face measurements.");
        }

        var fps = input.FramesPerSecond.GetValueOrDefault(0d);
        var fpsScore = fps switch
        {
            >= 29d => 100d,
            >= 20d => 82d,
            >= 10d => 60d,
            > 0d => 38d,
            _ => 70d
        };
        if (fps is > 0d and < 20d)
        {
            issues.Add($"camera frame rate is low: {fps:0.#} fps");
            suggestions.Add("Use 30 fps when possible so blink and jaw motion timing stay useful.");
        }

        var format = input.InputFormat ?? "";
        var formatScore = format.Contains("mjpg", StringComparison.OrdinalIgnoreCase)
            || format.Contains("mjpeg", StringComparison.OrdinalIgnoreCase)
            || format.Contains("nv12", StringComparison.OrdinalIgnoreCase)
            || format.Contains("yuy2", StringComparison.OrdinalIgnoreCase)
                ? 100d
                : string.IsNullOrWhiteSpace(format) ? 84d : 74d;

        return Round(pixelScore * 0.66d + fpsScore * 0.24d + formatScore * 0.10d);
    }

    private static FaceScaleScore CalculateFaceScaleScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        var bounds = GetBounds(input.LandmarkFrame.FaceContour);
        if (bounds is null)
        {
            issues.Add("face contour unavailable");
            suggestions.Add("Wait for dense face landmarks before collecting avatar measurements.");
            return new FaceScaleScore(0d, null, null);
        }

        var widthPercent = bounds.Value.Width * 100d;
        var heightPercent = bounds.Value.Height * 100d;
        var dimensionScore = ScoreIdealRange(widthPercent, 18d, 55d, 9d, 72d) * 0.55d
            + ScoreIdealRange(heightPercent, 24d, 70d, 13d, 86d) * 0.45d;
        if (widthPercent < 14d || heightPercent < 18d)
        {
            issues.Add($"face is small in frame: {widthPercent:0}% wide, {heightPercent:0}% tall");
            suggestions.Add("Move closer, improve lighting, or let the camera track tighter for eyelid detail.");
        }
        else if (widthPercent > 70d || heightPercent > 86d)
        {
            issues.Add($"face is very close/cropped: {widthPercent:0}% wide, {heightPercent:0}% tall");
            suggestions.Add("Move back slightly so the full face, eyes, lips, and jaw remain visible.");
        }

        return new FaceScaleScore(Round(dimensionScore), Round(widthPercent), Round(heightPercent));
    }

    private static double CalculateEyeEvidenceScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        var metrics = input.Metrics;
        var contourScore = input.LandmarkFrame.HasEyeContours ? 100d : 30d;
        var confidenceScore = Math.Clamp(metrics.EyeConfidence * 100d, 0d, 100d);
        var qualityScore = Math.Clamp(metrics.EyeMeasurementQualityPercent, 0d, 100d);
        var imageScore = metrics.EyeImageQualityAvailable
            ? Math.Clamp(
                (100d - metrics.EyeGlarePercent) * 0.36d
                + metrics.EyeContrastPercent * 0.34d
                + metrics.EyeSharpnessPercent * 0.30d,
                0d,
                100d)
            : 64d;
        var reconstructionPenalty = metrics.AnyEyeReconstructed ? 12d : 0d;
        var artifactPenalty = metrics.EyeArtifactSuppressed ? 10d : 0d;
        var score = Round(
            contourScore * 0.20d
            + confidenceScore * 0.22d
            + qualityScore * 0.36d
            + imageScore * 0.22d
            - reconstructionPenalty
            - artifactPenalty);

        if (!metrics.IsEyeMeasurementUsable)
        {
            issues.Add("eye measurement is weak");
            suggestions.Add("Reduce glasses glare, sharpen focus, and keep both eyelids visible.");
        }

        if (metrics.EyeImageQualityAvailable && metrics.EyeGlarePercent > 32d)
        {
            issues.Add($"eye glare is high: {metrics.EyeGlarePercent:0}%");
            suggestions.Add("Shift monitor brightness, room light, camera angle, or glasses angle to reduce reflections.");
        }

        return Math.Clamp(score, 0d, 100d);
    }

    private static double CalculateMouthEvidenceScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        var metrics = input.Metrics;
        var contourScore = input.LandmarkFrame.HasMouthContours ? 100d : 35d;
        var confidenceScore = Math.Clamp(metrics.MouthConfidence * 100d, 0d, 100d);
        var qualityScore = Math.Clamp(metrics.MouthMeasurementQualityPercent, 0d, 100d);
        var imageScore = metrics.MouthImageQualityAvailable
            ? Math.Clamp(
                (100d - metrics.MouthGlarePercent) * 0.22d
                + metrics.MouthContrastPercent * 0.40d
                + metrics.MouthSharpnessPercent * 0.38d,
                0d,
                100d)
            : 64d;
        var reconstructionPenalty = metrics.MouthReconstructed ? 10d : 0d;
        var score = Round(
            contourScore * 0.18d
            + confidenceScore * 0.20d
            + qualityScore * 0.38d
            + imageScore * 0.24d
            - reconstructionPenalty);

        if (!metrics.IsMouthMeasurementUsable || !metrics.IsJawDroopMeasurementUsable)
        {
            issues.Add("mouth/jaw measurement is weak");
            suggestions.Add("Keep the lower face visible and use enough light for lip contrast.");
        }

        return Math.Clamp(score, 0d, 100d);
    }

    private static double CalculateStabilityScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        var stability = input.Stability;
        if (stability.SampleCount < 3)
        {
            issues.Add("face lock is still warming");
            suggestions.Add("Hold a stable pose briefly before collecting long-term measurements.");
            return 38d;
        }

        var score = Round(
            stability.CompositeReliabilityPercent * 0.42d
            + stability.FaceContinuityPercent * 0.24d
            + stability.EyeReliabilityPercent * 0.22d
            + stability.MouthReliabilityPercent * 0.12d);
        if (score < 68d)
        {
            issues.Add($"face lock stability is limited: {score:0}%");
            suggestions.Add("Improve light, reduce motion blur, or wait for the tracker to settle.");
        }

        return Math.Clamp(score, 0d, 100d);
    }

    private static double CalculateGlassesRiskScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        var metrics = input.Metrics;
        if (!metrics.EyeImageQualityAvailable)
        {
            return metrics.EyeArtifactSuppressed || metrics.PossibleOneEyeArtifact ? 48d : 70d;
        }

        var score = 100d
            - Math.Clamp(metrics.EyeGlarePercent * 0.95d, 0d, 55d)
            - Math.Clamp((100d - metrics.EyeContrastPercent) * 0.18d, 0d, 18d)
            - Math.Clamp((100d - metrics.EyeSharpnessPercent) * 0.16d, 0d, 16d)
            - (metrics.PossibleOneEyeArtifact ? 12d : 0d)
            - (metrics.EyeArtifactSuppressed ? 10d : 0d);
        if (score < 58d)
        {
            issues.Add("glasses/eye artifact risk is high");
            suggestions.Add("Collect a quick lighting/glare check before relying on these measurements for avatar capture.");
        }

        return Round(Math.Clamp(score, 0d, 100d));
    }

    private static double CalculateStorageScore(
        AvatarCaptureQualityInput input,
        ICollection<string> issues,
        ICollection<string> suggestions)
    {
        return 100d;
    }

    private static AvatarCaptureQualityAssessment BuildAssessment(
        string label,
        double scorePercent,
        bool canCollect,
        bool avatarReady,
        string primaryReason,
        double cameraModeScore,
        double faceScaleScore,
        double eyeScore,
        double mouthScore,
        double stabilityScore,
        double glassesScore,
        double storageScore,
        double? faceWidthPercent,
        double? faceHeightPercent,
        IReadOnlyList<string> issues,
        IReadOnlyList<string> suggestions)
    {
        var status = BuildStatusLine(label, scorePercent, avatarReady, primaryReason, faceWidthPercent, eyeScore, mouthScore, glassesScore);
        return new AvatarCaptureQualityAssessment
        {
            Label = label,
            ScorePercent = Round(scorePercent),
            CanCollectMeasurements = canCollect,
            StrongEnoughForAvatarLearning = avatarReady,
            PrimaryReason = primaryReason,
            StatusLine = status,
            CameraModeScorePercent = Round(cameraModeScore),
            FaceScaleScorePercent = Round(faceScaleScore),
            EyeEvidenceScorePercent = Round(eyeScore),
            MouthEvidenceScorePercent = Round(mouthScore),
            StabilityScorePercent = Round(stabilityScore),
            GlassesRiskScorePercent = Round(glassesScore),
            StorageScorePercent = Round(storageScore),
            FaceWidthPercent = faceWidthPercent,
            FaceHeightPercent = faceHeightPercent,
            Issues = issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Suggestions = suggestions
                .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList()
        };
    }

    private static string BuildPrimaryReason(
        bool canCollect,
        bool avatarReady,
        double score,
        IReadOnlyList<string> issues)
    {
        if (avatarReady)
        {
            return "strong enough for long-term avatar measurements";
        }

        if (canCollect)
        {
            return "usable for personal measurements; not avatar-grade yet";
        }

        return issues.Count > 0
            ? issues[0]
            : $"quality score {score:0}% below collection threshold";
    }

    private static string BuildStatusLine(
        string label,
        double score,
        bool avatarReady,
        string reason,
        double? faceWidthPercent,
        double eyeScore,
        double mouthScore,
        double glassesScore)
    {
        var face = faceWidthPercent is double width
            ? $"face {width:0}% frame"
            : "face --";
        var avatar = avatarReady ? "avatar-grade" : reason;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Capture quality: {label} {score:0}% | {face} | eyes {eyeScore:0}% | mouth {mouthScore:0}% | glasses {glassesScore:0}% | {avatar}");
    }

    private static Rect? GetBounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var left = points.Min(static point => point.X);
        var top = points.Min(static point => point.Y);
        var right = points.Max(static point => point.X);
        var bottom = points.Max(static point => point.Y);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    private static double ScoreIdealRange(
        double value,
        double idealMin,
        double idealMax,
        double weakMin,
        double weakMax)
    {
        if (value >= idealMin && value <= idealMax)
        {
            return 100d;
        }

        if (value < idealMin)
        {
            return value <= weakMin
                ? Math.Clamp(value / weakMin * 42d, 0d, 42d)
                : 42d + (value - weakMin) / (idealMin - weakMin) * 58d;
        }

        return value >= weakMax
            ? 45d
            : 100d - (value - idealMax) / (weakMax - idealMax) * 55d;
    }

    private static double Round(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed record FaceScaleScore(double ScorePercent, double? FaceWidthPercent, double? FaceHeightPercent);
}
