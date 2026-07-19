using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.OpenCv;
using AvatarBuilder.Modules.Vision.Common;
using System.Windows.Media.Imaging;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Pipeline;

public sealed class CompositeFaceLandmarkTracker : IStatefulFaceLandmarkTracker
{
    private static readonly TimeSpan PreviousFaceRecoveryWindow = TimeSpan.FromSeconds(2.5d);

    private readonly IReadOnlyList<IFaceLandmarkTracker> _trackers;
    private string _lastBackendStatus = "waiting";
    private Rect? _lastFusedFace;
    private DateTime _lastFusedFaceCapturedAtUtc = DateTime.MinValue;

    public CompositeFaceLandmarkTracker()
        : this(CreateDefaultTrackers())
    {
    }

    public CompositeFaceLandmarkTracker(IReadOnlyList<IFaceLandmarkTracker> trackers)
    {
        _trackers = trackers;
    }

    public string Name => "Composite landmark tracker";

    public bool IsAvailable => _trackers.Any(tracker => tracker.IsAvailable);

    public string LastBackendStatus => _lastBackendStatus;

    public int MaxDetectionDimension
    {
        get => _trackers.Count == 0 ? 960 : _trackers.Max(tracker => tracker.MaxDetectionDimension);
        set
        {
            foreach (var tracker in _trackers)
            {
                tracker.MaxDetectionDimension = value;
            }
        }
    }

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        var statuses = new List<string>();
        FaceLandmarkTrackingResult? fused = null;
        IFaceLandmarkCropRefiner? cropRefiner = null;
        foreach (var tracker in _trackers)
        {
            if (cropRefiner is null && tracker is IFaceLandmarkCropRefiner refiner && refiner.IsAvailable)
            {
                cropRefiner = refiner;
            }

            if (!tracker.IsAvailable)
            {
                var skipped = tracker.Detect(bitmap, capturedAtUtc);
                if (!string.IsNullOrWhiteSpace(skipped.BackendStatus))
                {
                    statuses.Add($"{tracker.Name}: {skipped.BackendStatus}");
                }

                continue;
            }

            var result = tracker.Detect(bitmap, capturedAtUtc);
            if (result.HasFace)
            {
                statuses.Add($"{result.BackendName}: {result.BackendStatus}");
                if (HasMediaPipeDenseLock(result) && HasUsableFaceCueGeometry(result))
                {
                    _lastBackendStatus = string.Join(" | ", statuses);
                    _lastFusedFace = GetFaceBounds(result) ?? _lastFusedFace;
                    _lastFusedFaceCapturedAtUtc = capturedAtUtc;
                    return new FaceLandmarkTrackingResult
                    {
                        BackendName = result.BackendName,
                        BackendStatus = _lastBackendStatus,
                        FeatureDetection = result.FeatureDetection,
                        LandmarkFrame = result.LandmarkFrame,
                        Diagnostics = result.Diagnostics
                    };
                }

                fused = fused is null ? result : FuseResults(fused, result, capturedAtUtc, _lastFusedFace);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.BackendStatus))
            {
                statuses.Add($"{result.BackendName}: {result.BackendStatus}");
            }
        }

        if (fused is not null)
        {
            fused = TryRefineWithFaceCrop(bitmap, capturedAtUtc, fused, cropRefiner, statuses);
            _lastBackendStatus = string.Join(" | ", statuses);
            _lastFusedFace = GetFaceBounds(fused) ?? _lastFusedFace;
            _lastFusedFaceCapturedAtUtc = capturedAtUtc;
            return new FaceLandmarkTrackingResult
            {
                BackendName = fused.BackendName,
                BackendStatus = _lastBackendStatus,
                FeatureDetection = fused.FeatureDetection,
                LandmarkFrame = fused.LandmarkFrame,
                Diagnostics = fused.Diagnostics
            };
        }

        var recovered = TryRecoverWithPreviousFaceCrop(bitmap, capturedAtUtc, cropRefiner, statuses);
        if (recovered is not null)
        {
            _lastBackendStatus = string.Join(" | ", statuses);
            _lastFusedFace = GetFaceBounds(recovered) ?? _lastFusedFace;
            _lastFusedFaceCapturedAtUtc = capturedAtUtc;
            return new FaceLandmarkTrackingResult
            {
                BackendName = recovered.BackendName,
                BackendStatus = _lastBackendStatus,
                FeatureDetection = recovered.FeatureDetection,
                LandmarkFrame = recovered.LandmarkFrame,
                Diagnostics = recovered.Diagnostics
            };
        }

        _lastBackendStatus = statuses.Count == 0
            ? (IsAvailable ? "all trackers searching" : "no landmark backend available")
            : string.Join(" | ", statuses);
        if (capturedAtUtc - _lastFusedFaceCapturedAtUtc > PreviousFaceRecoveryWindow)
        {
            _lastFusedFace = null;
            _lastFusedFaceCapturedAtUtc = DateTime.MinValue;
        }

        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = _lastBackendStatus
        };
    }

    public void Dispose()
    {
        foreach (var tracker in _trackers)
        {
            tracker.Dispose();
        }
    }

    private static IReadOnlyList<IFaceLandmarkTracker> CreateDefaultTrackers()
    {
        var trackers = new List<IFaceLandmarkTracker>(3);
        try
        {
            AddOwnedTracker(trackers, static () => new MediaPipeFaceLandmarkerSidecarTracker());
            AddOwnedTracker(trackers, static () => new OpenCvFacemarkLandmarkTracker());
            AddOwnedTracker(trackers, static () => new OpenCvApertureLandmarkTracker());
            return trackers;
        }
        catch
        {
            foreach (var tracker in trackers)
            {
                tracker.Dispose();
            }

            throw;
        }
    }

    private static void AddOwnedTracker(
        ICollection<IFaceLandmarkTracker> trackers,
        Func<IFaceLandmarkTracker> createTracker)
    {
        IFaceLandmarkTracker? tracker = null;
        try
        {
            tracker = createTracker();
            trackers.Add(tracker);
            tracker = null;
        }
        finally
        {
            tracker?.Dispose();
        }
    }

    private static FaceLandmarkTrackingResult TryRefineWithFaceCrop(
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        FaceLandmarkTrackingResult fused,
        IFaceLandmarkCropRefiner? cropRefiner,
        List<string> statuses)
    {
        if (cropRefiner is null || HasMediaPipeDenseLock(fused))
        {
            return fused;
        }

        var faceHint = GetFaceBounds(fused);
        if (faceHint is not Rect face || face.Width <= 0d || face.Height <= 0d)
        {
            return fused;
        }

        var cropResult = cropRefiner.DetectFaceCrop(bitmap, face, capturedAtUtc);
        if (!string.IsNullOrWhiteSpace(cropResult.BackendStatus))
        {
            statuses.Add($"{cropResult.BackendName}: {cropResult.BackendStatus}");
        }

        if (!cropResult.HasFace || !HasMediaPipeDenseLock(cropResult) || !FacesAgree(cropResult, fused))
        {
            return fused;
        }

        return FuseResults(cropResult, fused, capturedAtUtc, previousFusedFace: null);
    }

    private FaceLandmarkTrackingResult? TryRecoverWithPreviousFaceCrop(
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        IFaceLandmarkCropRefiner? cropRefiner,
        List<string> statuses)
    {
        if (cropRefiner is null
            || _lastFusedFace is not Rect previousFace
            || previousFace.Width <= 0d
            || previousFace.Height <= 0d)
        {
            return null;
        }

        var previousAge = capturedAtUtc - _lastFusedFaceCapturedAtUtc;
        if (previousAge < TimeSpan.Zero || previousAge > PreviousFaceRecoveryWindow)
        {
            return null;
        }

        var cropResult = cropRefiner.DetectFaceCrop(bitmap, previousFace, capturedAtUtc);
        if (!string.IsNullOrWhiteSpace(cropResult.BackendStatus))
        {
            statuses.Add($"{cropResult.BackendName}: {cropResult.BackendStatus}");
        }

        if (!cropResult.HasFace
            || !HasMediaPipeDenseLock(cropResult)
            || !HasUsableFaceCueGeometry(cropResult))
        {
            return null;
        }

        statuses.Add($"{cropResult.BackendName}: temporal recovery from previous face hint ({previousAge.TotalSeconds:0.00}s old)");
        return new FaceLandmarkTrackingResult
        {
            BackendName = cropResult.BackendName,
            BackendStatus = $"{cropResult.BackendStatus}; temporal recovery from previous face hint ({previousAge.TotalSeconds:0.00}s old)",
            FeatureDetection = cropResult.FeatureDetection,
            LandmarkFrame = cropResult.LandmarkFrame
        };
    }

    private static FaceLandmarkTrackingResult FuseResults(
        FaceLandmarkTrackingResult primary,
        FaceLandmarkTrackingResult candidate,
        DateTime capturedAtUtc,
        Rect? previousFusedFace)
    {
        if (!primary.HasFace)
        {
            return candidate;
        }

        if (!candidate.HasFace || !FacesAgree(primary, candidate))
        {
            return ChooseDisagreementResult(primary, candidate, previousFusedFace);
        }

        var primaryFrame = primary.LandmarkFrame;
        var candidateFrame = candidate.LandmarkFrame;
        var useCandidateEyes = ShouldUseCandidateEyes(primaryFrame, candidateFrame, candidate.BackendName);
        var useCandidateMouth = ShouldUseCandidateMouth(primaryFrame, candidateFrame, candidate.BackendName);
        if (!useCandidateEyes && !useCandidateMouth)
        {
            return primary;
        }

        var primaryFeature = primary.FeatureDetection;
        var candidateFeature = candidate.FeatureDetection;
        var fusedFrame = new FaceLandmarkFrame
        {
            HasFace = true,
            Source = $"{primaryFrame.Source}; fused {candidateFrame.Source}",
            CapturedAtUtc = capturedAtUtc,
            TrackingConfidence = Math.Max(primaryFrame.TrackingConfidence, candidateFrame.TrackingConfidence * 0.92d),
            EyeConfidence = useCandidateEyes ? candidateFrame.EyeConfidence : primaryFrame.EyeConfidence,
            MouthConfidence = useCandidateMouth ? candidateFrame.MouthConfidence : primaryFrame.MouthConfidence,
            EyeImageQualityAvailable = useCandidateEyes ? candidateFrame.EyeImageQualityAvailable : primaryFrame.EyeImageQualityAvailable,
            MouthImageQualityAvailable = useCandidateMouth ? candidateFrame.MouthImageQualityAvailable : primaryFrame.MouthImageQualityAvailable,
            EyeGlarePercent = useCandidateEyes ? candidateFrame.EyeGlarePercent : primaryFrame.EyeGlarePercent,
            MouthGlarePercent = useCandidateMouth ? candidateFrame.MouthGlarePercent : primaryFrame.MouthGlarePercent,
            EyeContrastPercent = useCandidateEyes ? candidateFrame.EyeContrastPercent : primaryFrame.EyeContrastPercent,
            MouthContrastPercent = useCandidateMouth ? candidateFrame.MouthContrastPercent : primaryFrame.MouthContrastPercent,
            EyeSharpnessPercent = useCandidateEyes ? candidateFrame.EyeSharpnessPercent : primaryFrame.EyeSharpnessPercent,
            MouthSharpnessPercent = useCandidateMouth ? candidateFrame.MouthSharpnessPercent : primaryFrame.MouthSharpnessPercent,
            EyeDarkCoveragePercent = useCandidateEyes ? candidateFrame.EyeDarkCoveragePercent : primaryFrame.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = useCandidateMouth ? candidateFrame.MouthDarkCoveragePercent : primaryFrame.MouthDarkCoveragePercent,
            LeftEyeReconstructed = useCandidateEyes ? candidateFrame.LeftEyeReconstructed : primaryFrame.LeftEyeReconstructed,
            RightEyeReconstructed = useCandidateEyes ? candidateFrame.RightEyeReconstructed : primaryFrame.RightEyeReconstructed,
            MouthReconstructed = useCandidateMouth ? candidateFrame.MouthReconstructed : primaryFrame.MouthReconstructed,
            EyeArtifactSuppressed = useCandidateEyes ? candidateFrame.EyeArtifactSuppressed : primaryFrame.EyeArtifactSuppressed,
            HeadYawDegrees = primaryFrame.HeadYawDegrees,
            HeadPitchDegrees = primaryFrame.HeadPitchDegrees,
            HeadRollDegrees = primaryFrame.HeadRollDegrees,
            BlendshapeScores = primaryFrame.BlendshapeScores.Count > 0 ? primaryFrame.BlendshapeScores : candidateFrame.BlendshapeScores,
            DenseMeshTopology = primaryFrame.DenseMeshPoints.Count > 0 ? primaryFrame.DenseMeshTopology : candidateFrame.DenseMeshTopology,
            DenseMeshPoints = primaryFrame.DenseMeshPoints.Count > 0 ? primaryFrame.DenseMeshPoints : candidateFrame.DenseMeshPoints,
            FacialTransformationMatrix = primaryFrame.FacialTransformationMatrix.Count > 0 ? primaryFrame.FacialTransformationMatrix : candidateFrame.FacialTransformationMatrix,
            FaceContour = primaryFrame.FaceContour.Count > 0 ? primaryFrame.FaceContour : candidateFrame.FaceContour,
            LeftEyeContour = useCandidateEyes ? candidateFrame.LeftEyeContour : primaryFrame.LeftEyeContour,
            RightEyeContour = useCandidateEyes ? candidateFrame.RightEyeContour : primaryFrame.RightEyeContour,
            LeftBrowContour = useCandidateEyes && candidateFrame.LeftBrowContour.Count > 0 ? candidateFrame.LeftBrowContour : primaryFrame.LeftBrowContour.Count > 0 ? primaryFrame.LeftBrowContour : candidateFrame.LeftBrowContour,
            RightBrowContour = useCandidateEyes && candidateFrame.RightBrowContour.Count > 0 ? candidateFrame.RightBrowContour : primaryFrame.RightBrowContour.Count > 0 ? primaryFrame.RightBrowContour : candidateFrame.RightBrowContour,
            OuterLipContour = useCandidateMouth ? candidateFrame.OuterLipContour : primaryFrame.OuterLipContour,
            InnerLipContour = useCandidateMouth ? candidateFrame.InnerLipContour : primaryFrame.InnerLipContour,
            JawContour = primaryFrame.JawContour.Count > 0 ? primaryFrame.JawContour : candidateFrame.JawContour
        };

        var fusedFeature = new FaceFeatureDetection
        {
            HasFace = true,
            Source = $"{primaryFeature.Source}; fused {candidateFeature.Source}",
            FaceBox = primaryFeature.HasFace ? primaryFeature.FaceBox : candidateFeature.FaceBox,
            LeftEyeBox = useCandidateEyes ? candidateFeature.LeftEyeBox ?? primaryFeature.LeftEyeBox : primaryFeature.LeftEyeBox,
            RightEyeBox = useCandidateEyes ? candidateFeature.RightEyeBox ?? primaryFeature.RightEyeBox : primaryFeature.RightEyeBox,
            MouthBox = useCandidateMouth ? candidateFeature.MouthBox ?? primaryFeature.MouthBox : primaryFeature.MouthBox,
            TrackingConfidence = fusedFrame.TrackingConfidence,
            EyeConfidence = fusedFrame.EyeConfidence,
            MouthConfidence = fusedFrame.MouthConfidence,
            EyeImageQualityAvailable = fusedFrame.EyeImageQualityAvailable,
            MouthImageQualityAvailable = fusedFrame.MouthImageQualityAvailable,
            EyeGlarePercent = fusedFrame.EyeGlarePercent,
            MouthGlarePercent = fusedFrame.MouthGlarePercent,
            EyeContrastPercent = fusedFrame.EyeContrastPercent,
            MouthContrastPercent = fusedFrame.MouthContrastPercent,
            EyeSharpnessPercent = fusedFrame.EyeSharpnessPercent,
            MouthSharpnessPercent = fusedFrame.MouthSharpnessPercent,
            EyeDarkCoveragePercent = fusedFrame.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = fusedFrame.MouthDarkCoveragePercent,
            FaceContour = fusedFrame.FaceContour,
            LeftEyeContour = fusedFrame.LeftEyeContour,
            RightEyeContour = fusedFrame.RightEyeContour,
            OuterLipContour = fusedFrame.OuterLipContour,
            InnerLipContour = fusedFrame.InnerLipContour,
            JawContour = fusedFrame.JawContour
        };

        return new FaceLandmarkTrackingResult
        {
            BackendName = "Composite landmark fusion",
            BackendStatus = $"{primary.BackendStatus}; fused {candidate.BackendStatus}",
            FeatureDetection = fusedFeature,
            LandmarkFrame = fusedFrame
        };
    }

    private static bool ShouldUseCandidateEyes(FaceLandmarkFrame primary, FaceLandmarkFrame candidate, string candidateBackend)
    {
        if (!candidate.HasEyeContours || candidate.EyeConfidence < 0.20d)
        {
            return false;
        }

        if (!primary.HasEyeContours)
        {
            return true;
        }

        if (IsHighFidelityLandmarkSource(primary.Source)
            && candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
        {
            return primary.EyeConfidence < 0.55d
                || candidate.EyeConfidence >= primary.EyeConfidence + 0.16d
                || primary.EyeArtifactSuppressed
                || !primary.HasEyeContours;
        }

        return candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase)
            || candidate.EyeConfidence >= primary.EyeConfidence + 0.08d;
    }

    private static bool ShouldUseCandidateMouth(FaceLandmarkFrame primary, FaceLandmarkFrame candidate, string candidateBackend)
    {
        if (!candidate.HasMouthContours || candidate.MouthConfidence < 0.18d)
        {
            return false;
        }

        if (!primary.HasMouthContours)
        {
            return true;
        }

        if (IsHighFidelityLandmarkSource(primary.Source)
            && candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
        {
            return primary.MouthConfidence < 0.52d
                || candidate.MouthConfidence >= primary.MouthConfidence + 0.16d
                || primary.MouthReconstructed
                || !primary.HasMouthContours;
        }

        return candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase)
            || candidate.MouthConfidence >= primary.MouthConfidence + 0.08d;
    }

    private static bool IsHighFidelityLandmarkSource(string source)
    {
        return source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase)
            || source.Contains("dense", StringComparison.OrdinalIgnoreCase)
            || source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMediaPipeDenseLock(FaceLandmarkTrackingResult result)
    {
        return result.BackendStatus.Contains("MediaPipe dense landmark lock", StringComparison.OrdinalIgnoreCase)
            || result.LandmarkFrame.Source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase)
            || result.LandmarkFrame.Source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUsableFaceCueGeometry(FaceLandmarkTrackingResult result)
    {
        return result.LandmarkFrame.HasEyeContours || result.LandmarkFrame.HasMouthContours;
    }

    private static FaceLandmarkTrackingResult ChooseDisagreementResult(
        FaceLandmarkTrackingResult primary,
        FaceLandmarkTrackingResult candidate,
        Rect? previousFusedFace)
    {
        if (!candidate.HasFace)
        {
            return primary;
        }

        if (previousFusedFace is Rect previous)
        {
            var primaryContinuity = CalculateContinuityScore(GetFaceBounds(primary), previous);
            var candidateContinuity = CalculateContinuityScore(GetFaceBounds(candidate), previous);
            if (candidateContinuity >= primaryContinuity + 0.18d)
            {
                return new FaceLandmarkTrackingResult
                {
                    BackendName = candidate.BackendName,
                    BackendStatus = $"{candidate.BackendStatus}; selected over disagreeing {primary.BackendName} by temporal face continuity",
                    FeatureDetection = candidate.FeatureDetection,
                    LandmarkFrame = candidate.LandmarkFrame
                };
            }

            if (primaryContinuity >= candidateContinuity + 0.08d)
            {
                return primary;
            }
        }

        if (candidate.BackendName.Contains("aperture", StringComparison.OrdinalIgnoreCase)
            && !primary.BackendName.Contains("dense", StringComparison.OrdinalIgnoreCase)
            && candidate.LandmarkFrame.TrackingConfidence >= primary.LandmarkFrame.TrackingConfidence - 0.20d
            && (candidate.LandmarkFrame.EyeConfidence >= primary.LandmarkFrame.EyeConfidence
                || candidate.LandmarkFrame.MouthConfidence >= primary.LandmarkFrame.MouthConfidence))
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = candidate.BackendName,
                BackendStatus = $"{candidate.BackendStatus}; selected over disagreeing {primary.BackendName} by aperture cue confidence",
                FeatureDetection = candidate.FeatureDetection,
                LandmarkFrame = candidate.LandmarkFrame
            };
        }

        return primary;
    }

    private static double CalculateContinuityScore(Rect? face, Rect previous)
    {
        if (face is not Rect current || current.Width <= 0d || current.Height <= 0d)
        {
            return 0d;
        }

        var currentCenter = new Point(current.Left + current.Width / 2d, current.Top + current.Height / 2d);
        var previousCenter = new Point(previous.Left + previous.Width / 2d, previous.Top + previous.Height / 2d);
        var distance = Math.Sqrt(Math.Pow(currentCenter.X - previousCenter.X, 2d) + Math.Pow(currentCenter.Y - previousCenter.Y, 2d));
        var previousDiagonal = Math.Sqrt(previous.Width * previous.Width + previous.Height * previous.Height);
        var proximity = 1d - Math.Clamp(distance / Math.Max(0.001d, previousDiagonal * 1.20d), 0d, 1d);
        var overlap = OverlapOverSmaller(current, previous);
        var scaleSimilarity = LogSimilarity(
            current.Width * current.Height,
            Math.Max(0.000001d, previous.Width * previous.Height),
            toleranceFactor: 4d);
        return proximity * 0.42d + overlap * 0.42d + scaleSimilarity * 0.16d;
    }

    private static bool FacesAgree(FaceLandmarkTrackingResult primary, FaceLandmarkTrackingResult candidate)
    {
        var primaryBox = GetFaceBounds(primary);
        var candidateBox = GetFaceBounds(candidate);
        if (primaryBox is null || candidateBox is null)
        {
            return true;
        }

        var intersection = Rect.Intersect(primaryBox.Value, candidateBox.Value);
        var intersectionArea = Math.Max(0d, intersection.Width) * Math.Max(0d, intersection.Height);
        var smallerArea = Math.Min(primaryBox.Value.Width * primaryBox.Value.Height, candidateBox.Value.Width * candidateBox.Value.Height);
        if (smallerArea > 0d && intersectionArea / smallerArea >= 0.20d)
        {
            return true;
        }

        var primaryCenter = new Point(primaryBox.Value.Left + primaryBox.Value.Width / 2d, primaryBox.Value.Top + primaryBox.Value.Height / 2d);
        var candidateCenter = new Point(candidateBox.Value.Left + candidateBox.Value.Width / 2d, candidateBox.Value.Top + candidateBox.Value.Height / 2d);
        var distance = Math.Sqrt(Math.Pow(primaryCenter.X - candidateCenter.X, 2d) + Math.Pow(primaryCenter.Y - candidateCenter.Y, 2d));
        return distance <= Math.Max(primaryBox.Value.Height, candidateBox.Value.Height) * 0.45d;
    }

    private static double OverlapOverSmaller(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        var intersectionArea = Math.Max(0d, intersection.Width) * Math.Max(0d, intersection.Height);
        var smallerArea = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return smallerArea <= 0d ? 0d : intersectionArea / smallerArea;
    }

    private static double LogSimilarity(double value, double target, double toleranceFactor)
    {
        if (value <= 0d || target <= 0d)
        {
            return 0d;
        }

        var distance = Math.Abs(Math.Log(value / target));
        return 1d - Math.Clamp(distance / Math.Log(Math.Max(1.01d, toleranceFactor)), 0d, 1d);
    }

    private static Rect? GetFaceBounds(FaceLandmarkTrackingResult result)
    {
        if (result.FeatureDetection.HasFace && result.FeatureDetection.FaceBox.Width > 0d && result.FeatureDetection.FaceBox.Height > 0d)
        {
            return result.FeatureDetection.FaceBox;
        }

        return Bounds(result.LandmarkFrame.FaceContour);
    }

    private static Rect? Bounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var minX = points.Min(static point => point.X);
        var maxX = points.Max(static point => point.X);
        var minY = points.Min(static point => point.Y);
        var maxY = points.Max(static point => point.Y);
        return maxX <= minX || maxY <= minY
            ? null
            : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public void Reset()
    {
        _lastBackendStatus = "waiting";
        _lastFusedFace = null;
        _lastFusedFaceCapturedAtUtc = DateTime.MinValue;
        foreach (var tracker in _trackers.OfType<IStatefulFaceLandmarkTracker>())
        {
            tracker.Reset();
        }
    }
}
