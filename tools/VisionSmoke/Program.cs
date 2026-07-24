using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;
using AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;
using AvatarBuilder.Modules.Vision.Reconstruction.Warping;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.DirectX12;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

var brow = MediaPipeBrowGeometrySelfTest.Run();
var measuredFace = MediaPipeNormalizedFaceReconstructorSelfTest.Run();
var stereoFace = MediaPipeStereoFaceReconstructorSelfTest.Run();
var denseStereo = MediaPipeDenseStereoMatcherSelfTest.Run();
var denseWarp = EvidenceWeightedDenseFaceWarperSelfTest.Run();
var liveCameraPipeline = LiveCameraPipelineSelfTest.Run();
var faceGeometry = MediaPipeFaceGeometryEstimatorSelfTest.Run();
using var peopleMemory = new PersonIdentityMemory();
var peopleMemoryPolicy = PersonIdentityMemorySelfTest.Run();

var results = new List<(string Name, bool Succeeded, string Detail)>
{
    ("MediaPipe brow geometry", brow.Succeeded, brow.Detail),
    ("MediaPipe measured face", measuredFace.Succeeded, measuredFace.Detail),
    ("Calibrated stereo face", stereoFace.Succeeded, stereoFace.Detail),
    ("Dense stereo image matching", denseStereo.Succeeded, denseStereo.Detail),
    ("MediaPipe-guided dense warp", denseWarp.Succeeded, denseWarp.Detail),
    ("Live camera no-backlog pipeline", liveCameraPipeline.Succeeded, liveCameraPipeline.Detail)
    ,("MediaPipe GPU face geometry", faceGeometry.Succeeded, faceGeometry.Detail),
    (
        "Multi-person identity model",
        peopleMemory.IsAvailable,
        peopleMemory.Status),
    (
        "People-memory retention and consent policy",
        peopleMemoryPolicy.Succeeded,
        peopleMemoryPolicy.Detail)
};

var identityImageIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--identity-image",
        StringComparison.OrdinalIgnoreCase));
if (identityImageIndex >= 0)
{
    if (identityImageIndex + 1 >= args.Length)
    {
        results.Add((
            "People-memory real-image observation",
            false,
            "--identity-image requires an image path."));
    }
    else
    {
        try
        {
            using var source = Cv2.ImRead(
                Path.GetFullPath(args[identityImageIndex + 1]),
                ImreadModes.Color);
            using var bgra = new Mat();
            Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
            var pixels = new byte[checked(bgra.Width * bgra.Height * 4)];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
            peopleMemory.ObserveBgra(
                pixels,
                bgra.Width,
                bgra.Height,
                bgra.Width * 4,
                DateTime.UtcNow);
            var snapshot = peopleMemory.LatestSnapshot;
            results.Add((
                "People-memory real-image observation",
                snapshot.People.Count > 0,
                snapshot.People.Count > 0
                    ? $"{snapshot.Status}; backend {snapshot.Backend}."
                    : "YuNet/SFace did not produce a face observation."));
        }
        catch (Exception ex)
        {
            results.Add((
                "People-memory real-image observation",
                false,
                ex.ToString()));
        }
    }
}

var directMlImageIndex = Array.FindIndex(
    args,
    value => string.Equals(value, "--directml-image", StringComparison.OrdinalIgnoreCase));
