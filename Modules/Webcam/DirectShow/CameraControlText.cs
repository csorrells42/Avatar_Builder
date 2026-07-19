using AvatarBuilder.Modules.Webcam.Common;
namespace AvatarBuilder.Modules.Webcam.DirectShow;

internal static class CameraControlText
{
    internal static string FormatChooseCameraControlsStatus()
    {
        return "Choose a camera to load controls.";
    }

    internal static string FormatNoCameraControlsStatus()
    {
        return "No standard Windows camera controls were exposed by this source.";
    }

    internal static string FormatCameraControlsLoadedStatus(CameraDevice camera, int controlCount)
    {
        return $"{controlCount} Windows camera controls exposed by {camera.Name}.";
    }

    internal static string FormatCameraControlSetStatus(CameraControlItem control, int value, bool isAuto, bool success)
    {
        return success
            ? $"{control.Name}: {(isAuto ? "Auto" : FormatCameraControlValue(value))}"
            : $"Could not set {control.Name}. The camera may be busy or this control may be locked by its driver.";
    }

    internal static int RoundCameraControlToStep(double value, CameraControlItem control)
    {
        var step = Math.Max(1, control.Step);
        var rounded = control.Minimum + (int)Math.Round((value - control.Minimum) / step) * step;
        return Math.Clamp(rounded, control.Minimum, control.Maximum);
    }

    internal static int ApplyCameraControlDefaultMagnet(int value, CameraControlItem control)
    {
        var snapDistance = Math.Max(control.Step * 1.25d, (control.Maximum - control.Minimum) * 0.025d);
        return Math.Abs(value - control.DefaultValue) <= snapDistance
            ? control.DefaultValue
            : value;
    }

    internal static string FormatCameraControlValue(int value)
    {
        return value.ToString("0");
    }
}
