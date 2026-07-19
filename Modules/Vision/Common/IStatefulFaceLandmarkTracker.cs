namespace AvatarBuilder.Modules.Vision.Common;

public interface IStatefulFaceLandmarkTracker : IFaceLandmarkTracker
{
    void Reset();
}