if (directMlImageIndex >= 0)
{
    if (directMlImageIndex + 1 >= args.Length)
    {
        results.Add((
            "MediaPipe DirectML app transport",
            false,
            "--directml-image requires an image path."));
    }
    else
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(args[directMlImageIndex + 1]));
            bitmap.EndInit();
            bitmap.Freeze();

			using var tracker = new MediaPipeFaceLandmarkerSidecarTracker(
				MediaPipeExecutionBackend.Gpu,
				collectDiagnostics: true);
            var directMlResult = tracker.Detect(bitmap, DateTime.UtcNow);
            var startupMilliseconds = directMlResult.Diagnostics.EndToEndMilliseconds;
            var steadySamples = new List<double>();
            var allFramesValid =
                directMlResult.HasFace &&
                directMlResult.LandmarkFrame.DenseMeshPoints.Count == 478 &&
                directMlResult.LandmarkFrame.FacialTransformationMatrix.Count == 16;
            for (var frameNumber = 1; frameNumber < 8; frameNumber++)
            {
                directMlResult = tracker.Detect(
                    bitmap,
                    DateTime.UtcNow.AddMilliseconds(frameNumber));
                allFramesValid &=
                    directMlResult.HasFace &&
                    directMlResult.LandmarkFrame.DenseMeshPoints.Count == 478 &&
                    directMlResult.LandmarkFrame.FacialTransformationMatrix.Count == 16;
                steadySamples.Add(directMlResult.Diagnostics.EndToEndMilliseconds);
            }
            var steadyMilliseconds = steadySamples.Average();
            var densePointCount = directMlResult.LandmarkFrame.DenseMeshPoints.Count;
            results.Add((
                "MediaPipe DirectML app transport",
                allFramesValid,
                $"{directMlResult.BackendStatus}; {densePointCount} dense points; " +
                $"{directMlResult.LandmarkFrame.FacialTransformationMatrix.Count} pose values; " +
                $"cold start {startupMilliseconds:0.00} ms; steady app round trip " +
                $"{steadyMilliseconds:0.00} ms ({1000.0 / steadyMilliseconds:0.0} fps)."));
        }
        catch (Exception ex)
        {
            results.Add((
                "MediaPipe DirectML app transport",
                false,
                ex.ToString()));
        }
    }
}

var poseParityVideoIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--mediapipe-pose-parity-video",
        StringComparison.OrdinalIgnoreCase));
if (poseParityVideoIndex >= 0)
{
    if (poseParityVideoIndex + 1 >= args.Length)
    {
        results.Add((
            "MediaPipe CPU/GPU pose parity",
            false,
            "--mediapipe-pose-parity-video requires a video path."));
    }
    else
    {
        try
        {
            var videoPath = Path.GetFullPath(
                args[poseParityVideoIndex + 1]);
            using var capture = new VideoCapture(videoPath);
            if (!capture.IsOpened())
            {
                throw new InvalidOperationException(
                    "OpenCV could not open the pose-parity video.");
            }
            var frameCount = Math.Max(
                1,
                (int)capture.Get(VideoCaptureProperties.FrameCount));
            var sampleIndices = Enumerable.Range(0, 14)
                .Select(index => (int)Math.Round(
                    index * (frameCount - 1) / 13.0))
                .Distinct()
                .ToArray();
            using var cpuTracker =
                new MediaPipeFaceLandmarkerSidecarTracker(
                    MediaPipeExecutionBackend.Cpu);
            using var gpuTracker =
                new MediaPipeFaceLandmarkerSidecarTracker(
                    MediaPipeExecutionBackend.Gpu);
            using var bgr = new Mat();
            using var bgra = new Mat();
            var yawErrors = new List<double>();
            var pitchErrors = new List<double>();
            var rollErrors = new List<double>();
            var start = DateTime.UtcNow;
            for (var sample = 0; sample < sampleIndices.Length; sample++)
            {
                capture.Set(
                    VideoCaptureProperties.PosFrames,
                    sampleIndices[sample]);
                if (!capture.Read(bgr) || bgr.Empty())
                {
                    continue;
                }
                Cv2.CvtColor(
                    bgr,
                    bgra,
                    ColorConversionCodes.BGR2BGRA);
                var stride = checked(bgra.Width * 4);
                var pixels = new byte[checked(stride * bgra.Height)];
                Marshal.Copy(
                    bgra.Data,
                    pixels,
                    0,
                    pixels.Length);
                var bitmap = BitmapSource.Create(
                    bgra.Width,
                    bgra.Height,
                    96.0,
                    96.0,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);
                bitmap.Freeze();
                var capturedAtUtc =
                    start.AddMilliseconds(sample + 1);
                var cpu = cpuTracker.Detect(
                    bitmap,
                    capturedAtUtc);
                var gpu = gpuTracker.Detect(
                    bitmap,
                    capturedAtUtc);
                if (!cpu.HasFace
                    || !gpu.HasFace
                    || cpu.LandmarkFrame.FacialTransformationMatrix.Count != 16
                    || gpu.LandmarkFrame.FacialTransformationMatrix.Count != 16)
                {
                    continue;
                }
                yawErrors.Add(Math.Abs(
                    cpu.LandmarkFrame.HeadYawDegrees
                    - gpu.LandmarkFrame.HeadYawDegrees));
                pitchErrors.Add(Math.Abs(
                    cpu.LandmarkFrame.HeadPitchDegrees
                    - gpu.LandmarkFrame.HeadPitchDegrees));
                rollErrors.Add(Math.Abs(
                    cpu.LandmarkFrame.HeadRollDegrees
                    - gpu.LandmarkFrame.HeadRollDegrees));
            }
            var enoughSamples = yawErrors.Count >= 10;
            var meanYaw = enoughSamples ? yawErrors.Average() : double.PositiveInfinity;
            var meanPitch = enoughSamples ? pitchErrors.Average() : double.PositiveInfinity;
            var meanRoll = enoughSamples ? rollErrors.Average() : double.PositiveInfinity;
            var maximumYaw = enoughSamples ? yawErrors.Max() : double.PositiveInfinity;
            var maximumPitch = enoughSamples ? pitchErrors.Max() : double.PositiveInfinity;
            var maximumRoll = enoughSamples ? rollErrors.Max() : double.PositiveInfinity;
            var parityPassed =
                enoughSamples
                && meanYaw < 3.0
                && meanPitch < 3.0
                && meanRoll < 3.0
                && maximumYaw < 7.0
                && maximumPitch < 7.0
                && maximumRoll < 7.0;
            results.Add((
                "MediaPipe CPU/GPU pose parity",
                parityPassed,
                $"{yawErrors.Count} matched frames; " +
                $"mean absolute yaw/pitch/roll error " +
                $"{meanYaw:0.00}/{meanPitch:0.00}/{meanRoll:0.00} degrees; " +
                $"maximum {maximumYaw:0.00}/{maximumPitch:0.00}/{maximumRoll:0.00} degrees."));
        }
        catch (Exception ex)
        {
            results.Add((
                "MediaPipe CPU/GPU pose parity",
                false,
                ex.ToString()));
        }
    }
}

var directMlTextureVideoIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--directml-texture-video",
        StringComparison.OrdinalIgnoreCase));
if (directMlTextureVideoIndex >= 0)
{
    if (directMlTextureVideoIndex + 1 >= args.Length)
    {
        results.Add((
            "MediaPipe DirectML GPU texture",
            false,
            "--directml-texture-video requires a video path."));
    }
    else
    {
        try
        {
            var videoPath = Path.GetFullPath(
                args[directMlTextureVideoIndex + 1]);
            using var capture = new VideoCapture(videoPath);
            using var bgr = new Mat();
            using var bgra = new Mat();
            if (!capture.IsOpened()
                || !capture.Read(bgr)
                || bgr.Empty())
            {
                throw new InvalidOperationException(
                    "OpenCV could not decode the first video frame.");
            }
            Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            var width = bgra.Width;
            var height = bgra.Height;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
            var textureResult = MediaPipeGpuTextureSelfTest.RunBgra(
                pixels,
                width,
                height,
                stride);
            results.Add((
                "MediaPipe DirectML GPU texture",
                textureResult.Succeeded,
                textureResult.Detail));
        }
        catch (Exception ex)
        {
            results.Add((
                "MediaPipe DirectML GPU texture",
                false,
                ex.ToString()));
        }
    }
}

var directMlCameraIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--directml-camera",
        StringComparison.OrdinalIgnoreCase));
