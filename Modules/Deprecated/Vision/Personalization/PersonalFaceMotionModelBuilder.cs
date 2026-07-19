namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMotionModelBuilder
{
    private const double MinimumUsableQualityPercent = 35d;
    private const double MaximumPairGapSeconds = 12d;
    private const double MinimumPairGapSeconds = 0.033d;
    private const double MotionEpsilonPerSecond = 0.002d;

    public PersonalFaceMotionModel Build(IEnumerable<PersonalFaceMotionObservation> observations)
    {
        var ordered = observations
            .Where(static observation => observation.CapturedAtUtc != default)
            .OrderBy(static observation => observation.CapturedAtUtc)
            .ToList();
        var model = new PersonalFaceMotionModel();
        if (ordered.Count == 0)
        {
            model.Warnings.Add("No motion observations were available.");
            return model;
        }

        var first = ordered[0];
        model.SubjectId = first.SubjectId;
        model.SubjectDisplayName = first.SubjectDisplayName;
        model.SubjectCollectionMode = first.SubjectCollectionMode;
        model.CreatedAtUtc = ordered.First().CapturedAtUtc;
        model.UpdatedAtUtc = ordered.Last().CapturedAtUtc;
        model.ObservationCount = ordered.Count;
        model.CapturedDurationSeconds = Math.Max(0d, (model.UpdatedAtUtc - model.CreatedAtUtc).TotalSeconds);

        var quality = new DistributionAccumulator();
        var reliability = new DistributionAccumulator();
        var yaw = new DistributionAccumulator();
        var pitch = new DistributionAccumulator();
        var roll = new DistributionAccumulator();
        var zApparentDistance = new DistributionAccumulator();
        var zRelativeToReference = new DistributionAccumulator();
        var zConfidence = new DistributionAccumulator();
        var eyeOpening = new DistributionAccumulator();
        var mouthOpening = new DistributionAccumulator();
        var jawDroop = new DistributionAccumulator();
        var browHeight = new DistributionAccumulator();
        var mediaPipeBlink = new DistributionAccumulator();
        var mediaPipeJawOpen = new DistributionAccumulator();
        var mediaPipeMouthClose = new DistributionAccumulator();
        var eyeClosingVelocity = new DistributionAccumulator();
        var eyeOpeningVelocity = new DistributionAccumulator();
        var mouthOpeningVelocity = new DistributionAccumulator();
        var mouthClosingVelocity = new DistributionAccumulator();
        var jawDroopVelocity = new DistributionAccumulator();
        var jawRecoveryVelocity = new DistributionAccumulator();
        var browRaiseVelocity = new DistributionAccumulator();
        var browLowerVelocity = new DistributionAccumulator();
        var yawVelocity = new DistributionAccumulator();
        var pitchVelocity = new DistributionAccumulator();
        var rollVelocity = new DistributionAccumulator();
        var zApparentVelocity = new DistributionAccumulator();
        PersonalFaceMotionObservation? previous = null;
        var eyeClosingPairs = 0;
        var eyeClosingWithMouthOpening = 0;
        var eyeClosingWithJawDroop = 0;
        var eyeClosingWithBrowLowering = 0;
        var mouthOpeningPairs = 0;
        var mouthOpeningWithJawDroop = 0;

        foreach (var observation in ordered)
        {
            if (observation.AcceptedForPersonalModel)
            {
                model.PersonalModelAcceptedObservationCount++;
            }

            if (observation.EyeArtifactSuppressed)
            {
                model.EyeArtifactSuppressedObservations++;
            }

            if (observation.AnyEyeReconstructed)
            {
                model.EyeReconstructedObservations++;
            }

            if (observation.MouthReconstructed)
            {
                model.MouthReconstructedObservations++;
            }

            var weight = ObservationWeight(observation);
            if (weight > 0d)
            {
                model.UsableObservationCount++;
                quality.Add(observation.OverallQualityPercent, weight);
                reliability.Add(observation.FaceReliabilityPercent, weight);
                yaw.Add(observation.HeadYawDegrees, weight);
                pitch.Add(observation.HeadPitchDegrees, weight);
                roll.Add(observation.HeadRollDegrees, weight);
                zApparentDistance.Add(observation.ZApparentDistanceUnits, weight);
                zRelativeToReference.Add(observation.ZRelativeToReference, weight);
                zConfidence.Add(observation.ZConfidencePercent, weight);
                eyeOpening.Add(observation.AverageEyeOpeningRatio, weight);
                mouthOpening.Add(observation.MouthOpeningRatio, weight);
                jawDroop.Add(observation.JawDroopRatio, weight);
                browHeight.Add(observation.AverageBrowHeightRatio, weight);
                mediaPipeBlink.Add(observation.MediaPipeAverageEyeBlinkPercent, weight);
                mediaPipeJawOpen.Add(observation.MediaPipeJawOpenPercent, weight);
                mediaPipeMouthClose.Add(observation.MediaPipeMouthClosePercent, weight);
            }

            if (previous is not null)
            {
                var deltaSeconds = (observation.CapturedAtUtc - previous.CapturedAtUtc).TotalSeconds;
                if (deltaSeconds is >= MinimumPairGapSeconds and <= MaximumPairGapSeconds)
                {
                    var pairWeight = Math.Min(weight, ObservationWeight(previous));
                    if (pairWeight > 0d)
                    {
                        model.MotionPairCount++;
                        var eyeSlope = Slope(previous.AverageEyeOpeningRatio, observation.AverageEyeOpeningRatio, deltaSeconds);
                        var mouthSlope = Slope(previous.MouthOpeningRatio, observation.MouthOpeningRatio, deltaSeconds);
                        var jawSlope = Slope(previous.JawDroopRatio, observation.JawDroopRatio, deltaSeconds);
                        var browSlope = Slope(previous.AverageBrowHeightRatio, observation.AverageBrowHeightRatio, deltaSeconds);

                        AddSignedVelocity(eyeSlope, eyeClosingVelocity, eyeOpeningVelocity, pairWeight, negativeMeansPrimary: true);
                        AddSignedVelocity(mouthSlope, mouthOpeningVelocity, mouthClosingVelocity, pairWeight, negativeMeansPrimary: false);
                        AddSignedVelocity(jawSlope, jawDroopVelocity, jawRecoveryVelocity, pairWeight, negativeMeansPrimary: false);
                        AddSignedVelocity(browSlope, browRaiseVelocity, browLowerVelocity, pairWeight, negativeMeansPrimary: false);
                        yawVelocity.Add(Math.Abs((observation.HeadYawDegrees - previous.HeadYawDegrees) / deltaSeconds), pairWeight);
                        pitchVelocity.Add(Math.Abs((observation.HeadPitchDegrees - previous.HeadPitchDegrees) / deltaSeconds), pairWeight);
                        rollVelocity.Add(Math.Abs((observation.HeadRollDegrees - previous.HeadRollDegrees) / deltaSeconds), pairWeight);
                        var zSlope = Slope(previous.ZApparentDistanceUnits, observation.ZApparentDistanceUnits, deltaSeconds);
                        if (zSlope is double zVelocity && Math.Abs(zVelocity) >= MotionEpsilonPerSecond)
                        {
                            zApparentVelocity.Add(Math.Abs(zVelocity), pairWeight);
                        }

                        var eyeClosing = eyeSlope is < -MotionEpsilonPerSecond;
                        var mouthOpeningNow = mouthSlope is > MotionEpsilonPerSecond;
                        var jawDrooping = jawSlope is > MotionEpsilonPerSecond;
                        var browLowering = browSlope is < -MotionEpsilonPerSecond;
                        if (eyeClosing)
                        {
                            eyeClosingPairs++;
                            if (mouthOpeningNow)
                            {
                                eyeClosingWithMouthOpening++;
                            }

                            if (jawDrooping)
                            {
                                eyeClosingWithJawDroop++;
                            }

                            if (browLowering)
                            {
                                eyeClosingWithBrowLowering++;
                            }
                        }

                        if (mouthOpeningNow)
                        {
                            mouthOpeningPairs++;
                            if (jawDrooping)
                            {
                                mouthOpeningWithJawDroop++;
                            }
                        }
                    }
                }
            }

            previous = observation;
        }

        model.AverageObservationQualityPercent = Round(quality.ToModel().Average ?? 0d);
        model.AverageFaceReliabilityPercent = Round(reliability.ToModel().Average ?? 0d);
        model.HeadYawDegrees = yaw.ToModel();
        model.HeadPitchDegrees = pitch.ToModel();
        model.HeadRollDegrees = roll.ToModel();
        model.ZApparentDistanceUnits = zApparentDistance.ToModel();
        model.ZRelativeToReference = zRelativeToReference.ToModel();
        model.ZConfidencePercent = zConfidence.ToModel();
        model.AverageEyeOpeningRatio = eyeOpening.ToModel();
        model.MouthOpeningRatio = mouthOpening.ToModel();
        model.JawDroopRatio = jawDroop.ToModel();
        model.AverageBrowHeightRatio = browHeight.ToModel();
        model.MediaPipeAverageEyeBlinkPercent = mediaPipeBlink.ToModel();
        model.MediaPipeJawOpenPercent = mediaPipeJawOpen.ToModel();
        model.MediaPipeMouthClosePercent = mediaPipeMouthClose.ToModel();
        model.EyeClosingVelocityPerSecond = eyeClosingVelocity.ToModel();
        model.EyeOpeningVelocityPerSecond = eyeOpeningVelocity.ToModel();
        model.MouthOpeningVelocityPerSecond = mouthOpeningVelocity.ToModel();
        model.MouthClosingVelocityPerSecond = mouthClosingVelocity.ToModel();
        model.JawDroopVelocityPerSecond = jawDroopVelocity.ToModel();
        model.JawRecoveryVelocityPerSecond = jawRecoveryVelocity.ToModel();
        model.BrowRaiseVelocityPerSecond = browRaiseVelocity.ToModel();
        model.BrowLowerVelocityPerSecond = browLowerVelocity.ToModel();
        model.HeadYawVelocityDegreesPerSecond = yawVelocity.ToModel();
        model.HeadPitchVelocityDegreesPerSecond = pitchVelocity.ToModel();
        model.HeadRollVelocityDegreesPerSecond = rollVelocity.ToModel();
        model.ZApparentVelocityUnitsPerSecond = zApparentVelocity.ToModel();
        model.EyeClosingWithMouthOpeningRate = Rate(eyeClosingWithMouthOpening, eyeClosingPairs);
        model.EyeClosingWithJawDroopRate = Rate(eyeClosingWithJawDroop, eyeClosingPairs);
        model.EyeClosingWithBrowLoweringRate = Rate(eyeClosingWithBrowLowering, eyeClosingPairs);
        model.MouthOpeningWithJawDroopRate = Rate(mouthOpeningWithJawDroop, mouthOpeningPairs);
        AddWarnings(model);
        return model;
    }

    private static double ObservationWeight(PersonalFaceMotionObservation observation)
    {
        if (observation.OverallQualityPercent < MinimumUsableQualityPercent)
        {
            return 0d;
        }

        var quality = Math.Clamp(observation.OverallQualityPercent / 100d, 0d, 1d);
        var reliability = Math.Clamp(observation.FaceReliabilityPercent / 100d, 0d, 1d);
        var continuity = Math.Clamp(observation.FaceContinuityPercent / 100d, 0d, 1d);
        var sourceWeight = Math.Clamp(observation.SampleWeight, 0.05d, 2.00d);
        var reconstructionPenalty =
            observation.EyeArtifactSuppressed || observation.AnyEyeReconstructed || observation.MouthReconstructed
                ? 0.82d
                : 1d;
        return Math.Clamp(sourceWeight * (quality * 0.45d + reliability * 0.35d + continuity * 0.20d) * reconstructionPenalty, 0d, 2.00d);
    }

    private static double? Slope(double? previous, double? current, double deltaSeconds)
    {
        return previous is double a && current is double b && deltaSeconds > 0d
            ? (b - a) / deltaSeconds
            : null;
    }

    private static void AddSignedVelocity(
        double? slope,
        DistributionAccumulator primary,
        DistributionAccumulator recovery,
        double weight,
        bool negativeMeansPrimary)
    {
        if (slope is not double value || Math.Abs(value) < MotionEpsilonPerSecond)
        {
            return;
        }

        if (negativeMeansPrimary ? value < 0d : value > 0d)
        {
            primary.Add(Math.Abs(value), weight);
        }
        else
        {
            recovery.Add(Math.Abs(value), weight);
        }
    }

    private static void AddWarnings(PersonalFaceMotionModel model)
    {
        if (model.UsableObservationCount < 30)
        {
            model.Warnings.Add("Motion model is early: fewer than 30 usable observations.");
        }

        if (model.MotionPairCount < 20)
        {
            model.Warnings.Add("Motion velocity estimates are sparse; record longer explicit motion/evaluation clips for steadier animation behavior.");
        }

        if (model.EyeArtifactSuppressedObservations > 0 || model.EyeReconstructedObservations > 0)
        {
            model.Warnings.Add("Some eye observations used reconstruction or artifact suppression; review glasses/glare clips before using this for avatar animation.");
        }
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : Round(count / (double)total);
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed class DistributionAccumulator
    {
        private const double EmaAlpha = 0.045d;
        private int _count;
        private double _totalWeight;
        private double _weightedSum;
        private double _weightedSumSquares;
        private double? _minimum;
        private double? _maximum;
        private double? _ema;

        public void Add(double? value, double weight)
        {
            if (value is not double number || double.IsNaN(number) || double.IsInfinity(number) || weight <= 0d)
            {
                return;
            }

            var boundedWeight = Math.Clamp(weight, 0.05d, 2.00d);
            _count++;
            _totalWeight += boundedWeight;
            _weightedSum += number * boundedWeight;
            _weightedSumSquares += number * number * boundedWeight;
            _minimum = _minimum is double min ? Math.Min(min, number) : number;
            _maximum = _maximum is double max ? Math.Max(max, number) : number;
            var alpha = EmaAlpha * Math.Clamp(boundedWeight, 0.35d, 1.40d);
            _ema = _ema is double previous ? previous + (number - previous) * alpha : number;
        }

        public PersonalMetricDistribution ToModel()
        {
            double? average = _totalWeight <= 0d ? null : _weightedSum / _totalWeight;
            double? standardDeviation = _count <= 1 || average is not double mean
                ? null
                : Math.Sqrt(Math.Max(0d, _weightedSumSquares / _totalWeight - mean * mean));
            return new PersonalMetricDistribution
            {
                SampleCount = _count,
                TotalWeight = _totalWeight,
                Average = average,
                Minimum = _minimum,
                Maximum = _maximum,
                StandardDeviation = standardDeviation,
                ExponentialMovingAverage = _ema,
                NormalLow = average is double lowMean && standardDeviation is double lowStd
                    ? lowMean - lowStd * 2d
                    : _minimum,
                NormalHigh = average is double highMean && standardDeviation is double highStd
                    ? highMean + highStd * 2d
                    : _maximum
            };
        }
    }
}
