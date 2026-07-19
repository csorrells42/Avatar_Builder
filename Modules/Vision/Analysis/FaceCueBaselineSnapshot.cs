namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceCueBaselineSnapshot
{
    public int BaselineSamples { get; set; }

    public double EyeBaseline { get; set; }

    public double LeftEyeBaseline { get; set; }

    public double RightEyeBaseline { get; set; }

    public double JawBaseline { get; set; }

    public double LeftJawBaseline { get; set; }

    public double RightJawBaseline { get; set; }

    public double LowerFaceCenterBaseline { get; set; }

    public double FaceCenterBaseline { get; set; }
}