if (directMlCameraIndex >= 0)
{
    try
    {
        var requestedCamera =
            directMlCameraIndex + 1 < args.Length
            && !args[directMlCameraIndex + 1].StartsWith(
                "--",
                StringComparison.Ordinal)
                ? args[directMlCameraIndex + 1]
                : string.Empty;
        var cameras = CameraSourceSelection.GetCameras();
        var camera = string.IsNullOrWhiteSpace(requestedCamera)
            ? cameras.FirstOrDefault()
            : cameras.FirstOrDefault(
                candidate => candidate.Name.Contains(
                    requestedCamera,
                    StringComparison.OrdinalIgnoreCase));
        if (camera == null)
        {
            throw new InvalidOperationException(
                $"Camera '{requestedCamera}' was not found. Available: " +
                string.Join(", ", cameras.Select(candidate => candidate.Name)));
        }

        using var frameReady = new ManualResetEventSlim(false);
        using var stream = new TextureNativeCameraStream(
            camera,
            CameraVideoMode.Auto,
            startImmediately: false);
        var lastCameraStatus = string.Empty;
        stream.StatusChanged += (_, status) =>
            lastCameraStatus = status;
        TextureNativeFrameLease? retainedFrame = null;
        var observedFrames = 0;
        stream.TextureFrameAvailable += (_, frame) =>
        {
            if (Interlocked.Increment(ref observedFrames) < 4)
            {
                return;
            }
            var duplicate = frame.Duplicate();
            if (duplicate == null)
            {
                return;
            }
            if (Interlocked.CompareExchange(
                ref retainedFrame,
                duplicate,
                null) == null)
            {
                frameReady.Set();
            }
            else
            {
                duplicate.Dispose();
            }
        };
        stream.Start();
        if (!frameReady.Wait(TimeSpan.FromSeconds(8)))
        {
            throw new TimeoutException(
                "The camera did not provide a retained GPU texture within 8 seconds.");
        }
        stream.Stop();

        using (retainedFrame)
        {
            if (retainedFrame == null)
            {
                throw new InvalidOperationException(
                    "The camera handoff completed without a retained texture.");
            }
            var tracked =
                MediaPipeGpuTextureSelfTest.RunFrame(retainedFrame);
            var textureProbe =
                D3D11Nv12TextureProbe.Run(retainedFrame);
            results.Add((
                "Live camera D3D11 bridge to DirectML",
                tracked.Succeeded && textureProbe.Succeeded,
                $"{camera.Name}; {retainedFrame.DeviceMode}; " +
                $"subresource {retainedFrame.Subresource}; " +
                $"shared texture {retainedFrame.D3D12SharedTextureHandle != IntPtr.Zero}; " +
                $"stream status {lastCameraStatus}; " +
                $"{tracked.Detail} Texture pixels: {textureProbe.Detail}"));
        }
    }
    catch (Exception ex)
    {
        results.Add((
            "Live camera D3D11 bridge to DirectML",
            false,
            ex.ToString()));
    }
}

var cameraSoakIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--camera-soak-seconds",
        StringComparison.OrdinalIgnoreCase));
