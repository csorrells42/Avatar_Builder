using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarCapturePlanBuilder
{
    private const long EstimatedMeasurementBytesPerMinute = 60_000L;

    public MeasurementAvatarCapturePlan Build(
        PersonalFaceModel faceModel,
        PersonalFaceMotionModel motionModel,
        PersonalFaceCorpusReadiness readiness,
        FaceReconstructionSubjectGate subjectGate,
        long measurementJournalBytes,
        long measurementBudgetBytes = PersonalFaceMeasurementJournal.DefaultBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(faceModel);
        ArgumentNullException.ThrowIfNull(motionModel);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(subjectGate);

        var plan = new MeasurementAvatarCapturePlan
        {
            SubjectId = faceModel.SubjectId,
            SubjectDisplayName = faceModel.SubjectDisplayName,
            SubjectCollectionMode = faceModel.SubjectCollectionMode,
            UnknownSubjectPolicy = faceModel.UnknownSubjectPolicy,
            SubjectGate = subjectGate,
            MeasurementJournalBytes = Math.Max(0L, measurementJournalBytes),
            MeasurementBudgetBytes = Math.Max(1L, measurementBudgetBytes)
        };

        plan.MeasurementBudgetUsedPercent = Round(100d * plan.MeasurementJournalBytes / plan.MeasurementBudgetBytes);
        plan.CanCollectMeasurements = string.Equals(subjectGate.GateDecision, "accepted", StringComparison.OrdinalIgnoreCase);
        plan.LowestReadinessScorePercent = Round(new[]
        {
            readiness.BaselineCoveragePercent,
            readiness.MotionCoveragePercent,
            readiness.PoseCoveragePercent,
            readiness.PoseBucketCoveragePercent,
            readiness.DistanceCoveragePercent,
            readiness.ZDistanceEvidenceHealthPercent,
            readiness.ExpressionCoveragePercent,
            readiness.IdentityCoveragePercent,
            readiness.ContourShapeCoveragePercent,
            readiness.ContourDepthProfileHealthPercent,
            readiness.SurfaceShapeCoveragePercent,
            readiness.SurfaceDepthProfileHealthPercent,
            readiness.SurfaceGeometryHealthPercent,
            readiness.EyeBehindGlassesTrustPercent,
            readiness.MouthJawTrustPercent,
            readiness.DirectFeatureMeasurementTrustPercent,
            readiness.ApertureConsistencyHealthPercent,
            readiness.QualityCoveragePercent,
            readiness.CaptureQualityCoveragePercent,
            readiness.StorageHealthPercent,
            readiness.DataAuditHealthPercent
        }.Min());

        AddDefaultRules(plan);
        AddCollectionItems(plan, readiness, motionModel);
        if (plan.Items.Count == 0)
        {
            AddMaintenanceItem(plan, readiness);
        }

        plan.Items = plan.Items
            .OrderBy(static item => item.Priority)
            .ThenBy(static item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        plan.TotalTargetMinutes = plan.Items.Sum(static item => Math.Max(0, item.TargetMinutes));
        plan.EstimatedMeasurementBytes = plan.Items.Sum(static item => Math.Max(0L, item.EstimatedMeasurementBytes));
        plan.CollectionDecision = BuildDecision(plan);
        return plan;
    }

    private static void AddDefaultRules(MeasurementAvatarCapturePlan plan)
    {
        plan.PreSessionChecks.Add("Confirm the checkbox says this is the enrolled subject before collecting measurements.");
        plan.PreSessionChecks.Add("Use the normal glasses/camera/lighting setup whenever possible so the learning data matches real use.");
        plan.PreSessionChecks.Add("Use 4K tracking when practical, disable face filters/background effects, and keep lighting soft enough to reduce glasses glare.");
        plan.StopRules.Add("Pause alert-baseline collection if sleepiness/cataplexy symptoms begin; labeled sleepy-state motion can still be useful when intentionally captured.");
        plan.StopRules.Add("Stop if someone else is in front of the camera or the subject checkbox is not correct.");
        plan.StopRules.Add("Do not collect raw media for passive learning; use explicit training-media sessions only when a future 3D worker asks for it.");
    }

    private static void AddCollectionItems(
        MeasurementAvatarCapturePlan plan,
        PersonalFaceCorpusReadiness readiness,
        PersonalFaceMotionModel motionModel)
    {
        if (!plan.CanCollectMeasurements)
        {
            AddItem(
                plan,
                "confirm-subject",
                1,
                "Subject Gate",
                "Confirm enrolled subject",
                "Turn on the subject confirmation checkbox only when the enrolled subject is in front of the camera.",
                "This prevents the long-running face model from blending multiple people.",
                "SubjectGate",
                0d,
                0,
                "The subject gate is accepted and the collection decision no longer says paused.");
            return;
        }

        if (readiness.DataAuditHealthPercent < 75d)
        {
            var instructions = readiness.DataAuditFindings.Count > 0
                ? string.Join(" ", readiness.DataAuditFindings.Take(2))
                : "Review the tracking overlay and generated avatar preview for pose, jaw, or feature anchoring issues before collecting more data.";
            AddItem(
                plan,
                "data-audit-review",
                Priority(readiness.DataAuditHealthPercent),
                "Data Audit",
                "Review tracking consistency",
                instructions,
                "Bad pose or feature anchoring data can make the avatar learn a face that slides around instead of a head that rotates.",
                "DataAuditHealthPercent",
                readiness.DataAuditHealthPercent,
                2,
                "Data audit health is at least 75% with no high-priority tracking consistency findings.");
        }

        if (readiness.PoseExplainedFeatureMotionHealthPercent is > 0d and < 70d)
        {
            AddItem(
                plan,
                "pose-explained-feature-review",
                Priority(readiness.PoseExplainedFeatureMotionHealthPercent),
                "Data Audit",
                "Review head-turn feature motion",
                "Open the overlay or Last 10 Good Features during a slow B-axis left/right head turn. The head should rotate while the eyes, mouth, and nose remain attached to the face instead of sliding sideways.",
                "This prevents the avatar from learning head turns as changes to the user's face shape.",
                "PoseExplainedFeatureMotionHealthPercent",
                readiness.PoseExplainedFeatureMotionHealthPercent,
                2,
                "Pose-explained feature motion health is at least 70%.");
        }

        if (readiness.StorageHealthPercent < 85d || readiness.MeasurementBudgetUsedPercent > 80d)
        {
            AddItem(
                plan,
                "storage-health",
                Priority(readiness.StorageHealthPercent),
                "Storage",
                "Protect the measurement budget",
                "Keep the output folder on the external drive and archive explicit training media before collecting more.",
                "The measurement data must stay under the 10 GB target without silently filling the local drive.",
                "StorageHealthPercent",
                readiness.StorageHealthPercent,
                0,
                "Storage health is above 85% and budget use is below 80%.");
        }

        if (readiness.BaselineCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "alert-baseline",
                Priority(readiness.BaselineCoveragePercent),
                "Baseline",
                "Alert neutral baseline",
                "Sit normally, symptom-free, eyes naturally open, lips relaxed/closed, and let the camera collect clean frames.",
                "Stable neutral measurements keep the avatar from changing its basic face every day.",
                "BaselineCoveragePercent",
                readiness.BaselineCoveragePercent,
                5,
                "Baseline coverage is at least 70% with clean accepted samples.");
        }

        if (readiness.MotionCoveragePercent < 70d || motionModel.MotionPairCount < 240)
        {
            AddItem(
                plan,
                "natural-motion",
                Priority(readiness.MotionCoveragePercent),
                "Motion",
                "Natural facial motion",
                "Talk normally, do a few slow blinks, relax and close the mouth, then repeat with small natural head movements.",
                "The future avatar needs motion timing, not just a static face shape.",
                "MotionCoveragePercent",
                readiness.MotionCoveragePercent,
                6,
                "Motion coverage is at least 70% and the motion model has at least 240 usable pairs.");
        }

        if (readiness.DistanceCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "distance-ladder",
                Priority(readiness.DistanceCoveragePercent),
                "Distance",
                "Distance ladder",
                "Collect short symptom-free segments leaning close, at normal desk distance, and leaning back across the room.",
                "The webcam will see the face at different sizes; the model needs all common distances.",
                "DistanceCoveragePercent",
                readiness.DistanceCoveragePercent,
                6,
                "Face width and height ranges cover close, normal, and leaned-back positions.");
        }

        if (readiness.ZDistanceCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "z-distance-pass",
                Priority(readiness.ZDistanceCoveragePercent),
                "XYZABC",
                "Z distance pass",
                "Sit at normal distance, lean closer, then lean back while keeping the whole face visible and glasses reflections low.",
                "Z coverage teaches the model that face-size changes are camera distance, not a different face shape.",
                "ZDistanceCoveragePercent",
                readiness.ZDistanceCoveragePercent,
                4,
                "Z distance coverage is at least 70%.");
        }

        if (readiness.ZDistanceEvidenceHealthPercent < 70d)
        {
            AddItem(
                plan,
                "z-evidence-calibration",
                Priority(readiness.ZDistanceEvidenceHealthPercent),
                "XYZABC",
                "Strengthen Z evidence",
                "Keep webcam zoom fixed, collect close/normal/far positions in one pass, and later add one known-distance reference if physical inches matter.",
                "Explicit Z evidence keeps close/far movement from being learned as a new face shape.",
                "ZDistanceEvidenceHealthPercent",
                readiness.ZDistanceEvidenceHealthPercent,
                4,
                "Z evidence health is at least 70% with stable zoom/reference support.");
        }

        if (readiness.SurfaceDepthProfileHealthPercent < 70d)
        {
            AddItem(
                plan,
                "surface-z-profile-pass",
                Priority(readiness.SurfaceDepthProfileHealthPercent),
                "XYZABC",
                "Surface Z profile pass",
                "Collect a slow front-neutral pass, then small B-axis three-quarter turns and slight A-axis up/down tilts with forehead, brows, nose bridge, and cheeks visible.",
                "This teaches the avatar which brow/nose/cheek/forehead points have real depth instead of drawing a flat face with lines on it.",
                "SurfaceDepthProfileHealthPercent",
                readiness.SurfaceDepthProfileHealthPercent,
                5,
                "Surface Z profile health is at least 70%.");
        }

        if (readiness.SurfaceGeometryPatchCount > 0 && readiness.SurfaceGeometryHealthPercent < 70d)
        {
            AddItem(
                plan,
                "surface-geometry-review-pass",
                Priority(readiness.SurfaceGeometryHealthPercent),
                "XYZABC",
                "Surface geometry review pass",
                "Open Avatar System and Last 10 Good Features, then collect slow stable frames for weak regions: thin mouth openings, nose bridge, cheeks, brows, and forehead.",
                "This prevents folded or uneven measured patches from being treated as a stable face surface in the long-term avatar model.",
                "SurfaceGeometryHealthPercent",
                readiness.SurfaceGeometryHealthPercent,
                4,
                "Surface geometry health is at least 70% with no review patches.");
        }

        if (readiness.ContourDepthProfileHealthPercent < 65d)
        {
            AddItem(
                plan,
                "feature-contour-z-profile-pass",
                Priority(readiness.ContourDepthProfileHealthPercent),
                "XYZABC",
                "Feature contour Z profile pass",
                "Collect slow blinks, lips closed/open, and gentle jaw-drop frames while holding the head steady, then repeat with small left/right turns.",
                "This keeps eyelids, lips, and jaw contours measurable in 3D rather than only as flat outlines.",
                "ContourDepthProfileHealthPercent",
                readiness.ContourDepthProfileHealthPercent,
                4,
                "Feature contour Z profile health is at least 65%.");
        }

        if (readiness.ARotationAroundXCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "a-rotation-pass",
                Priority(readiness.ARotationAroundXCoveragePercent),
                "XYZABC",
                "A rotation around X pass",
                "Slowly tilt the head slightly up and down while keeping eyes, brows, nose, and mouth visible.",
                "A coverage separates up/down head tilt from eyebrow, eyelid, nose, and mouth movement.",
                "ARotationAroundXCoveragePercent",
                readiness.ARotationAroundXCoveragePercent,
                4,
                "A rotation around X coverage is at least 70%.");
        }

        if (readiness.BRotationAroundYCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "b-rotation-pass",
                Priority(readiness.BRotationAroundYCoveragePercent),
                "XYZABC",
                "B rotation around Y pass",
                "Slowly turn left and right through straight-on and three-quarter views while keeping both eyes and the mouth measurable.",
                "B coverage is the main evidence that face features are rotating with the head instead of sliding sideways.",
                "BRotationAroundYCoveragePercent",
                readiness.BRotationAroundYCoveragePercent,
                5,
                "B rotation around Y coverage is at least 70%.");
        }

        if (readiness.CRotationAroundZCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "c-rotation-pass",
                Priority(readiness.CRotationAroundZCoveragePercent),
                "XYZABC",
                "C rotation around Z pass",
                "Gently tilt the head side-to-side while keeping glasses glare low and the mouth visible.",
                "C coverage separates head tilt from eyelid and lip opening measurements.",
                "CRotationAroundZCoveragePercent",
                readiness.CRotationAroundZCoveragePercent,
                4,
                "C rotation around Z coverage is at least 70%.");
        }

        if (readiness.PoseCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "pose-sweep",
                Priority(readiness.PoseCoveragePercent),
                "Pose",
                "Head pose sweep",
                "Slowly turn left/right through straight-on, three-quarter, and near-side B-axis views; then add slight A-axis up/down tilt and a few gentle C-axis head-tilt positions while keeping the face visible.",
                "Side and three-quarter frames help reconstruct nose projection, cheek volume, and forehead depth instead of treating head rotation as sliding facial features.",
                "PoseCoveragePercent",
                readiness.PoseCoveragePercent,
                8,
                "A, B, and C rotation coverage includes enough straight-on, three-quarter, and side evidence for the readiness report.");
        }

        if (readiness.PoseBucketCoveragePercent < 70d && readiness.PoseBuckets.Count > 0)
        {
            foreach (var bucket in readiness.PoseBuckets
                         .Where(static bucket => bucket.RequiredForAvatarCoverage && bucket.SampleCount < 45)
                         .OrderBy(static bucket => bucket.SampleCount)
                         .Take(2))
            {
                AddItem(
                    plan,
                    $"pose-bucket-{bucket.BucketId}",
                    Priority(readiness.PoseBucketCoveragePercent),
                    "Pose Bucket",
                    bucket.Label,
                    bucket.CaptureInstruction,
                    "Pose-specific buckets keep straight-on identity, side-depth evidence, turned-head motion, and animation correction from being averaged into the same face shape.",
                    "PoseBucketCoveragePercent",
                    readiness.PoseBucketCoveragePercent,
                    bucket.PrimaryNeutralReference ? 5 : 3,
                    $"{bucket.Label} has enough accepted subject-gated measurements for the pose bucket profile.");
            }
        }

        if (readiness.ExpressionCoveragePercent < 75d)
        {
            AddItem(
                plan,
                "expression-ladder",
                Priority(readiness.ExpressionCoveragePercent),
                "Expression",
                "Eye mouth jaw expression ladder",
                "While alert, cycle through eyes open, relaxed eyelids, slow blinks, lips closed, lips slightly open, speech, and deliberate jaw drop.",
                "Extreme accuracy needs eyelid, lip, and jaw ranges gathered while awake so sleepy/cataplexy events stand out.",
                "ExpressionCoveragePercent",
                readiness.ExpressionCoveragePercent,
                8,
                "Eye opening, mouth opening, jaw droop, and dense blendshape ranges are all broad enough.");
        }

        if (readiness.ApertureConsistencyHealthPercent < 75d)
        {
            AddItem(
                plan,
                "aperture-corroboration",
                Priority(readiness.ApertureConsistencyHealthPercent),
                "Aperture Consistency",
                "Corroborate eye mouth jaw openings",
                "Collect alert frames with slow blinks, eyes relaxed/open, lips closed/slightly open, natural speech, and gentle jaw drop while keeping glasses glare low.",
                "The narcolepsy tracker and avatar both need eyelid, lip, and jaw opening measurements that agree with dense blink and mouth evidence.",
                "ApertureConsistencyHealthPercent",
                readiness.ApertureConsistencyHealthPercent,
                5,
                "Aperture consistency is above 75% and no data-audit finding says eye, mouth, or jaw aperture evidence disagrees.");
        }

        if (readiness.IdentityCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "identity-lock",
                Priority(readiness.IdentityCoveragePercent),
                "Identity",
                "Identity signature strengthening",
                "Collect a stable symptom-free session with glasses in the normal position, no filters, and the face clearly visible.",
                "The system needs a non-image owner signature so it can pause before learning from someone else.",
                "IdentityCoveragePercent",
                readiness.IdentityCoveragePercent,
                5,
                "Identity coverage is at least 70% with enough accepted signature samples.");
        }

        if (readiness.ContourShapeCoveragePercent < 70d)
        {
            AddItem(
                plan,
                "contour-shape-pass",
                Priority(readiness.ContourShapeCoveragePercent),
                "Contour Shape",
                "Direct eye lip jaw outline pass",
                "Collect stable alert frames with eyes visible behind glasses, lips closed and slightly open, and the lower face fully visible.",
                "The future avatar needs learned eyelid, lip, and jaw outlines, not only opening ratios.",
                "ContourShapeCoveragePercent",
                readiness.ContourShapeCoveragePercent,
                5,
                "Contour shape coverage is at least 70% for direct eye, lip, and jaw observations.");
        }

        var eyeCaptureScore = Math.Min(readiness.EyeBehindGlassesTrustPercent, readiness.EyeApertureReliabilityHealthPercent);
        if (eyeCaptureScore < 70d)
        {
            AddItem(
                plan,
                "eye-glasses-trust",
                Priority(eyeCaptureScore),
                "Eye Trust",
                "Behind-glasses eye accuracy pass",
                "Collect alert frames with glasses on, both eyes visible, screen glare minimized, and slow open-relaxed-blink eyelid changes.",
                "Eye closure is the primary sleep/cataplexy cue, so avatar and narcolepsy tracking need direct eye evidence behind glasses.",
                readiness.EyeApertureReliabilityHealthPercent < readiness.EyeBehindGlassesTrustPercent
                    ? "EyeApertureReliabilityHealthPercent"
                    : "EyeBehindGlassesTrustPercent",
                eyeCaptureScore,
                6,
                "Eye-behind-glasses trust and eye aperture reliability are at least 70% with direct, non-reconstructed eye contours.");
        }

        if (readiness.MouthJawTrustPercent < 70d)
        {
            AddItem(
                plan,
                "mouth-jaw-trust",
                Priority(readiness.MouthJawTrustPercent),
                "Mouth Jaw Trust",
                "Mouth and jaw visibility pass",
                "Collect alert frames with the lower face fully visible, lips closed, lips slightly open, natural speech, and gentle jaw drop.",
                "Jaw droop and lip opening are supporting sleep/cataplexy cues and important facial motion channels for the future avatar.",
                "MouthJawTrustPercent",
                readiness.MouthJawTrustPercent,
                5,
                "Mouth/jaw trust is at least 70% with direct mouth and jaw contours.");
        }

        if (readiness.QualityCoveragePercent < 75d)
        {
            AddItem(
                plan,
                "quality-lighting",
                Priority(readiness.QualityCoveragePercent),
                "Quality",
                "Lighting and glasses glare check",
                "Adjust room lighting, monitor brightness, camera angle, or glasses angle, then collect a short neutral segment.",
                "Glare and low contrast are direct threats to eyelid accuracy behind glasses.",
                "QualityCoveragePercent",
                readiness.QualityCoveragePercent,
                4,
                "Quality coverage is above 75% and eye glare/artifact warnings stop dominating the report.");
        }

        if (readiness.CaptureQualityCoveragePercent < 75d)
        {
            AddItem(
                plan,
                "avatar-grade-capture",
                Priority(readiness.CaptureQualityCoveragePercent),
                "Capture Quality",
                "Avatar-grade capture pass",
                "Set the camera to explicit 4K/30fps, keep the face comfortably large in frame, reduce glasses glare, and collect a short neutral segment.",
                "Extreme accuracy needs measurements that were good enough to enter the long-term learning data, not just frames that were reviewable.",
                "CaptureQualityCoveragePercent",
                readiness.CaptureQualityCoveragePercent,
                4,
                "Capture quality coverage is above 75%, collectable rate is high, and avatar-grade samples are present.");
        }
    }

    private static void AddMaintenanceItem(MeasurementAvatarCapturePlan plan, PersonalFaceCorpusReadiness readiness)
    {
        AddItem(
            plan,
            "maintenance",
            9,
            "Maintenance",
            "Balanced maintenance session",
            "Run a short symptom-free session that includes normal posture, a few head turns, natural speech, slow blinks, and a distance change.",
            "The learning data is strong enough for maintenance; balanced measurements keep it current without rapid daily jumps.",
            "OverallReadinessPercent",
            readiness.OverallReadinessPercent,
            8,
            "Readiness remains strong after routine use.");
    }

    private static void AddItem(
        MeasurementAvatarCapturePlan plan,
        string id,
        int priority,
        string category,
        string title,
        string instructions,
        string whyItMatters,
        string relatedScoreName,
        double relatedScorePercent,
        int targetMinutes,
        string completeWhen)
    {
        plan.Items.Add(new MeasurementAvatarCapturePlanItem
        {
            Id = id,
            Priority = priority,
            Category = category,
            Title = title,
            Instructions = instructions,
            WhyItMatters = whyItMatters,
            RelatedScoreName = relatedScoreName,
            RelatedScorePercent = Round(relatedScorePercent),
            TargetMinutes = Math.Max(0, targetMinutes),
            EstimatedMeasurementBytes = Math.Max(0, targetMinutes) * EstimatedMeasurementBytesPerMinute,
            CompleteWhen = completeWhen
        });
    }

    private static string BuildDecision(MeasurementAvatarCapturePlan plan)
    {
        if (!plan.CanCollectMeasurements)
        {
            return $"paused by subject gate: {plan.SubjectGate.Reason}";
        }

        if (plan.MeasurementBudgetUsedPercent > 90d)
        {
            return "storage review first; measurement budget is close to full";
        }

        var first = plan.Items.FirstOrDefault();
        return first is null
            ? "no capture plan items available"
            : $"next recommended capture: {first.Title}";
    }

    private static int Priority(double score)
    {
        if (score < 35d)
        {
            return 1;
        }

        if (score < 55d)
        {
            return 2;
        }

        if (score < 70d)
        {
            return 3;
        }

        return 4;
    }

    private static double Round(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }
}
