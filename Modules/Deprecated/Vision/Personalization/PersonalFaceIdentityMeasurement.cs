using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceIdentityMeasurement
{
    public bool HasMeasurement => UsableFeatureCount >= 5;

    public int UsableFeatureCount { get; set; }

    public double? FaceAspectRatio { get; set; }

    public double? EyeMidlineXToFaceWidth { get; set; }

    public double? MouthCenterXToFaceWidth { get; set; }

    public double? EyeToMouthXOffsetToFaceWidth { get; set; }

    public double? InterEyeDistanceToFaceWidth { get; set; }

    public double? LeftEyeWidthToFaceWidth { get; set; }

    public double? RightEyeWidthToFaceWidth { get; set; }

    public double? MouthWidthToFaceWidth { get; set; }

    public double? EyeMidlineYToFaceHeight { get; set; }

    public double? MouthCenterYToFaceHeight { get; set; }

    public double? EyeToMouthYDistanceToFaceHeight { get; set; }

    public static PersonalFaceIdentityMeasurement FromFrame(FaceLandmarkFrame frame)
    {
        var measurement = new PersonalFaceIdentityMeasurement();
        if (!frame.HasFace || frame.FaceContour.Count < 4)
        {
            return measurement;
        }

        var face = Bounds(frame.FaceContour);
        if (face.Width <= 0d || face.Height <= 0d)
        {
            return measurement;
        }

        var leftEye = frame.LeftEyeContour.Count >= 4 ? Bounds(frame.LeftEyeContour) : Rect.Empty;
        var rightEye = frame.RightEyeContour.Count >= 4 ? Bounds(frame.RightEyeContour) : Rect.Empty;
        var mouthContour = frame.OuterLipContour.Count >= 4
            ? frame.OuterLipContour
            : frame.InnerLipContour.Count >= 4 ? frame.InnerLipContour : [];
        var mouth = mouthContour.Count >= 4 ? Bounds(mouthContour) : Rect.Empty;

        measurement.FaceAspectRatio = SafeRatio(face.Height, face.Width);
        if (!leftEye.IsEmpty && !rightEye.IsEmpty)
        {
            var leftCenter = Center(leftEye);
            var rightCenter = Center(rightEye);
            var eyeCenterX = (leftCenter.X + rightCenter.X) / 2d;
            var eyeCenterY = (leftCenter.Y + rightCenter.Y) / 2d;
            measurement.EyeMidlineXToFaceWidth = SafeRatio(eyeCenterX - face.Left, face.Width);
            measurement.InterEyeDistanceToFaceWidth = SafeRatio(Math.Abs(rightCenter.X - leftCenter.X), face.Width);
            measurement.LeftEyeWidthToFaceWidth = SafeRatio(leftEye.Width, face.Width);
            measurement.RightEyeWidthToFaceWidth = SafeRatio(rightEye.Width, face.Width);
            measurement.EyeMidlineYToFaceHeight = SafeRatio(eyeCenterY - face.Top, face.Height);
            if (!mouth.IsEmpty)
            {
                var mouthCenter = Center(mouth);
                measurement.MouthCenterXToFaceWidth = SafeRatio(mouthCenter.X - face.Left, face.Width);
                measurement.EyeToMouthXOffsetToFaceWidth = SafeRatio(Math.Abs(mouthCenter.X - eyeCenterX), face.Width);
                measurement.MouthCenterYToFaceHeight = SafeRatio(mouthCenter.Y - face.Top, face.Height);
                measurement.EyeToMouthYDistanceToFaceHeight = SafeRatio(mouthCenter.Y - eyeCenterY, face.Height);
            }
        }

        if (!mouth.IsEmpty)
        {
            measurement.MouthWidthToFaceWidth = SafeRatio(mouth.Width, face.Width);
        }

        measurement.UsableFeatureCount = measurement.Values().Count(static value => value.HasValue);
        return measurement;
    }

    public IEnumerable<(string Name, double? Value)> NamedValues()
    {
        yield return ("Face aspect", FaceAspectRatio);
        yield return ("Eye horizontal position", EyeMidlineXToFaceWidth);
        yield return ("Mouth horizontal position", MouthCenterXToFaceWidth);
        yield return ("Eye-to-mouth horizontal offset", EyeToMouthXOffsetToFaceWidth);
        yield return ("Eye spacing / face width", InterEyeDistanceToFaceWidth);
        yield return ("Left eye width / face width", LeftEyeWidthToFaceWidth);
        yield return ("Right eye width / face width", RightEyeWidthToFaceWidth);
        yield return ("Mouth width / face width", MouthWidthToFaceWidth);
        yield return ("Eye vertical position", EyeMidlineYToFaceHeight);
        yield return ("Mouth vertical position", MouthCenterYToFaceHeight);
        yield return ("Eye-to-mouth vertical span", EyeToMouthYDistanceToFaceHeight);
    }

    private IEnumerable<double?> Values()
    {
        yield return FaceAspectRatio;
        yield return EyeMidlineXToFaceWidth;
        yield return MouthCenterXToFaceWidth;
        yield return EyeToMouthXOffsetToFaceWidth;
        yield return InterEyeDistanceToFaceWidth;
        yield return LeftEyeWidthToFaceWidth;
        yield return RightEyeWidthToFaceWidth;
        yield return MouthWidthToFaceWidth;
        yield return EyeMidlineYToFaceHeight;
        yield return MouthCenterYToFaceHeight;
        yield return EyeToMouthYDistanceToFaceHeight;
    }

    private static Rect Bounds(IReadOnlyList<Point> points)
    {
        var left = points.Min(static point => point.X);
        var top = points.Min(static point => point.Y);
        var right = points.Max(static point => point.X);
        var bottom = points.Max(static point => point.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Point Center(Rect rect)
    {
        return new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d);
    }

    private static double? SafeRatio(double numerator, double denominator)
    {
        return denominator > 0d && !double.IsNaN(numerator) && !double.IsInfinity(numerator)
            ? numerator / denominator
            : null;
    }
}