if (cameraSoakIndex >= 0)
{
    try
    {
        var durationSeconds =
            cameraSoakIndex + 1 < args.Length
            && double.TryParse(
                args[cameraSoakIndex + 1],
                out var parsedDurationSeconds)
                ? Math.Clamp(parsedDurationSeconds, 5.0, 86400.0)
                : 300.0;
        var cameraNameIndex = Array.FindIndex(
            args,
            value => string.Equals(
                value,
                "--camera-soak-camera",
                StringComparison.OrdinalIgnoreCase));
        var requestedCamera =
            cameraNameIndex >= 0
            && cameraNameIndex + 1 < args.Length
                ? args[cameraNameIndex + 1]
                : string.Empty;
        var cameras = CameraSourceSelection.GetCameras();
        var camera = string.IsNullOrWhiteSpace(requestedCamera)
            ? cameras.FirstOrDefault()
            : cameras.FirstOrDefault(
                candidate => candidate.Name.Contains(
                    requestedCamera,
                    StringComparison.OrdinalIgnoreCase));
        if (camera == null)
        {
            throw new InvalidOperationException(
                $"Camera '{requestedCamera}' was not found. Available: " +
                string.Join(", ", cameras.Select(candidate => candidate.Name)));
        }

        using var stream = new TextureNativeCameraStream(
            camera,
            CameraVideoMode.Auto,
            startImmediately: false);
        var observedTextureFrames = 0L;
        var injectedObserverFailures = 0L;
        var lastObservedTextureTimestamp = 0L;
        var maximumObservedTextureGap = TimeSpan.Zero;
        var lastStatus = string.Empty;
        stream.StatusChanged += (_, status) =>
            Volatile.Write(ref lastStatus, status);
        stream.TextureFrameAvailable += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(
                ref lastObservedTextureTimestamp,
                now);
            if (previous != 0L)
            {
                var gap = Stopwatch.GetElapsedTime(previous, now);
                if (gap > maximumObservedTextureGap)
                {
                    maximumObservedTextureGap = gap;
                }
            }
            var observed = Interlocked.Increment(
                ref observedTextureFrames);
            if (observed % 90L == 0L)
            {
                // This deliberately abuses the downstream observer. Source
                // ingestion must continue, dropping work while this lane sleeps.
                Thread.Sleep(250);
            }
            if (observed % 211L == 0L)
            {
                Interlocked.Increment(ref injectedObserverFailures);
                throw new InvalidOperationException(
                    "Injected downstream observer failure.");
            }
        };

        stream.Start();
        var soakStarted = Stopwatch.GetTimestamp();
        var lastSourceProgressTimestamp = soakStarted;
        var previousFramesRead = stream.FramesRead;
        var sourceStalled = false;
        while (Stopwatch.GetElapsedTime(soakStarted).TotalSeconds <
            durationSeconds)
        {
            Thread.Sleep(250);
            var framesRead = stream.FramesRead;
            if (framesRead > previousFramesRead)
            {
                previousFramesRead = framesRead;
                lastSourceProgressTimestamp = Stopwatch.GetTimestamp();
            }
            if (Stopwatch.GetElapsedTime(lastSourceProgressTimestamp) >
                TimeSpan.FromSeconds(2))
            {
                sourceStalled = true;
                break;
            }
        }
        var finalSourceTimestamp = stream.LastSourceFrameTimestamp;
        var sourceCurrentAtEnd = finalSourceTimestamp != 0L
            && Stopwatch.GetElapsedTime(finalSourceTimestamp) <
                TimeSpan.FromSeconds(2);
        stream.Stop();

        var elapsed = Stopwatch.GetElapsedTime(soakStarted);
        var sourceFrames = stream.FramesRead;
        var observedFrames = Interlocked.Read(
            ref observedTextureFrames);
        var sourceFps = sourceFrames /
            Math.Max(0.001, elapsed.TotalSeconds);
        var succeeded = !sourceStalled
            && sourceFrames > 0L
            && observedFrames > 0L
            && sourceCurrentAtEnd;
        results.Add((
            "Live camera continuity soak",
            succeeded,
            $"{camera.Name}; elapsed {elapsed}; source {sourceFrames} " +
            $"({sourceFps:0.0} fps); downstream observed {observedFrames}; " +
            $"pre-display busy drops {stream.FramesDroppedWhileProcessingBusy}; " +
            $"injected observer failures {injectedObserverFailures}; " +
            $"max downstream gap {maximumObservedTextureGap.TotalMilliseconds:0} ms; " +
            $"source stalled {sourceStalled}; last status {lastStatus}"));
    }
    catch (Exception ex)
    {
        results.Add((
            "Live camera continuity soak",
            false,
            ex.ToString()));
    }
}

var reopenCyclesIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--camera-reopen-cycles",
        StringComparison.OrdinalIgnoreCase));
