using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Webcam.Common;

public static class CameraModeRecommendation
{
	public static CameraVideoMode? FindRecommendedMode(IReadOnlyList<CameraVideoMode> modes, int maximumWidth, double targetFramesPerSecond)
	{
		List<CameraVideoMode> list = modes.Where(delegate(CameraVideoMode mode)
		{
			if (!mode.IsAuto)
			{
				int? width = mode.Width;
				if (width.HasValue && width.GetValueOrDefault() > 0)
				{
					width = mode.Height;
					if (width.HasValue)
					{
						return width.GetValueOrDefault() > 0;
					}
					return false;
				}
			}
			return false;
		}).ToList();
		if (list.Count == 0)
		{
			return modes.FirstOrDefault((CameraVideoMode mode) => mode.IsAuto) ?? modes.FirstOrDefault();
		}
		List<CameraVideoMode> list2 = list.Where((CameraVideoMode mode) => mode.Width.GetValueOrDefault() <= maximumWidth).ToList();
		if (list2.Count == 0)
		{
			return (from mode in list
				orderby mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault(), FrameRateLoadPriority(mode.FramesPerSecond, targetFramesPerSecond), CaptureFormatPriority(mode.InputFormat)
				select mode).FirstOrDefault();
		}
		return (from mode in list2
			orderby mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault() descending, FrameRateLoadPriority(mode.FramesPerSecond, targetFramesPerSecond), CaptureFormatPriority(mode.InputFormat)
			select mode).FirstOrDefault();
	}

	public static double FrameRateLoadPriority(double? framesPerSecond, double targetFramesPerSecond)
	{
		if (framesPerSecond.HasValue)
		{
			double valueOrDefault = framesPerSecond.GetValueOrDefault();
			if (!(valueOrDefault <= 0.0))
			{
				double num = Math.Clamp(targetFramesPerSecond, 1.0, 60.0);
				if (!(valueOrDefault <= num + 0.25))
				{
					return 1000.0 + valueOrDefault - num;
				}
				return num - valueOrDefault;
			}
		}
		return double.MaxValue;
	}

	public static int CaptureFormatPriority(string? format)
	{
		switch (format?.ToLowerInvariant())
		{
		case "mjpeg":
		case "mjpg":
			return 0;
		case "h264":
			return 1;
		case "nv12":
			return 2;
		case "rgb32":
			return 3;
		case null:
			return 4;
		default:
			return 5;
		}
	}
}
