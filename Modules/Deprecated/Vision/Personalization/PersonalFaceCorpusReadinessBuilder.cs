namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCorpusReadinessBuilder
{
    private const double StrongBaselineSampleCount = 360d;
    private const double StrongMotionPairCount = 480d;
    private const double StrongFaceSizeRange = 0.18d;
    private const double StrongZApparentDistanceRange = 1.25d;
    private const double StrongZRelativeRange = 0.34d;
    private const double StrongHeadYawRangeDegrees = 32d;
    private const double StrongHeadPitchRangeDegrees = 18d;
    private const double StrongHeadRollRangeDegrees = 14d;
    private const double StrongPoseBucketSampleCount = 45d;
    private const double StrongPoseBucketWeight = 36d;
    private const double StrongEyeOpeningRange = 0.16d;
    private const double StrongMouthOpeningRange = 0.22d;
    private const double StrongJawDroopRange = 0.14d;
    private const double StrongBlendshapeRangePercent = 55d;
    private const double StrongIdentitySignatureSampleCount = 240d;
    private const double StrongContourShapeSampleCount = 180d;
    private const double StrongCaptureQualityScorePercent = 86d;
    private const double UsefulMaximumNextSampleInfluencePercent = 5d;
    private const double StrongMaximumNextSampleInfluencePercent = 2d;
    private const double SuspiciouslyLowPoseAxisRangeDegrees = 0.75d;
    private const double UsefulFaceMotionRange = 0.08d;
    private const double PoseExplainedYawRangeDegrees = 10d;
    private const double PoseExplainedPitchRangeDegrees = 8d;
    private const double PoseExplainedRollRangeDegrees = 8d;
    private const double SuspiciousFeatureRange = 0.16d;
    private const double PoseExplainedFeatureRange = 0.46d;
    private const double ExtremeFeatureRange = 0.54d;
    private const double BasePoseExplainedFeatureAllowance = 0.10d;
    private const double MaximumPoseExplainedFeatureAllowance = 0.48d;
    private const int MinimumMouthAnchorAuditSamples = 12;
    private const double MinimumPlausibleMouthCenterYToFaceHeight = 0.55d;
    private const double MinimumPlausibleEyeToMouthYDistanceToFaceHeight = 0.20d;
    private const double ReviewMouthAnchorSuspiciousRate = 0.12d;
    private const double HoldMouthAnchorSuspiciousRate = 0.30d;
    private const double SuspiciousJawDroopAverage = 0.60d;
    private const double SuspiciousJawDroopClamp = 1.40d;
    private const int MinimumEyeApertureAuditSamples = 24;
    private const double ReviewOneEyeArtifactRate = 0.08d;
    private const double HoldOneEyeArtifactRate = 0.20d;
    private const double ReviewEyeArtifactSuppressedRate = 0.08d;
    private const double HoldEyeArtifactSuppressedRate = 0.18d;
    private const double ReviewEyeReconstructedRate = 0.25d;
    private const double HoldEyeReconstructedRate = 0.60d;
    private const double ReviewEyeAgreementAveragePercent = 76d;
    private const double HoldEyeAgreementAveragePercent = 62d;
    private const double ReviewEyeAgreementMinimumPercent = 42d;
    private const int MinimumIdentitySessionAuditSamples = 12;
    private const double ReviewRecentIdentityOutlierRate = 0.12d;
    private const double HoldRecentIdentityOutlierRate = 0.30d;
    private const double ReviewRecentIdentityConfidencePercent = 58d;
    private const double HoldRecentIdentityConfidencePercent = 38d;

    public PersonalFaceCorpusReadiness Build(
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        IReadOnlyList<PersonalFaceMeasurementSample> recentSamples,
        long measurementJournalBytes,
        long measurementBudgetBytes = PersonalFaceMeasurementJournal.DefaultBudgetBytes)
    {
        var model = new PersonalFaceCorpusReadiness
        {
            SubjectId = faceModel.SubjectId,
            SubjectDisplayName = faceModel.SubjectDisplayName,
            SubjectCollectionMode = faceModel.SubjectCollectionMode,
            UnknownSubjectPolicy = faceModel.UnknownSubjectPolicy,
            CreatedAtUtc = faceModel.CreatedAtUtc != default ? faceModel.CreatedAtUtc : motionModel.CreatedAtUtc,
            UpdatedAtUtc = Max(faceModel.UpdatedAtUtc, motionModel.UpdatedAtUtc),
            AcceptedBaselineSamples = faceModel.AcceptedSamples,
            RecentMeasurementSamplesReviewed = recentSamples.Count,
            MotionUsableObservations = motionModel.UsableObservationCount,
            MotionPairs = motionModel.MotionPairCount,
            IdentitySignatureSamples = faceModel.IdentitySignatureSamples,
            LeftEyeShapeSamples = faceModel.LeftEyeShape.SampleCount,
            RightEyeShapeSamples = faceModel.RightEyeShape.SampleCount,
            OuterLipShapeSamples = faceModel.OuterLipShape.SampleCount,
            InnerLipShapeSamples = faceModel.InnerLipShape.SampleCount,
            JawShapeSamples = faceModel.JawShape.SampleCount,
            LeftBrowShapeSamples = faceModel.LeftBrowShape.SampleCount,
            RightBrowShapeSamples = faceModel.RightBrowShape.SampleCount,
            NoseBridgeShapeSamples = faceModel.NoseBridgeShape.SampleCount,
            NoseBaseShapeSamples = faceModel.NoseBaseShape.SampleCount,
            LeftCheekSurfaceSamples = faceModel.LeftCheekSurface.SampleCount,
            RightCheekSurfaceSamples = faceModel.RightCheekSurface.SampleCount,
            ForeheadSurfaceSamples = faceModel.ForeheadSurface.SampleCount,
            MeasurementJournalBytes = Math.Max(0L, measurementJournalBytes),
            MeasurementBudgetBytes = Math.Max(1L, measurementBudgetBytes)
        };

        model.MeasurementBudgetUsedPercent = Round(100d * model.MeasurementJournalBytes / model.MeasurementBudgetBytes);
        model.FaceWidthRange = Range(faceModel.FaceWidth);
        model.FaceHeightRange = Range(faceModel.FaceHeight);
        model.ZEstimateSamples = faceModel.ZEstimateSamples;
        model.ZCalibratedSamples = faceModel.ZCalibratedSamples;
        model.ZCameraFovEstimatedSamples = faceModel.ZCameraFovEstimatedSamples;
        model.ZLearnedReferenceSamples = faceModel.ZLearnedReferenceSamples;
        model.ZApparentOnlySamples = faceModel.ZApparentOnlySamples;
        model.ZApparentDistanceRange = Range(faceModel.ZApparentDistanceUnits);
        model.ZRelativeToReferenceRange = Range(faceModel.ZRelativeToReference);
        model.AverageZConfidencePercent = ValueOrNull(faceModel.ZConfidencePercent);
        model.MinimumZConfidencePercent = faceModel.ZConfidencePercent.Minimum;
        model.ZCalibratedRate = Rate(faceModel.ZCalibratedSamples, faceModel.ZEstimateSamples);
        model.ZCameraFovEstimatedRate = Rate(faceModel.ZCameraFovEstimatedSamples, faceModel.ZEstimateSamples);
        model.ZLearnedReferenceRate = Rate(faceModel.ZLearnedReferenceSamples, faceModel.ZEstimateSamples);
        model.ZApparentOnlyRate = Rate(faceModel.ZApparentOnlySamples, faceModel.ZEstimateSamples);
        model.HeadYawRangeDegrees = Range(faceModel.HeadYawDegrees);
        model.HeadPitchRangeDegrees = Range(faceModel.HeadPitchDegrees);
        model.HeadRollRangeDegrees = Range(faceModel.HeadRollDegrees);
        model.EyeOpeningRange = Range(faceModel.AverageEyeOpeningRatio);
        model.MouthOpeningRange = Range(faceModel.MouthOpeningRatio);
        model.JawDroopRange = Range(faceModel.JawDroopRatio);
        model.MediaPipeBlinkRangePercent = Range(faceModel.MediaPipeAverageEyeBlinkPercent);
        model.MediaPipeJawOpenRangePercent = Range(faceModel.MediaPipeJawOpenPercent);
        model.FaceAspectRatioRange = Range(faceModel.FaceAspectRatio);
        model.EyeMidlineXToFaceWidthRange = Range(faceModel.EyeMidlineXToFaceWidth);
        model.MouthCenterXToFaceWidthRange = Range(faceModel.MouthCenterXToFaceWidth);
        model.EyeToMouthXOffsetToFaceWidthRange = Range(faceModel.EyeToMouthXOffsetToFaceWidth);
        model.InterEyeDistanceToFaceWidthRange = Range(faceModel.InterEyeDistanceToFaceWidth);
        model.MouthWidthToFaceWidthRange = Range(faceModel.MouthWidthToFaceWidth);
        model.EyeMidlineYToFaceHeightRange = Range(faceModel.EyeMidlineYToFaceHeight);
        model.MouthCenterYToFaceHeightRange = Range(faceModel.MouthCenterYToFaceHeight);
        model.EyeToMouthYDistanceToFaceHeightRange = Range(faceModel.EyeToMouthYDistanceToFaceHeight);
        model.EyeArtifactSuppressedRate = Rate(faceModel.EyeArtifactSuppressedSamples, faceModel.AcceptedSamples);
        model.PossibleOneEyeArtifactRate = Rate(faceModel.PossibleOneEyeArtifactSamples, faceModel.AcceptedSamples);
        model.EyeReconstructedRate = Rate(faceModel.LeftEyeReconstructedSamples + faceModel.RightEyeReconstructedSamples, Math.Max(1, faceModel.AcceptedSamples * 2));
        model.MouthReconstructedRate = Rate(faceModel.MouthReconstructedSamples, faceModel.AcceptedSamples);
        model.LearningAnchorPercent = Round(faceModel.LearningStability.AnchorPercent);
        model.LearningAnchorStatus = faceModel.LearningStability.AnchorStatus;
        model.MinimumTrackedDistributionWeight = Round(faceModel.LearningStability.MinimumTrackedDistributionWeight);
        model.MaximumNextSampleInfluencePercent = Round(faceModel.LearningStability.MaximumNextSampleInfluencePercent);
        model.MaximumEventLikeNextSampleInfluencePercent = Round(faceModel.LearningStability.MaximumEventLikeNextSampleInfluencePercent);
        model.PoseBuckets = NormalizePoseBuckets(faceModel.PoseBuckets);
        model.PoseBucketRequiredCount = model.PoseBuckets.Count(static bucket => bucket.RequiredForAvatarCoverage);
        model.PoseBucketCoveredCount = model.PoseBuckets.Count(static bucket => bucket.RequiredForAvatarCoverage && bucket.SampleCount > 0);
        model.PoseBucketCoveragePercent = ScorePoseBucketCoverage(model.PoseBuckets);
        UpdateCaptureQuality(model, recentSamples);

        model.BaselineCoveragePercent = ScoreCount(faceModel.AcceptedSamples, StrongBaselineSampleCount);
        model.LearningStabilityCoveragePercent = LearningStabilityScore(faceModel.LearningStability);
        model.MotionCoveragePercent = ScoreCount(motionModel.MotionPairCount, StrongMotionPairCount);
        model.BRotationAroundYCoveragePercent = ScoreRange(model.HeadYawRangeDegrees, StrongHeadYawRangeDegrees);
        model.ARotationAroundXCoveragePercent = ScoreRange(model.HeadPitchRangeDegrees, StrongHeadPitchRangeDegrees);
        model.CRotationAroundZCoveragePercent = ScoreRange(model.HeadRollRangeDegrees, StrongHeadRollRangeDegrees);
        var poseRangeCoverage = Average(
            model.BRotationAroundYCoveragePercent,
            model.ARotationAroundXCoveragePercent,
            model.CRotationAroundZCoveragePercent);
        model.PoseCoveragePercent = Round(poseRangeCoverage * 0.55d + model.PoseBucketCoveragePercent * 0.45d);
        model.DistanceCoveragePercent = Average(
            ScoreRange(model.FaceWidthRange, StrongFaceSizeRange),
            ScoreRange(model.FaceHeightRange, StrongFaceSizeRange));
        model.ZDistanceEvidenceHealthPercent = ScoreZDistanceEvidenceHealth(model);
        model.ZDistanceCoveragePercent = Round(Average(
            model.DistanceCoveragePercent,
            ScoreRange(model.ZApparentDistanceRange, StrongZApparentDistanceRange),
            ScoreOptionalRange(model.ZRelativeToReferenceRange, StrongZRelativeRange, fallback: model.DistanceCoveragePercent),
            model.ZDistanceEvidenceHealthPercent));
        model.XYZABCCoveragePercent = Average(
            model.ZDistanceCoveragePercent,
            model.ARotationAroundXCoveragePercent,
            model.BRotationAroundYCoveragePercent,
            model.CRotationAroundZCoveragePercent);
        model.ExpressionCoveragePercent = Average(
            ScoreRange(model.EyeOpeningRange, StrongEyeOpeningRange),
            ScoreRange(model.MouthOpeningRange, StrongMouthOpeningRange),
            ScoreRange(model.JawDroopRange, StrongJawDroopRange),
            ScoreOptionalRange(model.MediaPipeBlinkRangePercent, StrongBlendshapeRangePercent, fallback: 45d),
            ScoreOptionalRange(model.MediaPipeJawOpenRangePercent, StrongBlendshapeRangePercent, fallback: 45d));
        model.IdentityCoveragePercent = Average(
            ScoreCount(faceModel.IdentitySignatureSamples, StrongIdentitySignatureSampleCount),
            ScoreIdentityDistribution(faceModel.FaceAspectRatio),
            ScoreIdentityDistribution(faceModel.EyeMidlineXToFaceWidth),
            ScoreIdentityDistribution(faceModel.MouthCenterXToFaceWidth),
            ScoreIdentityDistribution(faceModel.EyeToMouthXOffsetToFaceWidth),
            ScoreIdentityDistribution(faceModel.InterEyeDistanceToFaceWidth),
            ScoreIdentityDistribution(faceModel.MouthWidthToFaceWidth),
            ScoreIdentityDistribution(faceModel.EyeMidlineYToFaceHeight),
            ScoreIdentityDistribution(faceModel.MouthCenterYToFaceHeight));
        var contourProfiles = new[]
        {
            faceModel.LeftEyeShape,
            faceModel.RightEyeShape,
            faceModel.OuterLipShape,
            faceModel.InnerLipShape,
            faceModel.JawShape
        };
        var surfaceProfiles = new[]
        {
            faceModel.LeftBrowShape,
            faceModel.RightBrowShape,
            faceModel.NoseBridgeShape,
            faceModel.NoseBaseShape,
            faceModel.LeftCheekSurface,
            faceModel.RightCheekSurface,
            faceModel.ForeheadSurface
        };
        model.ContourShapeCoveragePercent = Average(contourProfiles.Select(ScoreContourShapeProfile).ToArray());
        model.ContourDepthProfileHealthPercent = Average(contourProfiles.Select(ScoreContourDepthProfile).ToArray());
        model.SurfaceShapeCoveragePercent = Average(surfaceProfiles.Select(ScoreContourShapeProfile).ToArray());
        model.SurfaceDepthProfileHealthPercent = Average(surfaceProfiles.Select(ScoreContourDepthProfile).ToArray());
        UpdateSurfaceGeometryAudit(model, contourProfiles.Concat(surfaceProfiles).ToArray());
        model.ApertureConsistency = PersonalFaceApertureConsistencyAnalyzer.Analyze(recentSamples);
        model.ApertureConsistencyHealthPercent = model.ApertureConsistency.HealthPercent;
        UpdateMouthVerticalAnchorAudit(model, faceModel, recentSamples);
        UpdateEyeApertureReliabilityAudit(model, faceModel);
        model.EyeBehindGlassesTrustPercent = ScoreEyeBehindGlassesTrust(faceModel, model);
        model.MouthJawTrustPercent = ScoreMouthJawTrust(faceModel, model);
        model.DirectFeatureMeasurementTrustPercent = Round(
            model.EyeBehindGlassesTrustPercent * 0.44d
            + model.MouthJawTrustPercent * 0.32d
            + model.ApertureConsistencyHealthPercent * 0.16d
            + model.ContourShapeCoveragePercent * 0.08d);
        model.QualityCoveragePercent = QualityScore(faceModel, motionModel, model);
        model.CaptureQualityCoveragePercent = CaptureQualityScore(model);
        model.StorageHealthPercent = StorageScore(model.MeasurementBudgetUsedPercent);
        UpdateDataAudit(model, faceModel, motionModel, recentSamples);
        var coverageReadiness = Round(
            model.BaselineCoveragePercent * 0.11d
            + model.LearningStabilityCoveragePercent * 0.06d
            + model.MotionCoveragePercent * 0.15d
            + model.PoseCoveragePercent * 0.11d
            + model.DistanceCoveragePercent * 0.08d
            + model.ExpressionCoveragePercent * 0.15d
            + model.IdentityCoveragePercent * 0.07d
            + model.ContourShapeCoveragePercent * 0.04d
            + model.SurfaceShapeCoveragePercent * 0.03d
            + model.DirectFeatureMeasurementTrustPercent * 0.10d
            + model.QualityCoveragePercent * 0.06d
            + model.CaptureQualityCoveragePercent * 0.03d
            + model.StorageHealthPercent * 0.02d);
        model.OverallReadinessPercent = Round(coverageReadiness * 0.88d + model.DataAuditHealthPercent * 0.12d);

        AddStrengths(model);
        AddWarningsAndSuggestions(model, faceModel, motionModel);
        return model;
    }

    private static void AddStrengths(PersonalFaceCorpusReadiness model)
    {
        if (model.BaselineCoveragePercent >= 70d)
        {
            model.Strengths.Add("Baseline sample count is becoming useful for stable per-person averages.");
        }

        if (model.LearningStabilityCoveragePercent >= 70d)
        {
            model.Strengths.Add("Learning stability is becoming useful; individual new samples have limited influence on the personal model.");
        }

        if (model.MotionCoveragePercent >= 70d)
        {
            model.Strengths.Add("Motion-pair coverage is becoming useful for animation timing and facial movement trends.");
        }

        if (model.XYZABCCoveragePercent >= 70d)
        {
            model.Strengths.Add("XYZABC coverage is becoming useful: Z distance and A/B/C rotation ranges are separating head motion from face-shape changes.");
        }

        if (model.ExpressionCoveragePercent >= 70d)
        {
            model.Strengths.Add("Eye, mouth, and jaw ranges are broad enough to start characterizing facial movement.");
        }

        if (model.IdentityCoveragePercent >= 70d)
        {
            model.Strengths.Add("Measurement-only identity signature is becoming useful for avoiding mixed-person learning.");
        }

        if (model.ContourShapeCoveragePercent >= 70d)
        {
            model.Strengths.Add("Aggregate eye, lip, and jaw shape profiles are becoming useful for avatar outline controls.");
        }

        if (model.SurfaceShapeCoveragePercent >= 70d)
        {
            model.Strengths.Add("Aggregate brow, nose, cheek, and forehead surface profiles are becoming useful for 3D face fitting.");
        }

        if (model.SurfaceGeometryHealthPercent >= 80d && model.SurfaceGeometryPatchCount >= 8)
        {
            model.Strengths.Add("Surface patch geometry is coherent enough for the measurement preview to expose brows, nose, cheeks, lips, eyes, and jaw as measured 3D regions.");
        }

        if (model.DirectFeatureMeasurementTrustPercent >= 70d)
        {
            model.Strengths.Add("Direct eye and mouth/jaw measurement trust is becoming useful for high-accuracy avatar fitting.");
        }

        if (model.ApertureConsistencyHealthPercent >= 85d)
        {
            model.Strengths.Add("Eye, mouth, and jaw aperture measurements agree with dense blendshape corroboration.");
        }

        if (model.QualityCoveragePercent >= 75d)
        {
            model.Strengths.Add("Recent measurements are high quality enough for long-term weighting.");
        }

        if (model.CaptureQualityCoveragePercent >= 75d)
        {
            model.Strengths.Add("Capture-quality gates are passing often enough for long-term measurement collection.");
        }

        if (model.DataAuditHealthPercent >= 85d)
        {
            model.Strengths.Add("Data audit checks are passing; pose, feature anchoring, jaw scale, and retained measurements look internally consistent.");
        }

        if (model.IdentitySessionHealthPercent >= 85d && model.RecentIdentityMeasurementSamples >= MinimumIdentitySessionAuditSamples)
        {
            model.Strengths.Add("Recent identity-session measurements agree with the learned subject signature.");
        }

        if (model.PoseExplainedFeatureMotionHealthPercent >= 85d && model.PoseExplainedFeatureObservedRange is > 0d)
        {
            model.Strengths.Add("Face-local feature motion is plausibly explained by measured head pose instead of a face-shape rewrite.");
        }

        if (model.MouthVerticalAnchorHealthPercent >= 90d && model.MouthVerticalAnchorSamplesReviewed >= MinimumMouthAnchorAuditSamples)
        {
            model.Strengths.Add("Mouth vertical anchor audit is strong; lip measurements sit below the eye line where expected.");
        }

        if (model.PoseBucketConsistencyHealthPercent >= 85d && model.PoseBucketConsistency.ComparedPoseBucketCount > 0)
        {
            model.Strengths.Add("Pose-bucket consistency is strong; turned-head buckets still match the front-neutral identity proportions.");
        }
    }

    private static void AddWarningsAndSuggestions(
        PersonalFaceCorpusReadiness model,
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel)
    {
        if (model.AcceptedBaselineSamples < 60)
        {
            model.Warnings.Add("The learning data is early: fewer than 60 accepted awake baseline samples.");
            model.NextCaptureSuggestions.Add("Collect several alert, symptom-free sessions with the subject confirmation checkbox enabled.");
        }

        if (model.LearningAnchorPercent < 50d || model.MaximumNextSampleInfluencePercent > UsefulMaximumNextSampleInfluencePercent)
        {
            model.Warnings.Add($"The personal model is still weakly anchored: anchor {model.LearningAnchorPercent:0.#}%, one new stable sample can move the weighted profile by up to {model.MaximumNextSampleInfluencePercent:0.#}%.");
            model.NextCaptureSuggestions.Add($"Continue subject-confirmed alert sessions until max next-sample influence is below {UsefulMaximumNextSampleInfluencePercent:0.#}%; below {StrongMaximumNextSampleInfluencePercent:0.#}% is the stronger avatar target.");
        }

        if (model.MotionPairs < 60)
        {
            model.Warnings.Add("Motion coverage is sparse: fewer than 60 usable motion pairs.");
            model.NextCaptureSuggestions.Add("Record longer subject-confirmed sessions with natural talking, slow blinks, relaxed mouth, and head movement.");
        }

        if (model.DistanceCoveragePercent < 55d)
        {
            model.Warnings.Add("Face size/distance coverage is narrow.");
            model.NextCaptureSuggestions.Add("Capture alert sessions leaning close, normal desk distance, and leaning back so the model sees face scale changes.");
        }

        if (model.ZDistanceCoveragePercent < 55d)
        {
            model.Warnings.Add($"Z distance coverage is narrow: {model.ZDistanceCoveragePercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Collect a short Z-distance pass: sit normal, lean closer, then lean back while keeping the face visible and glasses reflections low.");
        }

        if (model.ZDistanceEvidenceHealthPercent < 60d)
        {
            model.Warnings.Add($"Z evidence health is weak: {model.ZDistanceEvidenceHealthPercent:0.#}% from {model.ZEstimateSamples} explicit Z sample(s), average Z confidence {FormatPercentValue(model.AverageZConfidencePercent)}.");
            model.NextCaptureSuggestions.Add("For stronger Z truth, collect a close/normal/far pass at the same zoom level; later add one known-distance calibration point if physical inches are needed.");
        }

        if (model.ZEstimateSamples > 0
            && (model.ZApparentOnlyRate ?? 0d) > 0.65d
            && (model.ZCalibratedRate ?? 0d) <= 0d
            && (model.ZCameraFovEstimatedRate ?? 0d) <= 0d)
        {
            model.Warnings.Add("Most Z samples are apparent-scale only, so zoom/FOV changes can still look like distance changes.");
            model.NextCaptureSuggestions.Add("Keep webcam zoom fixed during avatar-learning passes, or calibrate the camera/zoom before comparing absolute Z distances.");
        }

        if (model.ARotationAroundXCoveragePercent < 55d)
        {
            model.Warnings.Add($"A rotation around X coverage is narrow: {model.ARotationAroundXCoveragePercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Collect a short A-axis pass: slight up/down head tilt while keeping the eyes, brows, nose, and mouth visible.");
        }

        if (model.BRotationAroundYCoveragePercent < 55d)
        {
            model.Warnings.Add($"B rotation around Y coverage is narrow: {model.BRotationAroundYCoveragePercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Collect a short B-axis pass: slow left/right head turns so features can be checked against the rotating head instead of sliding on it.");
        }

        if (model.CRotationAroundZCoveragePercent < 55d)
        {
            model.Warnings.Add($"C rotation around Z coverage is narrow: {model.CRotationAroundZCoveragePercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Collect a short C-axis pass: slight side-to-side head tilt while keeping both eyes and the mouth measurable.");
        }

        if (model.PoseCoveragePercent < 55d)
        {
            model.Warnings.Add("Head pose coverage is narrow.");
            model.NextCaptureSuggestions.Add("Capture alert left/right turns, slight up/down tilt, and a few C-axis head-tilt positions while keeping glasses visible.");
        }

        if (model.PoseBucketCoveragePercent < 55d)
        {
            model.Warnings.Add($"Pose bucket coverage is early: {model.PoseBucketCoveredCount}/{model.PoseBucketRequiredCount} required pose buckets have measurements.");
            foreach (var bucket in model.PoseBuckets
                         .Where(static bucket => bucket.RequiredForAvatarCoverage && bucket.SampleCount < StrongPoseBucketSampleCount)
                         .OrderBy(static bucket => bucket.SampleCount)
                         .Take(3))
            {
                model.NextCaptureSuggestions.Add(bucket.CaptureInstruction);
            }
        }

        if (model.PoseBucketConsistency.ComparedPoseBucketCount > 0 && model.PoseBucketConsistencyHealthPercent < 70d)
        {
            model.Warnings.Add($"Pose-bucket consistency needs review: {model.PoseBucketConsistencyHealthPercent:0.#}% health. Turned-head buckets may be changing identity-shaped measurements instead of only changing head pose.");
            model.NextCaptureSuggestions.Add("Open Avatar System and Last 10 Good Features, then compare turned-head samples against the neutral face; if eyes, nose, or mouth slide across the head, rebuild avatar data after fixing tracking.");
        }

        if (model.PoseExplainedFeatureMotionHealthPercent < 70d)
        {
            model.Warnings.Add($"Pose-explained feature motion needs review: {model.PoseExplainedFeatureMotionHealthPercent:0.#}% health. Feature drift may be too large for the measured head pose.");
            model.NextCaptureSuggestions.Add("Review a turned-head overlay or Last 10 Good Features view. If facial features slide sideways instead of the head rotating, collect new data only after tracking is corrected.");
        }

        if (model.ExpressionCoveragePercent < 55d)
        {
            model.Warnings.Add("Eye/mouth/jaw expression coverage is narrow.");
            model.NextCaptureSuggestions.Add("Capture intentional awake expressions: eyes open/relaxed/slow blink, lips closed/slightly open, speech, and jaw drop.");
        }

        foreach (var finding in model.DataAuditFindings.Take(6))
        {
            model.Warnings.Add($"Data audit: {finding}");
        }

        if (model.ApertureConsistencyHealthPercent < 70d)
        {
            model.Warnings.Add($"Aperture consistency needs review: {model.ApertureConsistencyHealthPercent:0.#}% health. Eye, mouth, or jaw opening may not agree with dense corroboration.");
            model.NextCaptureSuggestions.Add("Collect a short aperture corroboration pass: reduce glasses glare, perform slow blinks, lips closed/slightly open, natural speech, and gentle jaw drop while alert.");
        }

        if (model.EyeApertureReliabilityHealthPercent < 70d)
        {
            model.Warnings.Add($"Eye aperture reliability needs review: {model.EyeApertureReliabilityHealthPercent:0.#}% health. Glasses glare, one-eye artifacts, or reconstructed eye frames may be weakening eyelid-open measurements.");
            model.NextCaptureSuggestions.Add("Run a short alert eye pass with glasses on: minimize monitor reflections, keep both eyes visible, and slowly move through eyes open, relaxed, and slow blink positions.");
        }

        if (model.MouthVerticalAnchorHealthPercent < 70d)
        {
            model.Warnings.Add($"Mouth vertical anchor needs review: {model.MouthVerticalAnchorHealthPercent:0.#}% health. Some lip measurements may be locking above the mouth area.");
            model.NextCaptureSuggestions.Add("Review the overlay while opening/closing the mouth; the lip outline should stay below the nose and centered on the actual lips before avatar capture continues.");
        }

        if (model.IdentityCoveragePercent < 55d)
        {
            model.Warnings.Add("Measurement-only identity coverage is early.");
            model.NextCaptureSuggestions.Add("Collect more subject-confirmed alert frames in stable lighting with no face filters so the app can learn a non-image identity signature.");
        }

        if (model.IdentitySessionHealthPercent is > 0d and < 70d)
        {
            model.Warnings.Add($"Recent identity session needs review: {model.IdentitySessionHealthPercent:0.#}% health. Accepted frames may be drifting away from the learned subject signature.");
            model.NextCaptureSuggestions.Add("Review the collection audit and overlay; keep avatar capture off if someone else is at the camera or the face geometry looks inconsistent.");
        }

        if (model.ContourShapeCoveragePercent < 55d)
        {
            model.Warnings.Add("Aggregate eye/lip/jaw contour shape coverage is early.");
            model.NextCaptureSuggestions.Add("Collect direct, high-quality eye, lip, and jaw observations: reduce glasses glare, keep the full lower face visible, and include relaxed closed-mouth plus slightly open-mouth poses.");
        }

        if (model.ContourDepthProfileHealthPercent < 45d)
        {
            model.Warnings.Add($"Eye/lip/jaw Z profile health is early: {model.ContourDepthProfileHealthPercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Keep collecting subject-confirmed frames with stable face lock so eyelid, lip, and jaw contours gain repeatable face-local Z evidence rather than only flat outlines.");
        }

        if (model.SurfaceShapeCoveragePercent < 55d)
        {
            model.Warnings.Add("Aggregate brow/nose/cheek/forehead surface coverage is early.");
            model.NextCaptureSuggestions.Add("Collect subject-confirmed dense-mesh passes with the forehead, brows, nose bridge, and cheeks visible: front-neutral, slow B turns, slight A tilt, and normal/close/leaned-back distances.");
        }

        if (model.SurfaceDepthProfileHealthPercent < 45d)
        {
            model.Warnings.Add($"Brow/nose/cheek/forehead Z profile health is early: {model.SurfaceDepthProfileHealthPercent:0.#}%.");
            model.NextCaptureSuggestions.Add("Collect slow front, three-quarter, and slight up/down passes with the forehead, nose bridge, and cheeks visible so the surface model learns depth instead of a flat sketch.");
        }

        if (model.SurfaceGeometryPatchCount > 0 && model.SurfaceGeometryHealthPercent < 55d)
        {
            model.Warnings.Add($"Surface patch geometry needs review: {model.SurfaceGeometryHealthPercent:0.#}% health, {model.SurfaceGeometryReviewPatchCount} review patch(es).");
            model.NextCaptureSuggestions.Add("Open Avatar System and Last 10 Good Features; collect stable subject-confirmed frames for any weak region, especially thin mouth openings, nose bridge, cheeks, and brows.");
        }

        if (model.EyeBehindGlassesTrustPercent < 60d)
        {
            model.Warnings.Add("Eye-behind-glasses trust is early or limited.");
            model.NextCaptureSuggestions.Add("Run a short alert eye pass: reduce monitor reflections, keep both eyes visible behind glasses, and include open, relaxed, and slow-blink eyelid positions.");
        }

        if (model.MouthJawTrustPercent < 60d)
        {
            model.Warnings.Add("Mouth/jaw trust is early or limited.");
            model.NextCaptureSuggestions.Add("Run a short alert mouth/jaw pass with the lower face fully visible: lips closed, slightly open, natural speech, and a gentle jaw drop.");
        }

        if (model.QualityCoveragePercent < 65d)
        {
            model.Warnings.Add("Measurement quality is limiting the learning data.");
            model.NextCaptureSuggestions.Add("Improve lighting, reduce glasses glare, keep the camera at 4K when practical, and avoid face filters/background effects.");
        }

        if (model.CaptureQualitySamples > 0 && model.CaptureQualityCanCollectRate < 0.85d)
        {
            model.Warnings.Add($"Capture-quality gate is rejecting too many recent samples: {model.CaptureQualityCanCollectRate:P0} collectable.");
            model.NextCaptureSuggestions.Add("Use explicit 4K/30fps capture, keep the face large enough in frame, and reduce glasses glare before collecting avatar measurements.");
        }

        if (model.CaptureQualitySamples > 0 && model.CaptureQualityAvatarGradeRate < 0.30d)
        {
            model.Warnings.Add($"Avatar-grade capture coverage is low: {model.CaptureQualityAvatarGradeRate:P0} of recent samples.");
            model.NextCaptureSuggestions.Add("Collect a short high-quality avatar pass with stable lighting, full face visible, glasses glare minimized, and the camera in 4K mode.");
        }

        foreach (var issue in model.CaptureQualityIssueLabels.Take(3))
        {
            model.Warnings.Add($"Capture-quality issue: {issue}.");
        }

        if (model.MeasurementBudgetUsedPercent > 80d)
        {
            model.Warnings.Add("Measurement journal storage is approaching the 10 GB budget.");
            model.NextCaptureSuggestions.Add("Move the output folder to a larger external drive or archive old explicit training media.");
        }

        if (motionModel.Warnings.Count > 0)
        {
            foreach (var warning in motionModel.Warnings.Take(3))
            {
                model.Warnings.Add(warning);
            }
        }

        if (faceModel.MediaPipeAverageEyeBlinkPercent.SampleCount == 0 || faceModel.MediaPipeJawOpenPercent.SampleCount == 0)
        {
            model.NextCaptureSuggestions.Add("Enable the MediaPipe dense landmark sidecar when possible so blendshape evidence can corroborate glasses-heavy eye and mouth contours.");
        }
    }

    private static void UpdateDataAudit(
        PersonalFaceCorpusReadiness model,
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        IReadOnlyList<PersonalFaceMeasurementSample> recentSamples)
    {
        model.MeasurementJournalCoveragePercent = faceModel.AcceptedSamples <= 0
            ? 100d
            : Round(Math.Clamp(recentSamples.Count / (double)Math.Max(1, faceModel.AcceptedSamples) * 100d, 0d, 100d));
        model.PoseEstimationHealthPercent = ScorePoseEstimationHealth(model);
        model.FeatureAnchoringHealthPercent = ScoreFeatureAnchoringHealth(model);
        UpdatePoseExplainedFeatureMotionAudit(model, faceModel);
        model.PoseBucketConsistency = PersonalFacePoseBucketConsistencyAnalyzer.Analyze(model.PoseBuckets);
        model.PoseBucketConsistencyHealthPercent = model.PoseBucketConsistency.HealthPercent;
        model.ApertureConsistency = PersonalFaceApertureConsistencyAnalyzer.Analyze(recentSamples);
        model.ApertureConsistencyHealthPercent = model.ApertureConsistency.HealthPercent;
        model.JawDroopScaleHealthPercent = ScoreJawDroopScaleHealth(faceModel);

        if (faceModel.AcceptedSamples >= 120 && model.MeasurementJournalCoveragePercent < 60d)
        {
            model.DataAuditFindings.Add($"retained measurement journal covers {model.MeasurementJournalCoveragePercent:0.#}% of aggregate accepted samples; this is expected with throttled measurement writes, but aggregate-only history cannot be replayed frame by frame.");
        }

        var yawRange = model.HeadYawRangeDegrees ?? 0d;
        var pitchRange = model.HeadPitchRangeDegrees ?? 0d;
        var rollRange = model.HeadRollRangeDegrees ?? 0d;
        var faceMotionRange = Math.Max(model.FaceWidthRange ?? 0d, model.FaceHeightRange ?? 0d);
        var positionMotionRange = Math.Max(Range(faceModel.FaceCenterX) ?? 0d, Range(faceModel.FaceCenterY) ?? 0d);
        if (faceModel.AcceptedSamples >= 60
            && yawRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && pitchRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && (rollRange >= 8d || faceMotionRange >= UsefulFaceMotionRange || positionMotionRange >= UsefulFaceMotionRange))
        {
            var movementKind = rollRange >= 8d
                ? "C-axis tilt changed"
                : "the face position or scale changed";
            model.DataAuditFindings.Add($"A/B rotation coverage is still early while {movementKind}. This is not treated as a tracking failure by itself; collect deliberate left/right and up/down head turns before trusting turned-head avatar fitting.");
        }

        var eyeXRange = model.EyeMidlineXToFaceWidthRange ?? 0d;
        var mouthXRange = model.MouthCenterXToFaceWidthRange ?? 0d;
        var eyeMouthXRange = model.EyeToMouthXOffsetToFaceWidthRange ?? 0d;
        var interEyeRange = model.InterEyeDistanceToFaceWidthRange ?? 0d;
        var mouthWidthRange = model.MouthWidthToFaceWidthRange ?? 0d;
        var eyeMouthRange = Range(faceModel.EyeToMouthYDistanceToFaceHeight) ?? 0d;
        var worstFeatureRange = new[] { eyeXRange, mouthXRange, eyeMouthXRange, interEyeRange, mouthWidthRange, eyeMouthRange }.Max();
        if (worstFeatureRange >= SuspiciousFeatureRange && !HasPoseRangeThatCanExplainProjectedFeatureMotion(model))
        {
            model.DataAuditFindings.Add($"face-local feature anchors are drifting more than expected (eye X range {eyeXRange:0.###}, mouth X range {mouthXRange:0.###}, eye-mouth X range {eyeMouthXRange:0.###}, eye spacing range {interEyeRange:0.###}, mouth width range {mouthWidthRange:0.###}, eye-to-mouth Y range {eyeMouthRange:0.###}); review overlay/video for features sliding on the head.");
        }

        if (model.PoseExplainedFeatureMotionHealthPercent < 60d)
        {
            model.DataAuditFindings.Add($"pose-explained feature motion is weak ({model.PoseExplainedFeatureMotionHealthPercent:0.#}%): observed feature range {model.PoseExplainedFeatureObservedRange:0.###} is high for expected pose-projected range {model.PoseExplainedFeatureExpectedRange:0.###}; review whether eyes/mouth are sliding on the head.");
        }

        if (model.ZEstimateSamples <= 0 && faceModel.AcceptedSamples >= 30)
        {
            model.DataAuditFindings.Add("accepted avatar samples do not include explicit Z estimates yet; distance coverage is falling back to face width/height only.");
        }
        else if (model.ZDistanceEvidenceHealthPercent < 55d)
        {
            model.DataAuditFindings.Add($"Z distance evidence is weak ({model.ZDistanceEvidenceHealthPercent:0.#}%): samples {model.ZEstimateSamples}, average Z confidence {FormatPercentValue(model.AverageZConfidencePercent)}, apparent-only rate {FormatPercent(model.ZApparentOnlyRate)}.");
        }

        if (model.SurfaceDepthProfileHealthPercent < 45d)
        {
            model.DataAuditFindings.Add($"surface Z profile health is early ({model.SurfaceDepthProfileHealthPercent:0.#}%): brow/nose/cheek/forehead profiles may still render flatter than the measured face.");
        }

        if (model.SurfaceGeometryPatchCount > 0 && model.SurfaceGeometryHealthPercent < 50d)
        {
            var maturity = faceModel.AcceptedSamples >= 120
                ? "surface geometry health is weak"
                : "surface geometry health is warming";
            model.DataAuditFindings.Add($"{maturity} ({model.SurfaceGeometryHealthPercent:0.#}%): {model.SurfaceGeometryReviewPatchCount} measured patch(es) need review; status {model.SurfaceGeometryStatus}.");
        }

        if (model.ContourDepthProfileHealthPercent < 45d)
        {
            model.DataAuditFindings.Add($"feature contour Z profile health is early ({model.ContourDepthProfileHealthPercent:0.#}%): eye/lip/jaw outlines may be measured mostly as 2D contours until more stable dense frames accumulate.");
        }

        if (model.RecentIdentityMeasurementSamples is > 0 and < MinimumIdentitySessionAuditSamples)
        {
            model.DataAuditFindings.Add($"recent identity-session audit is warming up ({model.RecentIdentityMeasurementSamples}/{MinimumIdentitySessionAuditSamples} comparable samples).");
        }

        var identitySessionAuditReady = model.AcceptedBaselineSamples >= PersonalFaceIdentityAnalyzer.MinimumSamplesForWarmupStrongMismatchGate
            && model.RecentIdentityMeasurementSamples >= MinimumIdentitySessionAuditSamples;

        if (identitySessionAuditReady
            && model.RecentIdentityOutlierFrameRate is double identityOutlierRate
            && identityOutlierRate >= ReviewRecentIdentityOutlierRate)
        {
            var severity = identityOutlierRate >= HoldRecentIdentityOutlierRate ? "high" : "review";
            var context = IsPoseStressedIdentitySession(model)
                ? " during strong A/B/C or distance motion; add front-neutral samples if the subject gate was correct"
                : "";
            model.DataAuditFindings.Add($"recent identity-session {severity}: {identityOutlierRate:P0} of comparable accepted frames had identity outlier features{context}.");
        }

        if (model.AverageRecentIdentityConfidencePercent is double averageIdentityConfidence
            && identitySessionAuditReady
            && averageIdentityConfidence < ReviewRecentIdentityConfidencePercent)
        {
            var context = IsPoseStressedIdentitySession(model)
                ? " This may be pose-stressed rather than a subject mismatch, but front-neutral confirmation frames would strengthen the signature."
                : "";
            model.DataAuditFindings.Add($"recent identity-session confidence is low ({averageIdentityConfidence:0.#}% average); verify the subject gate before learning from this session.{context}");
        }

        if (model.EyeApertureReliabilityHealthPercent < 60d)
        {
            model.DataAuditFindings.Add($"eye aperture reliability is weak ({model.EyeApertureReliabilityHealthPercent:0.#}%): one-eye artifact rate {FormatPercent(model.PossibleOneEyeArtifactRate)}, suppressed-eye rate {FormatPercent(model.EyeArtifactSuppressedRate)}, reconstructed-eye rate {FormatPercent(model.EyeReconstructedRate)}, eye agreement avg {FormatPercentValue(model.EyeAgreementAveragePercent)}.");
        }

        foreach (var finding in model.PoseBucketConsistency.Findings)
        {
            model.DataAuditFindings.Add(finding);
        }

        foreach (var finding in model.ApertureConsistency.Findings)
        {
            model.DataAuditFindings.Add(finding);
        }

        if ((faceModel.JawDroopRatio.Average ?? 0d) >= SuspiciousJawDroopAverage
            || (faceModel.JawDroopRatio.Maximum ?? 0d) >= SuspiciousJawDroopClamp)
        {
            model.DataAuditFindings.Add($"jaw droop scale is suspicious (avg {(faceModel.JawDroopRatio.Average ?? 0d):0.###}, max {(faceModel.JawDroopRatio.Maximum ?? 0d):0.###}); treat it as baseline-relative evidence, not a direct avatar jaw offset.");
        }

        if (motionModel.HeadYawVelocityDegreesPerSecond.SampleCount > 0
            && (motionModel.HeadYawVelocityDegreesPerSecond.Maximum ?? 0d) <= 0.001d
            && (motionModel.HeadRollVelocityDegreesPerSecond.Maximum ?? 0d) > 1d)
        {
            model.DataAuditFindings.Add("motion model sees C rotation changes but no B rotation changes; pose estimation should be verified before trusting side-to-side head animation.");
        }

        model.DataAuditHealthPercent = Average(
            model.PoseEstimationHealthPercent,
            model.FeatureAnchoringHealthPercent,
            model.PoseExplainedFeatureMotionHealthPercent,
            model.ZDistanceEvidenceHealthPercent,
            Math.Max(65d, model.ContourDepthProfileHealthPercent),
            Math.Max(65d, model.SurfaceDepthProfileHealthPercent),
            model.IdentitySessionHealthPercent,
            model.EyeApertureReliabilityHealthPercent,
            model.MouthVerticalAnchorHealthPercent,
            model.PoseBucketConsistencyHealthPercent,
            model.ApertureConsistencyHealthPercent,
            model.JawDroopScaleHealthPercent,
            Math.Max(50d, model.SurfaceGeometryHealthPercent),
            Math.Max(65d, model.MeasurementJournalCoveragePercent));
    }

    private static void UpdateSurfaceGeometryAudit(
        PersonalFaceCorpusReadiness model,
        IReadOnlyList<PersonalFaceContourShapeProfile> profiles)
    {
        var scores = profiles
            .Select(ScoreSurfaceGeometryProfile)
            .Where(static score => score.HasPatch)
            .ToList();
        model.SurfaceGeometryPatchCount = scores.Count;
        if (scores.Count == 0)
        {
            model.SurfaceGeometryHealthPercent = 0d;
            model.SurfaceGeometryMinimumPatchHealthPercent = null;
            model.SurfaceGeometryReviewPatchCount = 0;
            model.SurfaceGeometryStatus = "surface geometry waiting for measured patches";
            return;
        }

        model.SurfaceGeometryHealthPercent = Round(scores.Average(static score => score.HealthPercent));
        model.SurfaceGeometryMinimumPatchHealthPercent = Round(scores.Min(static score => score.HealthPercent));
        model.SurfaceGeometryReviewPatchCount = scores.Count(static score => score.HealthPercent < 55d || score.NormalConsistencyPercent < 55d);
        var weakest = scores.OrderBy(static score => score.HealthPercent).First();
        model.SurfaceGeometryStatus = model.SurfaceGeometryReviewPatchCount > 0
            ? $"{model.SurfaceGeometryReviewPatchCount} patch(es) need review; weakest {weakest.Label} {weakest.HealthPercent:0.#}% ({weakest.Status})"
            : $"surface geometry coherent across {scores.Count} measured patch(es); weakest {weakest.Label} {weakest.HealthPercent:0.#}%";
    }

    private static void UpdatePoseExplainedFeatureMotionAudit(
        PersonalFaceCorpusReadiness model,
        PersonalFaceModel faceModel)
    {
        var eyeXRange = model.EyeMidlineXToFaceWidthRange ?? 0d;
        var mouthXRange = model.MouthCenterXToFaceWidthRange ?? 0d;
        var eyeMouthXRange = model.EyeToMouthXOffsetToFaceWidthRange ?? 0d;
        var interEyeRange = model.InterEyeDistanceToFaceWidthRange ?? 0d;
        var mouthWidthRange = model.MouthWidthToFaceWidthRange ?? 0d;
        var eyeMouthYRange = Range(faceModel.EyeToMouthYDistanceToFaceHeight) ?? 0d;
        var observedRange = new[] { eyeXRange, mouthXRange, eyeMouthXRange, interEyeRange, mouthWidthRange, eyeMouthYRange }.Max();
        var poseStrength = Math.Max(
            Math.Max(
                Math.Abs(model.HeadYawRangeDegrees ?? 0d) / 60d,
                Math.Abs(model.HeadPitchRangeDegrees ?? 0d) / 45d),
            Math.Abs(model.HeadRollRangeDegrees ?? 0d) / 45d);
        var expectedRange = BasePoseExplainedFeatureAllowance
            + Math.Clamp(poseStrength, 0d, 1d)
            * (MaximumPoseExplainedFeatureAllowance - BasePoseExplainedFeatureAllowance);

        model.PoseExplainedFeatureObservedRange = Round(observedRange);
        model.PoseExplainedFeatureExpectedRange = Round(expectedRange);
        if (observedRange <= 0d)
        {
            model.PoseExplainedFeatureMotionHealthPercent = 55d;
            return;
        }

        if (observedRange <= expectedRange)
        {
            var usedAllowance = observedRange / Math.Max(0.001d, expectedRange);
            model.PoseExplainedFeatureMotionHealthPercent = Round(Math.Clamp(96d - usedAllowance * 10d, 82d, 96d));
            return;
        }

        var overage = observedRange - expectedRange;
        model.PoseExplainedFeatureMotionHealthPercent = Round(Math.Clamp(
            82d - overage / Math.Max(0.001d, ExtremeFeatureRange - expectedRange) * 62d,
            20d,
            82d));
    }

    private static void UpdateEyeApertureReliabilityAudit(
        PersonalFaceCorpusReadiness model,
        PersonalFaceModel faceModel)
    {
        model.EyeAgreementAveragePercent = ValueOrNull(faceModel.EyeAgreementPercent);
        model.EyeAgreementMinimumPercent = faceModel.EyeAgreementPercent.Minimum;

        var oneEyeArtifactRate = model.PossibleOneEyeArtifactRate ?? 0d;
        var suppressedRate = model.EyeArtifactSuppressedRate ?? 0d;
        var reconstructedRate = model.EyeReconstructedRate ?? 0d;
        var health = faceModel.AcceptedSamples < MinimumEyeApertureAuditSamples ? 88d : 100d;
        health -= oneEyeArtifactRate * 175d;
        health -= suppressedRate * 155d;
        health -= reconstructedRate * 35d;

        if (model.EyeAgreementAveragePercent is double averageAgreement)
        {
            if (averageAgreement < ReviewEyeAgreementAveragePercent)
            {
                health -= (ReviewEyeAgreementAveragePercent - averageAgreement) * 1.25d;
            }

            if (averageAgreement < HoldEyeAgreementAveragePercent)
            {
                health = Math.Min(health, 48d);
            }
        }
        else if (faceModel.AcceptedSamples >= MinimumEyeApertureAuditSamples)
        {
            health = Math.Min(health, 68d);
        }

        if (model.EyeAgreementMinimumPercent is double minimumAgreement
            && minimumAgreement < ReviewEyeAgreementMinimumPercent
            && faceModel.EyeAgreementPercent.SampleCount >= MinimumEyeApertureAuditSamples)
        {
            health -= (ReviewEyeAgreementMinimumPercent - minimumAgreement) * 0.65d;
        }

        if (oneEyeArtifactRate >= HoldOneEyeArtifactRate
            || suppressedRate >= HoldEyeArtifactSuppressedRate
            || reconstructedRate >= HoldEyeReconstructedRate)
        {
            health = Math.Min(health, 42d);
        }

        model.EyeApertureReliabilityHealthPercent = Round(Math.Clamp(health, 15d, 100d));

        if (faceModel.AcceptedSamples is > 0 and < MinimumEyeApertureAuditSamples)
        {
            model.DataAuditFindings.Add($"eye aperture reliability audit is warming up ({faceModel.AcceptedSamples}/{MinimumEyeApertureAuditSamples} accepted samples).");
        }

        if (oneEyeArtifactRate >= ReviewOneEyeArtifactRate)
        {
            var severity = oneEyeArtifactRate >= HoldOneEyeArtifactRate ? "high" : "review";
            model.DataAuditFindings.Add($"eye aperture reliability {severity}: {oneEyeArtifactRate:P0} of accepted frames were flagged as possible one-eye glasses/contour artifacts.");
        }

        if (suppressedRate >= ReviewEyeArtifactSuppressedRate)
        {
            var severity = suppressedRate >= HoldEyeArtifactSuppressedRate ? "high" : "review";
            model.DataAuditFindings.Add($"eye aperture reliability {severity}: {suppressedRate:P0} of accepted frames suppressed eye evidence because likely artifacts were detected.");
        }

        if (reconstructedRate >= ReviewEyeReconstructedRate)
        {
            var severity = reconstructedRate >= HoldEyeReconstructedRate ? "high" : "review";
            model.DataAuditFindings.Add($"eye aperture reliability {severity}: {reconstructedRate:P0} of eye-side measurements were reconstructed instead of directly observed.");
        }

        if (model.EyeAgreementAveragePercent is double eyeAgreementAverage
            && eyeAgreementAverage < ReviewEyeAgreementAveragePercent)
        {
            model.DataAuditFindings.Add($"eye aperture reliability review: left/right eye agreement average is {eyeAgreementAverage:0.#}%, which may weaken eye-open measurements behind glasses.");
        }

        if (faceModel.AcceptedSamples >= MinimumEyeApertureAuditSamples
            && model.EyeAgreementAveragePercent is null)
        {
            model.DataAuditFindings.Add("eye aperture reliability review: accepted samples do not include left/right eye agreement data.");
        }
    }

    private static void UpdateMouthVerticalAnchorAudit(
        PersonalFaceCorpusReadiness model,
        PersonalFaceModel faceModel,
        IReadOnlyList<PersonalFaceMeasurementSample> recentSamples)
    {
        var samples = recentSamples
            .Where(static sample => sample.CaptureQualityCanCollect && sample.IdentityMeasurementAvailable)
            .Where(static sample => sample.MouthCenterYToFaceHeight.HasValue && sample.EyeToMouthYDistanceToFaceHeight.HasValue)
            .Select(static sample => new MouthAnchorSample(
                sample.MouthCenterYToFaceHeight!.Value,
                sample.EyeToMouthYDistanceToFaceHeight!.Value))
            .ToList();
        model.MouthVerticalAnchorSamplesReviewed = samples.Count;
        var suspiciousSamples = samples.Count(static sample => IsSuspiciousMouthAnchor(sample.MouthCenterYToFaceHeight, sample.EyeToMouthYDistanceToFaceHeight));
        model.MouthVerticalAnchorSuspiciousSampleRate = Rate(suspiciousSamples, samples.Count);

        var aggregateMouthCenter = ValueOrNull(faceModel.MouthCenterYToFaceHeight);
        var aggregateEyeMouthSpan = ValueOrNull(faceModel.EyeToMouthYDistanceToFaceHeight);
        var aggregateLooksHigh = aggregateMouthCenter is double aggregateMouthCenterValue
            && aggregateEyeMouthSpan is double aggregateEyeMouthSpanValue
            && IsSuspiciousMouthAnchor(aggregateMouthCenterValue, aggregateEyeMouthSpanValue);

        if (samples.Count == 0 && !aggregateLooksHigh)
        {
            model.MouthVerticalAnchorHealthPercent = aggregateMouthCenter.HasValue && aggregateEyeMouthSpan.HasValue ? 82d : 65d;
            return;
        }

        var health = samples.Count < MinimumMouthAnchorAuditSamples ? 72d : 100d;
        var suspiciousRate = model.MouthVerticalAnchorSuspiciousSampleRate ?? 0d;
        health -= suspiciousRate * 130d;

        if (aggregateLooksHigh
            && aggregateMouthCenter is double aggregateMouthCenterMessageValue
            && aggregateEyeMouthSpan is double aggregateEyeMouthSpanMessageValue)
        {
            health = Math.Min(health, 42d);
            model.DataAuditFindings.Add(
                $"mouth vertical anchor aggregate is suspicious (mouth Y {aggregateMouthCenterMessageValue:0.###}, eye-to-mouth span {aggregateEyeMouthSpanMessageValue:0.###}); verify lip tracking is not locked on the area under the nose.");
        }

        if (samples.Count >= MinimumMouthAnchorAuditSamples && suspiciousRate >= ReviewMouthAnchorSuspiciousRate)
        {
            var severity = suspiciousRate >= HoldMouthAnchorSuspiciousRate ? "high" : "review";
            model.DataAuditFindings.Add(
                $"mouth vertical anchor {severity}: {suspiciousRate:P0} of recent collectable samples place the mouth too high or too close to the eye line; verify the overlay before trusting lip/jaw avatar data.");
        }

        if (samples.Count is > 0 and < MinimumMouthAnchorAuditSamples && faceModel.AcceptedSamples >= 60)
        {
            model.DataAuditFindings.Add($"mouth vertical anchor audit is waiting for more retained samples ({samples.Count}/{MinimumMouthAnchorAuditSamples}).");
        }

        model.MouthVerticalAnchorHealthPercent = Round(Math.Clamp(health, 20d, 100d));
    }

    private static double ScorePoseEstimationHealth(PersonalFaceCorpusReadiness model)
    {
        var yawRange = model.HeadYawRangeDegrees ?? 0d;
        var pitchRange = model.HeadPitchRangeDegrees ?? 0d;
        var rollRange = model.HeadRollRangeDegrees ?? 0d;
        var faceRange = Math.Max(model.FaceWidthRange ?? 0d, model.FaceHeightRange ?? 0d);
        if (yawRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && pitchRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && rollRange < 4d
            && faceRange < UsefulFaceMotionRange)
        {
            return 100d;
        }

        if (yawRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && pitchRange <= SuspiciouslyLowPoseAxisRangeDegrees
            && (rollRange >= 8d || faceRange >= UsefulFaceMotionRange))
        {
            return 62d;
        }

        return Round(Average(
            ScoreRange(yawRange, 18d),
            ScoreRange(pitchRange, 12d),
            ScoreRange(rollRange, 10d)));
    }

    private static double ScoreZDistanceEvidenceHealth(PersonalFaceCorpusReadiness model)
    {
        if (model.ZEstimateSamples <= 0)
        {
            return 22d;
        }

        var sampleScore = ScoreCount(model.ZEstimateSamples, StrongBaselineSampleCount * 0.35d);
        var confidenceScore = model.AverageZConfidencePercent is double averageConfidence
            ? Math.Clamp(averageConfidence, 0d, 100d)
            : 35d;
        var sourceScore = Math.Clamp(
            (model.ZCalibratedRate ?? 0d) * 100d
            + (model.ZCameraFovEstimatedRate ?? 0d) * 82d
            + (model.ZLearnedReferenceRate ?? 0d) * 72d
            + (model.ZApparentOnlyRate ?? 0d) * 42d,
            0d,
            100d);
        if (model.MinimumZConfidencePercent is double minimumConfidence && minimumConfidence < 42d)
        {
            sourceScore = Math.Min(sourceScore, 68d);
        }

        return Round(sampleScore * 0.34d + confidenceScore * 0.36d + sourceScore * 0.30d);
    }

    private static double ScoreFeatureAnchoringHealth(PersonalFaceCorpusReadiness model)
    {
        var worstRange = Math.Max(
            Math.Max(model.EyeMidlineXToFaceWidthRange ?? 0d, model.MouthCenterXToFaceWidthRange ?? 0d),
            Math.Max(
                Math.Max(model.EyeToMouthXOffsetToFaceWidthRange ?? 0d, model.InterEyeDistanceToFaceWidthRange ?? 0d),
                model.MouthWidthToFaceWidthRange ?? 0d));
        if (worstRange <= 0d)
        {
            return 45d;
        }

        if (HasPoseRangeThatCanExplainProjectedFeatureMotion(model))
        {
            if (worstRange >= ExtremeFeatureRange)
            {
                return 55d;
            }

            var poseAwareScore = 96d - Math.Max(0d, worstRange - SuspiciousFeatureRange)
                / Math.Max(0.001d, PoseExplainedFeatureRange - SuspiciousFeatureRange)
                * 26d;
            return Round(Math.Clamp(poseAwareScore, 70d, 96d));
        }

        if (worstRange >= 0.24d)
        {
            return 25d;
        }

        if (worstRange >= SuspiciousFeatureRange)
        {
            return 55d;
        }

        return Round(Math.Clamp(100d - worstRange / SuspiciousFeatureRange * 25d, 70d, 100d));
    }

    private static bool HasPoseRangeThatCanExplainProjectedFeatureMotion(PersonalFaceCorpusReadiness model)
    {
        return (model.HeadYawRangeDegrees ?? 0d) >= PoseExplainedYawRangeDegrees
            || (model.HeadPitchRangeDegrees ?? 0d) >= PoseExplainedPitchRangeDegrees
            || (model.HeadRollRangeDegrees ?? 0d) >= PoseExplainedRollRangeDegrees;
    }

    private static double ScoreJawDroopScaleHealth(PersonalFaceModel faceModel)
    {
        var average = faceModel.JawDroopRatio.Average ?? 0d;
        var maximum = faceModel.JawDroopRatio.Maximum ?? 0d;
        if (average >= SuspiciousJawDroopAverage || maximum >= SuspiciousJawDroopClamp)
        {
            return 30d;
        }

        if (maximum >= 0.45d)
        {
            return 65d;
        }

        return 100d;
    }

    private static double QualityScore(
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness readiness)
    {
        var reliability = Math.Clamp(faceModel.AverageFaceReliabilityPercent, 0d, 100d);
        var continuity = Math.Clamp(faceModel.AverageFaceContinuityPercent, 0d, 100d);
        var motionQuality = Math.Clamp(motionModel.AverageObservationQualityPercent, 0d, 100d);
        if (motionModel.UsableObservationCount <= 0)
        {
            motionQuality = (reliability + continuity) / 2d;
        }

        var artifactPenalty =
            Math.Clamp((readiness.EyeArtifactSuppressedRate ?? 0d) * 35d, 0d, 20d)
            + Math.Clamp((readiness.PossibleOneEyeArtifactRate ?? 0d) * 35d, 0d, 20d)
            + Math.Clamp((readiness.EyeReconstructedRate ?? 0d) * 30d, 0d, 18d)
            + Math.Clamp((readiness.MouthReconstructedRate ?? 0d) * 25d, 0d, 14d);
        return Round(Math.Clamp(reliability * 0.38d + continuity * 0.27d + motionQuality * 0.35d - artifactPenalty, 0d, 100d));
    }

    private static void UpdateCaptureQuality(
        PersonalFaceCorpusReadiness model,
        IReadOnlyList<PersonalFaceMeasurementSample> recentSamples)
    {
        var samples = recentSamples
            .Where(static sample => !string.IsNullOrWhiteSpace(sample.CaptureQualityLabel) || sample.CaptureQualityScorePercent > 0d)
            .ToList();
        model.CaptureQualitySamples = samples.Count;
        model.CaptureQualityCanCollectSamples = samples.Count(static sample => sample.CaptureQualityCanCollect);
        model.CaptureQualityAvatarGradeSamples = samples.Count(static sample => sample.CaptureQualityAvatarGrade);
        model.CaptureQualityCanCollectRate = RateValue(model.CaptureQualityCanCollectSamples, model.CaptureQualitySamples);
        model.CaptureQualityAvatarGradeRate = RateValue(model.CaptureQualityAvatarGradeSamples, model.CaptureQualitySamples);
        model.AverageCaptureQualityScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityScorePercent));
        model.MinimumCaptureQualityScorePercent = MinimumOptional(samples.Select(static sample => (double?)sample.CaptureQualityScorePercent));
        model.AverageCaptureQualityCameraModeScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityCameraModeScorePercent));
        model.AverageCaptureQualityFaceScaleScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityFaceScaleScorePercent));
        model.AverageCaptureQualityEyeScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityEyeScorePercent));
        model.AverageCaptureQualityMouthScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityMouthScorePercent));
        model.AverageCaptureQualityStabilityScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityStabilityScorePercent));
        model.AverageCaptureQualityGlassesScorePercent = AverageOptional(samples.Select(static sample => (double?)sample.CaptureQualityGlassesScorePercent));
        var identitySamples = samples
            .Where(static sample => sample.IdentityMeasurementAvailable && sample.IdentityComparedFeatureCount > 0)
            .ToList();
        model.RecentIdentityMeasurementSamples = identitySamples.Count;
        model.AverageRecentIdentityConfidencePercent = AverageOptional(identitySamples.Select(static sample => (double?)sample.IdentityConfidencePercent));
        model.MinimumRecentIdentityConfidencePercent = MinimumOptional(identitySamples.Select(static sample => (double?)sample.IdentityConfidencePercent));
        model.RecentIdentityOutlierFrameRate = identitySamples.Count == 0
            ? null
            : RateValue(identitySamples.Count(static sample => sample.IdentityOutlierFeatureCount > 0), identitySamples.Count);
        model.IdentitySessionHealthPercent = ScoreIdentitySessionHealth(model);
        UpdateIdentitySessionAuditStatus(model);
        model.CaptureQualityIssueLabels = samples
            .SelectMany(static sample => sample.CaptureQualityIssues)
            .Where(static issue => !string.IsNullOrWhiteSpace(issue))
            .GroupBy(static issue => issue.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static group => $"{group.Key} ({group.Count()})")
            .ToList();
    }

    private static double CaptureQualityScore(PersonalFaceCorpusReadiness model)
    {
        if (model.CaptureQualitySamples <= 0)
        {
            return 0d;
        }

        var score = model.AverageCaptureQualityScorePercent is double averageScore
            ? Math.Clamp(averageScore / StrongCaptureQualityScorePercent * 100d, 0d, 100d)
            : 0d;
        var minimum = model.MinimumCaptureQualityScorePercent is double minimumScore
            ? Math.Clamp(minimumScore / 70d * 100d, 0d, 100d)
            : 0d;
        var collectable = Math.Clamp(model.CaptureQualityCanCollectRate * 100d, 0d, 100d);
        var avatarGrade = Math.Clamp(model.CaptureQualityAvatarGradeRate / 0.45d * 100d, 0d, 100d);
        return Round(score * 0.42d + minimum * 0.18d + collectable * 0.26d + avatarGrade * 0.14d);
    }

    private static double ScoreIdentitySessionHealth(PersonalFaceCorpusReadiness model)
    {
        if (model.RecentIdentityMeasurementSamples <= 0)
        {
            return 58d;
        }

        if (model.AcceptedBaselineSamples < PersonalFaceIdentityAnalyzer.MinimumSamplesForWarmupStrongMismatchGate)
        {
            return model.RecentIdentityMeasurementSamples >= MinimumIdentitySessionAuditSamples ? 76d : 64d;
        }

        var sampleScore = Math.Clamp(model.RecentIdentityMeasurementSamples / (double)MinimumIdentitySessionAuditSamples * 100d, 0d, 100d);
        var averageConfidence = model.AverageRecentIdentityConfidencePercent ?? 0d;
        var minimumConfidence = model.MinimumRecentIdentityConfidencePercent ?? averageConfidence;
        var outlierRate = model.RecentIdentityOutlierFrameRate ?? 0d;
        var poseStressedSameSubjectSession = IsPoseStressedIdentitySession(model)
            && averageConfidence >= ReviewRecentIdentityConfidencePercent
            && outlierRate < HoldRecentIdentityOutlierRate;
        var health = sampleScore * 0.18d
            + Math.Clamp(averageConfidence, 0d, 100d) * 0.42d
            + Math.Clamp(minimumConfidence, 0d, 100d) * 0.14d
            + Math.Clamp((1d - outlierRate) * 100d, 0d, 100d) * 0.26d;

        if (outlierRate >= HoldRecentIdentityOutlierRate || averageConfidence < HoldRecentIdentityConfidencePercent)
        {
            health = Math.Min(health, 42d);
        }
        else if (outlierRate >= ReviewRecentIdentityOutlierRate || averageConfidence < ReviewRecentIdentityConfidencePercent)
        {
            health = poseStressedSameSubjectSession
                ? Math.Max(health, 72d)
                : Math.Min(health, 68d);
        }

        return Round(Math.Clamp(health, 15d, 100d));
    }

    private static void UpdateIdentitySessionAuditStatus(PersonalFaceCorpusReadiness model)
    {
        if (model.RecentIdentityMeasurementSamples <= 0)
        {
            model.IdentitySessionAuditStage = "waiting";
            model.IdentitySessionAuditStatus = "waiting for comparable identity measurements";
            return;
        }

        if (model.RecentIdentityMeasurementSamples < MinimumIdentitySessionAuditSamples)
        {
            model.IdentitySessionAuditStage = "sample-warmup";
            model.IdentitySessionAuditStatus = $"warming up comparable identity samples ({model.RecentIdentityMeasurementSamples}/{MinimumIdentitySessionAuditSamples})";
            return;
        }

        if (model.AcceptedBaselineSamples < PersonalFaceIdentityAnalyzer.MinimumSamplesForWarmupStrongMismatchGate)
        {
            model.IdentitySessionAuditStage = "baseline-warmup";
            model.IdentitySessionAuditStatus = $"identity signature warming; mature drift hold starts after {PersonalFaceIdentityAnalyzer.MinimumSamplesForWarmupStrongMismatchGate} accepted baseline samples";
            return;
        }

        var averageConfidence = model.AverageRecentIdentityConfidencePercent ?? 0d;
        var outlierRate = model.RecentIdentityOutlierFrameRate ?? 0d;
        var poseStressedSameSubjectSession = IsPoseStressedIdentitySession(model)
            && averageConfidence >= ReviewRecentIdentityConfidencePercent
            && outlierRate < HoldRecentIdentityOutlierRate;
        if (outlierRate >= HoldRecentIdentityOutlierRate || averageConfidence < HoldRecentIdentityConfidencePercent)
        {
            model.IdentitySessionAuditStage = "hold";
            model.IdentitySessionAuditStatus = $"mature identity-session hold: confidence {averageConfidence:0.#}%, outlier frames {outlierRate:P0}";
            return;
        }

        if (poseStressedSameSubjectSession)
        {
            model.IdentitySessionAuditStage = outlierRate >= ReviewRecentIdentityOutlierRate
                ? "pose-stressed-review"
                : "pose-stressed-stable";
            model.IdentitySessionAuditStatus = $"pose-stressed identity session: confidence {averageConfidence:0.#}%, outlier frames {outlierRate:P0}; strong A/B/C or distance motion can lower the straight-on identity signature, so keep the subject gate confirmed and add front-neutral samples if this was the enrolled subject.";
            return;
        }

        if (outlierRate >= ReviewRecentIdentityOutlierRate || averageConfidence < ReviewRecentIdentityConfidencePercent)
        {
            model.IdentitySessionAuditStage = "review";
            model.IdentitySessionAuditStatus = $"mature identity-session review: confidence {averageConfidence:0.#}%, outlier frames {outlierRate:P0}";
            return;
        }

        model.IdentitySessionAuditStage = "mature-stable";
        model.IdentitySessionAuditStatus = $"mature identity-session stable: confidence {averageConfidence:0.#}%, outlier frames {outlierRate:P0}";
    }

    private static bool IsPoseStressedIdentitySession(PersonalFaceCorpusReadiness model)
    {
        var strongestRotationRange = new[]
        {
            model.HeadYawRangeDegrees ?? 0d,
            model.HeadPitchRangeDegrees ?? 0d,
            model.HeadRollRangeDegrees ?? 0d
        }.Max();
        var faceScaleRange = Math.Max(model.FaceWidthRange ?? 0d, model.FaceHeightRange ?? 0d);
        var captureQualityStrong =
            model.AverageCaptureQualityScorePercent is >= 80d
            && model.CaptureQualityCanCollectRate >= 0.85d;
        return captureQualityStrong
            && HasPoseRangeThatCanExplainProjectedFeatureMotion(model)
            && (strongestRotationRange >= 24d || faceScaleRange >= 0.12d);
    }

    private static double StorageScore(double budgetUsedPercent)
    {
        if (budgetUsedPercent <= 65d)
        {
            return 100d;
        }

        if (budgetUsedPercent <= 100d)
        {
            return Round(100d - (budgetUsedPercent - 65d) / 35d * 70d);
        }

        return 10d;
    }

    private static double LearningStabilityScore(PersonalFaceLearningStability stability)
    {
        var anchorScore = Math.Clamp(stability.AnchorPercent, 0d, 100d);
        var influence = Math.Max(0d, stability.MaximumNextSampleInfluencePercent);
        var influenceScore = influence <= StrongMaximumNextSampleInfluencePercent
            ? 100d
            : influence >= 18d
                ? 0d
                : 100d - (influence - StrongMaximumNextSampleInfluencePercent) / 16d * 100d;

        return Round(anchorScore * 0.45d + influenceScore * 0.55d);
    }

    private static double ScoreCount(int count, double strongCount)
    {
        return Round(Math.Clamp(count / strongCount * 100d, 0d, 100d));
    }

    private static double ScoreRange(double? range, double strongRange)
    {
        return range is double value
            ? Round(Math.Clamp(value / strongRange * 100d, 0d, 100d))
            : 0d;
    }

    private static double ScoreOptionalRange(double? range, double strongRange, double fallback)
    {
        return range is double value ? ScoreRange(value, strongRange) : fallback;
    }

    private static double ScoreIdentityDistribution(PersonalMetricDistribution distribution)
    {
        if (distribution.SampleCount <= 0 || distribution.Average is null)
        {
            return 0d;
        }

        var countScore = ScoreCount(distribution.SampleCount, StrongIdentitySignatureSampleCount);
        var stabilityScore = distribution.StandardDeviation is double standardDeviation
            ? Round(Math.Clamp(100d - standardDeviation / 0.08d * 100d, 0d, 100d))
            : countScore * 0.5d;
        return Round(countScore * 0.65d + stabilityScore * 0.35d);
    }

    private static double ScoreContourShapeProfile(PersonalFaceContourShapeProfile profile)
    {
        if (!profile.HasProfile)
        {
            return 0d;
        }

        var countScore = ScoreCount(profile.SampleCount, StrongContourShapeSampleCount);
        var expectedPointCount = Math.Max(2, profile.PointCount);
        var populatedPoints = profile.Points.Count(point =>
            (point.X.Average.HasValue || point.X.ExponentialMovingAverage.HasValue)
            && (point.Y.Average.HasValue || point.Y.ExponentialMovingAverage.HasValue));
        var pointScore = Math.Clamp(populatedPoints / (double)expectedPointCount * 100d, 0d, 100d);
        var weightScore = Math.Clamp(profile.TotalWeight / StrongContourShapeSampleCount * 100d, 0d, 100d);
        var depthScore = ScoreContourDepthProfile(profile);
        return Round(countScore * 0.42d + pointScore * 0.23d + weightScore * 0.17d + depthScore * 0.18d);
    }

    private static double ScoreContourDepthProfile(PersonalFaceContourShapeProfile profile)
    {
        if (!profile.HasProfile)
        {
            return 0d;
        }

        var depthEvidence = ProfileDepthEvidence(profile);
        var depthStability = ProfileDepthStability(profile);
        return Round(depthEvidence * 0.58d + depthStability * 0.42d);
    }

    private static double ProfileDepthEvidence(PersonalFaceContourShapeProfile profile)
    {
        if (profile.DepthEvidencePercent > 0d)
        {
            return Math.Clamp(profile.DepthEvidencePercent, 0d, 100d);
        }

        var expectedPointCount = Math.Max(2, profile.PointCount);
        var depthValues = profile.Points
            .Select(static point => ValueOrNull(point.Z))
            .OfType<double>()
            .ToList();
        if (depthValues.Count == 0)
        {
            return 0d;
        }

        var pointScore = Math.Clamp(depthValues.Count / (double)expectedPointCount * 100d, 0d, 100d);
        var sampleScore = ScoreCount(profile.SampleCount, StrongContourShapeSampleCount);
        var weightScore = Math.Clamp(profile.TotalWeight / StrongContourShapeSampleCount * 100d, 0d, 100d);
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

        return Round(Math.Clamp(100d - standardDeviations.Average() / 0.050d * 100d, 0d, 100d));
    }

    private static SurfaceGeometryProfileScore ScoreSurfaceGeometryProfile(PersonalFaceContourShapeProfile profile)
    {
        if (!profile.HasProfile)
        {
            return SurfaceGeometryProfileScore.Waiting(profile.Label);
        }

        var points = profile.Points
            .Select(static point => new SurfaceGeometryPoint(
                ValueOrNull(point.X),
                ValueOrNull(point.Y),
                ValueOrNull(point.Z)))
            .Where(static point => point.X.HasValue && point.Y.HasValue && point.Z.HasValue)
            .Select(static point => new SurfaceGeometryPoint(point.X!.Value, point.Y!.Value, point.Z!.Value))
            .ToList();
        if (points.Count < 3)
        {
            return SurfaceGeometryProfileScore.Waiting(profile.Label);
        }

        var centerX = points.Average(static point => point.X!.Value);
        var centerY = points.Average(static point => point.Y!.Value);
        var centerZ = points.Average(static point => point.Z!.Value);
        var ordered = points
            .OrderBy(point => Math.Atan2(point.Y!.Value - centerY, point.X!.Value - centerX))
            .ThenByDescending(point =>
            {
                var dx = point.X!.Value - centerX;
                var dy = point.Y!.Value - centerY;
                return dx * dx + dy * dy;
            })
            .ToList();
        var totalArea = 0d;
        var weightedNormalX = 0d;
        var weightedNormalY = 0d;
        var weightedNormalZ = 0d;
        for (var index = 0; index < ordered.Count; index++)
        {
            var next = (index + 1) % ordered.Count;
            var p1 = ordered[index];
            var p2 = ordered[next];
            var ux = p1.X!.Value - centerX;
            var uy = p1.Y!.Value - centerY;
            var uz = p1.Z!.Value - centerZ;
            var vx = p2.X!.Value - centerX;
            var vy = p2.Y!.Value - centerY;
            var vz = p2.Z!.Value - centerZ;
            var nx = uy * vz - uz * vy;
            var ny = uz * vx - ux * vz;
            var nz = ux * vy - uy * vx;
            var normalMagnitude = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (normalMagnitude <= 0d)
            {
                continue;
            }

            var area = normalMagnitude / 2d;
            totalArea += area;
            weightedNormalX += nx / normalMagnitude * area;
            weightedNormalY += ny / normalMagnitude * area;
            weightedNormalZ += nz / normalMagnitude * area;
        }

        if (totalArea <= 0d)
        {
            return new SurfaceGeometryProfileScore(true, profile.Label, 0d, 0d, "review degenerate patch");
        }

        var weightedMagnitude = Math.Sqrt(weightedNormalX * weightedNormalX + weightedNormalY * weightedNormalY + weightedNormalZ * weightedNormalZ);
        var normalConsistency = Round(Math.Clamp(weightedMagnitude / totalArea * 100d, 0d, 100d));
        var depthValues = ordered.Select(static point => point.Z!.Value).ToList();
        var depthRelief = depthValues.Max() - depthValues.Min();
        var depthHealth = ScoreContourDepthProfile(profile);
        var triangleHealth = Math.Clamp((ordered.Count - 2d) / 6d * 100d, 0d, 100d);
        var areaHealth = Math.Clamp(totalArea / 0.0008d * 100d, 0d, 100d);
        var reliefHealth = Math.Clamp(depthRelief / 0.012d * 100d, 0d, 100d);
        var health = Round(
            normalConsistency * 0.36d
            + areaHealth * 0.16d
            + triangleHealth * 0.12d
            + depthHealth * 0.26d
            + reliefHealth * 0.10d);
        var status = normalConsistency < 35d
            ? "review thin/warped patch"
            : normalConsistency < 55d
                ? "review uneven surface"
                : totalArea < 0.0008d
                    ? "small patch"
                    : ordered.Count < 6
                        ? "low triangle coverage"
                        : health >= 80d ? "coherent surface" : "usable surface";
        return new SurfaceGeometryProfileScore(true, profile.Label, health, normalConsistency, status);
    }

    private static List<PersonalFacePoseBucketProfile> NormalizePoseBuckets(
        IReadOnlyList<PersonalFacePoseBucketProfile>? buckets)
    {
        var byId = (buckets ?? [])
            .Where(static bucket => !string.IsNullOrWhiteSpace(bucket.BucketId))
            .GroupBy(static bucket => bucket.BucketId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        return PersonalFacePoseBuckets.Definitions
            .Select(definition => byId.TryGetValue(definition.BucketId, out var bucket)
                ? bucket
                : EmptyPoseBucket(definition))
            .ToList();
    }

    private static PersonalFacePoseBucketProfile EmptyPoseBucket(PersonalFacePoseBucketDefinition definition)
    {
        return new PersonalFacePoseBucketProfile
        {
            BucketId = definition.BucketId,
            Label = definition.Label,
            Description = definition.Description,
            CaptureInstruction = definition.CaptureInstruction,
            PrimaryNeutralReference = definition.PrimaryNeutralReference,
            RequiredForAvatarCoverage = definition.RequiredForAvatarCoverage
        };
    }

    private static double ScorePoseBucketCoverage(IReadOnlyList<PersonalFacePoseBucketProfile> buckets)
    {
        var required = buckets
            .Where(static bucket => bucket.RequiredForAvatarCoverage)
            .ToList();
        if (required.Count == 0)
        {
            return 0d;
        }

        return Round(required.Average(ScorePoseBucket));
    }

    private static double ScorePoseBucket(PersonalFacePoseBucketProfile bucket)
    {
        var sampleScore = ScoreCount(bucket.SampleCount, StrongPoseBucketSampleCount);
        var weightScore = Round(Math.Clamp(bucket.TotalWeight / StrongPoseBucketWeight * 100d, 0d, 100d));
        var identityScore = bucket.HasIdentityProfile
            ? Average(
                ScoreIdentityDistribution(bucket.FaceAspectRatio),
                ScoreIdentityDistribution(bucket.EyeMidlineXToFaceWidth),
                ScoreIdentityDistribution(bucket.MouthCenterXToFaceWidth),
                ScoreIdentityDistribution(bucket.EyeToMouthXOffsetToFaceWidth),
                ScoreIdentityDistribution(bucket.InterEyeDistanceToFaceWidth),
                ScoreIdentityDistribution(bucket.MouthWidthToFaceWidth))
            : 0d;
        var reliability = Average(
            Math.Clamp(bucket.AverageFaceReliabilityPercent, 0d, 100d),
            Math.Clamp(bucket.AverageEyeReliabilityPercent, 0d, 100d),
            Math.Clamp(bucket.AverageMouthReliabilityPercent, 0d, 100d));
        if (bucket.SampleCount <= 0)
        {
            reliability = 0d;
        }

        return Round(sampleScore * 0.40d + weightScore * 0.25d + identityScore * 0.20d + reliability * 0.15d);
    }

    private static double ScoreEyeBehindGlassesTrust(
        PersonalFaceModel faceModel,
        PersonalFaceCorpusReadiness readiness)
    {
        var leftShape = ScoreContourShapeProfile(faceModel.LeftEyeShape);
        var rightShape = ScoreContourShapeProfile(faceModel.RightEyeShape);
        var shapeScore = Average(leftShape, rightShape);
        var eyeEvidence = ScoreOptionalPercent(readiness.AverageCaptureQualityEyeScorePercent, fallback: faceModel.AverageEyeReliabilityPercent);
        var glassesEvidence = ScoreOptionalPercent(readiness.AverageCaptureQualityGlassesScorePercent, fallback: 62d);
        var directEyeRate = Average(
            ScoreDirectRate(readiness.EyeReconstructedRate),
            ScoreDirectRate(readiness.PossibleOneEyeArtifactRate),
            ScoreDirectRate(readiness.EyeArtifactSuppressedRate));
        var openingRangeScore = ScoreRange(readiness.EyeOpeningRange, StrongEyeOpeningRange);

        return Round(
            shapeScore * 0.24d
            + eyeEvidence * 0.22d
            + glassesEvidence * 0.14d
            + directEyeRate * 0.12d
            + readiness.EyeApertureReliabilityHealthPercent * 0.16d
            + openingRangeScore * 0.12d);
    }

    private static double ScoreMouthJawTrust(
        PersonalFaceModel faceModel,
        PersonalFaceCorpusReadiness readiness)
    {
        var shapeScore = Average(
            ScoreContourShapeProfile(faceModel.OuterLipShape),
            ScoreContourShapeProfile(faceModel.InnerLipShape),
            ScoreContourShapeProfile(faceModel.JawShape));
        var mouthEvidence = ScoreOptionalPercent(readiness.AverageCaptureQualityMouthScorePercent, fallback: faceModel.AverageMouthReliabilityPercent);
        var directMouthRate = ScoreDirectRate(readiness.MouthReconstructedRate);
        var anchorScore = readiness.MouthVerticalAnchorHealthPercent;
        var expressionRangeScore = Average(
            ScoreRange(readiness.MouthOpeningRange, StrongMouthOpeningRange),
            ScoreRange(readiness.JawDroopRange, StrongJawDroopRange));

        return Round(
            shapeScore * 0.26d
            + mouthEvidence * 0.24d
            + directMouthRate * 0.18d
            + anchorScore * 0.16d
            + expressionRangeScore * 0.16d);
    }

    private static double ScoreOptionalPercent(double? value, double fallback)
    {
        return value is double number
            ? Round(Math.Clamp(number, 0d, 100d))
            : Round(Math.Clamp(fallback, 0d, 100d));
    }

    private static double ScoreDirectRate(double? reconstructedOrSuppressedRate)
    {
        return reconstructedOrSuppressedRate is double rate
            ? Round(Math.Clamp((1d - rate) * 100d, 0d, 100d))
            : 0d;
    }

    private static double? Range(PersonalMetricDistribution distribution)
    {
        return distribution.Minimum is double min && distribution.Maximum is double max && max >= min
            ? Round(max - min)
            : null;
    }

    private static bool IsSuspiciousMouthAnchor(double mouthCenterYToFaceHeight, double eyeToMouthYDistanceToFaceHeight)
    {
        return mouthCenterYToFaceHeight < MinimumPlausibleMouthCenterYToFaceHeight
            || eyeToMouthYDistanceToFaceHeight < MinimumPlausibleEyeToMouthYDistanceToFaceHeight;
    }

    private static double? ValueOrNull(PersonalMetricDistribution distribution)
    {
        var value = distribution.ExponentialMovingAverage ?? distribution.Average;
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value
            : null;
    }

    private static double? Rate(int count, int total)
    {
        return total <= 0 ? null : Round(count / (double)total);
    }

    private static double RateValue(int count, int total)
    {
        return total <= 0 ? 0d : Round(count / (double)total);
    }

    private static double? AverageOptional(IEnumerable<double?> values)
    {
        var valid = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();
        return valid.Count == 0 ? null : Round(valid.Average());
    }

    private static double? MinimumOptional(IEnumerable<double?> values)
    {
        var valid = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToList();
        return valid.Count == 0 ? null : Round(valid.Min());
    }

    private static string FormatPercent(double? rate)
    {
        return rate is double value ? value.ToString("P0") : "n/a";
    }

    private static string FormatPercentValue(double? percent)
    {
        return percent is double value ? $"{value:0.#}%" : "n/a";
    }

    private static double Average(params double[] values)
    {
        return values.Length == 0 ? 0d : Round(values.Average());
    }

    private static DateTime Max(DateTime first, DateTime second)
    {
        return first >= second ? first : second;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed record MouthAnchorSample(double MouthCenterYToFaceHeight, double EyeToMouthYDistanceToFaceHeight);

    private sealed record SurfaceGeometryPoint(double? X, double? Y, double? Z);

    private sealed record SurfaceGeometryProfileScore(
        bool HasPatch,
        string Label,
        double HealthPercent,
        double NormalConsistencyPercent,
        string Status)
    {
        public static SurfaceGeometryProfileScore Waiting(string label)
        {
            return new SurfaceGeometryProfileScore(false, label, 0d, 0d, "waiting");
        }
    }
}
