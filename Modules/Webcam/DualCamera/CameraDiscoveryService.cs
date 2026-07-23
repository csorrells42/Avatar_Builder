using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectShow;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

public static class CameraDiscoveryService
{
	public static Task<IReadOnlyList<CameraDevice>> GetVideoInputDevicesAsync()
	{
		TaskCompletionSource<IReadOnlyList<CameraDevice>> completion = new TaskCompletionSource<IReadOnlyList<CameraDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				completion.SetResult(CameraDeviceCatalog.MergeDevices(TryEnumerateMediaFoundationDevices(), TryEnumerateDirectShowDevices()));
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		});
		thread.IsBackground = true;
		thread.Name = "Avatar Builder Camera Enumerator";
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task;
	}

	private static IReadOnlyList<CameraDevice> TryEnumerateMediaFoundationDevices()
	{
		try
		{
			return MediaFoundationCameraEnumerator.GetVideoInputDevices();
		}
		catch
		{
			return Array.Empty<CameraDevice>();
		}
	}

	private static IReadOnlyList<CameraDevice> TryEnumerateDirectShowDevices()
	{
		try
		{
			return DirectShowCameraEnumerator.GetVideoInputDevices();
		}
		catch
		{
			return Array.Empty<CameraDevice>();
		}
	}
}
