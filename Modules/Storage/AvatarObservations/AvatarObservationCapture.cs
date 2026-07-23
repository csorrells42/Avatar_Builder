using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarObservationCapture(string ProfileFolder, string SubjectId, string SubjectDisplayName, AvatarReconstructionSnapshot Reconstruction, BitmapSource SourceFrame, AvatarCaptureQualityAssessment CaptureQuality, FaceFrameGeometry FaceGeometry);
