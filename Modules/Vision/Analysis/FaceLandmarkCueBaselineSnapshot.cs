namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkCueBaselineSnapshot
{
    public double EyeBaseline { get; set; }

    public double MouthBaseline { get; set; }

    public double JawDroopBaseline { get; set; }

    public double MediaPipeBlinkBaseline { get; set; }

    public double MediaPipeJawOpenBaseline { get; set; }

    public double MediaPipeMouthCloseBaseline { get; set; }

    public int EyeBaselineSamples { get; set; }

    public int MouthBaselineSamples { get; set; }

    public int JawDroopBaselineSamples { get; set; }

    public int MediaPipeBlinkBaselineSamples { get; set; }

    public int MediaPipeJawOpenBaselineSamples { get; set; }

    public int MediaPipeMouthCloseBaselineSamples { get; set; }
}
