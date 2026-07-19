using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Episodes;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.OpenCv;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Pipeline;
using AvatarBuilder.Modules.Vision.Reconstruction;
using OpenCvSharp;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

internal static class Program
{
    private const double OfflineReferenceHorizontalFovDegrees = 71.4d;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: AvatarBuilderVisionEval <image-or-video-path> [output-folder] [sample-fps] [--eye-inset bottom-right|left,top,width,height] [--write-overlays]");
            return 2;
        }

        try
        {
            var inputPath = Path.GetFullPath(args[0]);
            var outputFolder = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.Combine(
                    Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
                    $"{Path.GetFileNameWithoutExtension(inputPath)}_vision_eval");
            var sampleFps = args.Length >= 3 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
                ? Math.Clamp(parsedFps, 0.2d, 10d)
                : 2d;
            var eyeInsetRegion = ParseEyeInsetRegion(args, 3);
            var writeOverlays = args.Any(static arg => arg.Equals("--write-overlays", StringComparison.OrdinalIgnoreCase));

            Directory.CreateDirectory(outputFolder);
            var result = IsImagePath(inputPath)
                ? EvaluateImage(inputPath, outputFolder, eyeInsetRegion, writeOverlays)
                : EvaluateVideo(inputPath, outputFolder, sampleFps, eyeInsetRegion, writeOverlays);
            WriteOutputs(inputPath, outputFolder, sampleFps, result.AppliedEyeInsetRegion, writeOverlays, result.Records, result.PersonalModel);
            Console.WriteLine($"Vision evaluation wrote {result.Records.Count} frame record(s) to {outputFolder}");
            return result.Records.Count == 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static VisionEvaluationResult EvaluateImage(
        string inputPath,
        string outputFolder,
        EyeInsetRegion? eyeInsetRegion,
        bool writeOverlays)
    {
        using var image = Cv2.ImRead(inputPath, ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidOperationException($"Could not read image: {inputPath}");
        }

        using var tracker = new CompositeFaceLandmarkTracker { MaxDetectionDimension = 1280 };
        var reconstructor = new FaceLandmarkTemporalReconstructor();
        var calculator = new FaceLandmarkMetricCalculator();
        var cueAnalyzer = new FaceLandmarkCueAnalyzer();
        var trendAnalyzer = new FaceLandmarkTrendAnalyzer();
        var stabilityAnalyzer = new FaceLockStabilityAnalyzer();
        var headPoseEstimator = new HeadPoseEstimator();
        var eyeInsetCueAnalyzer = new EyeInsetCueAnalyzer();
        var personalModelBuilder = new PersonalFaceModelBuilder();
        var captureQualityAnalyzer = new PersonalFaceCaptureQualityAnalyzer();
        var records = new List<VisionFrameRecord>
        {
            EvaluateFrame(
            tracker,
            reconstructor,
            calculator,
            cueAnalyzer,
            trendAnalyzer,
            stabilityAnalyzer,
            headPoseEstimator,
            eyeInsetCueAnalyzer,
            personalModelBuilder,
            captureQualityAnalyzer,
            image,
            0,
            0d,
            image.Width,
            image.Height,
            null,
            eyeInsetRegion,
            CreateOverlayFolder(outputFolder, writeOverlays))
        };
        return new VisionEvaluationResult(records, personalModelBuilder.CurrentModel, eyeInsetRegion);
    }

    private static VisionEvaluationResult EvaluateVideo(
        string inputPath,
        string outputFolder,
        double sampleFps,
        EyeInsetRegion? eyeInsetRegion,
        bool writeOverlays)
    {
        using var capture = new VideoCapture(inputPath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException($"Could not open video: {inputPath}");
        }

        using var tracker = new CompositeFaceLandmarkTracker { MaxDetectionDimension = 1280 };
        var reconstructor = new FaceLandmarkTemporalReconstructor();
        var calculator = new FaceLandmarkMetricCalculator();
        var cueAnalyzer = new FaceLandmarkCueAnalyzer();
        var trendAnalyzer = new FaceLandmarkTrendAnalyzer();
        var stabilityAnalyzer = new FaceLockStabilityAnalyzer();
        var headPoseEstimator = new HeadPoseEstimator();
        var eyeInsetCueAnalyzer = new EyeInsetCueAnalyzer();
        var personalModelBuilder = new PersonalFaceModelBuilder();
        var captureQualityAnalyzer = new PersonalFaceCaptureQualityAnalyzer();
        var records = new List<VisionFrameRecord>();
        var overlayFolder = CreateOverlayFolder(outputFolder, writeOverlays);
        var sourceFps = capture.Fps > 0d ? capture.Fps : 30d;
        var frameStep = Math.Max(1, (int)Math.Round(sourceFps / sampleFps));
        var autoEyeInset = eyeInsetRegion?.Label.Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
        var selectedEyeInsetRegion = autoEyeInset
            ? SelectAutoEyeInsetRegion(capture, frameStep)
            : eyeInsetRegion;
        using var frame = new Mat();
        var frameIndex = 0;

        while (capture.Read(frame))
        {
            if (frame.Empty())
            {
                break;
            }

            if (frameIndex % frameStep == 0)
            {
                records.Add(EvaluateFrame(
                    tracker,
                    reconstructor,
                    calculator,
                    cueAnalyzer,
                    trendAnalyzer,
                    stabilityAnalyzer,
                    headPoseEstimator,
                    eyeInsetCueAnalyzer,
                    personalModelBuilder,
                    captureQualityAnalyzer,
                    frame,
                    frameIndex,
                    frameIndex / sourceFps,
                    frame.Width,
                    frame.Height,
                    sourceFps,
                    selectedEyeInsetRegion,
                    overlayFolder));
            }

            frameIndex++;
        }

        return new VisionEvaluationResult(records, personalModelBuilder.CurrentModel, selectedEyeInsetRegion);
    }

    private static EyeInsetRegion? SelectAutoEyeInsetRegion(VideoCapture capture, int frameStep)
    {
        const int targetSampleCount = 12;
        var sampledFrames = new List<Mat>();
        using var frame = new Mat();
        var frameIndex = 0;
        try
        {
            while (sampledFrames.Count < targetSampleCount && capture.Read(frame))
            {
                if (!frame.Empty() && frameIndex % frameStep == 0)
                {
                    sampledFrames.Add(frame.Clone());
                }

                frameIndex++;
            }

            return EyeInsetApertureAnalyzer.SelectBestRegion(sampledFrames);
        }
        finally
        {
            foreach (var sampledFrame in sampledFrames)
            {
                sampledFrame.Dispose();
            }

            capture.Set(VideoCaptureProperties.PosFrames, 0);
        }
    }

    private static VisionFrameRecord EvaluateFrame(
        CompositeFaceLandmarkTracker tracker,
        FaceLandmarkTemporalReconstructor reconstructor,
        FaceLandmarkMetricCalculator calculator,
        FaceLandmarkCueAnalyzer cueAnalyzer,
        FaceLandmarkTrendAnalyzer trendAnalyzer,
        FaceLockStabilityAnalyzer stabilityAnalyzer,
        HeadPoseEstimator headPoseEstimator,
        EyeInsetCueAnalyzer eyeInsetCueAnalyzer,
        PersonalFaceModelBuilder personalModelBuilder,
        PersonalFaceCaptureQualityAnalyzer captureQualityAnalyzer,
        Mat frame,
        int frameIndex,
        double timestampSeconds,
        int frameWidth,
        int frameHeight,
        double? sourceFramesPerSecond,
        EyeInsetRegion? eyeInsetRegion,
        string overlayFolder)
    {
        var capturedAtUtc = DateTime.UnixEpoch.AddSeconds(timestampSeconds);
        var bitmap = CreateBitmapSource(frame);
        var result = tracker.Detect(bitmap, capturedAtUtc);
        var reconstructedFrame = reconstructor.Update(result.LandmarkFrame);
        var metrics = calculator.Update(reconstructedFrame);
        var headPose = headPoseEstimator.Estimate(new HeadPoseEstimatorInput
        {
            Frame = reconstructedFrame,
            FrameWidthPixels = frameWidth,
            FrameHeightPixels = frameHeight,
            Calibration = CreateOfflineHeadPoseCalibration(personalModelBuilder.CurrentModel)
        });
        var identityMeasurement = PersonalFaceIdentityMeasurement.FromFrame(reconstructedFrame);
        var cue = cueAnalyzer.Analyze(metrics);
        var trend = trendAnalyzer.Update(metrics);
        var stability = stabilityAnalyzer.Update(result.FeatureDetection, reconstructedFrame, metrics);
        var preflightModelUpdate = new PersonalFaceModelUpdate(
            true,
            PersonalFaceModelRejectionKind.None,
            "capture-quality preflight",
            1d,
            personalModelBuilder.CurrentModel);
        var captureQuality = captureQualityAnalyzer.Analyze(new PersonalFaceCaptureQualityInput
        {
            VideoWidth = frameWidth,
            VideoHeight = frameHeight,
            FramesPerSecond = sourceFramesPerSecond,
            InputFormat = "offline",
            LandmarkFrame = reconstructedFrame,
            Metrics = metrics,
            Stability = stability,
            PersonalModelUpdate = preflightModelUpdate,
            MeasurementJournalBytes = 0L
        });
        PersonalFaceModelUpdate personalModelUpdate;
        if (!captureQuality.CanCollectMeasurements)
        {
            personalModelUpdate = new PersonalFaceModelUpdate(
                false,
                CaptureQualityRejectionKind(captureQuality),
                $"capture quality gate: {captureQuality.PrimaryReason}",
                0d,
                personalModelBuilder.CurrentModel);
        }
        else
        {
            personalModelUpdate = personalModelBuilder.Update(
                reconstructedFrame,
                metrics,
                stability,
                cue,
                trend,
                headPose,
                allowEventLikeMeasurements: true);
            captureQuality = captureQualityAnalyzer.Analyze(new PersonalFaceCaptureQualityInput
            {
                VideoWidth = frameWidth,
                VideoHeight = frameHeight,
                FramesPerSecond = sourceFramesPerSecond,
                InputFormat = "offline",
                LandmarkFrame = reconstructedFrame,
                Metrics = metrics,
                Stability = stability,
                PersonalModelUpdate = personalModelUpdate,
                MeasurementJournalBytes = 0L
            });
        }
        var inset = eyeInsetRegion is null
            ? null
            : eyeInsetRegion.Label.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? EyeInsetApertureAnalyzer.AnalyzeBest(frame)
                : EyeInsetApertureAnalyzer.Analyze(frame, eyeInsetRegion);
        var insetCue = eyeInsetCueAnalyzer.Analyze(inset);
        var record = new VisionFrameRecord(
            frameIndex,
            timestampSeconds,
            result.BackendName,
            result.BackendStatus,
            result.HasFace,
            metrics.ConfidenceLabel,
            metrics.TrackingConfidence,
            metrics.EyeConfidence,
            metrics.MouthConfidence,
            metrics.EyeMeasurementQualityPercent,
            metrics.MouthMeasurementQualityPercent,
            metrics.OverallMeasurementQualityPercent,
            metrics.HeadYawDegrees,
            metrics.HeadPitchDegrees,
            metrics.HeadRollDegrees,
            stability.Status,
            stability.SampleCount,
            stability.CompositeReliabilityPercent,
            stability.FaceContinuityPercent,
            stability.EyeReliabilityPercent,
            stability.MouthReliabilityPercent,
            stability.FaceBoundsRatePercent,
            stability.EyeUsableRatePercent,
            stability.MouthUsableRatePercent,
            metrics.EyeImageQualityAvailable,
            metrics.MouthImageQualityAvailable,
            metrics.EyeGlarePercent,
            metrics.MouthGlarePercent,
            metrics.EyeContrastPercent,
            metrics.MouthContrastPercent,
            metrics.EyeSharpnessPercent,
            metrics.MouthSharpnessPercent,
            metrics.EyeDarkCoveragePercent,
            metrics.MouthDarkCoveragePercent,
            metrics.RawEyeAsymmetryPercent,
            metrics.EyeAsymmetryPercent,
            metrics.EyeAgreementPercent,
            metrics.PossibleOneEyeArtifact,
            metrics.LeftEyeReconstructed,
            metrics.RightEyeReconstructed,
            metrics.MouthReconstructed,
            metrics.EyeArtifactSuppressed,
            metrics.IsEyeMeasurementUsable,
            metrics.IsMouthMeasurementUsable,
            metrics.RawLeftEyeOpeningRatio,
            metrics.RawRightEyeOpeningRatio,
            metrics.RawAverageEyeOpeningRatio,
            metrics.RawMouthOpeningRatio,
            metrics.LeftEyeOpeningRatio,
            metrics.RightEyeOpeningRatio,
            metrics.AverageEyeOpeningRatio,
            metrics.MouthOpeningRatio,
            metrics.MouthOpeningVelocityPerSecond,
            metrics.RawJawDroopRatio,
            metrics.JawDroopRatio,
            metrics.JawDroopVelocityPerSecond,
            metrics.MediaPipeLeftEyeBlinkPercent,
            metrics.MediaPipeRightEyeBlinkPercent,
            metrics.MediaPipeAverageEyeBlinkPercent,
            metrics.MediaPipeJawOpenPercent,
            metrics.MediaPipeMouthClosePercent,
            metrics.MediaPipeEyeOpeningCorrectionRatio,
            metrics.MediaPipeMouthOpeningCorrectionRatio,
            metrics.MediaPipeEyeOpeningCorrected,
            metrics.MediaPipeMouthOpeningCorrected,
            cue.Status,
            cue.HasUsableMeasurements,
            cue.BaselineReady,
            cue.BaselineSamples,
            cue.EyeCueEligible,
            cue.MouthCueEligible,
            cue.EyeClosurePercent,
            cue.MouthOpeningChangePercent,
            cue.JawDroopBaselineRatio,
            cue.JawDroopChangePercent,
            cue.CompositeCuePercent,
            cue.MediaPipeBlinkBaselineReady,
            cue.MediaPipeMouthBaselineReady,
            cue.MediaPipeBlinkBaselinePercent,
            cue.MediaPipeJawOpenBaselinePercent,
            cue.MediaPipeMouthCloseBaselinePercent,
            cue.MediaPipeBlinkChangePercent,
            cue.MediaPipeJawOpenChangePercent,
            cue.MediaPipeMouthCloseDropPercent,
            cue.MediaPipeMouthOpeningEvidencePercent,
            trend.Status,
            trend.HasUsableTrend,
            trend.EyeClosingTrendPercent,
            trend.MouthOpeningTrendPercent,
            trend.EyeOpeningSlopePerSecond,
            trend.MouthOpeningSlopePerSecond,
            trend.TrendCuePercent,
            result.FeatureDetection.FaceBox.Left,
            result.FeatureDetection.FaceBox.Top,
            result.FeatureDetection.FaceBox.Width,
            result.FeatureDetection.FaceBox.Height,
            headPose.ApparentDistanceUnits,
            headPose.ZRelativeToReference,
            headPose.ZConfidencePercent,
            headPose.ZEstimateKind,
            headPose.ZQualityLabel,
            headPose.DistanceSource,
            headPose.DistanceInches,
            headPose.DistanceCalibrated,
            headPose.ZUsesCameraFov,
            headPose.ZUsesLearnedReference,
            identityMeasurement.HasMeasurement,
            identityMeasurement.UsableFeatureCount,
            identityMeasurement.FaceAspectRatio,
            identityMeasurement.EyeMidlineXToFaceWidth,
            identityMeasurement.MouthCenterXToFaceWidth,
            identityMeasurement.EyeToMouthXOffsetToFaceWidth,
            identityMeasurement.InterEyeDistanceToFaceWidth,
            identityMeasurement.LeftEyeWidthToFaceWidth,
            identityMeasurement.RightEyeWidthToFaceWidth,
            identityMeasurement.MouthWidthToFaceWidth,
            identityMeasurement.EyeMidlineYToFaceHeight,
            identityMeasurement.MouthCenterYToFaceHeight,
            identityMeasurement.EyeToMouthYDistanceToFaceHeight,
            inset?.RegionLabel ?? "",
            inset?.Status ?? "",
            inset?.HasMeasurement == true,
            inset?.LeftEyeOpeningRatio,
            inset?.RightEyeOpeningRatio,
            inset?.AverageEyeOpeningRatio,
            inset?.LeftEyeConfidence,
            inset?.RightEyeConfidence,
            inset?.MeasurementConfidence,
            inset?.ImageQualityAvailable == true,
            inset?.GlarePercent,
            inset?.ContrastPercent,
            inset?.SharpnessPercent,
            inset?.DarkCoveragePercent,
            inset?.RegionLeft,
            inset?.RegionTop,
            inset?.RegionWidth,
            inset?.RegionHeight,
            insetCue.Status,
            insetCue.HasMeasurement,
            insetCue.BaselineReady,
            insetCue.BaselineSamples,
            insetCue.CueEligible,
            insetCue.QualityPercent,
            insetCue.OpeningRatio,
            insetCue.BaselineOpeningRatio,
            insetCue.EyeClosurePercent,
            insetCue.CompositeCuePercent,
            personalModelUpdate.Accepted,
            personalModelUpdate.RejectionKind.ToString(),
            personalModelUpdate.Reason,
            personalModelUpdate.IdentityAnalysis?.Status ?? "",
            personalModelUpdate.IdentityAnalysis?.ConfidencePercent,
            personalModelUpdate.IdentityAnalysis?.ComparedFeatureCount ?? 0,
            personalModelUpdate.IdentityAnalysis?.OutlierFeatureCount ?? 0,
            captureQuality.Label,
            captureQuality.ScorePercent,
            captureQuality.CanCollectMeasurements,
            captureQuality.StrongEnoughForAvatarLearning,
            captureQuality.PrimaryReason,
            captureQuality.CameraModeScorePercent,
            captureQuality.FaceScaleScorePercent,
            captureQuality.EyeEvidenceScorePercent,
            captureQuality.MouthEvidenceScorePercent,
            captureQuality.StabilityScorePercent,
            captureQuality.GlassesRiskScorePercent,
            captureQuality.StorageScorePercent,
            captureQuality.FaceWidthPercent,
            captureQuality.FaceHeightPercent,
            string.Join(" | ", captureQuality.Issues),
            string.Join(" | ", captureQuality.Suggestions));
        if (!string.IsNullOrWhiteSpace(overlayFolder))
        {
            WriteOverlayFrame(
                overlayFolder,
                frame,
                frameIndex,
                timestampSeconds,
                result,
                reconstructedFrame,
                metrics,
                cue,
                trend,
                stability,
                inset,
                insetCue);
        }

        return record;
    }

    private static HeadPoseCalibration CreateOfflineHeadPoseCalibration(PersonalFaceModel model)
    {
        var reference = TryEstimateLearnedReferenceInterEyeFrameWidth(model, out var referenceSamples);
        return new HeadPoseCalibration
        {
            CameraHorizontalFovDegrees = OfflineReferenceHorizontalFovDegrees,
            ReferenceInterEyeFrameWidth = reference,
            ReferenceSampleCount = referenceSamples,
            ReferenceSource = reference is > 0d
                ? $"offline learned {model.SubjectDisplayName} face scale ({referenceSamples} samples)"
                : ""
        };
    }

    private static double? TryEstimateLearnedReferenceInterEyeFrameWidth(
        PersonalFaceModel model,
        out int referenceSamples)
    {
        referenceSamples = Math.Min(model.FaceWidth.SampleCount, model.InterEyeDistanceToFaceWidth.SampleCount);
        if (referenceSamples < 18)
        {
            return null;
        }

        var faceWidth = MetricValue(model.FaceWidth);
        var interEyeToFaceWidth = MetricValue(model.InterEyeDistanceToFaceWidth);
        if (faceWidth is not > 0.04d or > 0.95d
            || interEyeToFaceWidth is not > 0.08d or > 0.75d)
        {
            return null;
        }

        var interEyeFrameWidth = faceWidth.Value * interEyeToFaceWidth.Value;
        return interEyeFrameWidth is > 0.01d and < 0.75d
            ? interEyeFrameWidth
            : null;
    }

    private static double? MetricValue(PersonalMetricDistribution distribution)
    {
        return distribution.ExponentialMovingAverage ?? distribution.Average;
    }

    private static PersonalFaceModelRejectionKind CaptureQualityRejectionKind(PersonalFaceCaptureQualityAssessment captureQuality)
    {
        return captureQuality.Label.Equals("no-face", StringComparison.OrdinalIgnoreCase)
            ? PersonalFaceModelRejectionKind.NoFace
            : PersonalFaceModelRejectionKind.LowQuality;
    }

    private static BitmapSource CreateBitmapSource(Mat frame)
    {
        using var bgra = new Mat();
        if (frame.Channels() == 1)
        {
            Cv2.CvtColor(frame, bgra, ColorConversionCodes.GRAY2BGRA);
        }
        else if (frame.Channels() == 3)
        {
            Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
        }
        else if (frame.Channels() == 4)
        {
            frame.CopyTo(bgra);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported frame channel count: {frame.Channels()}");
        }

        var stride = (int)bgra.Step();
        var pixels = new byte[stride * bgra.Rows];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        var bitmap = BitmapSource.Create(
            bgra.Cols,
            bgra.Rows,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void WriteOutputs(
        string inputPath,
        string outputFolder,
        double sampleFps,
        EyeInsetRegion? eyeInsetRegion,
        bool writeOverlays,
        IReadOnlyList<VisionFrameRecord> records,
        PersonalFaceModel personalModel)
    {
        var csvPath = Path.Combine(outputFolder, "vision_eval.csv");
        var builder = new StringBuilder();
        builder.AppendLine("FrameIndex,TimestampSeconds,Backend,BackendStatus,HasFace,Confidence,TrackingConfidence,EyeConfidence,MouthConfidence,EyeQuality,MouthQuality,OverallQuality,ARotationAroundXDegrees,BRotationAroundYDegrees,CRotationAroundZDegrees,FaceReliabilityStatus,FaceReliabilitySamples,FaceReliability,FaceContinuity,EyeReliability,MouthReliability,FaceBoundsRate,EyeUsableRate,MouthUsableRate,EyeImageQualityAvailable,MouthImageQualityAvailable,EyeGlare,MouthGlare,EyeContrast,MouthContrast,EyeSharpness,MouthSharpness,EyeDarkCoverage,MouthDarkCoverage,RawEyeAsymmetry,EyeAsymmetry,EyeAgreement,PossibleOneEyeArtifact,LeftEyeReconstructed,RightEyeReconstructed,MouthReconstructed,EyeArtifactSuppressed,EyeUsable,MouthUsable,RawLeftEyeOpening,RawRightEyeOpening,RawAverageEyeOpening,RawMouthOpening,LeftEyeOpening,RightEyeOpening,AverageEyeOpening,MouthOpening,MouthOpeningVelocity,RawJawDroop,JawDroop,JawDroopVelocity,MediaPipeLeftEyeBlink,MediaPipeRightEyeBlink,MediaPipeAverageEyeBlink,MediaPipeJawOpen,MediaPipeMouthClose,MediaPipeEyeOpeningCorrection,MediaPipeMouthOpeningCorrection,MediaPipeEyeOpeningCorrected,MediaPipeMouthOpeningCorrected,CueStatus,CueUsable,CueBaselineReady,CueBaselineSamples,CueEyeEligible,CueMouthEligible,CueEyeClosure,CueMouthOpeningChange,CueJawDroopBaseline,CueJawDroopChange,CueScore,CueMediaPipeBlinkBaselineReady,CueMediaPipeMouthBaselineReady,CueMediaPipeBlinkBaseline,CueMediaPipeJawOpenBaseline,CueMediaPipeMouthCloseBaseline,CueMediaPipeBlinkChange,CueMediaPipeJawOpenChange,CueMediaPipeMouthCloseDrop,CueMediaPipeMouthOpeningEvidence,TrendStatus,TrendUsable,EyeClosingTrend,MouthOpeningTrend,TrendEyeSlope,TrendMouthSlope,TrendCueScore,FaceLeft,FaceTop,FaceWidth,FaceHeight,ZApparentDistanceUnits,ZRelativeToReference,ZConfidencePercent,ZEstimateKind,ZQualityLabel,ZDistanceSource,DistanceInches,DistanceCalibrated,ZUsesCameraFov,ZUsesLearnedReference,IdentityMeasurementAvailable,IdentityUsableFeatureCount,FaceAspectRatio,EyeMidlineXToFaceWidth,MouthCenterXToFaceWidth,EyeToMouthXOffsetToFaceWidth,InterEyeDistanceToFaceWidth,LeftEyeWidthToFaceWidth,RightEyeWidthToFaceWidth,MouthWidthToFaceWidth,EyeMidlineYToFaceHeight,MouthCenterYToFaceHeight,EyeToMouthYDistanceToFaceHeight,EyeInsetRegion,EyeInsetStatus,EyeInsetHasMeasurement,EyeInsetLeftOpening,EyeInsetRightOpening,EyeInsetAverageOpening,EyeInsetLeftConfidence,EyeInsetRightConfidence,EyeInsetConfidence,EyeInsetImageQualityAvailable,EyeInsetGlare,EyeInsetContrast,EyeInsetSharpness,EyeInsetDarkCoverage,EyeInsetRegionLeft,EyeInsetRegionTop,EyeInsetRegionWidth,EyeInsetRegionHeight,EyeInsetCueStatus,EyeInsetCueHasMeasurement,EyeInsetCueBaselineReady,EyeInsetCueBaselineSamples,EyeInsetCueEligible,EyeInsetCueQuality,EyeInsetCueOpening,EyeInsetCueBaselineOpening,EyeInsetCueClosure,EyeInsetCueScore,PersonalModelAccepted,PersonalModelRejectionKind,PersonalModelUpdateReason,PersonalIdentityStatus,PersonalIdentityConfidence,PersonalIdentityComparedFeatures,PersonalIdentityOutlierFeatures,CaptureQualityLabel,CaptureQualityScore,CaptureQualityCanCollect,CaptureQualityAvatarGrade,CaptureQualityReason,CaptureQualityCameraModeScore,CaptureQualityFaceScaleScore,CaptureQualityEyeScore,CaptureQualityMouthScore,CaptureQualityStabilityScore,CaptureQualityGlassesScore,CaptureQualityStorageScore,CaptureQualityFaceWidthPercent,CaptureQualityFaceHeightPercent,CaptureQualityIssues,CaptureQualitySuggestions");
        foreach (var record in records)
        {
            builder.AppendLine(string.Join(",", [
                record.FrameIndex.ToString(CultureInfo.InvariantCulture),
                record.TimestampSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(record.Backend),
                Csv(record.BackendStatus),
                record.HasFace.ToString(),
                Csv(record.Confidence),
                Format(record.TrackingConfidence),
                Format(record.EyeConfidence),
                Format(record.MouthConfidence),
                Format(record.EyeQuality),
                Format(record.MouthQuality),
                Format(record.OverallQuality),
                Format(record.HeadPitch),
                Format(record.HeadYaw),
                Format(record.HeadRoll),
                Csv(record.FaceReliabilityStatus),
                record.FaceReliabilitySamples.ToString(CultureInfo.InvariantCulture),
                Format(record.FaceReliability),
                Format(record.FaceContinuity),
                Format(record.EyeReliability),
                Format(record.MouthReliability),
                Format(record.FaceBoundsRate),
                Format(record.EyeUsableRate),
                Format(record.MouthUsableRate),
                record.EyeImageQualityAvailable.ToString(),
                record.MouthImageQualityAvailable.ToString(),
                Format(record.EyeGlare),
                Format(record.MouthGlare),
                Format(record.EyeContrast),
                Format(record.MouthContrast),
                Format(record.EyeSharpness),
                Format(record.MouthSharpness),
                Format(record.EyeDarkCoverage),
                Format(record.MouthDarkCoverage),
                Format(record.RawEyeAsymmetry),
                Format(record.EyeAsymmetry),
                Format(record.EyeAgreement),
                record.PossibleOneEyeArtifact.ToString(),
                record.LeftEyeReconstructed.ToString(),
                record.RightEyeReconstructed.ToString(),
                record.MouthReconstructed.ToString(),
                record.EyeArtifactSuppressed.ToString(),
                record.EyeUsable.ToString(),
                record.MouthUsable.ToString(),
                Format(record.RawLeftEyeOpening),
                Format(record.RawRightEyeOpening),
                Format(record.RawAverageEyeOpening),
                Format(record.RawMouthOpening),
                Format(record.LeftEyeOpening),
                Format(record.RightEyeOpening),
                Format(record.AverageEyeOpening),
                Format(record.MouthOpening),
                Format(record.MouthOpeningVelocity),
                Format(record.RawJawDroop),
                Format(record.JawDroop),
                Format(record.JawDroopVelocity),
                Format(record.MediaPipeLeftEyeBlink),
                Format(record.MediaPipeRightEyeBlink),
                Format(record.MediaPipeAverageEyeBlink),
                Format(record.MediaPipeJawOpen),
                Format(record.MediaPipeMouthClose),
                Format(record.MediaPipeEyeOpeningCorrection),
                Format(record.MediaPipeMouthOpeningCorrection),
                record.MediaPipeEyeOpeningCorrected.ToString(),
                record.MediaPipeMouthOpeningCorrected.ToString(),
                Csv(record.CueStatus),
                record.CueUsable.ToString(),
                record.CueBaselineReady.ToString(),
                record.CueBaselineSamples.ToString(CultureInfo.InvariantCulture),
                record.CueEyeEligible.ToString(),
                record.CueMouthEligible.ToString(),
                Format(record.CueEyeClosure),
                Format(record.CueMouthOpeningChange),
                Format(record.CueJawDroopBaseline),
                Format(record.CueJawDroopChange),
                Format(record.CueScore),
                record.CueMediaPipeBlinkBaselineReady.ToString(),
                record.CueMediaPipeMouthBaselineReady.ToString(),
                Format(record.CueMediaPipeBlinkBaseline),
                Format(record.CueMediaPipeJawOpenBaseline),
                Format(record.CueMediaPipeMouthCloseBaseline),
                Format(record.CueMediaPipeBlinkChange),
                Format(record.CueMediaPipeJawOpenChange),
                Format(record.CueMediaPipeMouthCloseDrop),
                Format(record.CueMediaPipeMouthOpeningEvidence),
                Csv(record.TrendStatus),
                record.TrendUsable.ToString(),
                Format(record.EyeClosingTrend),
                Format(record.MouthOpeningTrend),
                Format(record.TrendEyeSlope),
                Format(record.TrendMouthSlope),
                Format(record.TrendCueScore),
                Format(record.FaceLeft),
                Format(record.FaceTop),
                Format(record.FaceWidth),
                Format(record.FaceHeight),
                Format(record.ZApparentDistanceUnits),
                Format(record.ZRelativeToReference),
                Format(record.ZConfidencePercent),
                Csv(record.ZEstimateKind),
                Csv(record.ZQualityLabel),
                Csv(record.ZDistanceSource),
                Format(record.DistanceInches),
                record.DistanceCalibrated.ToString(),
                record.ZUsesCameraFov.ToString(),
                record.ZUsesLearnedReference.ToString(),
                record.IdentityMeasurementAvailable.ToString(),
                record.IdentityUsableFeatureCount.ToString(CultureInfo.InvariantCulture),
                Format(record.FaceAspectRatio),
                Format(record.EyeMidlineXToFaceWidth),
                Format(record.MouthCenterXToFaceWidth),
                Format(record.EyeToMouthXOffsetToFaceWidth),
                Format(record.InterEyeDistanceToFaceWidth),
                Format(record.LeftEyeWidthToFaceWidth),
                Format(record.RightEyeWidthToFaceWidth),
                Format(record.MouthWidthToFaceWidth),
                Format(record.EyeMidlineYToFaceHeight),
                Format(record.MouthCenterYToFaceHeight),
                Format(record.EyeToMouthYDistanceToFaceHeight),
                Csv(record.EyeInsetRegion),
                Csv(record.EyeInsetStatus),
                record.EyeInsetHasMeasurement.ToString(),
                Format(record.EyeInsetLeftOpening),
                Format(record.EyeInsetRightOpening),
                Format(record.EyeInsetAverageOpening),
                Format(record.EyeInsetLeftConfidence),
                Format(record.EyeInsetRightConfidence),
                Format(record.EyeInsetConfidence),
                record.EyeInsetImageQualityAvailable.ToString(),
                Format(record.EyeInsetGlare),
                Format(record.EyeInsetContrast),
                Format(record.EyeInsetSharpness),
                Format(record.EyeInsetDarkCoverage),
                Format(record.EyeInsetRegionLeft),
                Format(record.EyeInsetRegionTop),
                Format(record.EyeInsetRegionWidth),
                Format(record.EyeInsetRegionHeight),
                Csv(record.EyeInsetCueStatus),
                record.EyeInsetCueHasMeasurement.ToString(),
                record.EyeInsetCueBaselineReady.ToString(),
                record.EyeInsetCueBaselineSamples.ToString(CultureInfo.InvariantCulture),
                record.EyeInsetCueEligible.ToString(),
                Format(record.EyeInsetCueQuality),
                Format(record.EyeInsetCueOpening),
                Format(record.EyeInsetCueBaselineOpening),
                Format(record.EyeInsetCueClosure),
                Format(record.EyeInsetCueScore),
                record.PersonalModelAccepted.ToString(),
                Csv(record.PersonalModelRejectionKind),
                Csv(record.PersonalModelUpdateReason),
                Csv(record.PersonalIdentityStatus),
                Format(record.PersonalIdentityConfidence),
                record.PersonalIdentityComparedFeatures.ToString(CultureInfo.InvariantCulture),
                record.PersonalIdentityOutlierFeatures.ToString(CultureInfo.InvariantCulture),
                Csv(record.CaptureQualityLabel),
                Format(record.CaptureQualityScore),
                record.CaptureQualityCanCollect.ToString(),
                record.CaptureQualityAvatarGrade.ToString(),
                Csv(record.CaptureQualityReason),
                Format(record.CaptureQualityCameraModeScore),
                Format(record.CaptureQualityFaceScaleScore),
                Format(record.CaptureQualityEyeScore),
                Format(record.CaptureQualityMouthScore),
                Format(record.CaptureQualityStabilityScore),
                Format(record.CaptureQualityGlassesScore),
                Format(record.CaptureQualityStorageScore),
                Format(record.CaptureQualityFaceWidthPercent),
                Format(record.CaptureQualityFaceHeightPercent),
                Csv(record.CaptureQualityIssues),
                Csv(record.CaptureQualitySuggestions)
            ]));
        }

        File.WriteAllText(csvPath, builder.ToString(), Encoding.UTF8);

        var fullFrameEyeSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.AverageEyeOpening)));
        var rawFullFrameEyeSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.RawAverageEyeOpening)));
        var mouthOpeningSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.MouthOpening)));
        var rawMouthOpeningSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.RawMouthOpening)));
        var jawDroopSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.JawDroop)));
        var eyeInsetSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.EyeInsetAverageOpening)));
        var eyeInsetAgreement = EyeInsetAgreementAnalyzer.Analyze(records.Select(static record => new EyeInsetAgreementSample(
            record.TimestampSeconds,
            record.AverageEyeOpening,
            record.EyeInsetAverageOpening)));
        var summaryPath = Path.Combine(outputFolder, "vision_eval_summary.json");
        var reportPath = Path.Combine(outputFolder, "vision_eval_report.html");
        var personalModelPath = PersonalFaceModelStore.WriteFile(Path.Combine(outputFolder, "personal_face_model.json"), personalModel);
        var motionModel = BuildMotionModel(personalModel, records);
        var motionModelPath = PersonalFaceMotionModelStore.WriteFile(Path.Combine(outputFolder, "personal_face_motion_model.json"), motionModel);
        var corpusReadiness = new PersonalFaceCorpusReadinessBuilder().Build(
            personalModel,
            motionModel,
            BuildCorpusSamples(personalModel, records),
            measurementJournalBytes: 0L);
        var corpusReadinessPath = PersonalFaceCorpusReadinessStore.WriteFile(Path.Combine(outputFolder, "personal_face_corpus_readiness.json"), corpusReadiness);
        var corpusReadinessHtmlPath = PersonalFaceCorpusReadinessStore.GetHtmlPath(corpusReadinessPath);
        var previewFiles = WriteMeasurementFacePreview(outputFolder, personalModel);
        var collectionAudit = BuildCollectionAudit(personalModel, records);
        var avatarPackageFiles = WriteMeasurementAvatarTrainingPackage(outputFolder, personalModel, motionModel, corpusReadiness, collectionAudit);
        var avatarCapturePlanFiles = WriteMeasurementAvatarCapturePlan(outputFolder, personalModel, motionModel, corpusReadiness);
        var collectionAuditPath = PersonalFaceCollectionAuditStore.WriteFile(
            Path.Combine(outputFolder, PersonalFaceCollectionAuditStore.DefaultJsonFileName),
            collectionAudit);
        var collectionAuditHtmlPath = PersonalFaceCollectionAuditStore.GetHtmlPath(collectionAuditPath);

        var summary = new
        {
            InputPath = inputPath,
            OutputFolder = outputFolder,
            ReportPath = reportPath,
            OverlayFramesEnabled = writeOverlays,
            OverlayFramesFolder = writeOverlays ? Path.Combine(outputFolder, "overlay_frames") : "",
            OverlayFrameCount = CountOverlayFrames(outputFolder, writeOverlays),
            SampleFramesPerSecond = sampleFps,
            FrameCount = records.Count,
            FaceFrames = records.Count(record => record.HasFace),
            AxisDefinitions = new
            {
                X = "horizontal frame position",
                Y = "vertical frame position",
                Z = "apparent camera-space depth",
                A = "rotation around X",
                B = "rotation around Y",
                C = "rotation around Z"
            },
            Backends = records
                .Select(record => record.Backend)
                .Where(static backend => !string.IsNullOrWhiteSpace(backend))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static backend => backend)
                .ToList(),
            BackendStatuses = records
                .Select(record => record.BackendStatus)
                .Where(static status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static status => status)
                .ToList(),
            FaceDetectionRate = Rate(records.Count(record => record.HasFace), records.Count),
            FaceCenterXMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceLeft + record.FaceWidth / 2d))),
            FaceCenterXMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceLeft + record.FaceWidth / 2d))),
            FaceCenterXAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceLeft + record.FaceWidth / 2d))),
            FaceCenterXRange = Range(records.Where(static record => record.HasFace).Select(static record => record.FaceLeft + record.FaceWidth / 2d)),
            FaceCenterYMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceTop + record.FaceHeight / 2d))),
            FaceCenterYMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceTop + record.FaceHeight / 2d))),
            FaceCenterYAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)(record.FaceTop + record.FaceHeight / 2d))),
            FaceCenterYRange = Range(records.Where(static record => record.HasFace).Select(static record => record.FaceTop + record.FaceHeight / 2d)),
            FaceWidthMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceWidth)),
            FaceWidthMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceWidth)),
            FaceWidthAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceWidth)),
            FaceWidthRange = Range(records.Where(static record => record.HasFace).Select(static record => record.FaceWidth)),
            FaceHeightMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceHeight)),
            FaceHeightMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceHeight)),
            FaceHeightAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)record.FaceHeight)),
            FaceHeightRange = Range(records.Where(static record => record.HasFace).Select(static record => record.FaceHeight)),
            ARotationAroundXMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadPitch)),
            ARotationAroundXMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadPitch)),
            ARotationAroundXAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadPitch)),
            ARotationAroundXRange = Range(records.Where(static record => record.HasFace).Select(static record => record.HeadPitch)),
            BRotationAroundYMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadYaw)),
            BRotationAroundYMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadYaw)),
            BRotationAroundYAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadYaw)),
            BRotationAroundYRange = Range(records.Where(static record => record.HasFace).Select(static record => record.HeadYaw)),
            CRotationAroundZMinimum = Minimum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadRoll)),
            CRotationAroundZMaximum = Maximum(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadRoll)),
            CRotationAroundZAverage = Average(records.Where(static record => record.HasFace).Select(record => (double?)record.HeadRoll)),
            CRotationAroundZRange = Range(records.Where(static record => record.HasFace).Select(static record => record.HeadRoll)),
            IdentityMeasurementFrames = records.Count(record => record.IdentityMeasurementAvailable),
            IdentityMeasurementRate = Rate(records.Count(record => record.IdentityMeasurementAvailable), records.Count),
            FaceAspectRatioRange = Range(records.Select(record => record.FaceAspectRatio).OfType<double>()),
            EyeMidlineXToFaceWidthRange = Range(records.Select(record => record.EyeMidlineXToFaceWidth).OfType<double>()),
            MouthCenterXToFaceWidthRange = Range(records.Select(record => record.MouthCenterXToFaceWidth).OfType<double>()),
            EyeToMouthXOffsetToFaceWidthRange = Range(records.Select(record => record.EyeToMouthXOffsetToFaceWidth).OfType<double>()),
            InterEyeDistanceToFaceWidthRange = Range(records.Select(record => record.InterEyeDistanceToFaceWidth).OfType<double>()),
            MouthWidthToFaceWidthRange = Range(records.Select(record => record.MouthWidthToFaceWidth).OfType<double>()),
            EyeMidlineYToFaceHeightRange = Range(records.Select(record => record.EyeMidlineYToFaceHeight).OfType<double>()),
            MouthCenterYToFaceHeightRange = Range(records.Select(record => record.MouthCenterYToFaceHeight).OfType<double>()),
            EyeToMouthYDistanceToFaceHeightRange = Range(records.Select(record => record.EyeToMouthYDistanceToFaceHeight).OfType<double>()),
            LandmarkEyeMeasurementRate = Rate(records.Count(record => record.AverageEyeOpening.HasValue), records.Count),
            LandmarkMouthMeasurementRate = Rate(records.Count(record => record.MouthOpening.HasValue), records.Count),
            LandmarkJawDroopMeasurementRate = Rate(records.Count(record => record.JawDroop.HasValue), records.Count),
            LandmarkEyeUsableRate = Rate(records.Count(record => record.EyeUsable), records.Count),
            LandmarkMouthUsableRate = Rate(records.Count(record => record.MouthUsable), records.Count),
            LandmarkAverageOverallQuality = Average(records.Select(record => (double?)record.OverallQuality)),
            LandmarkMinimumOverallQuality = Minimum(records.Select(record => (double?)record.OverallQuality)),
            FaceReliabilityUsableRate = Rate(records.Count(record => record.FaceReliabilitySamples >= 3 && record.FaceReliability >= 55d), records.Count),
            FaceReliabilityAverage = Average(records.Select(record => (double?)record.FaceReliability)),
            FaceReliabilityMinimum = Minimum(records.Select(record => (double?)record.FaceReliability)),
            FaceContinuityAverage = Average(records.Select(record => (double?)record.FaceContinuity)),
            EyeReliabilityAverage = Average(records.Select(record => (double?)record.EyeReliability)),
            MouthReliabilityAverage = Average(records.Select(record => (double?)record.MouthReliability)),
            LandmarkEyeImageQualityRate = Rate(records.Count(record => record.EyeImageQualityAvailable), records.Count),
            LandmarkMouthImageQualityRate = Rate(records.Count(record => record.MouthImageQualityAvailable), records.Count),
            LandmarkAverageEyeGlare = Average(records.Where(static record => record.EyeImageQualityAvailable).Select(record => (double?)record.EyeGlare)),
            LandmarkMaximumEyeGlare = Maximum(records.Where(static record => record.EyeImageQualityAvailable).Select(record => (double?)record.EyeGlare)),
            LandmarkAverageEyeContrast = Average(records.Where(static record => record.EyeImageQualityAvailable).Select(record => (double?)record.EyeContrast)),
            LandmarkAverageEyeSharpness = Average(records.Where(static record => record.EyeImageQualityAvailable).Select(record => (double?)record.EyeSharpness)),
            LandmarkAverageEyeDarkCoverage = Average(records.Where(static record => record.EyeImageQualityAvailable).Select(record => (double?)record.EyeDarkCoverage)),
            LandmarkMaximumRawEyeAsymmetry = Maximum(records.Select(record => record.RawEyeAsymmetry)),
            LandmarkMaximumEyeAsymmetry = Maximum(records.Select(record => record.EyeAsymmetry)),
            LandmarkMinimumEyeAgreement = Minimum(records
                .Where(static record => record.LeftEyeOpening.HasValue
                    && record.RightEyeOpening.HasValue
                    && record.EyeAsymmetry.HasValue)
                .Select(record => (double?)record.EyeAgreement)),
            LandmarkPossibleOneEyeArtifactFrames = records.Count(record => record.PossibleOneEyeArtifact),
            LandmarkLeftEyeReconstructedFrames = records.Count(record => record.LeftEyeReconstructed),
            LandmarkRightEyeReconstructedFrames = records.Count(record => record.RightEyeReconstructed),
            LandmarkMouthReconstructedFrames = records.Count(record => record.MouthReconstructed),
            LandmarkEyeArtifactSuppressedFrames = records.Count(record => record.EyeArtifactSuppressed),
            LandmarkCueUsableRate = Rate(records.Count(record => record.CueUsable), records.Count),
            LandmarkCueBaselineReadyRate = Rate(records.Count(record => record.CueBaselineReady), records.Count),
            LandmarkCueEyeEligibleRate = Rate(records.Count(record => record.CueEyeEligible), records.Count),
            LandmarkCueMouthEligibleRate = Rate(records.Count(record => record.CueMouthEligible), records.Count),
            LandmarkMaximumEyeClosureCue = Maximum(records.Select(record => record.CueEyeClosure)),
            LandmarkMaximumMouthOpeningCue = Maximum(records.Select(record => record.CueMouthOpeningChange)),
            LandmarkMaximumJawDroopCue = Maximum(records.Select(record => record.CueJawDroopChange)),
            LandmarkMaximumCueScore = Maximum(records.Select(record => record.CueScore)),
            LandmarkTrendUsableRate = Rate(records.Count(record => record.TrendUsable), records.Count),
            LandmarkMaximumEyeClosingTrend = Maximum(records.Select(record => record.EyeClosingTrend)),
            LandmarkMaximumMouthOpeningTrend = Maximum(records.Select(record => record.MouthOpeningTrend)),
            LandmarkMinimumTrendEyeSlope = Minimum(records.Select(record => record.TrendEyeSlope)),
            LandmarkMaximumTrendMouthSlope = Maximum(records.Select(record => record.TrendMouthSlope)),
            LandmarkMaximumTrendCueScore = Maximum(records.Select(record => record.TrendCueScore)),
            MediaPipeDenseLockFrames = records.Count(HasMediaPipeDenseLock),
            MediaPipeDenseLockRate = Rate(records.Count(HasMediaPipeDenseLock), records.Count),
            MediaPipeBlendshapeFrames = records.Count(HasMediaPipeBlendshapeEvidence),
            MediaPipeBlendshapeFrameRate = Rate(records.Count(HasMediaPipeBlendshapeEvidence), records.Count),
            MediaPipeMaximumAverageEyeBlink = Maximum(records.Select(record => record.MediaPipeAverageEyeBlink)),
            MediaPipeMaximumJawOpen = Maximum(records.Select(record => record.MediaPipeJawOpen)),
            MediaPipeMinimumMouthClose = Minimum(records.Select(record => record.MediaPipeMouthClose)),
            MediaPipeEyeOpeningCorrectedFrames = records.Count(record => record.MediaPipeEyeOpeningCorrected),
            MediaPipeMouthOpeningCorrectedFrames = records.Count(record => record.MediaPipeMouthOpeningCorrected),
            MediaPipeMaximumAbsoluteEyeOpeningCorrection = Maximum(records.Select(record => record.MediaPipeEyeOpeningCorrection is double correction ? Math.Abs(correction) : (double?)null)),
            MediaPipeMaximumAbsoluteMouthOpeningCorrection = Maximum(records.Select(record => record.MediaPipeMouthOpeningCorrection is double correction ? Math.Abs(correction) : (double?)null)),
            RawAverageEyeOpening = Average(records.Select(record => record.RawAverageEyeOpening)),
            RawMinimumEyeOpening = Minimum(records.Select(record => record.RawAverageEyeOpening)),
            RawEyeOpeningSlopePerSecond = rawFullFrameEyeSlope,
            EyeOpeningRawWorkingPairedFrames = records.Count(record => record.RawAverageEyeOpening.HasValue && record.AverageEyeOpening.HasValue),
            EyeOpeningRawWorkingMaximumAbsoluteDelta = Maximum(records.Select(record => AbsDifference(record.RawAverageEyeOpening, record.AverageEyeOpening))),
            RawAverageMouthOpening = Average(records.Select(record => record.RawMouthOpening)),
            RawMaximumMouthOpening = Maximum(records.Select(record => record.RawMouthOpening)),
            RawMouthOpeningSlopePerSecond = rawMouthOpeningSlope,
            MouthOpeningRawWorkingPairedFrames = records.Count(record => record.RawMouthOpening.HasValue && record.MouthOpening.HasValue),
            MouthOpeningRawWorkingMaximumAbsoluteDelta = Maximum(records.Select(record => AbsDifference(record.RawMouthOpening, record.MouthOpening))),
            MediaPipeCueBlinkBaselineReadyRate = Rate(records.Count(record => record.CueMediaPipeBlinkBaselineReady), records.Count),
            MediaPipeCueMouthBaselineReadyRate = Rate(records.Count(record => record.CueMediaPipeMouthBaselineReady), records.Count),
            MediaPipeCueMaximumBlinkChange = Maximum(records.Select(record => record.CueMediaPipeBlinkChange)),
            MediaPipeCueMaximumJawOpenChange = Maximum(records.Select(record => record.CueMediaPipeJawOpenChange)),
            MediaPipeCueMaximumMouthCloseDrop = Maximum(records.Select(record => record.CueMediaPipeMouthCloseDrop)),
            MediaPipeCueMaximumMouthOpeningEvidence = Maximum(records.Select(record => record.CueMediaPipeMouthOpeningEvidence)),
            AverageEyeOpening = Average(records.Select(record => record.AverageEyeOpening)),
            MinimumEyeOpening = Minimum(records.Select(record => record.AverageEyeOpening)),
            EyeOpeningSlopePerSecond = fullFrameEyeSlope,
            AverageMouthOpening = Average(records.Select(record => record.MouthOpening)),
            MaximumMouthOpening = Maximum(records.Select(record => record.MouthOpening)),
            MouthOpeningSlopePerSecond = mouthOpeningSlope,
            AverageJawDroop = Average(records.Select(record => record.JawDroop)),
            MaximumJawDroop = Maximum(records.Select(record => record.JawDroop)),
            JawDroopSlopePerSecond = jawDroopSlope,
            EyeInsetEnabled = eyeInsetRegion is not null,
            EyeInsetRegion = eyeInsetRegion,
            EyeInsetDetectedRegions = records
                .Select(record => record.EyeInsetRegion)
                .Where(static region => !string.IsNullOrWhiteSpace(region))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static region => region)
                .ToList(),
            EyeInsetDominantRegion = records
                .Where(static record => record.EyeInsetHasMeasurement && !string.IsNullOrWhiteSpace(record.EyeInsetRegion))
                .GroupBy(static record => record.EyeInsetRegion, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static group => group.Count())
                .Select(static group => group.Key)
                .FirstOrDefault() ?? "",
            EyeInsetMeasuredFrames = records.Count(record => record.EyeInsetHasMeasurement),
            EyeInsetMeasurementRate = Rate(records.Count(record => record.EyeInsetHasMeasurement), records.Count),
            EyeInsetAverageOpening = Average(records.Select(record => record.EyeInsetAverageOpening)),
            EyeInsetMinimumOpening = Minimum(records.Select(record => record.EyeInsetAverageOpening)),
            EyeInsetMaximumOpening = Maximum(records.Select(record => record.EyeInsetAverageOpening)),
            EyeInsetOpeningSlopePerSecond = eyeInsetSlope,
            EyeInsetCueHasMeasurementRate = Rate(records.Count(record => record.EyeInsetCueHasMeasurement), records.Count),
            EyeInsetCueBaselineReadyRate = Rate(records.Count(record => record.EyeInsetCueBaselineReady), records.Count),
            EyeInsetCueEligibleRate = Rate(records.Count(record => record.EyeInsetCueEligible), records.Count),
            EyeInsetCueAverageQuality = Average(records.Select(record => (double?)record.EyeInsetCueQuality)),
            EyeInsetCueMaximumClosure = Maximum(records.Select(record => record.EyeInsetCueClosure)),
            EyeInsetCueMaximumScore = Maximum(records.Select(record => (double?)record.EyeInsetCueScore)),
            EyeInsetFullFramePairedSamples = eyeInsetAgreement.PairedSamples,
            EyeInsetFullFramePairedRate = eyeInsetAgreement.PairedRate,
            EyeInsetFullFrameOpeningCorrelation = eyeInsetAgreement.OpeningCorrelation,
            EyeInsetFullFrameNormalizedMeanAbsoluteError = eyeInsetAgreement.NormalizedMeanAbsoluteError,
            EyeInsetFullFrameDirectionAgreement = eyeInsetAgreement.DirectionAgreement,
            EyeInsetFullFrameSlopeDirectionAgreement = eyeInsetAgreement.SlopeDirectionAgreement,
            EyeInsetFullFrameAgreementTrustPercent = eyeInsetRegion is null ? (double?)null : eyeInsetAgreement.AgreementTrustPercent,
            EyeInsetAverageConfidence = Average(records.Select(record => record.EyeInsetConfidence)),
            EyeInsetImageQualityRate = Rate(records.Count(record => record.EyeInsetImageQualityAvailable), records.Count),
            EyeInsetAverageGlare = Average(records.Where(static record => record.EyeInsetImageQualityAvailable).Select(record => record.EyeInsetGlare)),
            EyeInsetMaximumGlare = Maximum(records.Where(static record => record.EyeInsetImageQualityAvailable).Select(record => record.EyeInsetGlare)),
            EyeInsetAverageContrast = Average(records.Where(static record => record.EyeInsetImageQualityAvailable).Select(record => record.EyeInsetContrast)),
            EyeInsetAverageSharpness = Average(records.Where(static record => record.EyeInsetImageQualityAvailable).Select(record => record.EyeInsetSharpness)),
            EyeInsetAverageDarkCoverage = Average(records.Where(static record => record.EyeInsetImageQualityAvailable).Select(record => record.EyeInsetDarkCoverage)),
            PersonalModelPath = personalModelPath,
            PersonalFaceMotionModelPath = motionModelPath,
            PersonalFaceMotionModelStatus = motionModel.Status,
            PersonalFaceMotionModelUsableObservations = motionModel.UsableObservationCount,
            PersonalFaceMotionModelMotionPairs = motionModel.MotionPairCount,
            PersonalFaceMotionModelEyeClosingVelocity = motionModel.EyeClosingVelocityPerSecond.Average,
            PersonalFaceMotionModelMouthOpeningVelocity = motionModel.MouthOpeningVelocityPerSecond.Average,
            PersonalFaceMotionModelJawDroopVelocity = motionModel.JawDroopVelocityPerSecond.Average,
            PersonalFaceMotionModelEyeClosingWithMouthOpeningRate = motionModel.EyeClosingWithMouthOpeningRate,
            PersonalFaceMotionModelEyeClosingWithJawDroopRate = motionModel.EyeClosingWithJawDroopRate,
            PersonalFaceCorpusReadinessPath = corpusReadinessPath,
            PersonalFaceCorpusReadinessHtmlPath = corpusReadinessHtmlPath,
            PersonalFaceCorpusReadinessStatus = corpusReadiness.Status,
            PersonalFaceCorpusReadinessPercent = corpusReadiness.OverallReadinessPercent,
            PersonalFaceCorpusDataAuditHealthPercent = corpusReadiness.DataAuditHealthPercent,
            PersonalFaceCorpusPoseEstimationHealthPercent = corpusReadiness.PoseEstimationHealthPercent,
            PersonalFaceCorpusFeatureAnchoringHealthPercent = corpusReadiness.FeatureAnchoringHealthPercent,
            PersonalFaceCorpusIdentitySessionHealthPercent = corpusReadiness.IdentitySessionHealthPercent,
            PersonalFaceCorpusIdentitySessionAuditStage = corpusReadiness.IdentitySessionAuditStage,
            PersonalFaceCorpusIdentitySessionAuditStatus = corpusReadiness.IdentitySessionAuditStatus,
            PersonalFaceCorpusRecentIdentityMeasurementSamples = corpusReadiness.RecentIdentityMeasurementSamples,
            PersonalFaceCorpusAverageRecentIdentityConfidencePercent = corpusReadiness.AverageRecentIdentityConfidencePercent,
            PersonalFaceCorpusMinimumRecentIdentityConfidencePercent = corpusReadiness.MinimumRecentIdentityConfidencePercent,
            PersonalFaceCorpusRecentIdentityOutlierFrameRate = corpusReadiness.RecentIdentityOutlierFrameRate,
            PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent = corpusReadiness.PoseExplainedFeatureMotionHealthPercent,
            PersonalFaceCorpusPoseExplainedFeatureObservedRange = corpusReadiness.PoseExplainedFeatureObservedRange,
            PersonalFaceCorpusPoseExplainedFeatureExpectedRange = corpusReadiness.PoseExplainedFeatureExpectedRange,
            PersonalFaceCorpusEyeApertureReliabilityHealthPercent = corpusReadiness.EyeApertureReliabilityHealthPercent,
            PersonalFaceCorpusPossibleOneEyeArtifactRate = corpusReadiness.PossibleOneEyeArtifactRate,
            PersonalFaceCorpusEyeAgreementAveragePercent = corpusReadiness.EyeAgreementAveragePercent,
            PersonalFaceCorpusEyeAgreementMinimumPercent = corpusReadiness.EyeAgreementMinimumPercent,
            PersonalFaceCorpusMouthVerticalAnchorHealthPercent = corpusReadiness.MouthVerticalAnchorHealthPercent,
            PersonalFaceCorpusMouthVerticalAnchorSamplesReviewed = corpusReadiness.MouthVerticalAnchorSamplesReviewed,
            PersonalFaceCorpusMouthVerticalAnchorSuspiciousSampleRate = corpusReadiness.MouthVerticalAnchorSuspiciousSampleRate,
            PersonalFaceCorpusJawDroopScaleHealthPercent = corpusReadiness.JawDroopScaleHealthPercent,
            PersonalFaceCorpusMeasurementJournalCoveragePercent = corpusReadiness.MeasurementJournalCoveragePercent,
            PersonalFaceCorpusDataAuditFindings = corpusReadiness.DataAuditFindings,
            PersonalFaceCorpusLearningStabilityCoveragePercent = corpusReadiness.LearningStabilityCoveragePercent,
            PersonalFaceCorpusContourShapeCoveragePercent = corpusReadiness.ContourShapeCoveragePercent,
            PersonalFaceCorpusContourDepthProfileHealthPercent = corpusReadiness.ContourDepthProfileHealthPercent,
            PersonalFaceCorpusSurfaceShapeCoveragePercent = corpusReadiness.SurfaceShapeCoveragePercent,
            PersonalFaceCorpusSurfaceDepthProfileHealthPercent = corpusReadiness.SurfaceDepthProfileHealthPercent,
            PersonalFaceCorpusZDistanceCoveragePercent = corpusReadiness.ZDistanceCoveragePercent,
            PersonalFaceCorpusZDistanceEvidenceHealthPercent = corpusReadiness.ZDistanceEvidenceHealthPercent,
            PersonalFaceCorpusZEstimateSamples = corpusReadiness.ZEstimateSamples,
            PersonalFaceCorpusAverageZConfidencePercent = corpusReadiness.AverageZConfidencePercent,
            PersonalFaceCorpusMinimumZConfidencePercent = corpusReadiness.MinimumZConfidencePercent,
            PersonalFaceCorpusZApparentDistanceRange = corpusReadiness.ZApparentDistanceRange,
            PersonalFaceCorpusZRelativeToReferenceRange = corpusReadiness.ZRelativeToReferenceRange,
            PersonalFaceCorpusZApparentOnlyRate = corpusReadiness.ZApparentOnlyRate,
            PersonalFaceCorpusARotationAroundXCoveragePercent = corpusReadiness.ARotationAroundXCoveragePercent,
            PersonalFaceCorpusBRotationAroundYCoveragePercent = corpusReadiness.BRotationAroundYCoveragePercent,
            PersonalFaceCorpusCRotationAroundZCoveragePercent = corpusReadiness.CRotationAroundZCoveragePercent,
            PersonalFaceCorpusXYZABCCoveragePercent = corpusReadiness.XYZABCCoveragePercent,
            PersonalFaceCorpusEyeBehindGlassesTrustPercent = corpusReadiness.EyeBehindGlassesTrustPercent,
            PersonalFaceCorpusMouthJawTrustPercent = corpusReadiness.MouthJawTrustPercent,
            PersonalFaceCorpusDirectFeatureMeasurementTrustPercent = corpusReadiness.DirectFeatureMeasurementTrustPercent,
            PersonalFaceCorpusLeftEyeShapeSamples = corpusReadiness.LeftEyeShapeSamples,
            PersonalFaceCorpusRightEyeShapeSamples = corpusReadiness.RightEyeShapeSamples,
            PersonalFaceCorpusOuterLipShapeSamples = corpusReadiness.OuterLipShapeSamples,
            PersonalFaceCorpusInnerLipShapeSamples = corpusReadiness.InnerLipShapeSamples,
            PersonalFaceCorpusJawShapeSamples = corpusReadiness.JawShapeSamples,
            PersonalFaceCorpusLeftBrowShapeSamples = corpusReadiness.LeftBrowShapeSamples,
            PersonalFaceCorpusRightBrowShapeSamples = corpusReadiness.RightBrowShapeSamples,
            PersonalFaceCorpusNoseBridgeShapeSamples = corpusReadiness.NoseBridgeShapeSamples,
            PersonalFaceCorpusNoseBaseShapeSamples = corpusReadiness.NoseBaseShapeSamples,
            PersonalFaceCorpusLeftCheekSurfaceSamples = corpusReadiness.LeftCheekSurfaceSamples,
            PersonalFaceCorpusRightCheekSurfaceSamples = corpusReadiness.RightCheekSurfaceSamples,
            PersonalFaceCorpusForeheadSurfaceSamples = corpusReadiness.ForeheadSurfaceSamples,
            PersonalFaceCorpusReadinessWarnings = corpusReadiness.Warnings,
            PersonalFaceCorpusReadinessNextCaptureSuggestions = corpusReadiness.NextCaptureSuggestions,
            PersonalFaceCollectionAuditPath = collectionAuditPath,
            PersonalFaceCollectionAuditHtmlPath = collectionAuditHtmlPath,
            PersonalFaceCollectionAuditStatus = collectionAudit.Status,
            PersonalFaceCollectionAuditFramesReviewed = collectionAudit.TotalFramesReviewed,
            PersonalFaceCollectionAuditFaceDetectionRate = collectionAudit.FaceDetectionRate,
            PersonalFaceCollectionAuditAcceptedRate = collectionAudit.PersonalModelAcceptedRate,
            PersonalFaceCollectionAuditCollectableRate = collectionAudit.CaptureQualityCollectableRate,
            PersonalFaceCollectionAuditAvatarGradeRate = collectionAudit.CaptureQualityAvatarGradeRate,
            PersonalFaceCollectionAuditTopRejections = collectionAudit.TopPersonalModelRejectionReasons,
            PersonalFaceCollectionAuditTopCaptureIssues = collectionAudit.TopCaptureQualityIssues,
            PersonalFaceCollectionAuditNextActions = collectionAudit.NextActions,
            CaptureQualityAverageScore = Average(records.Select(record => (double?)record.CaptureQualityScore)),
            CaptureQualityMinimumScore = Minimum(records.Select(record => (double?)record.CaptureQualityScore)),
            CaptureQualityCanCollectFrames = records.Count(record => record.CaptureQualityCanCollect),
            CaptureQualityCanCollectRate = Rate(records.Count(record => record.CaptureQualityCanCollect), records.Count),
            CaptureQualityAvatarGradeFrames = records.Count(record => record.CaptureQualityAvatarGrade),
            CaptureQualityAvatarGradeRate = Rate(records.Count(record => record.CaptureQualityAvatarGrade), records.Count),
            CaptureQualityAverageEyeScore = Average(records.Select(record => (double?)record.CaptureQualityEyeScore)),
            CaptureQualityAverageMouthScore = Average(records.Select(record => (double?)record.CaptureQualityMouthScore)),
            CaptureQualityAverageGlassesScore = Average(records.Select(record => (double?)record.CaptureQualityGlassesScore)),
            CaptureQualityAverageFaceScaleScore = Average(records.Select(record => (double?)record.CaptureQualityFaceScaleScore)),
            CaptureQualityLabels = records
                .Select(record => record.CaptureQualityLabel)
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static label => label)
                .ToList(),
            CaptureQualityTopIssues = TopPipeValues(records.Select(record => record.CaptureQualityIssues), 8),
            CaptureQualityTopSuggestions = TopPipeValues(records.Select(record => record.CaptureQualitySuggestions), 8),
            MeasurementFacePreviewJsonPath = previewFiles.JsonPath,
            MeasurementFacePreviewHtmlPath = previewFiles.HtmlPath,
            MeasurementAvatarTrainingPackageJsonPath = avatarPackageFiles.JsonPath,
            MeasurementAvatarTrainingPackageHtmlPath = avatarPackageFiles.HtmlPath,
            MeasurementAvatarCapturePlanJsonPath = avatarCapturePlanFiles.JsonPath,
            MeasurementAvatarCapturePlanHtmlPath = avatarCapturePlanFiles.HtmlPath,
            PersonalModelStatus = personalModel.Status,
            PersonalModelObservedSamples = personalModel.ObservedSamples,
            PersonalModelAcceptedSamples = personalModel.AcceptedSamples,
            PersonalModelRejectedSamples = personalModel.RejectedSamples,
            PersonalModelEventLikeRejectedSamples = personalModel.EventLikeRejectedSamples,
            PersonalModelLowQualityRejectedSamples = personalModel.LowQualityRejectedSamples,
            PersonalModelTrackingArtifactRejectedSamples = personalModel.TrackingArtifactRejectedSamples,
            PersonalModelNoFaceRejectedSamples = personalModel.NoFaceRejectedSamples,
            PersonalModelSubjectMismatchRejectedSamples = personalModel.SubjectMismatchRejectedSamples,
            PersonalModelAcceptedRate = personalModel.AcceptedRate,
            PersonalModelAverageFaceReliability = personalModel.AverageFaceReliabilityPercent,
            PersonalModelAverageFaceContinuity = personalModel.AverageFaceContinuityPercent,
            PersonalModelIdentityGatePolicy = personalModel.IdentityGatePolicy,
            PersonalModelIdentitySignatureSamples = personalModel.IdentitySignatureSamples,
            PersonalModelIdentityCoveragePercent = corpusReadiness.IdentityCoveragePercent,
            PersonalModelContourShapeCoveragePercent = corpusReadiness.ContourShapeCoveragePercent,
            PersonalModelContourDepthProfileHealthPercent = corpusReadiness.ContourDepthProfileHealthPercent,
            PersonalModelSurfaceShapeCoveragePercent = corpusReadiness.SurfaceShapeCoveragePercent,
            PersonalModelSurfaceDepthProfileHealthPercent = corpusReadiness.SurfaceDepthProfileHealthPercent,
            PersonalModelZDistanceCoveragePercent = corpusReadiness.ZDistanceCoveragePercent,
            PersonalModelZDistanceEvidenceHealthPercent = corpusReadiness.ZDistanceEvidenceHealthPercent,
            PersonalModelZEstimateSamples = personalModel.ZEstimateSamples,
            PersonalModelZApparentDistanceRange = RangeOptional(personalModel.ZApparentDistanceUnits.Minimum, personalModel.ZApparentDistanceUnits.Maximum),
            PersonalModelAverageZConfidencePercent = personalModel.ZConfidencePercent.Average,
            PersonalModelARotationAroundXCoveragePercent = corpusReadiness.ARotationAroundXCoveragePercent,
            PersonalModelBRotationAroundYCoveragePercent = corpusReadiness.BRotationAroundYCoveragePercent,
            PersonalModelCRotationAroundZCoveragePercent = corpusReadiness.CRotationAroundZCoveragePercent,
            PersonalModelXYZABCCoveragePercent = corpusReadiness.XYZABCCoveragePercent,
            PersonalModelEyeBehindGlassesTrustPercent = corpusReadiness.EyeBehindGlassesTrustPercent,
            PersonalModelMouthJawTrustPercent = corpusReadiness.MouthJawTrustPercent,
            PersonalModelDirectFeatureMeasurementTrustPercent = corpusReadiness.DirectFeatureMeasurementTrustPercent,
            PersonalModelLearningAnchorPercent = personalModel.LearningStability.AnchorPercent,
            PersonalModelLearningAnchorStatus = personalModel.LearningStability.AnchorStatus,
            PersonalModelMaxNextSampleInfluencePercent = personalModel.LearningStability.MaximumNextSampleInfluencePercent,
            PersonalModelMaxEventLikeNextSampleInfluencePercent = personalModel.LearningStability.MaximumEventLikeNextSampleInfluencePercent,
            PersonalModelFaceAspectAverage = personalModel.FaceAspectRatio.Average,
            PersonalModelEyeMidlineXToFaceWidthAverage = personalModel.EyeMidlineXToFaceWidth.Average,
            PersonalModelMouthCenterXToFaceWidthAverage = personalModel.MouthCenterXToFaceWidth.Average,
            PersonalModelEyeToMouthXOffsetToFaceWidthAverage = personalModel.EyeToMouthXOffsetToFaceWidth.Average,
            PersonalModelInterEyeDistanceToFaceWidthAverage = personalModel.InterEyeDistanceToFaceWidth.Average,
            PersonalModelMouthWidthToFaceWidthAverage = personalModel.MouthWidthToFaceWidth.Average,
            PersonalModelAverageEyeOpening = personalModel.AverageEyeOpeningRatio.Average,
            PersonalModelEyeOpeningNormalLow = personalModel.AverageEyeOpeningRatio.NormalLow,
            PersonalModelEyeOpeningNormalHigh = personalModel.AverageEyeOpeningRatio.NormalHigh,
            PersonalModelAverageMouthOpening = personalModel.MouthOpeningRatio.Average,
            PersonalModelMouthOpeningNormalLow = personalModel.MouthOpeningRatio.NormalLow,
            PersonalModelMouthOpeningNormalHigh = personalModel.MouthOpeningRatio.NormalHigh,
            PersonalModelAverageJawDroop = personalModel.JawDroopRatio.Average,
            PersonalModelFaceCenterXRange = RangeOptional(personalModel.FaceCenterX.Minimum, personalModel.FaceCenterX.Maximum),
            PersonalModelFaceCenterYRange = RangeOptional(personalModel.FaceCenterY.Minimum, personalModel.FaceCenterY.Maximum),
            PersonalModelFaceWidthRange = RangeOptional(personalModel.FaceWidth.Minimum, personalModel.FaceWidth.Maximum),
            PersonalModel = personalModel,
            CsvPath = csvPath
        };
        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        WriteHtmlReport(
            reportPath,
            inputPath,
            outputFolder,
            csvPath,
            summaryPath,
            personalModelPath,
            motionModelPath,
            corpusReadinessHtmlPath,
            collectionAuditHtmlPath,
            previewFiles.HtmlPath,
            avatarPackageFiles.HtmlPath,
            avatarCapturePlanFiles.HtmlPath,
            sampleFps,
            eyeInsetRegion,
            writeOverlays,
            records,
            fullFrameEyeSlope,
            mouthOpeningSlope,
            jawDroopSlope,
            eyeInsetSlope,
            eyeInsetAgreement);
    }

    private static MeasurementFacePreviewFiles WriteMeasurementFacePreview(string outputFolder, PersonalFaceModel personalModel)
    {
        var gate = FaceReconstructionSubjectGate.FromPersonalModel(
            personalModel,
            manualSubjectConfirmed: true,
            reason: "explicit offline evaluation media selected for this subject");
        var preview = new MeasurementFacePreviewBuilder().Build(personalModel, gate);
        return new MeasurementFacePreviewStore().Write(outputFolder, preview);
    }

    private static MeasurementAvatarTrainingPackageFiles WriteMeasurementAvatarTrainingPackage(
        string outputFolder,
        PersonalFaceModel personalModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness corpusReadiness,
        PersonalFaceCollectionAudit collectionAudit)
    {
        var gate = FaceReconstructionSubjectGate.FromPersonalModel(
            personalModel,
            manualSubjectConfirmed: true,
            reason: "explicit offline evaluation media selected for this subject");
        var package = new MeasurementAvatarTrainingPackageBuilder().Build(
            personalModel,
            motionModel,
            corpusReadiness,
            gate,
            measurementJournalBytes: 0L,
            collectionAudit: collectionAudit);
        return new MeasurementAvatarTrainingPackageStore().Write(outputFolder, package);
    }

    private static MeasurementAvatarCapturePlanFiles WriteMeasurementAvatarCapturePlan(
        string outputFolder,
        PersonalFaceModel personalModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness corpusReadiness)
    {
        var gate = FaceReconstructionSubjectGate.FromPersonalModel(
            personalModel,
            manualSubjectConfirmed: true,
            reason: "explicit offline evaluation media selected for this subject");
        var plan = new MeasurementAvatarCapturePlanBuilder().Build(
            personalModel,
            motionModel,
            corpusReadiness,
            gate,
            measurementJournalBytes: 0L);
        return new MeasurementAvatarCapturePlanStore().Write(outputFolder, plan);
    }

    private static PersonalFaceCollectionAudit BuildCollectionAudit(
        PersonalFaceModel personalModel,
        IReadOnlyList<VisionFrameRecord> records)
    {
        var start = DateTime.UnixEpoch;
        var observations = records
            .Select(record => new PersonalFaceCollectionAuditObservation
            {
                ReviewedAtUtc = start.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
                SubjectConfirmed = true,
                PausedForEventOrCalibration = record.PersonalModelRejectionKind.Equals(
                    PersonalFaceModelRejectionKind.EventLike.ToString(),
                    StringComparison.OrdinalIgnoreCase),
                HasFace = record.HasFace,
                PersonalModelAccepted = record.PersonalModelAccepted,
                PersonalModelRejectionKind = record.PersonalModelRejectionKind,
                PersonalModelUpdateReason = record.PersonalModelUpdateReason,
                CaptureQualityLabel = record.CaptureQualityLabel,
                CaptureQualityScorePercent = record.CaptureQualityScore,
                CaptureQualityCanCollect = record.CaptureQualityCanCollect,
                CaptureQualityAvatarGrade = record.CaptureQualityAvatarGrade,
                CaptureQualityReason = record.CaptureQualityReason,
                CaptureQualityCameraModeScorePercent = record.CaptureQualityCameraModeScore,
                CaptureQualityFaceScaleScorePercent = record.CaptureQualityFaceScaleScore,
                CaptureQualityEyeScorePercent = record.CaptureQualityEyeScore,
                CaptureQualityMouthScorePercent = record.CaptureQualityMouthScore,
                CaptureQualityStabilityScorePercent = record.CaptureQualityStabilityScore,
                CaptureQualityGlassesScorePercent = record.CaptureQualityGlassesScore,
                CaptureQualityStorageScorePercent = record.CaptureQualityStorageScore,
                CaptureQualityFaceWidthPercent = record.CaptureQualityFaceWidthPercent,
                CaptureQualityFaceHeightPercent = record.CaptureQualityFaceHeightPercent,
                CaptureQualityIssues = SplitPipeList(record.CaptureQualityIssues),
                CaptureQualitySuggestions = SplitPipeList(record.CaptureQualitySuggestions)
            })
            .ToList();
        return new PersonalFaceCollectionAuditBuilder().Build(personalModel, observations);
    }

    private static PersonalFaceMotionModel BuildMotionModel(
        PersonalFaceModel personalModel,
        IReadOnlyList<VisionFrameRecord> records)
    {
        var start = DateTime.UnixEpoch;
        var observations = records
            .Where(static record => record.HasFace && record.CaptureQualityCanCollect)
            .Select(record => new PersonalFaceMotionObservation
            {
                SubjectId = personalModel.SubjectId,
                SubjectDisplayName = personalModel.SubjectDisplayName,
                SubjectCollectionMode = personalModel.SubjectCollectionMode,
                CapturedAtUtc = start.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
                AcceptedForPersonalModel = record.PersonalModelAccepted,
                Source = record.Backend,
                SampleWeight = Math.Clamp(record.OverallQuality / 100d * 0.60d + record.FaceReliability / 100d * 0.40d, 0.05d, 1.25d),
                OverallQualityPercent = record.OverallQuality,
                FaceReliabilityPercent = record.FaceReliability,
                FaceContinuityPercent = record.FaceContinuity,
                EyeReliabilityPercent = record.EyeReliability,
                MouthReliabilityPercent = record.MouthReliability,
                HeadYawDegrees = record.HeadYaw,
                HeadPitchDegrees = record.HeadPitch,
                HeadRollDegrees = record.HeadRoll,
                ZApparentDistanceUnits = record.ZApparentDistanceUnits,
                ZRelativeToReference = record.ZRelativeToReference,
                ZConfidencePercent = record.ZConfidencePercent,
                ZEstimateKind = record.ZEstimateKind,
                AverageEyeOpeningRatio = record.AverageEyeOpening,
                MouthOpeningRatio = record.MouthOpening,
                JawDroopRatio = record.JawDroop,
                MediaPipeAverageEyeBlinkPercent = record.MediaPipeAverageEyeBlink,
                MediaPipeJawOpenPercent = record.MediaPipeJawOpen,
                MediaPipeMouthClosePercent = record.MediaPipeMouthClose,
                EyeArtifactSuppressed = record.EyeArtifactSuppressed,
                AnyEyeReconstructed = record.LeftEyeReconstructed || record.RightEyeReconstructed,
                MouthReconstructed = record.MouthReconstructed
            });

        return new PersonalFaceMotionModelBuilder().Build(observations);
    }

    private static IReadOnlyList<PersonalFaceMeasurementSample> BuildCorpusSamples(
        PersonalFaceModel personalModel,
        IReadOnlyList<VisionFrameRecord> records)
    {
        var start = DateTime.UnixEpoch;
        return records
            .Where(static record => record.HasFace && record.CaptureQualityCanCollect)
            .Select(record => new PersonalFaceMeasurementSample
            {
                SubjectId = personalModel.SubjectId,
                SubjectDisplayName = personalModel.SubjectDisplayName,
                SubjectCollectionMode = personalModel.SubjectCollectionMode,
                CapturedAtUtc = start.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
                SampleWeight = Math.Clamp(record.OverallQuality / 100d * 0.60d + record.FaceReliability / 100d * 0.40d, 0.05d, 1.25d),
                TrackingConfidence = record.TrackingConfidence,
                EyeConfidence = record.EyeConfidence,
                MouthConfidence = record.MouthConfidence,
                OverallQualityPercent = record.OverallQuality,
                EyeQualityPercent = record.EyeQuality,
                MouthQualityPercent = record.MouthQuality,
                CaptureQualityLabel = record.CaptureQualityLabel,
                CaptureQualityScorePercent = record.CaptureQualityScore,
                CaptureQualityCanCollect = record.CaptureQualityCanCollect,
                CaptureQualityAvatarGrade = record.CaptureQualityAvatarGrade,
                CaptureQualityReason = record.CaptureQualityReason,
                CaptureQualityCameraModeScorePercent = record.CaptureQualityCameraModeScore,
                CaptureQualityFaceScaleScorePercent = record.CaptureQualityFaceScaleScore,
                CaptureQualityEyeScorePercent = record.CaptureQualityEyeScore,
                CaptureQualityMouthScorePercent = record.CaptureQualityMouthScore,
                CaptureQualityStabilityScorePercent = record.CaptureQualityStabilityScore,
                CaptureQualityGlassesScorePercent = record.CaptureQualityGlassesScore,
                CaptureQualityStorageScorePercent = record.CaptureQualityStorageScore,
                CaptureQualityFaceWidthPercent = record.CaptureQualityFaceWidthPercent,
                CaptureQualityFaceHeightPercent = record.CaptureQualityFaceHeightPercent,
                CaptureQualityIssues = SplitPipeList(record.CaptureQualityIssues),
                CaptureQualitySuggestions = SplitPipeList(record.CaptureQualitySuggestions),
                FaceReliabilityPercent = record.FaceReliability,
                FaceContinuityPercent = record.FaceContinuity,
                EyeReliabilityPercent = record.EyeReliability,
                MouthReliabilityPercent = record.MouthReliability,
                FaceCenterX = record.FaceLeft + record.FaceWidth / 2d,
                FaceCenterY = record.FaceTop + record.FaceHeight / 2d,
                FaceWidth = record.FaceWidth,
                FaceHeight = record.FaceHeight,
                ZApparentDistanceUnits = record.ZApparentDistanceUnits,
                ZRelativeToReference = record.ZRelativeToReference,
                ZConfidencePercent = record.ZConfidencePercent,
                DistanceInches = record.DistanceInches,
                DistanceCalibrated = record.DistanceCalibrated,
                ZUsesCameraFov = record.ZUsesCameraFov,
                ZUsesLearnedReference = record.ZUsesLearnedReference,
                ZEstimateKind = record.ZEstimateKind,
                ZQualityLabel = record.ZQualityLabel,
                ZDistanceSource = record.ZDistanceSource,
                HeadYawDegrees = record.HeadYaw,
                HeadPitchDegrees = record.HeadPitch,
                HeadRollDegrees = record.HeadRoll,
                LeftEyeOpeningRatio = record.LeftEyeOpening,
                RightEyeOpeningRatio = record.RightEyeOpening,
                AverageEyeOpeningRatio = record.AverageEyeOpening,
                MouthOpeningRatio = record.MouthOpening,
                JawDroopRatio = record.JawDroop,
                MediaPipeAverageEyeBlinkPercent = record.MediaPipeAverageEyeBlink,
                MediaPipeJawOpenPercent = record.MediaPipeJawOpen,
                MediaPipeMouthClosePercent = record.MediaPipeMouthClose,
                FaceAspectRatio = record.FaceAspectRatio,
                EyeMidlineXToFaceWidth = record.EyeMidlineXToFaceWidth,
                MouthCenterXToFaceWidth = record.MouthCenterXToFaceWidth,
                EyeToMouthXOffsetToFaceWidth = record.EyeToMouthXOffsetToFaceWidth,
                InterEyeDistanceToFaceWidth = record.InterEyeDistanceToFaceWidth,
                LeftEyeWidthToFaceWidth = record.LeftEyeWidthToFaceWidth,
                RightEyeWidthToFaceWidth = record.RightEyeWidthToFaceWidth,
                MouthWidthToFaceWidth = record.MouthWidthToFaceWidth,
                EyeMidlineYToFaceHeight = record.EyeMidlineYToFaceHeight,
                MouthCenterYToFaceHeight = record.MouthCenterYToFaceHeight,
                EyeToMouthYDistanceToFaceHeight = record.EyeToMouthYDistanceToFaceHeight,
                IdentityMeasurementAvailable = record.IdentityMeasurementAvailable,
                IdentityConfidencePercent = record.PersonalIdentityConfidence ?? 0d,
                IdentityComparedFeatureCount = record.PersonalIdentityComparedFeatures > 0
                    ? record.PersonalIdentityComparedFeatures
                    : record.IdentityUsableFeatureCount,
                IdentityOutlierFeatureCount = record.PersonalIdentityOutlierFeatures,
                PossibleOneEyeArtifact = record.PossibleOneEyeArtifact,
                EyeArtifactSuppressed = record.EyeArtifactSuppressed,
                LeftEyeReconstructed = record.LeftEyeReconstructed,
                RightEyeReconstructed = record.RightEyeReconstructed,
                MouthReconstructed = record.MouthReconstructed,
                MediaPipeEyeOpeningCorrected = record.MediaPipeEyeOpeningCorrected,
                MediaPipeMouthOpeningCorrected = record.MediaPipeMouthOpeningCorrected
            })
            .ToList();
    }

    private static bool IsImagePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff";
    }

    private static EyeInsetRegion? ParseEyeInsetRegion(string[] args, int startIndex)
    {
        for (var index = startIndex; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals("--eye-inset", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--bottom-right-eye-inset", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return EyeInsetApertureAnalyzer.BottomRightDefaultRegion;
                }

                return ParseEyeInsetValue(args[index + 1]);
            }

            const string prefix = "--eye-inset=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ParseEyeInsetValue(argument[prefix.Length..]);
            }
        }

        return null;
    }

    private static EyeInsetRegion ParseEyeInsetValue(string value)
    {
        if (value.Equals("bottom-right", StringComparison.OrdinalIgnoreCase)
            || value.Equals("br", StringComparison.OrdinalIgnoreCase)
            || value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return EyeInsetApertureAnalyzer.BottomRightDefaultRegion;
        }

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("detect", StringComparison.OrdinalIgnoreCase))
        {
            return EyeInsetApertureAnalyzer.AutoSearchRegion;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new ArgumentException("Eye inset must be 'bottom-right' or normalized left,top,width,height values.");
        }

        var numbers = parts
            .Select(part => double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
        return new EyeInsetRegion(
            "custom",
            Math.Clamp(numbers[0], 0d, 0.99d),
            Math.Clamp(numbers[1], 0d, 0.99d),
            Math.Clamp(numbers[2], 0.01d, 1d),
            Math.Clamp(numbers[3], 0.01d, 1d));
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Format(double? value)
    {
        return value is double number ? number.ToString("0.######", CultureInfo.InvariantCulture) : "";
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var valid = values.OfType<double>().ToList();
        return valid.Count == 0 ? null : valid.Average();
    }

    private static double? Minimum(IEnumerable<double?> values)
    {
        var valid = values.OfType<double>().ToList();
        return valid.Count == 0 ? null : valid.Min();
    }

    private static double? Maximum(IEnumerable<double?> values)
    {
        var valid = values.OfType<double>().ToList();
        return valid.Count == 0 ? null : valid.Max();
    }

    private static double? Range(IEnumerable<double> values)
    {
        var valid = values.ToList();
        return valid.Count == 0 ? null : valid.Max() - valid.Min();
    }

    private static double? RangeOptional(double? minimum, double? maximum)
    {
        return minimum is double min && maximum is double max
            ? max - min
            : null;
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : count / (double)total;
    }

    private static IReadOnlyList<string> TopPipeValues(IEnumerable<string> values, int take)
    {
        return values
            .SelectMany(static value => value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take))
            .Select(static group => $"{group.Key} ({group.Count()})")
            .ToList();
    }

    private static List<string> SplitPipeList(string value)
    {
        return value
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasMediaPipeDenseLock(VisionFrameRecord record)
    {
        return record.BackendStatus.Contains("MediaPipe dense landmark lock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMediaPipeBlendshapeEvidence(VisionFrameRecord record)
    {
        return record.MediaPipeAverageEyeBlink.HasValue
            || record.MediaPipeJawOpen.HasValue
            || record.MediaPipeMouthClose.HasValue;
    }

    private static string CreateOverlayFolder(string outputFolder, bool writeOverlays)
    {
        if (!writeOverlays)
        {
            return "";
        }

        var overlayFolder = Path.Combine(outputFolder, "overlay_frames");
        Directory.CreateDirectory(overlayFolder);
        foreach (var existing in Directory.EnumerateFiles(overlayFolder, "*.png"))
        {
            File.Delete(existing);
        }

        return overlayFolder;
    }

    private static int CountOverlayFrames(string outputFolder, bool writeOverlays)
    {
        var overlayFolder = Path.Combine(outputFolder, "overlay_frames");
        return writeOverlays && Directory.Exists(overlayFolder)
            ? Directory.EnumerateFiles(overlayFolder, "*.png").Count()
            : 0;
    }

    private static void WriteHtmlReport(
        string reportPath,
        string inputPath,
        string outputFolder,
        string csvPath,
        string summaryPath,
        string personalModelPath,
        string personalFaceMotionModelPath,
        string personalFaceCorpusReadinessPath,
        string personalFaceCollectionAuditPath,
        string measurementFacePreviewPath,
        string measurementAvatarTrainingPackagePath,
        string measurementAvatarCapturePlanPath,
        double sampleFps,
        EyeInsetRegion? eyeInsetRegion,
        bool writeOverlays,
        IReadOnlyList<VisionFrameRecord> records,
        double? fullFrameEyeSlope,
        double? mouthOpeningSlope,
        double? jawDroopSlope,
        double? eyeInsetSlope,
        EyeInsetAgreementAnalysis eyeInsetAgreement)
    {
        var rawFullFrameEyeSlope = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(record => (record.TimestampSeconds, record.RawAverageEyeOpening)));
        var rawWorkingEyeMaxDelta = Maximum(records.Select(static record => AbsDifference(record.RawAverageEyeOpening, record.AverageEyeOpening)));
        var rawWorkingMouthMaxDelta = Maximum(records.Select(static record => AbsDifference(record.RawMouthOpening, record.MouthOpening)));
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Avatar Builder Vision Evaluation</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{color-scheme:dark;--bg:#071014;--panel:#0d1a22;--panel2:#112532;--line:#29465a;--text:#e7f4ff;--muted:#a8bfd1;--accent:#5fb8ff;--warn:#ffcc66;--good:#79d69c;--bad:#ff8a8a}");
        html.AppendLine("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}main{max-width:1380px;margin:0 auto;padding:24px}h1,h2{margin:0 0 12px}h1{font-size:26px}h2{font-size:18px;margin-top:24px}.subtle{color:var(--muted)}.notice{border:1px solid var(--line);background:#081820;padding:12px;margin:14px 0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:10px}.card{border:1px solid var(--line);background:var(--panel);padding:12px}.card b{display:block;font-size:20px;margin-top:4px}.links a,.frame a{color:var(--accent)}table{width:100%;border-collapse:collapse;background:var(--panel)}th,td{border:1px solid var(--line);padding:8px;vertical-align:top}th{background:var(--panel2);text-align:left}.frames{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:12px}.frame{border:1px solid var(--line);background:var(--panel);padding:10px}.frame img{width:100%;height:auto;border:1px solid #385970;background:#000}.pill{display:inline-block;border:1px solid var(--line);padding:2px 6px;margin:2px 4px 2px 0}.good{color:var(--good)}.warn{color:var(--warn)}.bad{color:var(--bad)}code{color:#cbe9ff}</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body><main>");
        html.AppendLine("<h1>Avatar Builder Vision Evaluation</h1>");
        html.AppendLine("<p class=\"subtle\">Local offline review artifact for face, eyelid, mouth, jaw, and optional zoomed eye-inset tracking. This is for data gathering and clinician review only; it does not diagnose a medical condition.</p>");
        html.AppendLine("<div class=\"notice\">");
        html.AppendLine($"<div><strong>Input:</strong> <code>{H(inputPath)}</code></div>");
        html.AppendLine($"<div><strong>Output:</strong> <code>{H(outputFolder)}</code></div>");
        html.AppendLine($"<div><strong>Sample rate:</strong> {Display(sampleFps)} fps</div>");
        html.AppendLine($"<div><strong>Eye inset:</strong> {H(eyeInsetRegion?.Label ?? "none")}</div>");
        html.AppendLine($"<div class=\"links\"><a href=\"{H(RelativeHref(outputFolder, csvPath))}\">CSV evidence</a> | <a href=\"{H(RelativeHref(outputFolder, summaryPath))}\">JSON summary</a> | <a href=\"{H(RelativeHref(outputFolder, personalModelPath))}\">Personal face model</a> | <a href=\"{H(RelativeHref(outputFolder, personalFaceMotionModelPath))}\">Face motion model</a> | <a href=\"{H(RelativeHref(outputFolder, personalFaceCorpusReadinessPath))}\">Learning-data health</a> | <a href=\"{H(RelativeHref(outputFolder, personalFaceCollectionAuditPath))}\">Collection audit</a> | <a href=\"{H(RelativeHref(outputFolder, measurementFacePreviewPath))}\">Measurement face preview</a> | <a href=\"{H(RelativeHref(outputFolder, measurementAvatarTrainingPackagePath))}\">Avatar package</a> | <a href=\"{H(RelativeHref(outputFolder, measurementAvatarCapturePlanPath))}\">Capture plan</a></div>");
        html.AppendLine("</div>");

        html.AppendLine("<h2>Evidence Summary</h2>");
        html.AppendLine("<section class=\"grid\">");
        AppendMetricCard(html, "Frames", records.Count.ToString(CultureInfo.InvariantCulture), $"{CountOverlayFrames(outputFolder, writeOverlays)} overlay frame(s)");
        AppendMetricCard(html, "Face Lock", DisplayRate(Rate(records.Count(static record => record.HasFace), records.Count)), $"{records.Count(static record => record.HasFace)} face frame(s)");
        AppendMetricCard(html, "Eye Measurements", DisplayRate(Rate(records.Count(static record => record.AverageEyeOpening.HasValue), records.Count)), $"min {Display(Minimum(records.Select(static record => record.AverageEyeOpening)))}; slope {Display(fullFrameEyeSlope)}");
        AppendMetricCard(
            html,
            "Raw vs Working Eye",
            $"max delta {Display(rawWorkingEyeMaxDelta)}",
            $"raw slope {Display(rawFullFrameEyeSlope)}; paired {records.Count(static record => record.RawAverageEyeOpening.HasValue && record.AverageEyeOpening.HasValue)} frame(s)");
        AppendMetricCard(html, "Mouth Measurements", DisplayRate(Rate(records.Count(static record => record.MouthOpening.HasValue), records.Count)), $"max {Display(Maximum(records.Select(static record => record.MouthOpening)))}; slope {Display(mouthOpeningSlope)}");
        AppendMetricCard(
            html,
            "Raw vs Working Mouth",
            $"max delta {Display(rawWorkingMouthMaxDelta)}",
            $"paired {records.Count(static record => record.RawMouthOpening.HasValue && record.MouthOpening.HasValue)} frame(s)");
        AppendMetricCard(html, "Jaw Droop", DisplayRate(Rate(records.Count(static record => record.JawDroop.HasValue), records.Count)), $"max {Display(Maximum(records.Select(static record => record.JawDroop)))}; slope {Display(jawDroopSlope)}");
        AppendMetricCard(html, "Cue Usable", DisplayRate(Rate(records.Count(static record => record.CueUsable), records.Count)), $"max cue {Display(Maximum(records.Select(static record => record.CueScore)), "0.#")}%");
        AppendMetricCard(html, "Average Quality", $"{Display(Average(records.Select(record => (double?)record.OverallQuality)), "0.#")}%", $"lowest {Display(Minimum(records.Select(record => (double?)record.OverallQuality)), "0.#")}%");
        AppendMetricCard(
            html,
            "Face Reliability",
            $"{Display(Average(records.Select(record => (double?)record.FaceReliability)), "0.#")}%",
            $"continuity {Display(Average(records.Select(record => (double?)record.FaceContinuity)), "0.#")}%; usable {DisplayRate(Rate(records.Count(static record => record.FaceReliabilitySamples >= 3 && record.FaceReliability >= 55d), records.Count))}");
        AppendMetricCard(
            html,
            "MediaPipe Dense",
            DisplayRate(Rate(records.Count(HasMediaPipeDenseLock), records.Count)),
            $"{records.Count(HasMediaPipeBlendshapeEvidence)} blendshape frame(s)");
        AppendMetricCard(
            html,
            "MediaPipe Corrections",
            $"{records.Count(static record => record.MediaPipeEyeOpeningCorrected)}/{records.Count(static record => record.MediaPipeMouthOpeningCorrected)}",
            $"max eye {Display(Maximum(records.Select(static record => record.MediaPipeEyeOpeningCorrection is double correction ? Math.Abs(correction) : (double?)null)))}; max mouth {Display(Maximum(records.Select(static record => record.MediaPipeMouthOpeningCorrection is double correction ? Math.Abs(correction) : (double?)null)))}");
        AppendMetricCard(
            html,
            "Capture Quality",
            $"{Display(Average(records.Select(static record => (double?)record.CaptureQualityScore)), "0.#")}%",
            $"avatar-grade {DisplayRate(Rate(records.Count(static record => record.CaptureQualityAvatarGrade), records.Count))}; can collect {DisplayRate(Rate(records.Count(static record => record.CaptureQualityCanCollect), records.Count))}");
        var horizontalDrift = Maximum([
            Range(records.Select(static record => record.EyeMidlineXToFaceWidth).OfType<double>()),
            Range(records.Select(static record => record.MouthCenterXToFaceWidth).OfType<double>()),
            Range(records.Select(static record => record.EyeToMouthXOffsetToFaceWidth).OfType<double>())
        ]);
        AppendMetricCard(
            html,
            "Feature Anchors",
            $"X drift {Display(horizontalDrift)}",
            $"identity frames {DisplayRate(Rate(records.Count(static record => record.IdentityMeasurementAvailable), records.Count))}; eye X {Display(Range(records.Select(static record => record.EyeMidlineXToFaceWidth).OfType<double>()))}; mouth X {Display(Range(records.Select(static record => record.MouthCenterXToFaceWidth).OfType<double>()))}");
        AppendMetricCard(html, "Eye Inset", DisplayRate(Rate(records.Count(static record => record.EyeInsetHasMeasurement), records.Count)), $"slope {Display(eyeInsetSlope)}; paired {DisplayRate(eyeInsetAgreement.PairedRate)}");
        if (eyeInsetRegion is not null)
        {
            AppendMetricCard(
                html,
                "Inset Agreement",
                $"{Display(eyeInsetAgreement.AgreementTrustPercent, "0.#")}%",
                $"corr {Display(eyeInsetAgreement.OpeningCorrelation, "0.##")}; error {Display(eyeInsetAgreement.NormalizedMeanAbsoluteError, "0.##")}; direction {DisplayRate(eyeInsetAgreement.DirectionAgreement ?? 0d)}");
        }
        html.AppendLine("</section>");
        var captureQualityIssues = TopPipeValues(records.Select(static record => record.CaptureQualityIssues), 5);
        if (captureQualityIssues.Count > 0)
        {
            html.AppendLine($"<p class=\"subtle\"><strong>Capture quality issues:</strong> {H(string.Join("; ", captureQualityIssues))}</p>");
        }

        html.AppendLine("<h2>Backend Statuses</h2>");
        html.AppendLine("<p>");
        foreach (var backend in records
            .Select(static record => string.IsNullOrWhiteSpace(record.Backend) ? "unknown" : record.Backend)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static backend => backend))
        {
            html.AppendLine($"<span class=\"pill\">{H(backend)}</span>");
        }
        html.AppendLine("</p>");

        html.AppendLine("<h2>Selected Review Frames</h2>");
        if (!writeOverlays)
        {
            html.AppendLine("<p class=\"warn\">Overlay frames were not requested for this run. Re-run with <code>--write-overlays</code> to include visual audit thumbnails.</p>");
        }

        var reviewFrames = SelectReviewFrames(records);
        if (reviewFrames.Count == 0)
        {
            html.AppendLine("<p>No frame records were produced.</p>");
        }
        else
        {
            html.AppendLine("<section class=\"frames\">");
            foreach (var reviewFrame in reviewFrames)
            {
                AppendReviewFrame(html, outputFolder, writeOverlays, reviewFrame);
            }

            html.AppendLine("</section>");
        }

        html.AppendLine("<h2>Frame Table</h2>");
        html.AppendLine("<table><thead><tr><th>Reason</th><th>Time</th><th>Frame</th><th>Face</th><th>XYZABC</th><th>Eye</th><th>Mouth</th><th>Jaw</th><th>MediaPipe Correction</th><th>Quality</th><th>Capture Quality</th><th>Reliability</th><th>Cue</th><th>Status</th></tr></thead><tbody>");
        foreach (var reviewFrame in reviewFrames)
        {
            var record = reviewFrame.Record;
            html.AppendLine("<tr>"
                + $"<td>{H(reviewFrame.Label)}</td>"
                + $"<td>{Display(record.TimestampSeconds)}s</td>"
                + $"<td>{record.FrameIndex}</td>"
                + $"<td>{H(record.HasFace ? "yes" : "no")}</td>"
                + $"<td>A {Display(record.HeadPitch, "0.#")} deg<br>B {Display(record.HeadYaw, "0.#")} deg<br>C {Display(record.HeadRoll, "0.#")} deg<br><span class=\"subtle\">Z scale {Display(FaceScale(record))}</span></td>"
                + $"<td>{Display(record.AverageEyeOpening)}<br><span class=\"subtle\">raw {Display(record.RawAverageEyeOpening)}</span></td>"
                + $"<td>{Display(record.MouthOpening)}<br><span class=\"subtle\">raw {Display(record.RawMouthOpening)}</span></td>"
                + $"<td>{Display(record.JawDroop)}</td>"
                + $"<td>eye {H(Display(record.MediaPipeEyeOpeningCorrection))}; mouth {H(Display(record.MediaPipeMouthOpeningCorrection))}</td>"
                + $"<td>{Display(record.OverallQuality, "0.#")}%</td>"
                + $"<td>{H(record.CaptureQualityLabel)} {Display(record.CaptureQualityScore, "0.#")}%<br>{H(record.CaptureQualityReason)}</td>"
                + $"<td>{Display(record.FaceReliability, "0.#")}%</td>"
                + $"<td>{Display(record.CueScore, "0.#")}%</td>"
                + $"<td>{H(Shorten(record.BackendStatus, 140))}</td>"
                + "</tr>");
        }
        html.AppendLine("</tbody></table>");

        html.AppendLine("</main></body></html>");
        File.WriteAllText(reportPath, html.ToString(), Encoding.UTF8);
    }

    private static List<ReviewFrame> SelectReviewFrames(IReadOnlyList<VisionFrameRecord> records)
    {
        var selected = new List<ReviewFrame>();
        if (records.Count == 0)
        {
            return selected;
        }

        AddReviewFrame(selected, "First sampled frame", records[0]);
        AddReviewFrame(selected, "Middle sampled frame", records[records.Count / 2]);
        AddReviewFrame(selected, "Last sampled frame", records[^1]);
        AddBestReviewFrame(selected, "Lowest eyelid opening", records, static record => record.AverageEyeOpening, lowest: true);
        AddBestReviewFrame(selected, "Largest mouth opening", records, static record => record.MouthOpening, lowest: false);
        AddBestReviewFrame(selected, "Largest jaw droop", records, static record => record.JawDroop, lowest: false);
        AddBestReviewFrame(selected, "Largest raw/working eye correction", records, static record => AbsDifference(record.RawAverageEyeOpening, record.AverageEyeOpening), lowest: false);
        AddBestReviewFrame(selected, "Largest raw/working mouth correction", records, static record => AbsDifference(record.RawMouthOpening, record.MouthOpening), lowest: false);
        AddBestReviewFrame(selected, "Strongest composite cue", records, static record => record.CueScore, lowest: false);
        AddBestReviewFrame(selected, "Strongest eye-inset closure", records, static record => record.EyeInsetCueClosure, lowest: false);
        AddBestReviewFrame(selected, "Lowest measurement quality", records, static record => record.OverallQuality, lowest: true);
        AddBestReviewFrame(selected, "Lowest A rotation around X", records, static record => record.HasFace ? record.HeadPitch : null, lowest: true);
        AddBestReviewFrame(selected, "Highest A rotation around X", records, static record => record.HasFace ? record.HeadPitch : null, lowest: false);
        AddBestReviewFrame(selected, "Lowest B rotation around Y", records, static record => record.HasFace ? record.HeadYaw : null, lowest: true);
        AddBestReviewFrame(selected, "Highest B rotation around Y", records, static record => record.HasFace ? record.HeadYaw : null, lowest: false);
        AddBestReviewFrame(selected, "Lowest C rotation around Z", records, static record => record.HasFace ? record.HeadRoll : null, lowest: true);
        AddBestReviewFrame(selected, "Highest C rotation around Z", records, static record => record.HasFace ? record.HeadRoll : null, lowest: false);
        AddBestReviewFrame(selected, "Farthest Z/apparent scale", records, FaceScale, lowest: true);
        AddBestReviewFrame(selected, "Closest Z/apparent scale", records, FaceScale, lowest: false);
        return selected;
    }

    private static void AddBestReviewFrame(
        List<ReviewFrame> selected,
        string label,
        IReadOnlyList<VisionFrameRecord> records,
        Func<VisionFrameRecord, double?> valueSelector,
        bool lowest)
    {
        var candidates = records
            .Select(record => (Record: record, Value: valueSelector(record)))
            .Where(static candidate => candidate.Value.HasValue)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var best = lowest
            ? candidates.OrderBy(static candidate => candidate.Value!.Value).First()
            : candidates.OrderByDescending(static candidate => candidate.Value!.Value).First();
        AddReviewFrame(selected, label, best.Record);
    }

    private static void AddReviewFrame(List<ReviewFrame> selected, string label, VisionFrameRecord record)
    {
        var existing = selected.FirstOrDefault(existing => existing.Record.FrameIndex == record.FrameIndex);
        if (existing is not null)
        {
            existing.Label += "; " + label;
            return;
        }

        selected.Add(new ReviewFrame(label, record));
    }

    private static void AppendMetricCard(StringBuilder html, string label, string value, string detail)
    {
        html.AppendLine("<div class=\"card\">");
        html.AppendLine($"<span class=\"subtle\">{H(label)}</span>");
        html.AppendLine($"<b>{H(value)}</b>");
        html.AppendLine($"<div>{H(detail)}</div>");
        html.AppendLine("</div>");
    }

    private static void AppendReviewFrame(StringBuilder html, string outputFolder, bool writeOverlays, ReviewFrame reviewFrame)
    {
        var record = reviewFrame.Record;
        html.AppendLine("<article class=\"frame\">");
        html.AppendLine($"<h3>{H(reviewFrame.Label)}</h3>");
        html.AppendLine($"<p class=\"subtle\">t={Display(record.TimestampSeconds)}s frame={record.FrameIndex} backend={H(record.Backend)}</p>");
        var overlayHref = GetOverlayHref(outputFolder, writeOverlays, record);
        if (overlayHref is not null)
        {
            html.AppendLine($"<a href=\"{H(overlayHref)}\"><img src=\"{H(overlayHref)}\" alt=\"Overlay frame {record.FrameIndex}\"></a>");
        }
        else
        {
            html.AppendLine("<p class=\"warn\">No overlay image available for this frame.</p>");
        }

        html.AppendLine("<p>"
            + $"eye {H(Display(record.AverageEyeOpening))}; "
            + $"raw eye {H(Display(record.RawAverageEyeOpening))}; "
            + $"mouth {H(Display(record.MouthOpening))}; "
            + $"raw mouth {H(Display(record.RawMouthOpening))}; "
            + $"jaw {H(Display(record.JawDroop))}; "
            + $"mp correction eye {H(Display(record.MediaPipeEyeOpeningCorrection))}; "
            + $"mouth {H(Display(record.MediaPipeMouthOpeningCorrection))}; "
            + $"quality {H(Display(record.OverallQuality, "0.#"))}%; "
            + $"capture {H(record.CaptureQualityLabel)} {H(Display(record.CaptureQualityScore, "0.#"))}%; "
            + $"reliability {H(Display(record.FaceReliability, "0.#"))}%; "
            + $"cue {H(Display(record.CueScore, "0.#"))}%"
            + "</p>");
        html.AppendLine("<p class=\"subtle\">"
            + $"XYZABC: A {H(Display(record.HeadPitch, "0.#"))} deg around X; "
            + $"B {H(Display(record.HeadYaw, "0.#"))} deg around Y; "
            + $"C {H(Display(record.HeadRoll, "0.#"))} deg around Z; "
            + $"Z apparent scale {H(Display(FaceScale(record)))}"
            + "</p>");
        if (!string.IsNullOrWhiteSpace(record.CaptureQualityReason))
        {
            html.AppendLine($"<p class=\"subtle\">Capture quality: {H(record.CaptureQualityReason)}</p>");
        }

        if (record.LeftEyeReconstructed || record.RightEyeReconstructed || record.MouthReconstructed || record.EyeArtifactSuppressed || record.PossibleOneEyeArtifact)
        {
            html.AppendLine("<p class=\"warn\">"
                + $"left eye reconstructed={record.LeftEyeReconstructed}; "
                + $"right eye reconstructed={record.RightEyeReconstructed}; "
                + $"mouth reconstructed={record.MouthReconstructed}; "
                + $"eye artifact suppressed={record.EyeArtifactSuppressed}; "
                + $"possible one-eye artifact={record.PossibleOneEyeArtifact}"
                + "</p>");
        }

        html.AppendLine("</article>");
    }

    private static string? GetOverlayHref(string outputFolder, bool writeOverlays, VisionFrameRecord record)
    {
        if (!writeOverlays)
        {
            return null;
        }

        var overlayPath = Path.Combine(outputFolder, "overlay_frames", $"frame_{record.FrameIndex:D6}.png");
        return File.Exists(overlayPath)
            ? RelativeHref(outputFolder, overlayPath)
            : null;
    }

    private static string RelativeHref(string outputFolder, string path)
    {
        return Path.GetRelativePath(outputFolder, path).Replace('\\', '/');
    }

    private static string Display(double value, string format = "0.###")
    {
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Display(double? value, string format = "0.###")
    {
        return value is double number ? Display(number, format) : "n/a";
    }

    private static double? AbsDifference(double? first, double? second)
    {
        return first is double left && second is double right
            ? Math.Abs(left - right)
            : null;
    }

    private static double? FaceScale(VisionFrameRecord record)
    {
        return record.HasFace ? record.FaceWidth * record.FaceHeight : null;
    }

    private static string DisplayRate(double value)
    {
        return (value * 100d).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    private static string H(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static void WriteOverlayFrame(
        string overlayFolder,
        Mat frame,
        int frameIndex,
        double timestampSeconds,
        FaceLandmarkTrackingResult result,
        FaceLandmarkFrame reconstructedFrame,
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis cue,
        FaceLandmarkTrendAnalysis trend,
        FaceLockStabilityAnalysis stability,
        EyeInsetApertureAnalysis? inset,
        EyeInsetCueAnalysis insetCue)
    {
        using var overlay = CreateBgrOverlayFrame(frame);
        if (result.FeatureDetection.HasFace)
        {
            DrawNormalizedRect(overlay, result.FeatureDetection.FaceBox, Scalar.LimeGreen, 2);
        }

        DrawNormalizedContour(overlay, reconstructedFrame.FaceContour, Scalar.LimeGreen, closed: true, thickness: 1);
        DrawNormalizedContour(
            overlay,
            reconstructedFrame.LeftEyeContour,
            reconstructedFrame.LeftEyeReconstructed || reconstructedFrame.EyeArtifactSuppressed ? Scalar.Gold : Scalar.DeepSkyBlue,
            closed: true,
            thickness: reconstructedFrame.EyeArtifactSuppressed ? 3 : 2);
        DrawNormalizedContour(
            overlay,
            reconstructedFrame.RightEyeContour,
            reconstructedFrame.RightEyeReconstructed || reconstructedFrame.EyeArtifactSuppressed ? Scalar.Gold : Scalar.DeepSkyBlue,
            closed: true,
            thickness: reconstructedFrame.EyeArtifactSuppressed ? 3 : 2);
        DrawNormalizedContour(overlay, reconstructedFrame.OuterLipContour, Scalar.Magenta, closed: true, thickness: 2);
        DrawNormalizedContour(overlay, reconstructedFrame.InnerLipContour, Scalar.HotPink, closed: true, thickness: 2);
        DrawNormalizedContour(overlay, reconstructedFrame.JawContour, Scalar.Yellow, closed: false, thickness: 2);

        if (inset is { HasRegion: true })
        {
            DrawNormalizedRect(
                overlay,
                new WpfRect(inset.RegionLeft, inset.RegionTop, inset.RegionWidth, inset.RegionHeight),
                inset.HasMeasurement ? Scalar.Orange : Scalar.Gray,
                2);
        }

        var lines = new[]
        {
            $"t={timestampSeconds:0.00}s frame={frameIndex}",
            Shorten(result.BackendName, 70),
            Shorten(result.BackendStatus, 90),
            $"eye lock={(metrics.IsEyeMeasurementUsable ? "usable" : "limited")} q={metrics.EyeMeasurementQualityPercent:0}% open={FormatRatio(metrics.AverageEyeOpeningRatio)} agree={metrics.EyeAgreementPercent:0}% mpBlink={FormatRatio(metrics.MediaPipeAverageEyeBlinkPercent)}",
            $"mouth lock={(metrics.IsMouthMeasurementUsable ? "usable" : "limited")} q={metrics.MouthMeasurementQualityPercent:0}% open={FormatRatio(metrics.MouthOpeningRatio)} jawDrop={FormatRatio(metrics.JawDroopRatio)} mpJaw={FormatRatio(metrics.MediaPipeJawOpenPercent)} mpClose={FormatRatio(metrics.MediaPipeMouthClosePercent)}",
            $"face reliability={stability.Label} {stability.CompositeReliabilityPercent:0}% continuity={stability.FaceContinuityPercent:0}% eye={stability.EyeReliabilityPercent:0}% mouth={stability.MouthReliabilityPercent:0}%",
            $"correction eye={FormatRatio(metrics.MediaPipeEyeOpeningCorrectionRatio)} mouth={FormatRatio(metrics.MediaPipeMouthOpeningCorrectionRatio)} overall q={metrics.OverallMeasurementQualityPercent:0}%",
            BuildOverlayFlagLine(metrics),
            $"cue={cue.CompositeCuePercent:0}% eyeClosure={FormatRatio(cue.EyeClosurePercent)} mouthCue={FormatRatio(cue.MouthOpeningChangePercent)} jawCue={FormatRatio(cue.JawDroopChangePercent)} mpMouthDelta={FormatRatio(cue.MediaPipeMouthOpeningEvidencePercent)}",
            $"trend eye={FormatRatio(trend.EyeOpeningSlopePerSecond)} mouth={FormatRatio(trend.MouthOpeningSlopePerSecond)}",
            $"inset cue={insetCue.CompositeCuePercent:0}% closure={FormatRatio(insetCue.EyeClosurePercent)} baseline={FormatRatio(insetCue.BaselineOpeningRatio)} q={insetCue.QualityPercent:0}%"
        }.Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        DrawTextBlock(overlay, lines);

        var outputPath = Path.Combine(overlayFolder, $"frame_{frameIndex:D6}.png");
        Cv2.ImWrite(outputPath, overlay);
    }

    private static Mat CreateBgrOverlayFrame(Mat frame)
    {
        var overlay = new Mat();
        if (frame.Channels() == 1)
        {
            Cv2.CvtColor(frame, overlay, ColorConversionCodes.GRAY2BGR);
        }
        else if (frame.Channels() == 3)
        {
            frame.CopyTo(overlay);
        }
        else if (frame.Channels() == 4)
        {
            Cv2.CvtColor(frame, overlay, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported frame channel count: {frame.Channels()}");
        }

        return overlay;
    }

    private static void DrawNormalizedRect(Mat image, WpfRect rect, Scalar color, int thickness)
    {
        if (rect.Width <= 0d || rect.Height <= 0d)
        {
            return;
        }

        var left = (int)Math.Round(Math.Clamp(rect.Left, 0d, 1d) * image.Width);
        var top = (int)Math.Round(Math.Clamp(rect.Top, 0d, 1d) * image.Height);
        var right = (int)Math.Round(Math.Clamp(rect.Right, 0d, 1d) * image.Width);
        var bottom = (int)Math.Round(Math.Clamp(rect.Bottom, 0d, 1d) * image.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        Cv2.Rectangle(image, new OpenCvSharp.Rect(left, top, right - left, bottom - top), color, thickness);
    }

    private static void DrawNormalizedContour(
        Mat image,
        IReadOnlyList<WpfPoint> contour,
        Scalar color,
        bool closed,
        int thickness)
    {
        if (contour.Count < 2)
        {
            return;
        }

        var points = contour
            .Select(point => new OpenCvSharp.Point(
                (int)Math.Round(Math.Clamp(point.X, 0d, 1d) * image.Width),
                (int)Math.Round(Math.Clamp(point.Y, 0d, 1d) * image.Height)))
            .ToArray();
        Cv2.Polylines(image, [points], closed, color, thickness, LineTypes.AntiAlias);
    }

    private static void DrawTextBlock(Mat image, IReadOnlyList<string> lines)
    {
        var baseline = 0;
        var scale = Math.Clamp(image.Width / 1280d, 0.45d, 0.85d);
        var lineHeight = Math.Max(18, (int)Math.Round(24 * scale));
        var padding = 8;
        var maximumChars = Math.Max(48, (int)Math.Floor(image.Width / Math.Max(7d, 11.5d * scale)) - 2);
        var displayLines = lines
            .Select(line => Shorten(line, maximumChars))
            .ToArray();
        var width = 0;
        foreach (var line in displayLines)
        {
            var size = Cv2.GetTextSize(line, HersheyFonts.HersheySimplex, scale, 1, out baseline);
            width = Math.Max(width, size.Width);
        }

        var height = lineHeight * displayLines.Length + padding * 2;
        Cv2.Rectangle(image, new OpenCvSharp.Rect(0, 0, Math.Min(image.Width, width + padding * 2), Math.Min(image.Height, height)), new Scalar(0, 0, 0), -1);
        for (var index = 0; index < displayLines.Length; index++)
        {
            Cv2.PutText(
                image,
                displayLines[index],
                new OpenCvSharp.Point(padding, padding + lineHeight * (index + 1) - 6),
                HersheyFonts.HersheySimplex,
                scale,
                Scalar.White,
                1,
                LineTypes.AntiAlias);
        }
    }

    private static string BuildOverlayFlagLine(FaceLandmarkMetrics metrics)
    {
        var flags = new List<string>();
        if (metrics.PossibleOneEyeArtifact)
        {
            flags.Add("possible one-eye artifact");
        }

        if (metrics.LeftEyeReconstructed)
        {
            flags.Add("left eye reconstructed");
        }

        if (metrics.RightEyeReconstructed)
        {
            flags.Add("right eye reconstructed");
        }

        if (metrics.MouthReconstructed)
        {
            flags.Add("mouth reconstructed");
        }

        if (metrics.EyeArtifactSuppressed)
        {
            flags.Add("eye artifact suppressed");
        }

        return flags.Count == 0 ? "" : "flags: " + string.Join(", ", flags);
    }

    private static string Shorten(string value, int maximumLength)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length <= maximumLength ? value : value[..Math.Max(0, maximumLength - 3)] + "...";
    }

    private static string FormatRatio(double? value)
    {
        return value is double number ? number.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";
    }

    private sealed record VisionFrameRecord(
        int FrameIndex,
        double TimestampSeconds,
        string Backend,
        string BackendStatus,
        bool HasFace,
        string Confidence,
        double TrackingConfidence,
        double EyeConfidence,
        double MouthConfidence,
        double EyeQuality,
        double MouthQuality,
        double OverallQuality,
        double HeadYaw,
        double HeadPitch,
        double HeadRoll,
        string FaceReliabilityStatus,
        int FaceReliabilitySamples,
        double FaceReliability,
        double FaceContinuity,
        double EyeReliability,
        double MouthReliability,
        double FaceBoundsRate,
        double EyeUsableRate,
        double MouthUsableRate,
        bool EyeImageQualityAvailable,
        bool MouthImageQualityAvailable,
        double EyeGlare,
        double MouthGlare,
        double EyeContrast,
        double MouthContrast,
        double EyeSharpness,
        double MouthSharpness,
        double EyeDarkCoverage,
        double MouthDarkCoverage,
        double? RawEyeAsymmetry,
        double? EyeAsymmetry,
        double EyeAgreement,
        bool PossibleOneEyeArtifact,
        bool LeftEyeReconstructed,
        bool RightEyeReconstructed,
        bool MouthReconstructed,
        bool EyeArtifactSuppressed,
        bool EyeUsable,
        bool MouthUsable,
        double? RawLeftEyeOpening,
        double? RawRightEyeOpening,
        double? RawAverageEyeOpening,
        double? RawMouthOpening,
        double? LeftEyeOpening,
        double? RightEyeOpening,
        double? AverageEyeOpening,
        double? MouthOpening,
        double? MouthOpeningVelocity,
        double? RawJawDroop,
        double? JawDroop,
        double? JawDroopVelocity,
        double? MediaPipeLeftEyeBlink,
        double? MediaPipeRightEyeBlink,
        double? MediaPipeAverageEyeBlink,
        double? MediaPipeJawOpen,
        double? MediaPipeMouthClose,
        double? MediaPipeEyeOpeningCorrection,
        double? MediaPipeMouthOpeningCorrection,
        bool MediaPipeEyeOpeningCorrected,
        bool MediaPipeMouthOpeningCorrected,
        string CueStatus,
        bool CueUsable,
        bool CueBaselineReady,
        int CueBaselineSamples,
        bool CueEyeEligible,
        bool CueMouthEligible,
        double? CueEyeClosure,
        double? CueMouthOpeningChange,
        double? CueJawDroopBaseline,
        double? CueJawDroopChange,
        double? CueScore,
        bool CueMediaPipeBlinkBaselineReady,
        bool CueMediaPipeMouthBaselineReady,
        double? CueMediaPipeBlinkBaseline,
        double? CueMediaPipeJawOpenBaseline,
        double? CueMediaPipeMouthCloseBaseline,
        double? CueMediaPipeBlinkChange,
        double? CueMediaPipeJawOpenChange,
        double? CueMediaPipeMouthCloseDrop,
        double? CueMediaPipeMouthOpeningEvidence,
        string TrendStatus,
        bool TrendUsable,
        double? EyeClosingTrend,
        double? MouthOpeningTrend,
        double? TrendEyeSlope,
        double? TrendMouthSlope,
        double? TrendCueScore,
        double FaceLeft,
        double FaceTop,
        double FaceWidth,
        double FaceHeight,
        double? ZApparentDistanceUnits,
        double? ZRelativeToReference,
        double? ZConfidencePercent,
        string ZEstimateKind,
        string ZQualityLabel,
        string ZDistanceSource,
        double? DistanceInches,
        bool DistanceCalibrated,
        bool ZUsesCameraFov,
        bool ZUsesLearnedReference,
        bool IdentityMeasurementAvailable,
        int IdentityUsableFeatureCount,
        double? FaceAspectRatio,
        double? EyeMidlineXToFaceWidth,
        double? MouthCenterXToFaceWidth,
        double? EyeToMouthXOffsetToFaceWidth,
        double? InterEyeDistanceToFaceWidth,
        double? LeftEyeWidthToFaceWidth,
        double? RightEyeWidthToFaceWidth,
        double? MouthWidthToFaceWidth,
        double? EyeMidlineYToFaceHeight,
        double? MouthCenterYToFaceHeight,
        double? EyeToMouthYDistanceToFaceHeight,
        string EyeInsetRegion,
        string EyeInsetStatus,
        bool EyeInsetHasMeasurement,
        double? EyeInsetLeftOpening,
        double? EyeInsetRightOpening,
        double? EyeInsetAverageOpening,
        double? EyeInsetLeftConfidence,
        double? EyeInsetRightConfidence,
        double? EyeInsetConfidence,
        bool EyeInsetImageQualityAvailable,
        double? EyeInsetGlare,
        double? EyeInsetContrast,
        double? EyeInsetSharpness,
        double? EyeInsetDarkCoverage,
        double? EyeInsetRegionLeft,
        double? EyeInsetRegionTop,
        double? EyeInsetRegionWidth,
        double? EyeInsetRegionHeight,
        string EyeInsetCueStatus,
        bool EyeInsetCueHasMeasurement,
        bool EyeInsetCueBaselineReady,
        int EyeInsetCueBaselineSamples,
        bool EyeInsetCueEligible,
        double EyeInsetCueQuality,
        double? EyeInsetCueOpening,
        double? EyeInsetCueBaselineOpening,
        double? EyeInsetCueClosure,
        double EyeInsetCueScore,
        bool PersonalModelAccepted,
        string PersonalModelRejectionKind,
        string PersonalModelUpdateReason,
        string PersonalIdentityStatus,
        double? PersonalIdentityConfidence,
        int PersonalIdentityComparedFeatures,
        int PersonalIdentityOutlierFeatures,
        string CaptureQualityLabel,
        double CaptureQualityScore,
        bool CaptureQualityCanCollect,
        bool CaptureQualityAvatarGrade,
        string CaptureQualityReason,
        double CaptureQualityCameraModeScore,
        double CaptureQualityFaceScaleScore,
        double CaptureQualityEyeScore,
        double CaptureQualityMouthScore,
        double CaptureQualityStabilityScore,
        double CaptureQualityGlassesScore,
        double CaptureQualityStorageScore,
        double? CaptureQualityFaceWidthPercent,
        double? CaptureQualityFaceHeightPercent,
        string CaptureQualityIssues,
        string CaptureQualitySuggestions);

    private sealed record VisionEvaluationResult(
        IReadOnlyList<VisionFrameRecord> Records,
        PersonalFaceModel PersonalModel,
        EyeInsetRegion? AppliedEyeInsetRegion);

    private sealed class ReviewFrame
    {
        public ReviewFrame(string label, VisionFrameRecord record)
        {
            Label = label;
            Record = record;
        }

        public string Label { get; set; }

        public VisionFrameRecord Record { get; }
    }
}
