using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarTrainingPackageBuilder
{
    public MeasurementAvatarTrainingPackage Build(
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness readiness,
        FaceReconstructionSubjectGate subjectGate,
        long measurementJournalBytes,
        long measurementBudgetBytes = PersonalFaceMeasurementJournal.DefaultBudgetBytes,
        PersonalFaceCollectionAudit? collectionAudit = null,
        FaceReconstructionLaneStatus? reconstructionLane = null)
    {
        ArgumentNullException.ThrowIfNull(faceModel);
        ArgumentNullException.ThrowIfNull(motionModel);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(subjectGate);

        var package = new MeasurementAvatarTrainingPackage
        {
            CreatedAtUtc = faceModel.CreatedAtUtc != default ? faceModel.CreatedAtUtc : DateTime.UtcNow,
            UpdatedAtUtc = Max(faceModel.UpdatedAtUtc, motionModel.UpdatedAtUtc, readiness.UpdatedAtUtc),
            SubjectId = faceModel.SubjectId,
            SubjectDisplayName = faceModel.SubjectDisplayName,
            SubjectCollectionMode = faceModel.SubjectCollectionMode,
            UnknownSubjectPolicy = faceModel.UnknownSubjectPolicy,
            IdentityGatePolicy = faceModel.IdentityGatePolicy,
            SubjectGate = subjectGate,
            ObservedSamples = faceModel.ObservedSamples,
            AcceptedBaselineSamples = faceModel.AcceptedSamples,
            AcceptedSampleWeight = Round(faceModel.AcceptedSampleWeight),
            LearningStability = faceModel.LearningStability,
            MotionUsableObservations = motionModel.UsableObservationCount,
            MotionPairs = motionModel.MotionPairCount,
            IdentitySignatureSamples = faceModel.IdentitySignatureSamples,
            MeasurementJournalBytes = Math.Max(0L, measurementJournalBytes),
            MeasurementBudgetBytes = Math.Max(1L, measurementBudgetBytes),
            ReconstructionLane = reconstructionLane ?? FaceReconstructionLaneStatus.Waiting,
            Readiness = new MeasurementAvatarReadinessScores
            {
                OverallReadinessPercent = Round(readiness.OverallReadinessPercent),
                BaselineCoveragePercent = Round(readiness.BaselineCoveragePercent),
                LearningStabilityCoveragePercent = Round(readiness.LearningStabilityCoveragePercent),
                MotionCoveragePercent = Round(readiness.MotionCoveragePercent),
                PoseCoveragePercent = Round(readiness.PoseCoveragePercent),
                PoseBucketCoveragePercent = Round(readiness.PoseBucketCoveragePercent),
                DistanceCoveragePercent = Round(readiness.DistanceCoveragePercent),
                ZDistanceCoveragePercent = Round(readiness.ZDistanceCoveragePercent),
                ZDistanceEvidenceHealthPercent = Round(readiness.ZDistanceEvidenceHealthPercent),
                ARotationAroundXCoveragePercent = Round(readiness.ARotationAroundXCoveragePercent),
                BRotationAroundYCoveragePercent = Round(readiness.BRotationAroundYCoveragePercent),
                CRotationAroundZCoveragePercent = Round(readiness.CRotationAroundZCoveragePercent),
                XYZABCCoveragePercent = Round(readiness.XYZABCCoveragePercent),
                ExpressionCoveragePercent = Round(readiness.ExpressionCoveragePercent),
                IdentityCoveragePercent = Round(readiness.IdentityCoveragePercent),
                IdentitySessionHealthPercent = Round(readiness.IdentitySessionHealthPercent),
                IdentitySessionAuditStage = readiness.IdentitySessionAuditStage,
                IdentitySessionAuditStatus = readiness.IdentitySessionAuditStatus,
                ContourShapeCoveragePercent = Round(readiness.ContourShapeCoveragePercent),
                ContourDepthProfileHealthPercent = Round(readiness.ContourDepthProfileHealthPercent),
                SurfaceShapeCoveragePercent = Round(readiness.SurfaceShapeCoveragePercent),
                SurfaceDepthProfileHealthPercent = Round(readiness.SurfaceDepthProfileHealthPercent),
                SurfaceGeometryHealthPercent = Round(readiness.SurfaceGeometryHealthPercent),
                EyeBehindGlassesTrustPercent = Round(readiness.EyeBehindGlassesTrustPercent),
                MouthJawTrustPercent = Round(readiness.MouthJawTrustPercent),
                DirectFeatureMeasurementTrustPercent = Round(readiness.DirectFeatureMeasurementTrustPercent),
                ApertureConsistencyHealthPercent = Round(readiness.ApertureConsistencyHealthPercent),
                EyeApertureReliabilityHealthPercent = Round(readiness.EyeApertureReliabilityHealthPercent),
                QualityCoveragePercent = Round(readiness.QualityCoveragePercent),
                CaptureQualityCoveragePercent = Round(readiness.CaptureQualityCoveragePercent),
                StorageHealthPercent = Round(readiness.StorageHealthPercent),
                DataAuditHealthPercent = Round(readiness.DataAuditHealthPercent),
                PoseEstimationHealthPercent = Round(readiness.PoseEstimationHealthPercent),
                FeatureAnchoringHealthPercent = Round(readiness.FeatureAnchoringHealthPercent),
                PoseExplainedFeatureMotionHealthPercent = Round(readiness.PoseExplainedFeatureMotionHealthPercent),
                MouthVerticalAnchorHealthPercent = Round(readiness.MouthVerticalAnchorHealthPercent),
                PoseBucketConsistencyHealthPercent = Round(readiness.PoseBucketConsistencyHealthPercent),
                JawDroopScaleHealthPercent = Round(readiness.JawDroopScaleHealthPercent),
                MeasurementJournalCoveragePercent = Round(readiness.MeasurementJournalCoveragePercent)
            }
        };

        package.MeasurementBudgetUsedPercent = Round(100d * package.MeasurementJournalBytes / package.MeasurementBudgetBytes);
        package.MeasurementContributionPercent = CalculateMeasurementContribution(faceModel);
        package.TemplatePriorContributionPercent = Round(100d - package.MeasurementContributionPercent);
        package.CanUseForAvatarTraining = CanUseForAvatarTraining(faceModel, motionModel, subjectGate);
        package.TrainingDecision = BuildTrainingDecision(package, subjectGate, readiness);
        package.PoseCoverageProfile = readiness.PoseBuckets.Count > 0
            ? readiness.PoseBuckets
            : faceModel.PoseBuckets;
        package.PoseBucketConsistency = readiness.PoseBucketConsistency;
        package.ApertureConsistency = readiness.ApertureConsistency;

        AddNeutralFaceProfile(package, faceModel);
        AddMotionProfile(package, motionModel);
        AddIdentityProfile(package, faceModel);
        AddContourShapeProfiles(package, faceModel);
        AddQualityProfile(package, faceModel, motionModel, readiness, collectionAudit);
        AddSourceArtifacts(package);
        AddGuidance(package, readiness, motionModel, collectionAudit);
        return package;
    }

    private static bool CanUseForAvatarTraining(
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        FaceReconstructionSubjectGate subjectGate)
    {
        return string.Equals(subjectGate.GateDecision, "accepted", StringComparison.OrdinalIgnoreCase)
            && faceModel.AcceptedSamples > 0
            && motionModel.UsableObservationCount > 0;
    }

    private static string BuildTrainingDecision(
        MeasurementAvatarTrainingPackage package,
        FaceReconstructionSubjectGate subjectGate,
        PersonalFaceCorpusReadiness readiness)
    {
        if (!string.Equals(subjectGate.GateDecision, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            return $"paused by subject gate: {subjectGate.Reason}";
        }

        if (package.AcceptedBaselineSamples <= 0)
        {
            return "waiting for accepted subject-confirmed baseline measurements";
        }

        if (package.MotionUsableObservations <= 0)
        {
            return "neutral profile available; motion profile waiting for accepted measurement pairs";
        }

        return readiness.OverallReadinessPercent switch
        {
            >= 85d => "strong measurement package for avatar and motion consumers",
            >= 70d => "useful measurement package; continue broadening pose, distance, and expression coverage",
            >= 45d => "warming measurement package; useful for preview and early integration only",
            _ => "early measurement package; collect more subject-confirmed data before avatar fitting"
        };
    }

    private static void AddNeutralFaceProfile(MeasurementAvatarTrainingPackage package, PersonalFaceModel model)
    {
        Add(package.NeutralFaceProfile, "FaceCenterX", "Face center X", "normalized frame ratio", "Keeps expected desk position separate from face shape.", model.FaceCenterX);
        Add(package.NeutralFaceProfile, "FaceCenterY", "Face center Y", "normalized frame ratio", "Keeps expected desk position separate from face shape.", model.FaceCenterY);
        Add(package.NeutralFaceProfile, "FaceWidth", "Face width", "normalized frame ratio", "Seeds normalized face scale and distance changes.", model.FaceWidth);
        Add(package.NeutralFaceProfile, "FaceHeight", "Face height", "normalized frame ratio", "Seeds normalized face scale and distance changes.", model.FaceHeight);
        Add(package.NeutralFaceProfile, "ZApparentDistanceUnits", "Z apparent distance", "apparent face units", "Seeds camera-space toward/away distance without claiming physical inches unless calibrated.", model.ZApparentDistanceUnits);
        Add(package.NeutralFaceProfile, "ZRelativeToReference", "Z relative to reference", "ratio", "Keeps close/far movement separate from face-shape changes once a learned reference scale exists.", model.ZRelativeToReference);
        Add(package.NeutralFaceProfile, "ZConfidencePercent", "Z confidence", "percent", "Rates whether Z came from reliable eye span, camera/FOV, learned reference, or calibrated distance evidence.", model.ZConfidencePercent);
        Add(package.NeutralFaceProfile, "ARotationAroundXDegrees", "A rotation around X", "degrees", "Seeds neutral pose and pose range.", model.HeadPitchDegrees);
        Add(package.NeutralFaceProfile, "BRotationAroundYDegrees", "B rotation around Y", "degrees", "Seeds neutral pose and pose range.", model.HeadYawDegrees);
        Add(package.NeutralFaceProfile, "CRotationAroundZDegrees", "C rotation around Z", "degrees", "Seeds neutral pose and pose range.", model.HeadRollDegrees);
        Add(package.NeutralFaceProfile, "LeftEyeOpeningRatio", "Left eye opening", "eye height / eye width", "Seeds eyelid aperture for the left eye.", model.LeftEyeOpeningRatio);
        Add(package.NeutralFaceProfile, "RightEyeOpeningRatio", "Right eye opening", "eye height / eye width", "Seeds eyelid aperture for the right eye.", model.RightEyeOpeningRatio);
        Add(package.NeutralFaceProfile, "AverageEyeOpeningRatio", "Average eye opening", "eye height / eye width", "Primary neutral eyelid aperture signal.", model.AverageEyeOpeningRatio);
        Add(package.NeutralFaceProfile, "EyeAgreementPercent", "Eye agreement", "percent", "Flags whether left/right eyelid evidence is balanced.", model.EyeAgreementPercent);
        Add(package.NeutralFaceProfile, "MouthOpeningRatio", "Mouth opening", "mouth height / mouth width", "Seeds neutral lip aperture.", model.MouthOpeningRatio);
        Add(package.NeutralFaceProfile, "JawDroopRatio", "Jaw droop", "jaw offset / face width", "Seeds jaw relaxation and droop reference.", model.JawDroopRatio);
        Add(package.NeutralFaceProfile, "AverageBrowHeightRatio", "Average brow height", "brow-eye distance / face width", "Seeds eyebrow resting position for expression and eye-region stability.", model.AverageBrowHeightRatio);
        Add(package.NeutralFaceProfile, "BrowAsymmetryPercent", "Brow asymmetry", "percent", "Flags whether left/right brow positions are balanced.", model.BrowAsymmetryPercent);
        Add(package.NeutralFaceProfile, "MediaPipeAverageEyeBlinkPercent", "MediaPipe blink", "percent", "Dense-landmark corroboration for eyelid closure.", model.MediaPipeAverageEyeBlinkPercent);
        Add(package.NeutralFaceProfile, "MediaPipeJawOpenPercent", "MediaPipe jaw open", "percent", "Dense-landmark corroboration for jaw opening.", model.MediaPipeJawOpenPercent);
        Add(package.NeutralFaceProfile, "MediaPipeMouthClosePercent", "MediaPipe mouth close", "percent", "Dense-landmark corroboration for closed-mouth state.", model.MediaPipeMouthClosePercent);
    }

    private static void AddMotionProfile(MeasurementAvatarTrainingPackage package, PersonalFaceMotionModel model)
    {
        Add(package.MotionProfile, "EyeClosingVelocityPerSecond", "Eye closing velocity", "ratio / second", "Animation prior for eyelids closing over time.", model.EyeClosingVelocityPerSecond);
        Add(package.MotionProfile, "EyeOpeningVelocityPerSecond", "Eye opening velocity", "ratio / second", "Animation prior for eyelid recovery.", model.EyeOpeningVelocityPerSecond);
        Add(package.MotionProfile, "MouthOpeningVelocityPerSecond", "Mouth opening velocity", "ratio / second", "Animation prior for lip aperture opening.", model.MouthOpeningVelocityPerSecond);
        Add(package.MotionProfile, "MouthClosingVelocityPerSecond", "Mouth closing velocity", "ratio / second", "Animation prior for lip aperture recovery.", model.MouthClosingVelocityPerSecond);
        Add(package.MotionProfile, "JawDroopVelocityPerSecond", "Jaw droop velocity", "ratio / second", "Animation prior for jaw relaxation/droop onset.", model.JawDroopVelocityPerSecond);
        Add(package.MotionProfile, "JawRecoveryVelocityPerSecond", "Jaw recovery velocity", "ratio / second", "Animation prior for jaw recovery.", model.JawRecoveryVelocityPerSecond);
        Add(package.MotionProfile, "BrowRaiseVelocityPerSecond", "Brow raise velocity", "ratio / second", "Animation prior for brow lift.", model.BrowRaiseVelocityPerSecond);
        Add(package.MotionProfile, "BrowLowerVelocityPerSecond", "Brow lower velocity", "ratio / second", "Animation prior for brow lowering.", model.BrowLowerVelocityPerSecond);
        Add(package.MotionProfile, "ARotationAroundXVelocityDegreesPerSecond", "A rotation around X velocity", "degrees / second", "Animation prior for up/down head motion.", model.HeadPitchVelocityDegreesPerSecond);
        Add(package.MotionProfile, "BRotationAroundYVelocityDegreesPerSecond", "B rotation around Y velocity", "degrees / second", "Animation prior for side-to-side head motion.", model.HeadYawVelocityDegreesPerSecond);
        Add(package.MotionProfile, "CRotationAroundZVelocityDegreesPerSecond", "C rotation around Z velocity", "degrees / second", "Animation prior for C-axis head tilt motion.", model.HeadRollVelocityDegreesPerSecond);
        Add(package.MotionProfile, "ZApparentVelocityUnitsPerSecond", "Z apparent velocity", "apparent face units / second", "Animation prior for leaning closer/farther without rewriting facial feature positions.", model.ZApparentVelocityUnitsPerSecond);
        AddValue(package.MotionProfile, "EyeClosingWithMouthOpeningRate", "Eye closing with mouth opening", "rate", "Coupling hint for sleepy facial state transitions.", model.EyeClosingWithMouthOpeningRate, model.MotionPairCount);
        AddValue(package.MotionProfile, "EyeClosingWithJawDroopRate", "Eye closing with jaw droop", "rate", "Coupling hint for eyelid closure with jaw relaxation.", model.EyeClosingWithJawDroopRate, model.MotionPairCount);
        AddValue(package.MotionProfile, "EyeClosingWithBrowLoweringRate", "Eye closing with brow lowering", "rate", "Coupling hint for eyelid closure with brow motion.", model.EyeClosingWithBrowLoweringRate, model.MotionPairCount);
        AddValue(package.MotionProfile, "MouthOpeningWithJawDroopRate", "Mouth opening with jaw droop", "rate", "Coupling hint for lip opening with jaw relaxation.", model.MouthOpeningWithJawDroopRate, model.MotionPairCount);
    }

    private static void AddIdentityProfile(MeasurementAvatarTrainingPackage package, PersonalFaceModel model)
    {
        Add(package.IdentityProfile, "FaceAspectRatio", "Face aspect ratio", "face height / face width", "Measurement-only owner signature; helps avoid mixed-person learning.", model.FaceAspectRatio);
        Add(package.IdentityProfile, "EyeMidlineXToFaceWidth", "Eye horizontal position", "eye midpoint X / face width", "Measurement-only owner signature and feature-slide guard.", model.EyeMidlineXToFaceWidth);
        Add(package.IdentityProfile, "MouthCenterXToFaceWidth", "Mouth horizontal position", "mouth center X / face width", "Measurement-only owner signature and mouth-lock guard.", model.MouthCenterXToFaceWidth);
        Add(package.IdentityProfile, "EyeToMouthXOffsetToFaceWidth", "Eye-mouth horizontal offset", "absolute eye-to-mouth X offset / face width", "Measurement-only feature anchoring check; catches eyes or mouth sliding inside the head.", model.EyeToMouthXOffsetToFaceWidth);
        Add(package.IdentityProfile, "InterEyeDistanceToFaceWidth", "Eye spacing", "inter-eye distance / face width", "Measurement-only owner signature; helps avoid mixed-person learning.", model.InterEyeDistanceToFaceWidth);
        Add(package.IdentityProfile, "LeftEyeWidthToFaceWidth", "Left eye width", "eye width / face width", "Measurement-only owner signature; helps avoid mixed-person learning.", model.LeftEyeWidthToFaceWidth);
        Add(package.IdentityProfile, "RightEyeWidthToFaceWidth", "Right eye width", "eye width / face width", "Measurement-only owner signature; helps avoid mixed-person learning.", model.RightEyeWidthToFaceWidth);
        Add(package.IdentityProfile, "MouthWidthToFaceWidth", "Mouth width", "mouth width / face width", "Measurement-only owner signature; helps avoid mixed-person learning.", model.MouthWidthToFaceWidth);
        Add(package.IdentityProfile, "EyeMidlineYToFaceHeight", "Eye midline height", "eye midline Y / face height", "Measurement-only owner signature; helps avoid mixed-person learning.", model.EyeMidlineYToFaceHeight);
        Add(package.IdentityProfile, "MouthCenterYToFaceHeight", "Mouth center height", "mouth center Y / face height", "Measurement-only owner signature; helps avoid mixed-person learning.", model.MouthCenterYToFaceHeight);
        Add(package.IdentityProfile, "EyeToMouthYDistanceToFaceHeight", "Eye-to-mouth distance", "vertical distance / face height", "Measurement-only owner signature; helps avoid mixed-person learning.", model.EyeToMouthYDistanceToFaceHeight);
    }

    private static void AddContourShapeProfiles(MeasurementAvatarTrainingPackage package, PersonalFaceModel model)
    {
        AddContourShapeProfile(package, model.LeftEyeShape);
        AddContourShapeProfile(package, model.RightEyeShape);
        AddContourShapeProfile(package, model.OuterLipShape);
        AddContourShapeProfile(package, model.InnerLipShape);
        AddContourShapeProfile(package, model.JawShape);
        AddContourShapeProfile(package, model.LeftBrowShape);
        AddContourShapeProfile(package, model.RightBrowShape);
        AddContourShapeProfile(package, model.NoseBridgeShape);
        AddContourShapeProfile(package, model.NoseBaseShape);
        AddContourShapeProfile(package, model.LeftCheekSurface);
        AddContourShapeProfile(package, model.RightCheekSurface);
        AddContourShapeProfile(package, model.ForeheadSurface);
    }

    private static void AddContourShapeProfile(MeasurementAvatarTrainingPackage package, PersonalFaceContourShapeProfile profile)
    {
        if (profile.HasProfile)
        {
            package.ContourShapeProfiles[profile.FeatureId] = profile;
        }
    }

    private static void AddQualityProfile(
        MeasurementAvatarTrainingPackage package,
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness readiness,
        PersonalFaceCollectionAudit? collectionAudit)
    {
        AddValue(package.QualityProfile, "AverageFaceReliabilityPercent", "Face reliability", "percent", "Overall lock quality for accepted measurements.", faceModel.AverageFaceReliabilityPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "AverageFaceContinuityPercent", "Face continuity", "percent", "Temporal face lock continuity.", faceModel.AverageFaceContinuityPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "AverageEyeReliabilityPercent", "Eye reliability", "percent", "Eyelid measurement reliability.", faceModel.AverageEyeReliabilityPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "AverageMouthReliabilityPercent", "Mouth reliability", "percent", "Lip/jaw measurement reliability.", faceModel.AverageMouthReliabilityPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "LearningAnchorPercent", "Learning anchor", "percent", "How strongly the slow weighted model is anchored by accumulated accepted measurement weight.", faceModel.LearningStability.AnchorPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "MinimumTrackedDistributionWeight", "Weakest tracked weight", "weighted samples", "Smallest nonzero aggregate weight among learned face, eye, lip, jaw, identity, and shape distributions used to bound worst-case next-sample influence.", faceModel.LearningStability.MinimumTrackedDistributionWeight, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "MaximumNextSampleInfluencePercent", "Maximum next-sample influence", "percent", "Upper bound on how much one new high-quality measurement can move the weighted profile.", faceModel.LearningStability.MaximumNextSampleInfluencePercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "MaximumEventLikeNextSampleInfluencePercent", "Maximum event-like influence", "percent", "Upper bound on how much one accepted event-like measurement can move the weighted profile.", faceModel.LearningStability.MaximumEventLikeNextSampleInfluencePercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "IdentitySessionHealthPercent", "Identity session health", "percent", "Recent accepted-frame identity confidence and outlier rate; flags a session that may be drifting away from the learned subject.", readiness.IdentitySessionHealthPercent, readiness.RecentIdentityMeasurementSamples);
        AddValue(package.QualityProfile, "AverageRecentIdentityConfidencePercent", "Recent identity confidence", "percent", "Average measurement-only subject confidence across recent comparable accepted frames.", readiness.AverageRecentIdentityConfidencePercent, readiness.RecentIdentityMeasurementSamples);
        AddValue(package.QualityProfile, "RecentIdentityOutlierFrameRate", "Recent identity outlier frame rate", "rate", "Share of recent comparable accepted frames with one or more identity outlier features.", readiness.RecentIdentityOutlierFrameRate, readiness.RecentIdentityMeasurementSamples);
        AddValue(package.QualityProfile, "AverageObservationQualityPercent", "Motion observation quality", "percent", "Quality of observations used by the motion model.", motionModel.AverageObservationQualityPercent, motionModel.UsableObservationCount);
        AddValue(package.QualityProfile, "PoseBucketCoveragePercent", "Pose bucket coverage", "percent", "How much straight-on A, B, and C rotation evidence exists without mixing all poses into one identity average.", readiness.PoseBucketCoveragePercent, readiness.PoseBucketRequiredCount);
        AddValue(package.QualityProfile, "ApertureConsistencyHealthPercent", "Aperture consistency health", "percent", "Whether eye, mouth, and jaw aperture measurements agree with dense blink/jaw/mouth corroboration over recent accepted samples.", readiness.ApertureConsistencyHealthPercent, readiness.ApertureConsistency.EyeComparedSampleCount + readiness.ApertureConsistency.MouthComparedSampleCount + readiness.ApertureConsistency.JawComparedSampleCount);
        AddValue(package.QualityProfile, "EyeOpeningBlinkCorrelation", "Eye blink agreement", "correlation", "Eye opening should move opposite MediaPipe blink; wrong-direction correlation means behind-glasses eyelid evidence needs review.", readiness.ApertureConsistency.EyeOpeningBlinkCorrelation, readiness.ApertureConsistency.EyeComparedSampleCount);
        AddValue(package.QualityProfile, "MouthOpeningEvidenceCorrelation", "Mouth opening agreement", "correlation", "Lip opening should move with dense jaw-open or mouth-close-drop evidence.", readiness.ApertureConsistency.MouthOpeningEvidenceCorrelation, readiness.ApertureConsistency.MouthComparedSampleCount);
        AddValue(package.QualityProfile, "JawDroopEvidenceCorrelation", "Jaw droop agreement", "correlation", "Jaw droop should move with MediaPipe jaw-open evidence.", readiness.ApertureConsistency.JawDroopEvidenceCorrelation, readiness.ApertureConsistency.JawComparedSampleCount);
        AddValue(package.QualityProfile, "CaptureQualityCoveragePercent", "Capture quality coverage", "percent", "How often recent measurements pass the camera/face/glasses/storage gate.", readiness.CaptureQualityCoveragePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "AverageCaptureQualityScorePercent", "Average capture quality", "percent", "Average capture-quality score for recent measurement rows.", readiness.AverageCaptureQualityScorePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "MinimumCaptureQualityScorePercent", "Minimum capture quality", "percent", "Weakest recent capture-quality score; useful for finding unstable sessions.", readiness.MinimumCaptureQualityScorePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "CaptureQualityCanCollectRate", "Capture collectable rate", "rate", "Fraction of recent measurement rows allowed into long-term learning.", readiness.CaptureQualityCanCollectRate, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "CaptureQualityAvatarGradeRate", "Capture avatar-grade rate", "rate", "Fraction of recent measurement rows strong enough for avatar capture.", readiness.CaptureQualityAvatarGradeRate, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "AverageCaptureQualityEyeScorePercent", "Capture eye evidence", "percent", "Average eye-evidence score inside the capture-quality gate.", readiness.AverageCaptureQualityEyeScorePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "AverageCaptureQualityMouthScorePercent", "Capture mouth evidence", "percent", "Average mouth/jaw evidence score inside the capture-quality gate.", readiness.AverageCaptureQualityMouthScorePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "AverageCaptureQualityGlassesScorePercent", "Capture glasses score", "percent", "Average glasses/artifact score inside the capture-quality gate.", readiness.AverageCaptureQualityGlassesScorePercent, readiness.CaptureQualitySamples);
        AddValue(package.QualityProfile, "DataAuditHealthPercent", "Data audit health", "percent", "Whether the retained measurements look internally consistent for pose, feature anchoring, jaw scale, and journal traceability.", readiness.DataAuditHealthPercent, readiness.DataAuditFindings.Count);
        AddValue(package.QualityProfile, "EyeApertureReliabilityHealthPercent", "Eye aperture reliability health", "percent", "Flags glasses glare, one-eye artifacts, reconstructed eye frames, and weak left/right eye agreement before eyelid data is trusted.", readiness.EyeApertureReliabilityHealthPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "PoseEstimationHealthPercent", "Pose estimation health", "percent", "Flags dead A/B axes that can make turned-head data look like features sliding on the face.", readiness.PoseEstimationHealthPercent, readiness.DataAuditFindings.Count);
        AddValue(package.QualityProfile, "FeatureAnchoringHealthPercent", "Feature anchoring health", "percent", "Flags face-local eye/mouth proportions drifting more than expected.", readiness.FeatureAnchoringHealthPercent, readiness.DataAuditFindings.Count);
        AddValue(package.QualityProfile, "PoseExplainedFeatureMotionHealthPercent", "Pose-explained feature motion health", "percent", "Compares observed face-local feature motion against measured A/B/C rotation so head turns do not become face-shape rewrites.", readiness.PoseExplainedFeatureMotionHealthPercent, readiness.DataAuditFindings.Count);
        AddValue(package.QualityProfile, "PoseExplainedFeatureObservedRange", "Pose-explained observed feature range", "normalized face ratio", "Largest observed face-local eye/mouth/spacing range reviewed by the pose-explained feature audit.", readiness.PoseExplainedFeatureObservedRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "PoseExplainedFeatureExpectedRange", "Pose-explained expected feature range", "normalized face ratio", "Feature range allowed by measured A/B/C rotation before the audit starts suspecting sliding features.", readiness.PoseExplainedFeatureExpectedRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "MouthVerticalAnchorHealthPercent", "Mouth vertical anchor health", "percent", "Flags lip measurements that sit too high on the face or may be locked on the area under the nose.", readiness.MouthVerticalAnchorHealthPercent, readiness.MouthVerticalAnchorSamplesReviewed);
        AddValue(package.QualityProfile, "PoseBucketConsistencyHealthPercent", "Pose bucket consistency health", "percent", "Compares turned-head pose buckets with the front-neutral identity bucket so a head turn does not become a face-shape rewrite.", readiness.PoseBucketConsistencyHealthPercent, readiness.PoseBucketConsistency.ComparedPoseBucketCount);
        AddValue(package.QualityProfile, "JawDroopScaleHealthPercent", "Jaw droop scale health", "percent", "Flags jaw measurements that are saturated or should only be treated baseline-relatively.", readiness.JawDroopScaleHealthPercent, readiness.DataAuditFindings.Count);
        AddValue(package.QualityProfile, "MeasurementJournalCoveragePercent", "Journal coverage", "percent", "How much of the aggregate accepted model is represented by retained journal rows.", readiness.MeasurementJournalCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "ARotationAroundXRangeDegrees", "A rotation around X range", "degrees", "Confirms up/down head tilt is stored as pose instead of feature translation.", readiness.HeadPitchRangeDegrees, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "ARotationAroundXCoveragePercent", "A rotation around X coverage", "percent", "How much up/down A rotation variety exists for separating head tilt from face-shape changes.", readiness.ARotationAroundXCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "BRotationAroundYRangeDegrees", "B rotation around Y range", "degrees", "Confirms side-to-side head turns are stored as pose instead of 2D feature sliding.", readiness.HeadYawRangeDegrees, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "BRotationAroundYCoveragePercent", "B rotation around Y coverage", "percent", "How much left/right B rotation variety exists for separating head turns from feature sliding.", readiness.BRotationAroundYCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "CRotationAroundZRangeDegrees", "C rotation around Z range", "degrees", "Confirms C rotation is separated from A/B and not the only moving pose axis.", readiness.HeadRollRangeDegrees, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "CRotationAroundZCoveragePercent", "C rotation around Z coverage", "percent", "How much C rotation variety exists for separating head tilt from eyelid/lip measurements.", readiness.CRotationAroundZCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "FaceWidthRange", "Face width range", "normalized frame ratio", "Distance-change context for pose audits.", readiness.FaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "FaceHeightRange", "Face height range", "normalized frame ratio", "Distance-change context for pose audits.", readiness.FaceHeightRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "ZDistanceCoveragePercent", "Z distance coverage", "percent", "How much closer/farther camera-space distance variety exists for separating scale changes from face-shape changes.", readiness.ZDistanceCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "ZDistanceEvidenceHealthPercent", "Z evidence health", "percent", "Whether Z distance uses explicit eye-span/FOV/reference/calibration evidence instead of only face-fill fallback.", readiness.ZDistanceEvidenceHealthPercent, readiness.ZEstimateSamples);
        AddValue(package.QualityProfile, "ZApparentDistanceRange", "Z apparent distance range", "apparent face units", "Explicit apparent Z range from the head-pose estimator.", readiness.ZApparentDistanceRange, readiness.ZEstimateSamples);
        AddValue(package.QualityProfile, "ZRelativeToReferenceRange", "Z relative reference range", "ratio", "Close/far range relative to the learned face-scale reference.", readiness.ZRelativeToReferenceRange, readiness.ZEstimateSamples);
        AddValue(package.QualityProfile, "AverageZConfidencePercent", "Average Z confidence", "percent", "Average confidence of explicit Z estimates retained in the personal model.", readiness.AverageZConfidencePercent, readiness.ZEstimateSamples);
        AddValue(package.QualityProfile, "ZApparentOnlyRate", "Z apparent-only rate", "rate", "Fraction of Z samples that lacked calibration, camera/FOV, or learned-reference support.", readiness.ZApparentOnlyRate, readiness.ZEstimateSamples);
        AddValue(package.QualityProfile, "XYZABCCoveragePercent", "XYZABC coverage", "percent", "Balanced readiness across Z distance plus A/B/C rotation axes.", readiness.XYZABCCoveragePercent, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "ContourDepthProfileHealthPercent", "Contour Z profile health", "percent", "How much eye/lip/jaw contour data has repeatable face-local Z evidence instead of only 2D outline evidence.", readiness.ContourDepthProfileHealthPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "SurfaceDepthProfileHealthPercent", "Surface Z profile health", "percent", "How much brow/nose/cheek/forehead data has repeatable face-local Z evidence for non-flat surface reconstruction.", readiness.SurfaceDepthProfileHealthPercent, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "SurfaceGeometryHealthPercent", "Surface geometry health", "percent", "Whether learned eye/lip/jaw/brow/nose/cheek/forehead patches form coherent measured surface cells instead of folded or degenerate geometry.", readiness.SurfaceGeometryHealthPercent, readiness.SurfaceGeometryPatchCount);
        AddValue(package.QualityProfile, "EyeMidlineXToFaceWidthRange", "Eye horizontal drift", "normalized face ratio", "Eye midpoint drift can reveal eyes sliding across the head instead of head pose changing.", readiness.EyeMidlineXToFaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "MouthCenterXToFaceWidthRange", "Mouth horizontal drift", "normalized face ratio", "Mouth center drift can reveal a bad mouth lock or face-local feature slide.", readiness.MouthCenterXToFaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "EyeToMouthXOffsetToFaceWidthRange", "Eye-mouth horizontal drift", "normalized face ratio", "Tracks whether eye and mouth anchors move together instead of drifting apart.", readiness.EyeToMouthXOffsetToFaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "InterEyeDistanceToFaceWidthRange", "Eye spacing drift", "normalized face ratio", "Face-local drift can reveal features sliding across the head.", readiness.InterEyeDistanceToFaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "MouthWidthToFaceWidthRange", "Mouth width drift", "normalized face ratio", "Face-local mouth drift can reveal a bad mouth lock or head-pose fit.", readiness.MouthWidthToFaceWidthRange, readiness.RecentMeasurementSamplesReviewed);
        AddValue(package.QualityProfile, "FaceAspectRatioRange", "Face aspect drift", "normalized face ratio", "Large face-local aspect drift can indicate poor anchoring across pose/distance.", readiness.FaceAspectRatioRange, readiness.RecentMeasurementSamplesReviewed);
        Add(package.QualityProfile, "EyeGlarePercent", "Eye glare", "percent", "Glasses glare context for eyelid accuracy.", faceModel.EyeGlarePercent);
        Add(package.QualityProfile, "EyeContrastPercent", "Eye contrast", "percent", "Eye-region contrast context for eyelid accuracy.", faceModel.EyeContrastPercent);
        Add(package.QualityProfile, "EyeSharpnessPercent", "Eye sharpness", "percent", "Eye-region sharpness context for eyelid accuracy.", faceModel.EyeSharpnessPercent);
        AddValue(package.QualityProfile, "EyeArtifactSuppressedRate", "Eye artifact suppressed", "rate", "How often the tracker suppressed likely glasses/contour artifacts.", readiness.EyeArtifactSuppressedRate, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "PossibleOneEyeArtifactRate", "Possible one-eye artifact", "rate", "How often one eye looked like a glasses/contour artifact rather than a trustworthy eyelid measurement.", readiness.PossibleOneEyeArtifactRate, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "EyeReconstructedRate", "Eye reconstructed", "rate", "How often temporal reconstruction filled one or both eyes.", readiness.EyeReconstructedRate, faceModel.AcceptedSamples);
        AddValue(package.QualityProfile, "EyeAgreementAveragePercent", "Eye agreement average", "percent", "Average left/right eye agreement used to catch one-eye false locks behind glasses.", readiness.EyeAgreementAveragePercent, faceModel.EyeAgreementPercent.SampleCount);
        AddValue(package.QualityProfile, "EyeAgreementMinimumPercent", "Eye agreement minimum", "percent", "Worst retained left/right eye agreement, useful for finding intermittent glasses artifacts.", readiness.EyeAgreementMinimumPercent, faceModel.EyeAgreementPercent.SampleCount);
        AddValue(package.QualityProfile, "MouthReconstructedRate", "Mouth reconstructed", "rate", "How often temporal reconstruction filled mouth evidence.", readiness.MouthReconstructedRate, faceModel.AcceptedSamples);

        if (collectionAudit is null)
        {
            return;
        }

        AddValue(package.QualityProfile, "CollectionFramesReviewed", "Collection frames reviewed", "frames", "Number of frame-level audit observations behind this package.", collectionAudit.TotalFramesReviewed, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "PersonalModelAcceptedRate", "Personal model accepted rate", "rate", "Fraction of reviewed frames accepted into the subject-gated measurement model.", collectionAudit.PersonalModelAcceptedRate, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "CollectionAvatarGradeRate", "Collection avatar-grade rate", "rate", "Fraction of reviewed frames strong enough for avatar capture.", collectionAudit.CaptureQualityAvatarGradeRate, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "IdentityMeasuredFrames", "Identity measured frames", "frames", "Frame-level identity-signature checks available in the audit window.", collectionAudit.IdentityMeasuredFrames, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "IdentityAutoGateReadyFrames", "Identity auto-gate ready frames", "frames", "Frames where the measurement-only identity gate had enough history to judge the subject.", collectionAudit.IdentityAutoGateReadyFrames, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "SubjectMismatchGateFrames", "Subject mismatch frames", "frames", "Frames rejected because the measured face did not match the enrolled subject.", collectionAudit.SubjectMismatchGateFrames, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "TrackingAuditHoldFrames", "Tracking audit holds", "frames", "Frames held because pose or feature anchoring audit found high-risk tracking consistency problems.", collectionAudit.TrackingAuditHoldFrames, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "IdentityOutlierFrames", "Identity outlier frames", "frames", "Frames with at least one identity feature outside the learned subject range.", collectionAudit.IdentityOutlierFrames, collectionAudit.TotalFramesReviewed);
        AddValue(package.QualityProfile, "AverageIdentityConfidencePercent", "Identity confidence", "percent", "Average measurement-only subject confidence for reviewed frames.", collectionAudit.AverageIdentityConfidencePercent, collectionAudit.IdentityMeasuredFrames);
        AddValue(package.QualityProfile, "MinimumIdentityConfidencePercent", "Minimum identity confidence", "percent", "Weakest subject-confidence frame in the audit window.", collectionAudit.MinimumIdentityConfidencePercent, collectionAudit.IdentityMeasuredFrames);
        AddValue(package.QualityProfile, "MaximumIdentityOutlierFeatureCount", "Maximum identity outliers", "features", "Largest number of identity-signature features outside the learned subject range in one frame.", collectionAudit.MaximumIdentityOutlierFeatureCount, collectionAudit.IdentityMeasuredFrames);
    }

    private static void AddSourceArtifacts(MeasurementAvatarTrainingPackage package)
    {
        package.SourceArtifacts.AddRange([
            Artifact("Personal face model", "personal_face_model.json", "personal-model", "Weighted neutral face, identity, quality, and acceptance distributions."),
            Artifact("Motion model", "personal_face_motion_model.json", "motion-model", "Velocity and coupling distributions for facial movement."),
            Artifact("Learning data health", "personal_face_corpus_readiness.json", "readiness-report", "Coverage scores, warnings, and next capture suggestions."),
            Artifact("Measurement preview", "measurement_face_preview.json", "wireframe-preview", "Normalized measurement-only face preview geometry."),
            Artifact("Canonical face seed prior", "built-in", "low-trust-template-prior", "Temporary scaffold for early preview topology; never counted as observed personal measurements."),
            Artifact("Avatar capture plan", "measurement_avatar_capture_plan.json", "capture-plan", "Subject-gated measurement-only plan for the next data collection session."),
            Artifact("Measurement journal", "measurements/*.jsonl", "accepted-measurement-journal", "Append-only accepted measurement records, bounded by the passive learning budget."),
            Artifact("Aggregate contour and surface shape profiles", "personal_face_model.json", "shape-profile", "Weighted face-local eye, lip, jaw, brow, nose, cheek, and forehead distributions; not per-frame contours."),
            Artifact("Pose bucket profiles", "personal_face_model.json", "pose-profile", "Straight-on A, B, and C measurement buckets so neutral identity and turned-head motion are not averaged together."),
            Artifact("Data audit findings", "personal_face_corpus_readiness.json", "data-audit", "Pose, feature anchoring, jaw scale, and measurement-journal consistency checks."),
            Artifact("Aperture consistency audit", "personal_face_corpus_readiness.json", "aperture-consistency", "Measurement-only agreement check between eye/mouth/jaw openings and dense blink/jaw/mouth corroboration."),
            Artifact("Collection audit", "personal_face_collection_audit.json", "frame-level-collection-audit", "Frame-level acceptance, quality, identity confidence, and rejection reasons for early drift detection."),
            Artifact("3DDFA/ONNX reconstruction lane", "dependencies/vision/3ddfa-onnx", "avatar-reconstruction-backend", "Official 3DDFA_V2 ONNX sidecar bundle used for dense avatar reconstruction when installed and ready.")
        ]);
    }

    private static void AddGuidance(
        MeasurementAvatarTrainingPackage package,
        PersonalFaceCorpusReadiness readiness,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCollectionAudit? collectionAudit)
    {
        package.Strengths.AddRange(readiness.Strengths);
        package.Warnings.AddRange(readiness.Warnings);
        package.NextCaptureSuggestions.AddRange(readiness.NextCaptureSuggestions);
        package.IntegrationNotes.Add("Use the neutral face profile to seed a measurement-only face rig before photoreal reconstruction exists.");
        package.IntegrationNotes.Add("Prefer the front-neutral pose bucket for identity and neutral geometry; use A/B/C buckets for pose-aware correction and animation coverage.");
        package.IntegrationNotes.Add($"Template prior contribution: {package.TemplatePriorContributionPercent:0.#}%; measured contribution: {package.MeasurementContributionPercent:0.#}%. Treat template prior as visual scaffolding, not subject evidence.");
        package.IntegrationNotes.Add("Use contour shape profiles to seed eyelid, lip, and jaw outline controls; they are aggregate distributions, not raw frame landmarks.");
        package.IntegrationNotes.Add("Use surface shape profiles to seed brows, nose bridge/base, cheek volume, and forehead depth before photoreal reconstruction exists.");
        package.IntegrationNotes.Add("Use aperture consistency before fitting eyelids, lips, or jaw: eye opening should oppose blink evidence, while mouth and jaw opening should agree with dense mouth evidence.");
        package.IntegrationNotes.Add("Treat data-audit findings as blockers for high-accuracy fitting; dead pose axes or feature drift can make the face appear to reorganize instead of rotate.");
        package.IntegrationNotes.Add("Before fitting 3D geometry, compare pose health, feature anchoring health, and pose-bucket consistency. If any are weak, do not let the avatar rig update head-pose or face-local feature positions.");
        package.IntegrationNotes.Add($"Identity-session audit: {readiness.IdentitySessionAuditStatus}.");
        package.IntegrationNotes.Add("Use the motion profile as a slow-changing animation prior; do not treat one new session as an instant identity or motion rewrite.");
        package.IntegrationNotes.Add($"Learning stability: {readiness.SubjectDisplayName} model is {package.LearningStability.AnchorStatus}; one new stable measurement can influence the profile by at most {package.LearningStability.MaximumNextSampleInfluencePercent:0.##}%.");
        package.IntegrationNotes.Add("Use the identity profile only to decide whether learning should continue for the enrolled subject; it is not a public identity credential.");
        package.IntegrationNotes.Add("Any future photoreal worker should request explicit training media separately and keep that media outside passive learning.");
        package.IntegrationNotes.Add($"Reconstruction lane: {package.ReconstructionLane.TrustDecision}");
        if (!package.ReconstructionLane.AvatarReconstructionCanRunInference)
        {
            package.Warnings.Add("3DDFA/ONNX avatar reconstruction is not active yet; use this package as measurement-only evidence, not as a dense reconstructed head.");
        }

        if (readiness.PoseEstimationHealthPercent is > 0d and < 60d)
        {
            package.Warnings.Add($"Pose estimation audit is weak ({readiness.PoseEstimationHealthPercent:0.#}%); verify A/B rotation before using this package for head-turn animation.");
        }

        if (readiness.FeatureAnchoringHealthPercent is > 0d and < 60d)
        {
            package.Warnings.Add($"Feature anchoring audit is weak ({readiness.FeatureAnchoringHealthPercent:0.#}%); review overlays for eyes/mouth sliding on the head before 3D fitting.");
        }

        if (readiness.IdentitySessionAuditStage is "review" or "hold")
        {
            package.Warnings.Add($"Recent identity-session audit is {readiness.IdentitySessionAuditStage} ({readiness.IdentitySessionHealthPercent:0.#}%); verify the subject gate before 3D fitting.");
        }

        if (readiness.PoseExplainedFeatureMotionHealthPercent is > 0d and < 70d)
        {
            package.Warnings.Add($"Pose-explained feature motion audit is weak ({readiness.PoseExplainedFeatureMotionHealthPercent:0.#}%); verify head turns are stored as pose instead of feature drift before 3D fitting.");
        }

        if (readiness.MouthVerticalAnchorHealthPercent is > 0d and < 70d)
        {
            package.Warnings.Add($"Mouth vertical anchor audit is weak ({readiness.MouthVerticalAnchorHealthPercent:0.#}%); verify the lip outline is on the actual mouth before fitting lip or jaw motion.");
        }

        if (readiness.PoseBucketConsistency.ComparedPoseBucketCount > 0 && readiness.PoseBucketConsistencyHealthPercent < 70d)
        {
            package.Warnings.Add($"Pose-bucket consistency audit is weak ({readiness.PoseBucketConsistencyHealthPercent:0.#}%); compare turned-head buckets to the neutral face before fitting 3D geometry.");
        }

        if (readiness.ApertureConsistencyHealthPercent < 70d)
        {
            package.Warnings.Add($"Aperture consistency audit is weak ({readiness.ApertureConsistencyHealthPercent:0.#}%); verify eye, mouth, and jaw opening measurements before fitting eyelid or lip motion.");
        }

        if (collectionAudit is not null)
        {
            package.IntegrationNotes.Add($"Collection audit reviewed {collectionAudit.TotalFramesReviewed.ToString(System.Globalization.CultureInfo.InvariantCulture)} frames; identity confidence average {(collectionAudit.AverageIdentityConfidencePercent?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? "waiting")}%, subject mismatch frames {collectionAudit.SubjectMismatchGateFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)}, identity outlier frames {collectionAudit.IdentityOutlierFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

            if (collectionAudit.SubjectMismatchGateFrames > 0)
            {
                package.Warnings.Add($"Collection audit found {collectionAudit.SubjectMismatchGateFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)} subject-mismatch frame(s); do not train mixed-person identity data.");
            }

            if (collectionAudit.TrackingAuditHoldFrames > 0)
            {
                package.Warnings.Add($"Collection audit found {collectionAudit.TrackingAuditHoldFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)} tracking-audit hold frame(s); review pose and feature anchoring before high-accuracy fitting.");
            }

            if (collectionAudit.AverageIdentityConfidencePercent is < 55d)
            {
                package.Warnings.Add($"Collection audit identity confidence is low ({collectionAudit.AverageIdentityConfidencePercent:0.#}%); collect a stable subject-confirmed session before high-accuracy fitting.");
            }

            if (collectionAudit.IdentityOutlierFrames > Math.Max(3, collectionAudit.TotalFramesReviewed * 0.08d))
            {
                package.Warnings.Add($"Collection audit found {collectionAudit.IdentityOutlierFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)} identity-outlier frames; review subject gate and lighting before continuing avatar capture.");
            }
        }

        if (motionModel.Warnings.Count > 0)
        {
            foreach (var warning in motionModel.Warnings.Take(3))
            {
                if (!package.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                {
                    package.Warnings.Add(warning);
                }
            }
        }
    }

    private static MeasurementAvatarTrainingArtifact Artifact(
        string name,
        string fileName,
        string kind,
        string description)
    {
        return new MeasurementAvatarTrainingArtifact
        {
            Name = name,
            FileName = fileName,
            Kind = kind,
            Description = description,
            ContainsRawPixels = false,
            ContainsRawContinuousVideo = false
        };
    }

    private static void Add(
        IDictionary<string, MeasurementAvatarTrainingMetric> target,
        string key,
        string label,
        string units,
        string avatarUse,
        PersonalMetricDistribution distribution)
    {
        target[key] = new MeasurementAvatarTrainingMetric
        {
            Label = label,
            Units = units,
            AvatarUse = avatarUse,
            SampleCount = distribution.SampleCount,
            TotalWeight = Round(distribution.TotalWeight),
            Average = Round(distribution.Average),
            Minimum = Round(distribution.Minimum),
            Maximum = Round(distribution.Maximum),
            StandardDeviation = Round(distribution.StandardDeviation),
            ExponentialMovingAverage = Round(distribution.ExponentialMovingAverage),
            NormalLow = Round(distribution.NormalLow),
            NormalHigh = Round(distribution.NormalHigh)
        };
    }

    private static void AddValue(
        IDictionary<string, MeasurementAvatarTrainingMetric> target,
        string key,
        string label,
        string units,
        string avatarUse,
        double? value,
        int sampleCount)
    {
        target[key] = new MeasurementAvatarTrainingMetric
        {
            Label = label,
            Units = units,
            AvatarUse = avatarUse,
            SampleCount = Math.Max(0, sampleCount),
            TotalWeight = Math.Max(0, sampleCount),
            Average = Round(value),
            Minimum = Round(value),
            Maximum = Round(value),
            ExponentialMovingAverage = Round(value),
            NormalLow = Round(value),
            NormalHigh = Round(value)
        };
    }

    private static DateTime Max(params DateTime[] values)
    {
        return values
            .Where(static value => value != default)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();
    }

    private static double? Round(double? value)
    {
        return value is double number ? Round(number) : null;
    }

    private static double Round(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Round(value, 6, MidpointRounding.AwayFromZero);
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
}