if (reopenCyclesIndex >= 0)
{
    try
    {
        var requestedCycles =
            reopenCyclesIndex + 1 < args.Length
            && int.TryParse(
                args[reopenCyclesIndex + 1],
                out var parsedCycles)
                ? Math.Clamp(parsedCycles, 1, 100)
                : 10;
        var camera = CameraSourceSelection.GetCameras().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No camera is available for reopen testing.");
        var completedCycles = 0;
        var maximumOpenDuration = TimeSpan.Zero;
        var minimumFramesPerCycle = long.MaxValue;
        var cycleDetails = new List<string>();
        for (var cycle = 1; cycle <= requestedCycles; cycle++)
        {
            var openedAt = Stopwatch.GetTimestamp();
            using var stream = new TextureNativeCameraStream(
                camera,
                CameraVideoMode.Auto,
                startImmediately: false);
            stream.Start();
            var readyDeadline = Stopwatch.GetTimestamp()
                + Stopwatch.Frequency * 5L;
            while (stream.FramesRead < 5L
                && Stopwatch.GetTimestamp() < readyDeadline)
            {
                Thread.Sleep(10);
            }
            var openDuration = Stopwatch.GetElapsedTime(openedAt);
            maximumOpenDuration = openDuration > maximumOpenDuration
                ? openDuration
                : maximumOpenDuration;
            var frames = stream.FramesRead;
            minimumFramesPerCycle = Math.Min(
                minimumFramesPerCycle,
                frames);
            if (frames < 5L
                || stream.LastSourceFrameTimestamp == 0L
                || Stopwatch.GetElapsedTime(
                    stream.LastSourceFrameTimestamp) >
                    TimeSpan.FromSeconds(2))
            {
                throw new InvalidOperationException(
                    $"Cycle {cycle} did not reach five current frames " +
                    $"within five seconds (frames={frames}).");
            }
            stream.Stop();
            completedCycles++;
            cycleDetails.Add(
                $"{cycle}:{openDuration.TotalMilliseconds:0}ms");
            Thread.Sleep(100);
        }
        results.Add((
            "Live camera repeated reopen",
            completedCycles == requestedCycles,
            $"{camera.Name}; cycles {completedCycles}/{requestedCycles}; " +
            $"minimum frames before close {minimumFramesPerCycle}; " +
            $"maximum open-to-five-frames " +
            $"{maximumOpenDuration.TotalMilliseconds:0} ms; " +
            string.Join(", ", cycleDetails)));
    }
    catch (Exception ex)
    {
        results.Add((
            "Live camera repeated reopen",
            false,
            ex.ToString()));
    }
}

var previewSoakIndex = Array.FindIndex(
    args,
    value => string.Equals(
        value,
        "--dx12-preview-soak-seconds",
        StringComparison.OrdinalIgnoreCase));
