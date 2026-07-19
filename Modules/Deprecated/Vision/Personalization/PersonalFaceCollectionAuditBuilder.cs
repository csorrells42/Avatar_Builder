namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCollectionAuditBuilder
{
    public PersonalFaceCollectionAudit Build(
        PersonalFaceModel faceModel,
        IReadOnlyList<PersonalFaceCollectionAuditObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(faceModel);
        observations ??= Array.Empty<PersonalFaceCollectionAuditObservation>();

        var audit = new PersonalFaceCollectionAudit
        {
            SubjectId = faceModel.SubjectId,
            SubjectDisplayName = faceModel.SubjectDisplayName,
            SubjectCollectionMode = faceModel.SubjectCollectionMode,
            CreatedAtUtc = faceModel.CreatedAtUtc,
            UpdatedAtUtc = faceModel.UpdatedAtUtc,
            TotalFramesReviewed = observations.Count
        };

        if (observations.Count > 0)
        {
            audit.CreatedAtUtc = MinDate(observations.Select(static observation => observation.ReviewedAtUtc));
            audit.UpdatedAtUtc = MaxDate(observations.Select(static observation => observation.ReviewedAtUtc));
        }

        audit.FramesWithFace = observations.Count(static observation => observation.HasFace);
        audit.SubjectConfirmedFrames = observations.Count(static observation => observation.SubjectConfirmed);
        audit.SubjectGateOffFrames = observations.Count(static observation =>
            !observation.SubjectConfirmed
            || observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.SubjectNotConfirmed.ToString(), StringComparison.OrdinalIgnoreCase));
        audit.EventLikeGateFrames = observations.Count(static observation =>
            observation.PausedForEventOrCalibration
            || observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.EventLike.ToString(), StringComparison.OrdinalIgnoreCase));
        audit.NoFaceGateFrames = observations.Count(static observation =>
            !observation.HasFace
            || observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.NoFace.ToString(), StringComparison.OrdinalIgnoreCase)
            || observation.CaptureQualityLabel.Equals("no-face", StringComparison.OrdinalIgnoreCase));
        audit.LowQualityGateFrames = observations.Count(static observation =>
            observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.LowQuality.ToString(), StringComparison.OrdinalIgnoreCase)
            || (observation.HasFace && !observation.CaptureQualityCanCollect));
        audit.TrackingArtifactGateFrames = observations.Count(static observation =>
            observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.TrackingArtifact.ToString(), StringComparison.OrdinalIgnoreCase));
        audit.SubjectMismatchGateFrames = observations.Count(static observation =>
            observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.SubjectMismatch.ToString(), StringComparison.OrdinalIgnoreCase));
        audit.TrackingAuditHoldFrames = observations.Count(static observation =>
            observation.PersonalModelRejectionKind.Equals(PersonalFaceModelRejectionKind.TrackingAuditHold.ToString(), StringComparison.OrdinalIgnoreCase));
        audit.IdentityMeasuredFrames = observations.Count(static observation => observation.IdentityMeasurementAvailable);
        audit.IdentityAutoGateReadyFrames = observations.Count(static observation => observation.IdentityAutoGateReady);
        audit.IdentityWarmupStrongMismatchGateReadyFrames = observations.Count(static observation => observation.IdentityWarmupStrongMismatchGateReady);
        audit.IdentityOutlierFrames = observations.Count(static observation => observation.IdentityOutlierFeatureCount > 0);
        audit.PersonalModelAcceptedFrames = observations.Count(static observation => observation.PersonalModelAccepted);
        audit.PersonalModelRejectedFrames = Math.Max(0, observations.Count - audit.PersonalModelAcceptedFrames);
        audit.CaptureQualityCanCollectFrames = observations.Count(static observation => observation.CaptureQualityCanCollect);
        audit.CaptureQualityAvatarGradeFrames = observations.Count(static observation => observation.CaptureQualityAvatarGrade);

        audit.FaceDetectionRate = Rate(audit.FramesWithFace, observations.Count);
        audit.SubjectConfirmedRate = Rate(audit.SubjectConfirmedFrames, observations.Count);
        audit.PersonalModelAcceptedRate = Rate(audit.PersonalModelAcceptedFrames, observations.Count);
        audit.CaptureQualityCollectableRate = Rate(audit.CaptureQualityCanCollectFrames, observations.Count);
        audit.CaptureQualityAvatarGradeRate = Rate(audit.CaptureQualityAvatarGradeFrames, observations.Count);

        audit.AverageCaptureQualityScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityScorePercent));
        audit.MinimumCaptureQualityScorePercent = MinimumOptional(observations.Select(static observation => (double?)observation.CaptureQualityScorePercent));
        audit.AverageCaptureQualityCameraModeScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityCameraModeScorePercent));
        audit.AverageCaptureQualityFaceScaleScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityFaceScaleScorePercent));
        audit.AverageCaptureQualityEyeScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityEyeScorePercent));
        audit.AverageCaptureQualityMouthScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityMouthScorePercent));
        audit.AverageCaptureQualityStabilityScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityStabilityScorePercent));
        audit.AverageCaptureQualityGlassesScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityGlassesScorePercent));
        audit.AverageCaptureQualityStorageScorePercent = AverageOptional(observations.Select(static observation => (double?)observation.CaptureQualityStorageScorePercent));
        audit.AverageIdentityConfidencePercent = AverageOptional(observations
            .Where(static observation => observation.IdentityMeasurementAvailable && observation.IdentityComparedFeatureCount > 0)
            .Select(static observation => (double?)observation.IdentityConfidencePercent));
        audit.MinimumIdentityConfidencePercent = MinimumOptional(observations
            .Where(static observation => observation.IdentityMeasurementAvailable && observation.IdentityComparedFeatureCount > 0)
            .Select(static observation => (double?)observation.IdentityConfidencePercent));
        audit.MaximumIdentityOutlierFeatureCount = observations.Count == 0
            ? 0
            : observations.Max(static observation => observation.IdentityOutlierFeatureCount);
        audit.MinimumFaceWidthPercent = MinimumOptional(observations.Select(static observation => observation.CaptureQualityFaceWidthPercent));
        audit.MaximumFaceWidthPercent = MaximumOptional(observations.Select(static observation => observation.CaptureQualityFaceWidthPercent));
        audit.MinimumFaceHeightPercent = MinimumOptional(observations.Select(static observation => observation.CaptureQualityFaceHeightPercent));
        audit.MaximumFaceHeightPercent = MaximumOptional(observations.Select(static observation => observation.CaptureQualityFaceHeightPercent));

        audit.CaptureQualityLabels = observations
            .Select(static observation => observation.CaptureQualityLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        audit.TopPersonalModelRejectionReasons = TopValues(observations
            .Where(static observation => !observation.PersonalModelAccepted)
            .Select(static observation => JoinReason(observation.PersonalModelRejectionKind, observation.PersonalModelUpdateReason)), 8);
        audit.TopCaptureQualityIssues = TopValues(observations.SelectMany(static observation => observation.CaptureQualityIssues), 8);
        audit.TopCaptureQualitySuggestions = TopValues(observations.SelectMany(static observation => observation.CaptureQualitySuggestions), 8);
        AddNextActions(audit);
        return audit;
    }

    private static void AddNextActions(PersonalFaceCollectionAudit audit)
    {
        if (audit.TotalFramesReviewed <= 0)
        {
            audit.NextActions.Add("Run a subject-confirmed capture session so the audit can explain collection readiness.");
            return;
        }

        if (audit.SubjectConfirmedRate < 0.80d)
        {
            audit.NextActions.Add("Use the subject confirmation checkbox only when the selected avatar user is in front of the camera so the measurement data does not mix people.");
        }

        if (audit.FaceDetectionRate < 0.85d)
        {
            audit.NextActions.Add("Improve face visibility: keep the full face in frame, avoid extreme edge positions, and reduce backlighting.");
        }

        if (audit.CaptureQualityCollectableRate < 0.85d)
        {
            audit.NextActions.Add("Use explicit 4K/30fps capture, keep the face large enough in frame, and reduce glasses glare before collecting long-term measurements.");
        }

        if (audit.CaptureQualityAvatarGradeRate < 0.30d)
        {
            audit.NextActions.Add("Collect a short avatar-grade pass with stable lighting, full face visible, clear eyes behind glasses, and mouth contours unobstructed.");
        }

        if (audit.SubjectMismatchGateFrames > 0)
        {
            audit.NextActions.Add("Review subject mismatch frames before continuing avatar capture; leave collection off when someone else is at the camera.");
        }

        if (audit.TrackingArtifactGateFrames > 0)
        {
            audit.NextActions.Add("Tracking artifacts were rejected from avatar capture; reduce glasses glare and keep hands away from eyes, lips, and jaw during intentional collection.");
        }

        if (audit.TrackingAuditHoldFrames > 0)
        {
            audit.NextActions.Add("Avatar capture is paused by the tracking audit; review the overlay, Avatar System, and Last 10 Good Features for features sliding on the head before collecting more measurements.");
        }

        if (audit.IdentityMeasuredFrames > 0 && audit.AverageIdentityConfidencePercent is < 45d)
        {
            audit.NextActions.Add("Identity confidence is low; collect a stable selected-user-only session with the full face visible before trusting long-term avatar capture.");
        }

        if (audit.AverageCaptureQualityCameraModeScorePercent is < 60d)
        {
            audit.NextActions.Add("Select a high-resolution camera mode instead of a low-resolution fallback before using this session for avatar capture.");
        }

        if (audit.AverageCaptureQualityEyeScorePercent is < 70d)
        {
            audit.NextActions.Add("Tune lighting and monitor glare until the eye contour score is consistently usable behind glasses.");
        }

        if (audit.AverageCaptureQualityMouthScorePercent is < 70d)
        {
            audit.NextActions.Add("Keep the camera angle high enough to see the lips instead of the area under the nose.");
        }

        if (audit.EventLikeGateFrames > audit.TotalFramesReviewed * 0.20d)
        {
            audit.NextActions.Add("Collect alert-baseline measurements during symptom-free windows; event-like periods should remain evidence, not baseline training data.");
        }

        if (audit.NextActions.Count == 0)
        {
            audit.NextActions.Add("Collection gates look healthy; continue gathering varied distance, pose, blink, speech, and relaxed-mouth measurements.");
        }
    }

    private static string JoinReason(string kind, string reason)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? "no detail" : reason.Trim();
        return $"{kind}: {reason}";
    }

    private static List<string> TopValues(IEnumerable<string> values, int max)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(static group => $"{group.Key} ({group.Count()})")
            .ToList();
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : Round(count / (double)total);
    }

    private static double? AverageOptional(IEnumerable<double?> values)
    {
        var valid = Values(values).ToList();
        return valid.Count == 0 ? null : Round(valid.Average());
    }

    private static double? MinimumOptional(IEnumerable<double?> values)
    {
        var valid = Values(values).ToList();
        return valid.Count == 0 ? null : Round(valid.Min());
    }

    private static double? MaximumOptional(IEnumerable<double?> values)
    {
        var valid = Values(values).ToList();
        return valid.Count == 0 ? null : Round(valid.Max());
    }

    private static IEnumerable<double> Values(IEnumerable<double?> values)
    {
        return values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value));
    }

    private static DateTime MinDate(IEnumerable<DateTime> values)
    {
        var valid = values.Where(static value => value != default).ToList();
        return valid.Count == 0 ? DateTime.UtcNow : valid.Min();
    }

    private static DateTime MaxDate(IEnumerable<DateTime> values)
    {
        var valid = values.Where(static value => value != default).ToList();
        return valid.Count == 0 ? DateTime.UtcNow : valid.Max();
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }
}
