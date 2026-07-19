using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewBuilder
{
    private const int StrongSampleCount = 240;
    private const double StrongSampleWeight = 180d;
    private const double StrongPoseBucketSampleCount = 45d;
    private const double StrongPoseBucketWeight = 36d;

    public MeasurementFacePreviewModel Build(PersonalFaceModel model, FaceReconstructionSubjectGate subjectGate)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(subjectGate);

        var preview = CreateBasePreview(model, subjectGate);
        if (!IsGateAccepted(model, subjectGate, out var gateWarning))
        {
            preview.RenderDecision = "paused by subject gate";
            preview.Warnings.Add(gateWarning);
            return preview;
        }

        preview.CanRender = true;
        ConfigureProvenance(model, preview);
        preview.RenderDecision = model.AcceptedSamples <= 0
            ? "renderable canonical seed scaffold; waiting for accepted personal measurements"
            : "renderable measurement-only preview";
        AddConfidenceWarnings(model, preview);
        AddMetrics(model, preview);
        AddContourShapeProfiles(model, preview);
        AddPoseBucketSummaries(model, preview);
        AddSurfaceEvidence(model, preview);
        AddGeometry(model, preview);
        return preview;
    }

    private static MeasurementFacePreviewModel CreateBasePreview(
        PersonalFaceModel model,
        FaceReconstructionSubjectGate subjectGate)
    {
        return new MeasurementFacePreviewModel
        {
            SubjectId = model.SubjectId,
            SubjectDisplayName = model.SubjectDisplayName,
            SubjectCollectionMode = model.SubjectCollectionMode,
            UnknownSubjectPolicy = model.UnknownSubjectPolicy,
            SubjectGate = subjectGate,
            ObservedSamples = model.ObservedSamples,
            AcceptedSamples = model.AcceptedSamples,
            RejectedSamples = model.RejectedSamples,
            AcceptedSampleWeight = Round(model.AcceptedSampleWeight),
            AcceptedRate = Round(model.AcceptedRate),
            ConfidencePercent = CalculateConfidence(model)
        };
    }

    private static bool IsGateAccepted(
        PersonalFaceModel model,
        FaceReconstructionSubjectGate subjectGate,
        out string warning)
    {
        if (!string.Equals(subjectGate.GateDecision, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            warning = string.IsNullOrWhiteSpace(subjectGate.Reason)
                ? "Subject gate is not accepted."
                : subjectGate.Reason;
            return false;
        }

        if (!string.Equals(model.SubjectId, subjectGate.SubjectId, StringComparison.OrdinalIgnoreCase))
        {
            warning = $"Subject mismatch: model is '{model.SubjectId}', gate is '{subjectGate.SubjectId}'.";
            return false;
        }

        warning = "";
        return true;
    }

    private static void AddConfidenceWarnings(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        if (model.AcceptedSamples <= 0)
        {
            preview.Warnings.Add("Preview is seeded from a canonical face scaffold only; no personal measurements have been accepted yet.");
            preview.Warnings.Add("Seed geometry is for early visualization and rig topology only. It is not evidence about the subject's face.");
        }
        else if (model.AcceptedSamples < 60)
        {
            preview.Warnings.Add("Preview is early: fewer than 60 accepted measurements.");
        }
        else if (model.AcceptedSamples < StrongSampleCount)
        {
            preview.Warnings.Add("Preview is warming: more samples will make the face shape steadier.");
        }

        if (model.RejectedSamples > model.AcceptedSamples)
        {
            preview.Warnings.Add("Rejected samples exceed accepted samples; inspect lighting, glasses glare, or subject gate timing.");
        }

        if (model.MediaPipeAverageEyeBlinkPercent.SampleCount == 0
            && model.MediaPipeJawOpenPercent.SampleCount == 0)
        {
            preview.Warnings.Add(model.AcceptedSamples <= 0
                ? "MediaPipe blendshape baselines are not populated yet; preview uses neutral canonical eyelid and jaw values."
                : "MediaPipe blendshape baselines are not populated yet; preview is based on contour measurements.");
        }

        if (model.AcceptedSamples > 0)
        {
            preview.Warnings.Add("Neutral face preview separates feature shape from pose: turned-head samples shape the eye/lip/jaw outlines but should not slide neutral features sideways.");
            var neutralPose = GetNeutralPoseProfile(model);
            if (neutralPose is null || neutralPose.SampleCount < 12)
            {
                preview.Warnings.Add("Straight-on pose coverage is early; collect more front-facing alert measurements before trusting neutral face proportions.");
            }
        }
    }

    private static void AddMetrics(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        var neutralPose = GetNeutralPoseProfile(model);
        preview.Metrics["MeasurementContributionPercent"] = preview.MeasurementContributionPercent;
        preview.Metrics["TemplatePriorContributionPercent"] = preview.TemplatePriorContributionPercent;
        preview.Metrics["PoseBucketCount"] = model.PoseBuckets.Count;
        preview.Metrics["CoveredPoseBucketCount"] = model.PoseBuckets.Count(static bucket => bucket.SampleCount > 0);
        preview.Metrics["NeutralPoseSamples"] = neutralPose?.SampleCount ?? 0;
        preview.Metrics["NeutralPoseWeight"] = Round(neutralPose?.TotalWeight ?? 0d);
        var averageEyeOpening = Metric(PreferNeutral(neutralPose, static pose => pose.AverageEyeOpeningRatio, model.AverageEyeOpeningRatio), 0.26d, 0.02d, 0.60d);
        preview.Metrics["FaceCenterX"] = Metric(model.FaceCenterX, 0.50d, 0d, 1d);
        preview.Metrics["FaceCenterY"] = Metric(model.FaceCenterY, 0.50d, 0d, 1d);
        preview.Metrics["FaceWidth"] = Metric(model.FaceWidth, 0.42d, 0.16d, 0.95d);
        preview.Metrics["FaceHeight"] = Metric(model.FaceHeight, 0.62d, 0.24d, 1.20d);
        preview.Metrics["FaceAspectRatio"] = Metric(PreferNeutral(neutralPose, static pose => pose.FaceAspectRatio, model.FaceAspectRatio), 1.55d, 1.15d, 2.20d);
        preview.Metrics["ARotationAroundXDegrees"] = Metric(model.HeadPitchDegrees, 0d, -35d, 35d);
        preview.Metrics["BRotationAroundYDegrees"] = Metric(model.HeadYawDegrees, 0d, -45d, 45d);
        preview.Metrics["CRotationAroundZDegrees"] = Metric(model.HeadRollDegrees, 0d, -45d, 45d);
        preview.Metrics["LeftEyeOpeningRatio"] = Metric(model.LeftEyeOpeningRatio, averageEyeOpening, 0.02d, 0.60d);
        preview.Metrics["RightEyeOpeningRatio"] = Metric(model.RightEyeOpeningRatio, averageEyeOpening, 0.02d, 0.60d);
        preview.Metrics["AverageEyeOpeningRatio"] = averageEyeOpening;
        var mouthOpeningMetric = Metric(PreferNeutral(neutralPose, static pose => pose.MouthOpeningRatio, model.MouthOpeningRatio), 0.07d, 0.01d, 0.70d);
        preview.Metrics["MouthOpeningRatio"] = mouthOpeningMetric;
        preview.Metrics["JawDroopRatio"] = Metric(PreferNeutral(neutralPose, static pose => pose.JawDroopRatio, model.JawDroopRatio), 0d, -0.05d, 0.28d);
        preview.Metrics["JawPreviewOffsetRatio"] = CalculateJawPreviewOffset(model, mouthOpeningMetric);
        preview.Metrics["MediaPipeAverageEyeBlinkPercent"] = NullableMetric(model.MediaPipeAverageEyeBlinkPercent);
        preview.Metrics["MediaPipeJawOpenPercent"] = NullableMetric(model.MediaPipeJawOpenPercent);
        preview.Metrics["MediaPipeMouthClosePercent"] = NullableMetric(model.MediaPipeMouthClosePercent);
        preview.Metrics["EyeGlarePercent"] = NullableMetric(model.EyeGlarePercent);
        preview.Metrics["EyeContrastPercent"] = NullableMetric(model.EyeContrastPercent);
        preview.Metrics["EyeSharpnessPercent"] = NullableMetric(model.EyeSharpnessPercent);
        preview.Metrics["AverageFaceReliabilityPercent"] = Round(model.AverageFaceReliabilityPercent);
        preview.Metrics["AverageFaceContinuityPercent"] = Round(model.AverageFaceContinuityPercent);
        preview.Metrics["AverageEyeReliabilityPercent"] = Round(model.AverageEyeReliabilityPercent);
        preview.Metrics["AverageMouthReliabilityPercent"] = Round(model.AverageMouthReliabilityPercent);
    }

    private static void AddGeometry(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        const double canonicalFaceHeight = 0.72d;
        const double neutralFaceAspectRatio = 1.55d;
        var neutralPose = GetNeutralPoseProfile(model);
        var measuredFaceAspectRatio = Metric(PreferNeutral(neutralPose, static pose => pose.FaceAspectRatio, model.FaceAspectRatio), neutralFaceAspectRatio, 1.15d, 2.20d);
        var shapeMaturity = CalculateShapeMaturity(model);
        var previewFaceAspectRatio = Blend(neutralFaceAspectRatio, measuredFaceAspectRatio, shapeMaturity);
        var faceHeight = canonicalFaceHeight;
        var faceWidth = Math.Clamp(faceHeight / previewFaceAspectRatio, 0.36d, 0.58d);
        preview.Metrics["PreviewFaceWidth"] = Round(faceWidth);
        preview.Metrics["PreviewFaceHeight"] = Round(faceHeight);
        preview.Metrics["PreviewShapeMaturityPercent"] = Round(shapeMaturity * 100d);
        var averageEyeOpening = Metric(PreferNeutral(neutralPose, static pose => pose.AverageEyeOpeningRatio, model.AverageEyeOpeningRatio), 0.26d, 0.02d, 0.60d);
        var leftEyeOpening = Metric(model.LeftEyeOpeningRatio, averageEyeOpening, 0.02d, 0.60d);
        var rightEyeOpening = Metric(model.RightEyeOpeningRatio, averageEyeOpening, 0.02d, 0.60d);
        var mouthOpening = Metric(PreferNeutral(neutralPose, static pose => pose.MouthOpeningRatio, model.MouthOpeningRatio), 0.07d, 0.01d, 0.70d);
        var jawDroop = CalculateJawPreviewOffset(model, mouthOpening);
        var yaw = Metric(model.HeadYawDegrees, 0d, -45d, 45d);
        var pitch = Metric(model.HeadPitchDegrees, 0d, -35d, 35d);
        var eyeCenterX = (Metric(PreferNeutral(neutralPose, static pose => pose.EyeMidlineXToFaceWidth, model.EyeMidlineXToFaceWidth), 0.5d, 0.36d, 0.64d) - 0.5d) * faceWidth;
        var mouthCenterX = (Metric(PreferNeutral(neutralPose, static pose => pose.MouthCenterXToFaceWidth, model.MouthCenterXToFaceWidth), 0.5d, 0.34d, 0.66d) - 0.5d) * faceWidth;
        var interEyeDistance = Metric(PreferNeutral(neutralPose, static pose => pose.InterEyeDistanceToFaceWidth, model.InterEyeDistanceToFaceWidth), 0.40d, 0.22d, 0.58d) * faceWidth;
        var leftEyeWidth = Metric(model.LeftEyeWidthToFaceWidth, 0.18d, 0.08d, 0.34d) * faceWidth;
        var rightEyeWidth = Metric(model.RightEyeWidthToFaceWidth, 0.18d, 0.08d, 0.34d) * faceWidth;
        var mouthWidth = Metric(PreferNeutral(neutralPose, static pose => pose.MouthWidthToFaceWidth, model.MouthWidthToFaceWidth), 0.36d, 0.16d, 0.62d) * faceWidth;
        var eyeMidlineY = (Metric(PreferNeutral(neutralPose, static pose => pose.EyeMidlineYToFaceHeight, model.EyeMidlineYToFaceHeight), 0.32d, 0.18d, 0.46d) - 0.5d) * faceHeight
            + pitch / 45d * faceHeight * 0.025d;
        var learnedMouthY = (Metric(PreferNeutral(neutralPose, static pose => pose.MouthCenterYToFaceHeight, model.MouthCenterYToFaceHeight), 0.66d, 0.52d, 0.84d) - 0.5d) * faceHeight;

        var halfFaceWidth = faceWidth / 2d;
        var halfFaceHeight = faceHeight / 2d;
        var jawOffset = jawDroop * faceHeight * 0.35d;
        var leftEyeX = eyeCenterX - interEyeDistance / 2d;
        var rightEyeX = eyeCenterX + interEyeDistance / 2d;
        var leftEyeHalfWidth = Math.Max(faceWidth * 0.045d, leftEyeWidth / 2d);
        var rightEyeHalfWidth = Math.Max(faceWidth * 0.045d, rightEyeWidth / 2d);
        var mouthY = learnedMouthY + jawOffset * 0.30d;
        var mouthHalfWidth = Math.Max(faceWidth * 0.08d, mouthWidth / 2d);
        var mouthInnerHalfHeight = Math.Max(faceHeight * 0.006d, mouthHalfWidth * mouthOpening * 0.55d);
        var mouthOuterHalfHeight = faceHeight * 0.023d + mouthInnerHalfHeight * 0.55d;
        var jawCenterY = faceHeight * 0.32d + jawOffset * 0.45d;
        var jawHalfHeight = faceHeight * 0.18d + Math.Abs(jawOffset) * 0.50d;
        var jawDepthScale = RenderDepthScale(model.JawShape, 0.14d);
        var leftEyeDepthScale = RenderDepthScale(model.LeftEyeShape, 0.62d);
        var rightEyeDepthScale = RenderDepthScale(model.RightEyeShape, 0.62d);
        var leftBrowDepthScale = RenderDepthScale(model.LeftBrowShape, 0.18d);
        var rightBrowDepthScale = RenderDepthScale(model.RightBrowShape, 0.18d);
        var foreheadDepthScale = RenderDepthScale(model.ForeheadSurface, 0.13d);
        var leftCheekDepthScale = RenderDepthScale(model.LeftCheekSurface, 0.18d);
        var rightCheekDepthScale = RenderDepthScale(model.RightCheekSurface, 0.18d);
        var outerLipDepthScale = RenderDepthScale(model.OuterLipShape, 0.42d);
        var innerLipDepthScale = RenderDepthScale(model.InnerLipShape, 0.50d);
        var noseBridgeDepthScale = RenderDepthScale(model.NoseBridgeShape, 0.28d);
        var noseBaseDepthScale = RenderDepthScale(model.NoseBaseShape, 0.24d);
        AddDepthRenderMetrics(
            preview,
            jawDepthScale,
            leftEyeDepthScale,
            rightEyeDepthScale,
            leftBrowDepthScale,
            rightBrowDepthScale,
            foreheadDepthScale,
            leftCheekDepthScale,
            rightCheekDepthScale,
            outerLipDepthScale,
            innerLipDepthScale,
            noseBridgeDepthScale,
            noseBaseDepthScale);

        AddFaceOval(preview, halfFaceWidth, halfFaceHeight, jawOffset);
        if (!TryAddAnchoredContourProfile(preview, model.JawShape, "jaw", "Jaw contour", "jaw", 0d, jawCenterY, faceWidth * 0.34d, jawHalfHeight, faceWidth, faceHeight, z: 0.02d, depthScale: jawDepthScale))
        {
            AddJawLine(preview, faceWidth, faceHeight, jawOffset);
        }

        if (!TryAddAnchoredContourProfile(preview, model.LeftEyeShape, "left_eye", "Left eye learned contour", "eye", leftEyeX, eyeMidlineY, leftEyeHalfWidth, EyeHalfHeight(leftEyeHalfWidth, leftEyeOpening, faceHeight), faceWidth, faceHeight, z: 0.05d, depthScale: leftEyeDepthScale))
        {
            AddEllipse(preview, "left_eye", "Left eye opening", "eye", leftEyeX, eyeMidlineY, leftEyeHalfWidth, EyeHalfHeight(leftEyeHalfWidth, leftEyeOpening, faceHeight), 0.05d, 12);
        }

        if (!TryAddAnchoredContourProfile(preview, model.RightEyeShape, "right_eye", "Right eye learned contour", "eye", rightEyeX, eyeMidlineY, rightEyeHalfWidth, EyeHalfHeight(rightEyeHalfWidth, rightEyeOpening, faceHeight), faceWidth, faceHeight, z: 0.05d, depthScale: rightEyeDepthScale))
        {
            AddEllipse(preview, "right_eye", "Right eye opening", "eye", rightEyeX, eyeMidlineY, rightEyeHalfWidth, EyeHalfHeight(rightEyeHalfWidth, rightEyeOpening, faceHeight), 0.05d, 12);
        }

        TryAddAnchoredContourProfile(preview, model.LeftBrowShape, "left_brow", "Left brow learned surface", "brow", leftEyeX, eyeMidlineY - faceHeight * 0.075d, leftEyeHalfWidth * 1.25d, faceHeight * 0.026d, faceWidth, faceHeight, z: 0.035d, depthScale: leftBrowDepthScale);
        TryAddAnchoredContourProfile(preview, model.RightBrowShape, "right_brow", "Right brow learned surface", "brow", rightEyeX, eyeMidlineY - faceHeight * 0.075d, rightEyeHalfWidth * 1.25d, faceHeight * 0.026d, faceWidth, faceHeight, z: 0.035d, depthScale: rightBrowDepthScale);
        TryAddAnchoredContourProfile(preview, model.ForeheadSurface, "forehead", "Forehead learned surface", "forehead", 0d, -faceHeight * 0.355d, faceWidth * 0.35d, faceHeight * 0.075d, faceWidth, faceHeight, z: -0.045d, depthScale: foreheadDepthScale);
        TryAddAnchoredContourProfile(preview, model.LeftCheekSurface, "left_cheek", "Left cheek learned surface", "cheek", -faceWidth * 0.245d, faceHeight * 0.035d, faceWidth * 0.105d, faceHeight * 0.175d, faceWidth, faceHeight, z: -0.015d, depthScale: leftCheekDepthScale);
        TryAddAnchoredContourProfile(preview, model.RightCheekSurface, "right_cheek", "Right cheek learned surface", "cheek", faceWidth * 0.245d, faceHeight * 0.035d, faceWidth * 0.105d, faceHeight * 0.175d, faceWidth, faceHeight, z: -0.015d, depthScale: rightCheekDepthScale);

        if (!TryAddAnchoredContourProfile(preview, model.OuterLipShape, "mouth_outer", "Outer lip learned contour", "mouth", mouthCenterX, mouthY, mouthHalfWidth, mouthOuterHalfHeight, faceWidth, faceHeight, z: 0.04d, depthScale: outerLipDepthScale))
        {
            AddEllipse(preview, "mouth_outer", "Outer lip reference", "mouth", mouthCenterX, mouthY, mouthHalfWidth, mouthOuterHalfHeight, 0.04d, 14);
        }

        if (!TryAddAnchoredContourProfile(preview, model.InnerLipShape, "mouth_inner", "Mouth opening learned contour", "mouth-opening", mouthCenterX, mouthY, mouthHalfWidth * 0.70d, mouthInnerHalfHeight, faceWidth, faceHeight, z: 0.07d, depthScale: innerLipDepthScale))
        {
            AddEllipse(preview, "mouth_inner", "Mouth opening", "mouth-opening", mouthCenterX, mouthY, mouthHalfWidth * 0.70d, mouthInnerHalfHeight, 0.07d, 14);
        }

        if (!TryAddAnchoredContourProfile(preview, model.NoseBridgeShape, "nose_bridge", "Nose bridge learned surface", "nose", 0d, faceHeight * 0.005d, faceWidth * 0.055d, faceHeight * 0.145d, faceWidth, faceHeight, z: 0.14d, depthScale: noseBridgeDepthScale)
            || !TryAddAnchoredContourProfile(preview, model.NoseBaseShape, "nose_base", "Nose base learned surface", "nose", 0d, faceHeight * 0.125d, faceWidth * 0.115d, faceHeight * 0.030d, faceWidth, faceHeight, z: 0.12d, depthScale: noseBaseDepthScale))
        {
            AddNose(preview, faceWidth, faceHeight, yaw, pitch);
        }

        AddJawDroopMarker(preview, faceHeight, jawOffset);
        AddSurfacePatches(
            preview,
            model,
            jawDepthScale,
            leftEyeDepthScale,
            rightEyeDepthScale,
            leftBrowDepthScale,
            rightBrowDepthScale,
            foreheadDepthScale,
            leftCheekDepthScale,
            rightCheekDepthScale,
            outerLipDepthScale,
            innerLipDepthScale,
            noseBridgeDepthScale,
            noseBaseDepthScale);
    }

    private static void AddContourShapeProfiles(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        AddContourShapeProfile(preview, model.LeftEyeShape);
        AddContourShapeProfile(preview, model.RightEyeShape);
        AddContourShapeProfile(preview, model.OuterLipShape);
        AddContourShapeProfile(preview, model.InnerLipShape);
        AddContourShapeProfile(preview, model.JawShape);
        AddContourShapeProfile(preview, model.LeftBrowShape);
        AddContourShapeProfile(preview, model.RightBrowShape);
        AddContourShapeProfile(preview, model.NoseBridgeShape);
        AddContourShapeProfile(preview, model.NoseBaseShape);
        AddContourShapeProfile(preview, model.LeftCheekSurface);
        AddContourShapeProfile(preview, model.RightCheekSurface);
        AddContourShapeProfile(preview, model.ForeheadSurface);
    }

    private static void AddPoseBucketSummaries(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        preview.PoseBuckets = model.PoseBuckets
            .Where(static bucket => !string.IsNullOrWhiteSpace(bucket.BucketId))
            .Select(ToPreviewPoseBucket)
            .ToList();
        preview.PoseBucketConsistency = PersonalFacePoseBucketConsistencyAnalyzer.Analyze(model.PoseBuckets);
        if (preview.PoseBucketConsistency.Findings.Count > 0)
        {
            preview.Warnings.AddRange(preview.PoseBucketConsistency.Findings);
        }
    }

    private static MeasurementFacePreviewPoseBucket ToPreviewPoseBucket(PersonalFacePoseBucketProfile bucket)
    {
        var sampleScore = Math.Clamp(bucket.SampleCount / StrongPoseBucketSampleCount * 100d, 0d, 100d);
        var weightScore = Math.Clamp(bucket.TotalWeight / StrongPoseBucketWeight * 100d, 0d, 100d);
        var reliabilityValues = new[]
        {
            bucket.AverageFaceReliabilityPercent,
            bucket.AverageEyeReliabilityPercent,
            bucket.AverageMouthReliabilityPercent
        }.Where(static value => value > 0d).ToList();
        var reliabilityScore = reliabilityValues.Count == 0
            ? sampleScore
            : reliabilityValues.Average();
        var coverage = Round(sampleScore * 0.45d + weightScore * 0.35d + Math.Clamp(reliabilityScore, 0d, 100d) * 0.20d);
        var status = bucket.SampleCount <= 0
            ? "waiting"
            : coverage >= 80d
                ? "strong"
                : coverage >= 45d
                    ? "warming"
                    : "early";
        return new MeasurementFacePreviewPoseBucket
        {
            BucketId = bucket.BucketId,
            Label = bucket.Label,
            Description = bucket.Description,
            CaptureInstruction = bucket.CaptureInstruction,
            PrimaryNeutralReference = bucket.PrimaryNeutralReference,
            RequiredForAvatarCoverage = bucket.RequiredForAvatarCoverage,
            SampleCount = bucket.SampleCount,
            TotalWeight = Round(bucket.TotalWeight),
            CoveragePercent = coverage,
            HeadYawDegrees = Round(bucket.HeadYawDegrees.ExponentialMovingAverage ?? bucket.HeadYawDegrees.Average ?? 0d),
            HeadPitchDegrees = Round(bucket.HeadPitchDegrees.ExponentialMovingAverage ?? bucket.HeadPitchDegrees.Average ?? 0d),
            HeadRollDegrees = Round(bucket.HeadRollDegrees.ExponentialMovingAverage ?? bucket.HeadRollDegrees.Average ?? 0d),
            AverageFaceReliabilityPercent = Round(bucket.AverageFaceReliabilityPercent),
            AverageEyeReliabilityPercent = Round(bucket.AverageEyeReliabilityPercent),
            AverageMouthReliabilityPercent = Round(bucket.AverageMouthReliabilityPercent),
            Status = status
        };
    }

    private static void AddSurfaceEvidence(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        var frontPose = PoseCoverage(preview, PersonalFacePoseBuckets.FrontNeutral);
        var yawNegative = PoseCoverage(preview, PersonalFacePoseBuckets.YawNegative);
        var yawPositive = PoseCoverage(preview, PersonalFacePoseBuckets.YawPositive);
        var pitchNegative = PoseCoverage(preview, PersonalFacePoseBuckets.PitchNegative);
        var pitchPositive = PoseCoverage(preview, PersonalFacePoseBuckets.PitchPositive);
        var rollNegative = PoseCoverage(preview, PersonalFacePoseBuckets.RollNegative);
        var rollPositive = PoseCoverage(preview, PersonalFacePoseBuckets.RollPositive);
        var balancedYaw = BalancedPoseCoverage(yawNegative, yawPositive);
        var verticalPose = BalancedPoseCoverage(pitchNegative, pitchPositive);
        var rollPose = BalancedPoseCoverage(rollNegative, rollPositive);
        var frontGeometry = AverageEvidence(
            frontPose,
            DistributionEvidence(model.FaceWidth),
            DistributionEvidence(model.FaceHeight),
            DistributionEvidence(model.FaceAspectRatio),
            DistributionEvidence(model.InterEyeDistanceToFaceWidth));
        var eyeShape = AverageEvidence(
            ContourEvidence(model.LeftEyeShape),
            ContourEvidence(model.RightEyeShape),
            DistributionEvidence(model.AverageEyeOpeningRatio),
            DistributionEvidence(model.EyeAgreementPercent),
            Math.Clamp(model.AverageEyeReliabilityPercent, 0d, 100d));
        var browShape = AverageEvidence(
            ContourEvidence(model.LeftBrowShape),
            ContourEvidence(model.RightBrowShape),
            ContourEvidence(model.ForeheadSurface),
            DistributionEvidence(model.AverageBrowHeightRatio),
            DistributionEvidence(model.BrowAsymmetryPercent),
            eyeShape * 0.70d);
        var mouthShape = AverageEvidence(
            ContourEvidence(model.OuterLipShape),
            ContourEvidence(model.InnerLipShape),
            DistributionEvidence(model.MouthOpeningRatio),
            Math.Clamp(model.AverageMouthReliabilityPercent, 0d, 100d));
        var jawShape = AverageEvidence(
            ContourEvidence(model.JawShape),
            DistributionEvidence(model.JawDroopRatio),
            Math.Clamp(model.AverageMouthReliabilityPercent, 0d, 100d));
        var spacingShape = AverageEvidence(
            DistributionEvidence(model.EyeMidlineYToFaceHeight),
            DistributionEvidence(model.MouthCenterYToFaceHeight),
            DistributionEvidence(model.EyeToMouthYDistanceToFaceHeight),
            DistributionEvidence(model.MouthWidthToFaceWidth));
        var depthPose = AverageEvidence(balancedYaw, verticalPose, rollPose);
        var noseShape = AverageEvidence(
            ContourEvidence(model.NoseBridgeShape),
            ContourEvidence(model.NoseBaseShape));
        var cheekShape = AverageEvidence(
            ContourEvidence(model.LeftCheekSurface),
            ContourEvidence(model.RightCheekSurface));
        AddSurfaceEvidence(
            preview,
            "face_outline",
            "Face outline",
            "face",
            frontGeometry,
            AverageEvidence(depthPose, balancedYaw),
            "Front face width/height, neutral pose coverage, and turned-head consistency.",
            "Collect front-neutral and slow turned-head passes until face outline stays stable across pose.",
            Array.Empty<PersonalFaceContourShapeProfile>(),
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "eyes_behind_glasses",
            "Eyes behind glasses",
            "eye",
            eyeShape,
            AverageEvidence(frontPose, balancedYaw * 0.55d),
            "Direct eye contour aggregates, eyelid opening distribution, glare/eye agreement, and pose coverage.",
            "Collect alert slow blinks with glasses on, low glare, and both eyes visible.",
            new[] { model.LeftEyeShape, model.RightEyeShape },
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "brows_forehead",
            "Brows and forehead",
            "brow",
            browShape,
            AverageEvidence(verticalPose, balancedYaw * 0.65d, frontPose * 0.35d),
            "Brow-height aggregates plus A/B rotation evidence. Forehead depth remains inferred until pose coverage matures.",
            "Collect slow three-quarter turns plus slight A-axis up/down tilt while brows and forehead stay visible.",
            new[] { model.LeftBrowShape, model.RightBrowShape, model.ForeheadSurface },
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.PitchNegative,
            PersonalFacePoseBuckets.PitchPositive,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "nose_projection",
            "Nose projection",
            "nose",
            AverageEvidence(noseShape, frontGeometry * 0.35d, spacingShape * 0.35d),
            AverageEvidence(noseShape, balancedYaw),
            "Weighted nose bridge/base 3D surface profiles plus balanced three-quarter and side B-turn evidence.",
            "Collect slow left and right three-quarter/near-side head turns; keep the nose bridge and tip visible.",
            new[] { model.NoseBridgeShape, model.NoseBaseShape },
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "cheeks",
            "Cheeks",
            "face",
            AverageEvidence(cheekShape, frontGeometry, spacingShape),
            AverageEvidence(cheekShape, balancedYaw, verticalPose * 0.55d),
            "Weighted cheek 3D surface profiles, face outline, eye-mouth spacing, and side-depth pose coverage.",
            "Collect balanced three-quarter turns at normal, close, and leaned-back distances.",
            new[] { model.LeftCheekSurface, model.RightCheekSurface },
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "mouth_lips",
            "Mouth and lips",
            "mouth",
            mouthShape,
            AverageEvidence(frontPose, balancedYaw * 0.45d, verticalPose * 0.35d),
            "Outer/inner lip contour aggregates, mouth opening, and pose coverage.",
            "Collect lips closed, slightly open, and natural speech while turning slowly left and right.",
            new[] { model.OuterLipShape, model.InnerLipShape },
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
        AddSurfaceEvidence(
            preview,
            "jaw_chin",
            "Jaw and chin",
            "jaw",
            jawShape,
            AverageEvidence(balancedYaw, verticalPose),
            "Jaw contour aggregate, jaw droop distribution, and pose-depth coverage.",
            "Collect relaxed jaw, gentle jaw drop, and slow A/B changes with the lower face visible.",
            new[] { model.JawShape },
            PersonalFacePoseBuckets.FrontNeutral,
            PersonalFacePoseBuckets.PitchNegative,
            PersonalFacePoseBuckets.PitchPositive,
            PersonalFacePoseBuckets.YawNegative,
            PersonalFacePoseBuckets.YawPositive);
    }

    private static void AddSurfaceEvidence(
        MeasurementFacePreviewModel preview,
        string regionId,
        string label,
        string role,
        double frontEvidencePercent,
        double depthEvidencePercent,
        string evidenceBasis,
        string nextCaptureHint,
        IReadOnlyList<PersonalFaceContourShapeProfile> depthProfiles,
        params string[] supportingPoseBuckets)
    {
        frontEvidencePercent = Math.Clamp(frontEvidencePercent, 0d, 100d);
        depthEvidencePercent = Math.Clamp(depthEvidencePercent, 0d, 100d);
        var depthProfile = CalculateSurfaceDepthEvidence(depthProfiles);
        if (depthProfile.DepthProfileCoveragePercent > 0d)
        {
            depthEvidencePercent = Round(
                depthEvidencePercent * 0.62d
                + depthProfile.DepthProfileCoveragePercent * 0.26d
                + depthProfile.DepthStabilityPercent * 0.12d);
        }

        var depthLimited = role is "nose" or "face" or "jaw" or "brow";
        var overall = depthLimited
            ? frontEvidencePercent * 0.42d + depthEvidencePercent * 0.58d
            : frontEvidencePercent * 0.68d + depthEvidencePercent * 0.32d;
        var depthProfileIsWeak = depthProfile.DepthProfileCoveragePercent > 0d
            && depthProfile.DepthStabilityPercent < 38d;
        var status = depthProfileIsWeak && overall >= 45d
            ? "measured, depth unstable"
            : overall >= 80d && depthEvidencePercent >= 62d
            ? "strong measured"
            : overall >= 58d && depthEvidencePercent >= 38d
                ? "measured, depth warming"
                : overall >= 30d
                    ? "partial evidence"
                    : "mostly scaffold";
        preview.SurfaceEvidence.Add(new MeasurementFacePreviewSurfaceEvidence
        {
            RegionId = regionId,
            Label = label,
            Role = role,
            Status = status,
            FrontEvidencePercent = Round(frontEvidencePercent),
            DepthEvidencePercent = Round(depthEvidencePercent),
            DepthProfileCoveragePercent = Round(depthProfile.DepthProfileCoveragePercent),
            DepthStabilityPercent = Round(depthProfile.DepthStabilityPercent),
            DepthRange = depthProfile.DepthRange is double range ? Round(range) : null,
            AverageDepthStandardDeviation = depthProfile.AverageDepthStandardDeviation is double standardDeviation ? Round(standardDeviation) : null,
            OverallConfidencePercent = Round(Math.Clamp(overall, 0d, 100d)),
            EvidenceBasis = evidenceBasis,
            NextCaptureHint = nextCaptureHint,
            SupportingPoseBuckets = supportingPoseBuckets.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        });
    }

    private static double CalculateJawPreviewOffset(PersonalFaceModel model, double mouthOpening)
    {
        var mediaPipeJawOpen = Metric(model.MediaPipeJawOpenPercent, 0d, 0d, 100d);
        var mediaPipeOffset = mediaPipeJawOpen / 100d * 0.28d;
        var mouthOffset = Math.Clamp((mouthOpening - 0.04d) * 0.35d, 0d, 0.28d);
        return Round(Math.Clamp(Math.Max(mediaPipeOffset, mouthOffset), 0d, 0.28d));
    }

    private static void AddContourShapeProfile(MeasurementFacePreviewModel preview, PersonalFaceContourShapeProfile profile)
    {
        if (profile.HasProfile)
        {
            preview.ContourShapeProfiles[profile.FeatureId] = profile;
        }
    }

    private static void AddDepthRenderMetrics(
        MeasurementFacePreviewModel preview,
        double jawDepthScale,
        double leftEyeDepthScale,
        double rightEyeDepthScale,
        double leftBrowDepthScale,
        double rightBrowDepthScale,
        double foreheadDepthScale,
        double leftCheekDepthScale,
        double rightCheekDepthScale,
        double outerLipDepthScale,
        double innerLipDepthScale,
        double noseBridgeDepthScale,
        double noseBaseDepthScale)
    {
        preview.Metrics["JawRenderDepthScale"] = jawDepthScale;
        preview.Metrics["LeftEyeRenderDepthScale"] = leftEyeDepthScale;
        preview.Metrics["RightEyeRenderDepthScale"] = rightEyeDepthScale;
        preview.Metrics["LeftBrowRenderDepthScale"] = leftBrowDepthScale;
        preview.Metrics["RightBrowRenderDepthScale"] = rightBrowDepthScale;
        preview.Metrics["ForeheadRenderDepthScale"] = foreheadDepthScale;
        preview.Metrics["LeftCheekRenderDepthScale"] = leftCheekDepthScale;
        preview.Metrics["RightCheekRenderDepthScale"] = rightCheekDepthScale;
        preview.Metrics["OuterLipRenderDepthScale"] = outerLipDepthScale;
        preview.Metrics["InnerLipRenderDepthScale"] = innerLipDepthScale;
        preview.Metrics["NoseBridgeRenderDepthScale"] = noseBridgeDepthScale;
        preview.Metrics["NoseBaseRenderDepthScale"] = noseBaseDepthScale;
    }

    private static void AddSurfacePatches(
        MeasurementFacePreviewModel preview,
        PersonalFaceModel model,
        double jawDepthScale,
        double leftEyeDepthScale,
        double rightEyeDepthScale,
        double leftBrowDepthScale,
        double rightBrowDepthScale,
        double foreheadDepthScale,
        double leftCheekDepthScale,
        double rightCheekDepthScale,
        double outerLipDepthScale,
        double innerLipDepthScale,
        double noseBridgeDepthScale,
        double noseBaseDepthScale)
    {
        TryAddProfilePatch(preview, model.LeftEyeShape, "left_eye_depth_patch", "Left eye measured depth patch", "eye", "left_eye", leftEyeDepthScale, 0.18d);
        TryAddProfilePatch(preview, model.RightEyeShape, "right_eye_depth_patch", "Right eye measured depth patch", "eye", "right_eye", rightEyeDepthScale, 0.18d);
        TryAddProfilePatch(preview, model.OuterLipShape, "outer_lip_depth_patch", "Outer lip measured depth patch", "mouth", "mouth_outer", outerLipDepthScale, 0.20d);
        TryAddProfilePatch(preview, model.InnerLipShape, "inner_lip_depth_patch", "Mouth opening measured depth patch", "mouth-opening", "mouth_inner", innerLipDepthScale, 0.22d);
        TryAddProfilePatch(preview, model.JawShape, "jaw_depth_patch", "Jaw and chin measured depth patch", "jaw", "jaw", jawDepthScale, 0.13d);
        TryAddProfilePatch(preview, model.LeftBrowShape, "left_brow_depth_patch", "Left brow measured surface patch", "brow", "left_brow", leftBrowDepthScale, 0.14d);
        TryAddProfilePatch(preview, model.RightBrowShape, "right_brow_depth_patch", "Right brow measured surface patch", "brow", "right_brow", rightBrowDepthScale, 0.14d);
        TryAddProfilePatch(preview, model.ForeheadSurface, "forehead_depth_patch", "Forehead measured surface patch", "forehead", "forehead", foreheadDepthScale, 0.12d);
        TryAddProfilePatch(preview, model.LeftCheekSurface, "left_cheek_depth_patch", "Left cheek measured surface patch", "cheek", "left_cheek", leftCheekDepthScale, 0.14d);
        TryAddProfilePatch(preview, model.RightCheekSurface, "right_cheek_depth_patch", "Right cheek measured surface patch", "cheek", "right_cheek", rightCheekDepthScale, 0.14d);
        TryAddProfilePatch(preview, model.NoseBridgeShape, "nose_bridge_depth_patch", "Nose bridge measured surface patch", "nose", "nose_bridge", noseBridgeDepthScale, 0.18d);
        TryAddProfilePatch(preview, model.NoseBaseShape, "nose_base_depth_patch", "Nose base measured surface patch", "nose", "nose_base", noseBaseDepthScale, 0.16d);

        preview.Metrics["RenderSurfacePatchCount"] = preview.SurfacePatches.Count;
        preview.Metrics["RenderSurfaceTriangleCount"] = preview.SurfacePatches.Sum(static patch => patch.TriangleCount);
        preview.Metrics["RenderSurfacePatchTotalArea"] = Round(preview.SurfacePatches.Sum(static patch => patch.SurfaceArea));
        preview.Metrics["RenderSurfacePatchAverageDepthRelief"] = Round(preview.SurfacePatches.Count == 0 ? 0d : preview.SurfacePatches.Average(static patch => patch.DepthRelief));
        preview.Metrics["RenderSurfacePatchAverageNormalConsistencyPercent"] = Round(preview.SurfacePatches.Count == 0 ? 0d : preview.SurfacePatches.Average(static patch => patch.NormalConsistencyPercent));
    }

    private static bool TryAddProfilePatch(
        MeasurementFacePreviewModel preview,
        PersonalFaceContourShapeProfile profile,
        string id,
        string label,
        string role,
        string pointPrefix,
        double depthScale,
        double fillOpacity)
    {
        if (!profile.HasProfile || depthScale <= 0d)
        {
            return false;
        }

        var pointIds = Enumerable.Range(0, profile.PointCount)
            .Select(index => $"{pointPrefix}_shape_{index:00}")
            .Where(pointId => preview.Points.Any(point => string.Equals(point.Id, pointId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (pointIds.Count < 3)
        {
            return false;
        }

        var pointMap = preview.Points.ToDictionary(static point => point.Id, StringComparer.OrdinalIgnoreCase);
        var patchPoints = pointIds.Select(pointId => pointMap[pointId]).ToList();
        var centerX = patchPoints.Average(static point => point.X);
        var centerY = patchPoints.Average(static point => point.Y);
        var centerZ = patchPoints.Average(static point => point.Z);
        var confidence = patchPoints.Average(static point => point.ConfidencePercent);
        var orderedPointIds = OrderSurfacePatchBoundary(pointMap, pointIds, centerX, centerY);
        if (orderedPointIds.Count < 3)
        {
            return false;
        }

        var centerId = AddPoint(
            preview,
            $"{pointPrefix}_surface_center",
            $"{label} center",
            role,
            centerX,
            centerY,
            centerZ,
            provenance: "personal aggregate surface patch center",
            confidencePercent: confidence);
        var triangles = BuildSurfacePatchTriangles(centerId, orderedPointIds);
        if (triangles.Count == 0)
        {
            return false;
        }
        var patchMetrics = CalculateSurfacePatchMetrics(preview.Points, orderedPointIds, centerId, triangles);
        var patchHealth = CalculateSurfacePatchHealth(patchMetrics, triangles.Count);

        preview.SurfacePatches.Add(new MeasurementFacePreviewSurfacePatch
        {
            Id = id,
            Label = label,
            Role = role,
            Provenance = "confidence-gated aggregate personal Z surface patch",
            ConfidencePercent = Round(confidence),
            FillOpacity = Round(Math.Clamp(fillOpacity, 0.02d, 0.30d)),
            CenterPointId = centerId,
            TriangleCount = triangles.Count,
            SurfaceArea = patchMetrics.SurfaceArea,
            AverageTriangleArea = patchMetrics.AverageTriangleArea,
            DepthRelief = patchMetrics.DepthRelief,
            AverageNormalX = patchMetrics.AverageNormalX,
            AverageNormalY = patchMetrics.AverageNormalY,
            AverageNormalZ = patchMetrics.AverageNormalZ,
            NormalConsistencyPercent = patchMetrics.NormalConsistencyPercent,
            GeometryHealthPercent = patchHealth.HealthPercent,
            GeometryStatus = patchHealth.Status,
            GeometryFinding = patchHealth.Finding,
            PointIds = orderedPointIds,
            Triangles = triangles
        });
        return true;
    }

    private static SurfacePatchHealth CalculateSurfacePatchHealth(
        SurfacePatchMetrics metrics,
        int triangleCount)
    {
        var triangleHealth = Math.Clamp((triangleCount - 2d) / 6d * 100d, 0d, 100d);
        var areaHealth = Math.Clamp(metrics.SurfaceArea / 0.001d * 100d, 0d, 100d);
        var health = Round(Math.Min(metrics.NormalConsistencyPercent, Math.Min(triangleHealth, areaHealth)));
        if (metrics.NormalConsistencyPercent < 35d)
        {
            return new SurfacePatchHealth(
                health,
                "review thin/warped patch",
                "Triangle normals disagree; inspect for a folded surface or a very thin feature opening.");
        }

        if (metrics.NormalConsistencyPercent < 55d)
        {
            return new SurfacePatchHealth(
                health,
                "review uneven surface",
                "Patch is usable for visualization, but orientation is uneven enough to review.");
        }

        if (metrics.SurfaceArea < 0.001d)
        {
            return new SurfacePatchHealth(
                health,
                "small patch",
                "Patch is coherent but covers a small normalized area.");
        }

        if (triangleCount < 6)
        {
            return new SurfacePatchHealth(
                health,
                "low triangle coverage",
                "Patch is coherent but has few surface cells.");
        }

        return health >= 80d
            ? new SurfacePatchHealth(health, "coherent surface", "Patch triangles agree and are suitable for the measurement preview.")
            : new SurfacePatchHealth(health, "usable surface", "Patch is usable for the preview, with some measured unevenness.");
    }

    private static SurfacePatchMetrics CalculateSurfacePatchMetrics(
        IReadOnlyList<MeasurementFacePreviewPoint> points,
        IReadOnlyList<string> boundaryPointIds,
        string centerPointId,
        IReadOnlyList<MeasurementFacePreviewSurfaceTriangle> triangles)
    {
        var pointMap = points.ToDictionary(static point => point.Id, StringComparer.OrdinalIgnoreCase);
        var depthValues = boundaryPointIds
            .Where(pointMap.ContainsKey)
            .Select(pointId => pointMap[pointId].Z)
            .ToList();
        if (pointMap.TryGetValue(centerPointId, out var centerPoint))
        {
            depthValues.Add(centerPoint.Z);
        }

        var totalArea = 0d;
        var weightedNormalX = 0d;
        var weightedNormalY = 0d;
        var weightedNormalZ = 0d;
        foreach (var triangle in triangles)
        {
            if (triangle.PointIds.Count != 3
                || !pointMap.TryGetValue(triangle.PointIds[0], out var p0)
                || !pointMap.TryGetValue(triangle.PointIds[1], out var p1)
                || !pointMap.TryGetValue(triangle.PointIds[2], out var p2))
            {
                continue;
            }

            var ux = p1.X - p0.X;
            var uy = p1.Y - p0.Y;
            var uz = p1.Z - p0.Z;
            var vx = p2.X - p0.X;
            var vy = p2.Y - p0.Y;
            var vz = p2.Z - p0.Z;
            var nx = uy * vz - uz * vy;
            var ny = uz * vx - ux * vz;
            var nz = ux * vy - uy * vx;
            var normalMagnitude = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (normalMagnitude <= 0d)
            {
                continue;
            }

            var triangleArea = normalMagnitude / 2d;
            totalArea += triangleArea;
            weightedNormalX += nx / normalMagnitude * triangleArea;
            weightedNormalY += ny / normalMagnitude * triangleArea;
            weightedNormalZ += nz / normalMagnitude * triangleArea;
        }

        var weightedMagnitude = Math.Sqrt(weightedNormalX * weightedNormalX + weightedNormalY * weightedNormalY + weightedNormalZ * weightedNormalZ);
        var normalX = weightedMagnitude <= 0d ? 0d : weightedNormalX / weightedMagnitude;
        var normalY = weightedMagnitude <= 0d ? 0d : weightedNormalY / weightedMagnitude;
        var normalZ = weightedMagnitude <= 0d ? 0d : weightedNormalZ / weightedMagnitude;
        if (normalZ < 0d)
        {
            normalX = -normalX;
            normalY = -normalY;
            normalZ = -normalZ;
        }

        var depthRelief = depthValues.Count == 0 ? 0d : depthValues.Max() - depthValues.Min();
        var averageTriangleArea = triangles.Count == 0 ? 0d : totalArea / triangles.Count;
        var normalConsistency = totalArea <= 0d ? 0d : Math.Clamp(weightedMagnitude / totalArea * 100d, 0d, 100d);
        return new SurfacePatchMetrics(
            Round(totalArea),
            Round(averageTriangleArea),
            Round(depthRelief),
            Round(normalX),
            Round(normalY),
            Round(normalZ),
            Round(normalConsistency));
    }

    private static List<string> OrderSurfacePatchBoundary(
        IReadOnlyDictionary<string, MeasurementFacePreviewPoint> pointMap,
        IReadOnlyList<string> pointIds,
        double centerX,
        double centerY)
    {
        return pointIds
            .Where(pointMap.ContainsKey)
            .OrderBy(pointId => Math.Atan2(pointMap[pointId].Y - centerY, pointMap[pointId].X - centerX))
            .ThenByDescending(pointId =>
            {
                var point = pointMap[pointId];
                var dx = point.X - centerX;
                var dy = point.Y - centerY;
                return dx * dx + dy * dy;
            })
            .ThenBy(static pointId => pointId, StringComparer.Ordinal)
            .ToList();
    }

    private static List<MeasurementFacePreviewSurfaceTriangle> BuildSurfacePatchTriangles(
        string centerPointId,
        IReadOnlyList<string> orderedBoundaryPointIds)
    {
        var triangles = new List<MeasurementFacePreviewSurfaceTriangle>(orderedBoundaryPointIds.Count);
        for (var index = 0; index < orderedBoundaryPointIds.Count; index++)
        {
            var next = (index + 1) % orderedBoundaryPointIds.Count;
            var first = orderedBoundaryPointIds[index];
            var second = orderedBoundaryPointIds[next];
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            triangles.Add(new MeasurementFacePreviewSurfaceTriangle
            {
                PointIds = [centerPointId, first, second]
            });
        }

        return triangles;
    }

    private static double RenderDepthScale(PersonalFaceContourShapeProfile profile, double preferredScale)
    {
        if (!profile.HasProfile)
        {
            return 0d;
        }

        var depthEvidence = Math.Clamp(profile.DepthEvidencePercent, 0d, 100d);
        var depthStability = Math.Clamp(profile.DepthStabilityPercent, 0d, 100d);
        var depthCoverage = Math.Clamp(profile.DepthPointCoveragePercent, 0d, 100d);
        if (depthEvidence < 35d || depthStability < 30d || depthCoverage < 50d)
        {
            return 0d;
        }

        var evidenceGate = Math.Clamp((depthEvidence - 35d) / 65d, 0d, 1d);
        var stabilityGate = Math.Clamp((depthStability - 30d) / 70d, 0d, 1d);
        var coverageGate = Math.Clamp((depthCoverage - 50d) / 50d, 0d, 1d);
        var confidenceGate = Math.Min(evidenceGate, Math.Min(stabilityGate, coverageGate));
        return Round(preferredScale * (0.35d + confidenceGate * 0.65d));
    }

    private static bool TryAddAnchoredContourProfile(
        MeasurementFacePreviewModel preview,
        PersonalFaceContourShapeProfile profile,
        string prefix,
        string label,
        string role,
        double targetCenterX,
        double targetCenterY,
        double targetHalfWidth,
        double targetHalfHeight,
        double faceWidth,
        double faceHeight,
        double z,
        double depthScale = 0d)
    {
        if (!profile.HasProfile)
        {
            return false;
        }

        var usablePoints = profile.Points
            .OrderBy(static point => point.Index)
            .Select(point => new
            {
                point.Index,
                X = point.X.ExponentialMovingAverage ?? point.X.Average,
                Y = point.Y.ExponentialMovingAverage ?? point.Y.Average,
                Z = point.Z.ExponentialMovingAverage ?? point.Z.Average
            })
            .Where(static point => point.X.HasValue && point.Y.HasValue)
            .ToList();
        if (usablePoints.Count < 2)
        {
            return false;
        }

        var minX = usablePoints.Min(static point => point.X!.Value);
        var maxX = usablePoints.Max(static point => point.X!.Value);
        var minY = usablePoints.Min(static point => point.Y!.Value);
        var maxY = usablePoints.Max(static point => point.Y!.Value);
        var zValues = usablePoints
            .Select(static point => point.Z)
            .OfType<double>()
            .ToList();
        var centerZ = zValues.Count == 0 ? 0d : zValues.Average();
        var sourceHalfWidth = Math.Max(0.001d, (maxX - minX) / 2d);
        var sourceHalfHeight = Math.Max(0.001d, (maxY - minY) / 2d);
        var sourceCenterX = (minX + maxX) / 2d;
        var sourceCenterY = (minY + maxY) / 2d;
        var boundedTargetHalfWidth = Math.Clamp(targetHalfWidth, faceWidth * 0.015d, faceWidth * 0.48d);
        var boundedTargetHalfHeight = Math.Clamp(targetHalfHeight, faceHeight * 0.004d, faceHeight * 0.36d);
        var provenance = "pose-normalized personal aggregate contour profile";
        var ids = new List<string>(usablePoints.Count + 1);
        foreach (var point in usablePoints)
        {
            var localX = (point.X!.Value - sourceCenterX) / sourceHalfWidth;
            var localY = (point.Y!.Value - sourceCenterY) / sourceHalfHeight;
            var x = targetCenterX + localX * boundedTargetHalfWidth;
            var y = targetCenterY + localY * boundedTargetHalfHeight;
            var localZ = point.Z is double pointZ
                ? Math.Clamp((pointZ - centerZ) * depthScale, -0.18d, 0.18d)
                : 0d;
            x = Math.Clamp(x, -faceWidth * 0.49d, faceWidth * 0.49d);
            y = Math.Clamp(y, -faceHeight * 0.52d, faceHeight * 0.62d);
            ids.Add(AddPoint(
                preview,
                $"{prefix}_shape_{point.Index:00}",
                label,
                role,
                x,
                y,
                z + localZ,
                provenance: provenance));
        }

        if (profile.Closed)
        {
            ids.Add(ids[0]);
        }

        AddPolyline(
            preview,
            $"{prefix}_shape_line",
            label,
            role,
            ids,
            provenance: provenance);
        return true;
    }

    private static void AddFaceOval(
        MeasurementFacePreviewModel preview,
        double halfWidth,
        double halfHeight,
        double jawOffset)
    {
        var ids = new List<string>();
        const int count = 24;
        for (var index = 0; index < count; index++)
        {
            var angle = -Math.PI / 2d + Math.PI * 2d * index / count;
            var x = Math.Cos(angle) * halfWidth;
            var y = Math.Sin(angle) * halfHeight;
            if (y > 0d)
            {
                y += jawOffset * Math.Pow(Math.Clamp(y / halfHeight, 0d, 1d), 1.8d);
            }

            var z = -Math.Abs(x) / Math.Max(0.01d, halfWidth) * 0.10d + (y > 0d ? 0.03d : -0.02d);
            ids.Add(AddPoint(preview, $"face_{index:00}", "Face oval", "face", x, y, z));
        }

        ids.Add(ids[0]);
        AddPolyline(preview, "face_oval", "Face oval", "face", ids);
    }

    private static void AddJawLine(
        MeasurementFacePreviewModel preview,
        double faceWidth,
        double faceHeight,
        double jawOffset)
    {
        var points = new[]
        {
            (-faceWidth * 0.34d, faceHeight * 0.14d, -0.06d),
            (-faceWidth * 0.29d, faceHeight * 0.30d, -0.03d),
            (-faceWidth * 0.18d, faceHeight * 0.43d, 0.01d),
            (0d, faceHeight * 0.50d + jawOffset, 0.05d),
            (faceWidth * 0.18d, faceHeight * 0.43d, 0.01d),
            (faceWidth * 0.29d, faceHeight * 0.30d, -0.03d),
            (faceWidth * 0.34d, faceHeight * 0.14d, -0.06d)
        };
        var ids = new List<string>();
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            ids.Add(AddPoint(preview, $"jaw_{index:00}", "Jaw contour", "jaw", point.Item1, point.Item2, point.Item3));
        }

        AddPolyline(preview, "jaw_line", "Jaw contour", "jaw", ids);
    }

    private static void AddNose(
        MeasurementFacePreviewModel preview,
        double faceWidth,
        double faceHeight,
        double yaw,
        double pitch)
    {
        var yawOffset = yaw / 45d * faceWidth * 0.045d;
        var pitchOffset = pitch / 35d * faceHeight * 0.035d;
        var ids = new[]
        {
            AddPoint(preview, "nose_bridge", "Nose bridge", "nose", yawOffset * 0.35d, -faceHeight * 0.065d + pitchOffset, 0.11d),
            AddPoint(preview, "nose_tip", "Nose tip", "nose", yawOffset, faceHeight * 0.060d + pitchOffset, 0.20d),
            AddPoint(preview, "nose_base", "Nose base", "nose", yawOffset * 0.45d, faceHeight * 0.130d + pitchOffset, 0.10d)
        };
        AddPolyline(preview, "nose_line", "Nose pose hint", "nose", ids);
    }

    private static void AddJawDroopMarker(
        MeasurementFacePreviewModel preview,
        double faceHeight,
        double jawOffset)
    {
        var ids = new[]
        {
            AddPoint(preview, "jaw_droop_reference", "Neutral jaw reference", "jaw-droop", 0d, faceHeight * 0.50d, -0.02d),
            AddPoint(preview, "jaw_droop_current", "Current jaw droop estimate", "jaw-droop", 0d, faceHeight * 0.50d + jawOffset, 0.08d)
        };
        AddPolyline(preview, "jaw_droop_marker", "Jaw droop marker", "jaw-droop", ids);
    }

    private static void AddEllipse(
        MeasurementFacePreviewModel preview,
        string prefix,
        string label,
        string role,
        double centerX,
        double centerY,
        double halfWidth,
        double halfHeight,
        double z,
        int count)
    {
        var ids = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var angle = Math.PI * 2d * index / count;
            ids.Add(AddPoint(
                preview,
                $"{prefix}_{index:00}",
                label,
                role,
                centerX + Math.Cos(angle) * halfWidth,
                centerY + Math.Sin(angle) * halfHeight,
                z + Math.Sin(angle) * 0.015d));
        }

        ids.Add(ids[0]);
        AddPolyline(preview, $"{prefix}_line", label, role, ids);
    }

    private static string AddPoint(
        MeasurementFacePreviewModel preview,
        string id,
        string label,
        string role,
        double x,
        double y,
        double z,
        string provenance = "",
        double? confidencePercent = null)
    {
        preview.Points.Add(new MeasurementFacePreviewPoint
        {
            Id = id,
            Label = label,
            Role = role,
            Provenance = string.IsNullOrWhiteSpace(provenance) ? preview.GeometryProvenance : provenance,
            ConfidencePercent = Round(confidencePercent ?? preview.ConfidencePercent),
            X = Round(x),
            Y = Round(y),
            Z = Round(z)
        });
        return id;
    }

    private static void AddPolyline(
        MeasurementFacePreviewModel preview,
        string id,
        string label,
        string role,
        IEnumerable<string> pointIds,
        string provenance = "",
        double? confidencePercent = null)
    {
        preview.Polylines.Add(new MeasurementFacePreviewPolyline
        {
            Id = id,
            Label = label,
            Role = role,
            Provenance = string.IsNullOrWhiteSpace(provenance) ? preview.GeometryProvenance : provenance,
            ConfidencePercent = Round(confidencePercent ?? preview.ConfidencePercent),
            PointIds = pointIds.ToList()
        });
    }

    private static void ConfigureProvenance(PersonalFaceModel model, MeasurementFacePreviewModel preview)
    {
        var measurementContribution = CalculateMeasurementContribution(model);
        preview.MeasurementContributionPercent = measurementContribution;
        preview.TemplatePriorContributionPercent = Round(100d - measurementContribution);
        preview.TemplatePriorUsed = preview.TemplatePriorContributionPercent > 0.01d;
        preview.GeometryProvenance = model.AcceptedSamples <= 0
            ? "canonical template prior only"
            : preview.TemplatePriorUsed
                ? "personal measurements with canonical template fallbacks for missing or low-confidence fields"
                : "personal measurements";
    }

    private static double EyeHalfHeight(double eyeHalfWidth, double openingRatio, double faceHeight)
    {
        return Math.Clamp(eyeHalfWidth * openingRatio * 0.85d, faceHeight * 0.006d, faceHeight * 0.055d);
    }

    private static double CalculateConfidence(PersonalFaceModel model)
    {
        var sampleConfidence = Math.Clamp(model.AcceptedSamples / (double)StrongSampleCount * 100d, 0d, 100d);
        var weightConfidence = Math.Clamp(model.AcceptedSampleWeight / StrongSampleWeight * 100d, 0d, 100d);
        var reliabilityValues = new[]
        {
            model.AverageFaceReliabilityPercent,
            model.AverageFaceContinuityPercent,
            model.AverageEyeReliabilityPercent,
            model.AverageMouthReliabilityPercent
        }.Where(static value => value > 0d).ToList();
        var reliabilityConfidence = reliabilityValues.Count == 0 ? sampleConfidence : reliabilityValues.Average();
        return Round(sampleConfidence * 0.50d + weightConfidence * 0.20d + reliabilityConfidence * 0.30d);
    }

    private static double CalculateMeasurementContribution(PersonalFaceModel model)
    {
        if (model.AcceptedSamples <= 0 || model.AcceptedSampleWeight <= 0d)
        {
            return 0d;
        }

        var sampleContribution = Math.Clamp(model.AcceptedSamples / 60d * 55d, 0d, 55d);
        var weightContribution = Math.Clamp(model.AcceptedSampleWeight / 90d * 25d, 0d, 25d);
        var contourContribution = new[]
        {
            model.LeftEyeShape.HasProfile,
            model.RightEyeShape.HasProfile,
            model.OuterLipShape.HasProfile,
            model.InnerLipShape.HasProfile,
            model.JawShape.HasProfile
        }.Count(static hasProfile => hasProfile) / 5d * 20d;
        var surfaceContribution = new[]
        {
            model.LeftBrowShape.HasProfile,
            model.RightBrowShape.HasProfile,
            model.NoseBridgeShape.HasProfile,
            model.NoseBaseShape.HasProfile,
            model.LeftCheekSurface.HasProfile,
            model.RightCheekSurface.HasProfile,
            model.ForeheadSurface.HasProfile
        }.Count(static hasProfile => hasProfile) / 7d * 10d;
        return Round(Math.Clamp(sampleContribution + weightContribution + contourContribution + surfaceContribution, 0d, 100d));
    }

    private static double PoseCoverage(MeasurementFacePreviewModel preview, string bucketId)
    {
        return preview.PoseBuckets.FirstOrDefault(bucket =>
                string.Equals(bucket.BucketId, bucketId, StringComparison.OrdinalIgnoreCase))
            ?.CoveragePercent ?? 0d;
    }

    private static double BalancedPoseCoverage(double first, double second)
    {
        if (first <= 0d && second <= 0d)
        {
            return 0d;
        }

        var balanced = Math.Min(first, second);
        var average = (first + second) / 2d;
        return Round(balanced * 0.68d + average * 0.32d);
    }

    private static double DistributionEvidence(PersonalMetricDistribution distribution)
    {
        if (distribution.SampleCount <= 0 || distribution.TotalWeight <= 0d)
        {
            return 0d;
        }

        var sampleScore = Math.Clamp(distribution.SampleCount / StrongPoseBucketSampleCount * 45d, 0d, 45d);
        var weightScore = Math.Clamp(distribution.TotalWeight / StrongPoseBucketWeight * 35d, 0d, 35d);
        var valueScore = distribution.Average.HasValue || distribution.ExponentialMovingAverage.HasValue ? 20d : 0d;
        return Round(sampleScore + weightScore + valueScore);
    }

    private static double ContourEvidence(PersonalFaceContourShapeProfile profile)
    {
        if (!profile.HasProfile)
        {
            return 0d;
        }

        var sampleScore = Math.Clamp(profile.SampleCount / StrongPoseBucketSampleCount * 45d, 0d, 45d);
        var weightScore = Math.Clamp(profile.TotalWeight / StrongPoseBucketWeight * 35d, 0d, 35d);
        var pointScore = Math.Clamp(profile.Points.Count / (double)Math.Max(2, profile.PointCount) * 20d, 0d, 20d);
        return Round(sampleScore + weightScore + pointScore);
    }

    private static SurfaceDepthEvidence CalculateSurfaceDepthEvidence(IReadOnlyList<PersonalFaceContourShapeProfile> profiles)
    {
        var usableProfiles = profiles
            .Where(static profile => profile.HasProfile)
            .ToList();
        if (usableProfiles.Count == 0)
        {
            return SurfaceDepthEvidence.None;
        }

        var coverageValues = usableProfiles
            .Select(ProfileDepthEvidence)
            .Where(static value => value > 0d)
            .ToList();
        var stabilityValues = usableProfiles
            .Select(ProfileDepthStability)
            .Where(static value => value > 0d)
            .ToList();
        var depthRanges = usableProfiles
            .Select(ProfileDepthRange)
            .OfType<double>()
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();
        var standardDeviations = usableProfiles
            .Select(ProfileAverageDepthStandardDeviation)
            .OfType<double>()
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();

        return new SurfaceDepthEvidence(
            coverageValues.Count == 0 ? 0d : Round(coverageValues.Average()),
            stabilityValues.Count == 0 ? 0d : Round(stabilityValues.Average()),
            depthRanges.Count == 0 ? null : Round(depthRanges.Max()),
            standardDeviations.Count == 0 ? null : Round(standardDeviations.Average()));
    }

    private static double ProfileDepthEvidence(PersonalFaceContourShapeProfile profile)
    {
        if (profile.DepthEvidencePercent > 0d)
        {
            return Math.Clamp(profile.DepthEvidencePercent, 0d, 100d);
        }

        var expectedPointCount = Math.Max(2, profile.PointCount);
        var depthValues = profile.Points
            .Select(static point => Value(point.Z))
            .OfType<double>()
            .ToList();
        if (depthValues.Count == 0)
        {
            return 0d;
        }

        var pointScore = Math.Clamp(depthValues.Count / (double)expectedPointCount * 100d, 0d, 100d);
        var sampleScore = Math.Clamp(profile.SampleCount / StrongPoseBucketSampleCount * 100d, 0d, 100d);
        var weightScore = Math.Clamp(profile.TotalWeight / StrongPoseBucketWeight * 100d, 0d, 100d);
        var depthRangeScore = Math.Clamp((depthValues.Max() - depthValues.Min()) / 0.055d * 100d, 0d, 100d);
        return Round(pointScore * 0.30d + depthRangeScore * 0.30d + sampleScore * 0.22d + weightScore * 0.18d);
    }

    private static double ProfileDepthStability(PersonalFaceContourShapeProfile profile)
    {
        if (profile.DepthStabilityPercent > 0d)
        {
            return Math.Clamp(profile.DepthStabilityPercent, 0d, 100d);
        }

        var standardDeviations = profile.Points
            .Select(static point => point.Z.StandardDeviation)
            .OfType<double>()
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();
        if (standardDeviations.Count == 0)
        {
            return ProfileDepthEvidence(profile) * 0.50d;
        }

        var averageStandardDeviation = standardDeviations.Average();
        return Round(Math.Clamp(100d - averageStandardDeviation / 0.050d * 100d, 0d, 100d));
    }

    private static double? ProfileDepthRange(PersonalFaceContourShapeProfile profile)
    {
        if (profile.DepthRange.HasValue)
        {
            return profile.DepthRange;
        }

        var depthValues = profile.Points
            .Select(static point => Value(point.Z))
            .OfType<double>()
            .ToList();
        return depthValues.Count == 0 ? null : Round(depthValues.Max() - depthValues.Min());
    }

    private static double? ProfileAverageDepthStandardDeviation(PersonalFaceContourShapeProfile profile)
    {
        if (profile.AverageDepthStandardDeviation.HasValue)
        {
            return profile.AverageDepthStandardDeviation;
        }

        var standardDeviations = profile.Points
            .Select(static point => point.Z.StandardDeviation)
            .OfType<double>()
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();
        return standardDeviations.Count == 0 ? null : Round(standardDeviations.Average());
    }

    private static double? Value(PersonalMetricDistribution distribution)
    {
        var value = distribution.ExponentialMovingAverage ?? distribution.Average;
        return value is double number && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number
            : null;
    }

    private static double AverageEvidence(params double[] values)
    {
        var usable = values
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .Select(static value => Math.Clamp(value, 0d, 100d))
            .ToList();
        return usable.Count == 0 ? 0d : Round(usable.Average());
    }

    private static double CalculateShapeMaturity(PersonalFaceModel model)
    {
        if (model.AcceptedSamples <= 0 || model.AcceptedSampleWeight <= 0d)
        {
            return 0d;
        }

        var sampleMaturity = Math.Clamp(model.AcceptedSamples / 120d, 0d, 1d);
        var weightMaturity = Math.Clamp(model.AcceptedSampleWeight / StrongSampleWeight, 0d, 1d);
        return Math.Clamp(sampleMaturity * 0.45d + weightMaturity * 0.55d, 0d, 1d);
    }

    private static double Blend(double fallback, double measured, double measurementWeight)
    {
        var weight = Math.Clamp(measurementWeight, 0d, 1d);
        return fallback + (measured - fallback) * weight;
    }

    private static PersonalFacePoseBucketProfile? GetNeutralPoseProfile(PersonalFaceModel model)
    {
        return model.PoseBuckets.FirstOrDefault(static bucket =>
            bucket.PrimaryNeutralReference
            && bucket.SampleCount > 0
            && string.Equals(bucket.BucketId, PersonalFacePoseBuckets.FrontNeutral, StringComparison.OrdinalIgnoreCase));
    }

    private static PersonalMetricDistribution PreferNeutral(
        PersonalFacePoseBucketProfile? neutralPose,
        Func<PersonalFacePoseBucketProfile, PersonalMetricDistribution> selector,
        PersonalMetricDistribution fallback)
    {
        if (neutralPose is null)
        {
            return fallback;
        }

        var candidate = selector(neutralPose);
        return candidate.SampleCount > 0 ? candidate : fallback;
    }

    private static double Metric(PersonalMetricDistribution distribution, double fallback, double minimum, double maximum)
    {
        var value = distribution.ExponentialMovingAverage ?? distribution.Average ?? fallback;
        return Round(Bound(value, fallback, minimum, maximum));
    }

    private static double? NullableMetric(PersonalMetricDistribution distribution)
    {
        var value = distribution.ExponentialMovingAverage ?? distribution.Average;
        if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
        {
            return null;
        }

        return Round(number);
    }

    private static double Bound(double value, double fallback, double minimum, double maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Math.Clamp(fallback, minimum, maximum);
        }

        return Math.Clamp(value, minimum, maximum);
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed record SurfaceDepthEvidence(
        double DepthProfileCoveragePercent,
        double DepthStabilityPercent,
        double? DepthRange,
        double? AverageDepthStandardDeviation)
    {
        public static SurfaceDepthEvidence None { get; } = new(0d, 0d, null, null);
    }

    private sealed record SurfacePatchMetrics(
        double SurfaceArea,
        double AverageTriangleArea,
        double DepthRelief,
        double AverageNormalX,
        double AverageNormalY,
        double AverageNormalZ,
        double NormalConsistencyPercent);

    private sealed record SurfacePatchHealth(
        double HealthPercent,
        string Status,
        string Finding);
}