if (previewSoakIndex >= 0)
{
    var durationSeconds =
        previewSoakIndex + 1 < args.Length
        && double.TryParse(
            args[previewSoakIndex + 1],
            out var parsedPreviewDurationSeconds)
            ? Math.Clamp(parsedPreviewDurationSeconds, 5.0, 86400.0)
            : 120.0;
    var previewResultLock = new object();
    var previewSucceeded = false;
    var previewDetail = "DX12 preview soak did not complete.";
    var previewThread = new Thread(() =>
    {
        Dx12Camera? camera = null;
        Dx12CameraWindow? window = null;
        DispatcherTimer? timer = null;
        try
        {
            var devices = CameraSourceSelection.GetCameras();
            var device = devices.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No camera is available for the DX12 preview soak.");
            var renderedFrames = 0L;
            var downstreamFrames = 0L;
            var injectedDownstreamFailures = 0L;
            var lastRenderedTimestamp = 0L;
            var maximumRenderedGap = TimeSpan.Zero;
            var lastStatus = string.Empty;
            window = WebcamModule.CreateDx12CameraWindow(
                new Dx12CameraOptions
                {
                    Camera = device,
                    Mode = CameraVideoMode.Auto,
                    DiagnosticsChanged = (_, diagnostics) =>
                    {
                        var now = Stopwatch.GetTimestamp();
                        var previous = Interlocked.Exchange(
                            ref lastRenderedTimestamp,
                            now);
                        if (previous != 0L)
                        {
                            var gap = Stopwatch.GetElapsedTime(
                                previous,
                                now);
                            if (gap > maximumRenderedGap)
                            {
                                maximumRenderedGap = gap;
                            }
                        }
                        Interlocked.Exchange(
                            ref renderedFrames,
                            diagnostics.RenderedFrames);
                    },
                    TextureFrameAvailable = (_, _) =>
                    {
                        var observed = Interlocked.Increment(
                            ref downstreamFrames);
                        if (observed % 90L == 0L)
                        {
                            Thread.Sleep(250);
                        }
                        if (observed % 211L == 0L)
                        {
                            Interlocked.Increment(
                                ref injectedDownstreamFailures);
                            throw new InvalidOperationException(
                                "Injected preview observer failure.");
                        }
                    },
                    StatusChanged = (_, status) =>
                        Volatile.Write(ref lastStatus, status)
                },
                "Avatar Builder DX12 Camera Continuity Soak");
            window.Left = 24;
            window.Top = 24;
            window.ShowActivated = false;
            window.ShowInTaskbar = true;
            window.Show();
            camera = window.CameraSession
                ?? window.StartCamera();

            var started = Stopwatch.GetTimestamp();
            var sourceStalled = false;
            var displayStalled = false;
            timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(100),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = Stopwatch.GetElapsedTime(
                        started,
                        now);
                    if (elapsed > TimeSpan.FromSeconds(4))
                    {
                        var sourceTimestamp =
                            camera.LastSourceFrameTimestamp;
                        var presentedTimestamp =
                            camera.LastPresentedFrameTimestamp;
                        sourceStalled = sourceTimestamp == 0L
                            || Stopwatch.GetElapsedTime(
                                sourceTimestamp,
                                now) > TimeSpan.FromSeconds(2);
                        displayStalled = presentedTimestamp == 0L
                            || Stopwatch.GetElapsedTime(
                                presentedTimestamp,
                                now) > TimeSpan.FromSeconds(2);
                    }
                    if (elapsed.TotalSeconds < durationSeconds
                        && !sourceStalled
                        && !displayStalled)
                    {
                        return;
                    }

                    timer?.Stop();
                    var sourceFrames = camera.FramesRead;
                    var displayedFrames =
                        Interlocked.Read(ref renderedFrames);
                    var observedFrames =
                        Interlocked.Read(ref downstreamFrames);
                    var succeeded = !sourceStalled
                        && !displayStalled
                        && sourceFrames > 0L
                        && displayedFrames > 0L;
                    var detail =
                        $"{device.Name}; elapsed {elapsed}; " +
                        $"source {sourceFrames}; displayed {displayedFrames}; " +
                        $"downstream observed {observedFrames}; " +
                        $"pre-display busy drops " +
                        $"{camera.FramesDroppedWhileProcessingBusy}; " +
                        $"injected downstream failures " +
                        $"{injectedDownstreamFailures}; " +
                        $"max diagnostics gap " +
                        $"{maximumRenderedGap.TotalMilliseconds:0} ms; " +
                        $"source stalled {sourceStalled}; " +
                        $"display stalled {displayStalled}; " +
                        $"last status {lastStatus}";
                    lock (previewResultLock)
                    {
                        previewSucceeded = succeeded;
                        previewDetail = detail;
                    }
                    camera.Dispose();
                    camera = null;
                    window.Close();
                    window = null;
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Background);
                },
                Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            lock (previewResultLock)
            {
                previewSucceeded = false;
                previewDetail = ex.ToString();
            }
            try
            {
                timer?.Stop();
                camera?.Dispose();
                window?.Close();
            }
            catch
            {
            }
        }
    })
    {
        IsBackground = true,
        Name = "Avatar Builder DX12 preview soak"
    };
    previewThread.SetApartmentState(ApartmentState.STA);
    previewThread.Start();
    var previewCompleted = previewThread.Join(
        TimeSpan.FromSeconds(durationSeconds + 30.0));
    lock (previewResultLock)
    {
        results.Add((
            "DX12 displayed-camera continuity soak",
            previewCompleted && previewSucceeded,
            previewCompleted
                ? previewDetail
                : "DX12 preview soak exceeded its bounded completion time."));
    }
}

var failed = false;
foreach (var result in results)
{
    Console.WriteLine($"{(result.Succeeded ? "PASS" : "FAIL")} | {result.Name}");
    Console.WriteLine(result.Detail);
    failed |= !result.Succeeded;
}

return failed ? 1 : 0;
