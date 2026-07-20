using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarObservationStorageSelfTest
{
    private const string SubjectId = "storage-smoke";

    public static AvatarObservationStorageSelfTestResult Run(string outputFolder, bool preserveArtifacts = false)
    {
        var root = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(root);
        var session = Path.Combine(root, $"run-{Guid.NewGuid():N}");
        var profileFolder = Path.Combine(session, "AvatarSystem", "People", SubjectId);
        var reportPath = Path.Combine(root, "avatar-storage-self-test.txt");
        var messages = new List<string>();
        var timer = Stopwatch.StartNew();
        try
        {
            var repository = new AvatarObservationRepository();
            var first = repository.SaveCapture(CreateCapture(profileFolder, "first", 82d, 80d, 0d));
            Require(first.Accepted && !first.ReplacedExisting, "First useful observation was not accepted.");
            var firstTopologyPath = repository
                .ReadDataset(profileFolder, SubjectId, "Storage Smoke")
                .Observations
                .Single()
                .TopologyObjectPath;

            var duplicateRequest = repository.SaveCapture(CreateCapture(profileFolder, "first", 82d, 80d, 0d));
            Require(!duplicateRequest.Accepted, "Duplicate request ID was stored twice.");

            var replacement = repository.SaveCapture(CreateCapture(profileFolder, "replacement", 99d, 98d, 0.02d));
            Require(replacement.Accepted && replacement.ReplacedExisting, "Higher-quality near duplicate did not replace weaker evidence.");

            var lowerValue = repository.SaveCapture(CreateCapture(profileFolder, "lower", 70d, 65d, 0.01d));
            Require(!lowerValue.Accepted, "Lower-value near duplicate was retained.");

            var dataset = repository.ReadDataset(profileFolder, SubjectId, "Storage Smoke");
            Require(dataset.Observations.Count == 1, "Retained observation count was not bounded by replacement.");
            Require(dataset.AcceptedObservationCount == 2, "Accepted lifetime count was incorrect.");
            Require(dataset.RejectedObservationCount == 1, "Rejected lifetime count was incorrect.");
            Require(dataset.Revision == 2, "Catalog revision did not track accepted writes.");

            var metadata = dataset.Observations[0];
            Require(metadata.TopologyObjectPath == firstTopologyPath, "Matching 3DDFA topology was serialized more than once.");
            var loaded = repository.LoadObservation(dataset, metadata);
            var imagePath = repository.GetImagePath(dataset, metadata);
            Require(loaded.Vertices.Count == 1_600, "Dense scan binary round-trip lost vertices.");
            Require(loaded.CanonicalIdentityVertices.Count == 1_600, "Canonical scan binary round-trip lost vertices.");
            Require(dataset.DenseTopologyEdges.Count == 1_599, "Topology binary round-trip lost edges.");
            Require(!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath), "Paired source photo was not retained.");
            Require(File.Exists(AvatarStorageLayout.GetDatabasePath(dataset.StorageRoot)), "SQLite catalog was not created.");
            Require(Path.GetFileNameWithoutExtension(imagePath) == metadata.ImageSha256, "Photo content hash did not match its object name.");

            var model = AvatarModelBuilder.Build(dataset, repository);
            var modelHtmlPath = new AvatarModelStore().WriteViewer(profileFolder, model);
            var lastFiveSamples = LastGoodThreeDdfaStore.CreateSamples(dataset, repository);
            var lastFiveHtmlPath = new LastGoodThreeDdfaStore().Write(
                profileFolder,
                new LastGoodThreeDdfaReport
                {
                    SubjectId = SubjectId,
                    SubjectDisplayName = "Storage Smoke",
                    AvatarModelProgressHtmlPath = modelHtmlPath,
                    DenseTopologyEdges = dataset.DenseTopologyEdges.ToList(),
                    Samples = lastFiveSamples
                });
            Require(model.RecentSamples.Single().SourceImageUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase), "Model report did not link the paired source photo.");
            Require(File.ReadAllText(lastFiveHtmlPath).Contains("Photo Overlay", StringComparison.Ordinal), "Last 5 report did not expose photo-backed mesh validation.");

            var deleted = repository.ResetProfile(profileFolder, SubjectId);
            Require(deleted == 1, "Profile reset did not remove the retained observation.");
            var resetDataset = repository.ReadDataset(profileFolder, SubjectId, "Storage Smoke");
            Require(resetDataset.Observations.Count == 0, "Profile reset left catalog observations behind.");
            Require(!File.Exists(imagePath), "Profile reset left an unreferenced paired photo behind.");

            AvatarObservationStorageEventArgs? backgroundWrite = null;
            using (var completed = new ManualResetEventSlim())
            {
                var storageService = new AvatarObservationStorageService(repository, capacity: 1);
                try
                {
                    storageService.WriteCompleted += (_, eventArgs) =>
                    {
                        backgroundWrite = eventArgs;
                        completed.Set();
                    };
                    Require(
                        storageService.TryEnqueue(CreateCapture(profileFolder, "background", 94d, 92d, 0.08d)),
                        "Bounded background writer refused an empty queue.");
                    Require(completed.Wait(TimeSpan.FromSeconds(10)), "Bounded background writer did not complete in time.");
                }
                finally
                {
                    storageService.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            Require(backgroundWrite?.Error is null, $"Bounded background writer failed: {backgroundWrite?.Error}");
            Require(backgroundWrite?.Result is { Accepted: true }, "Bounded background writer did not persist the queued observation.");
            var backgroundDataset = repository.ReadDataset(profileFolder, SubjectId, "Storage Smoke");
            Require(backgroundDataset.Observations.Count == 1, "Bounded background writer produced an unexpected catalog state.");
            Require(repository.ResetProfile(profileFolder, SubjectId) == 1, "Final background-writer cleanup did not remove its observation.");

            timer.Stop();
            messages.Add("PASS: SQLite catalog, binary scan, topology, JPEG, ranking, replacement, reopen, bounded background writes, photo-backed reports, model build, and reset checks passed.");
            messages.Add($"Elapsed: {timer.Elapsed.TotalMilliseconds:0.0} ms");
            messages.Add($"Catalog revision before reset: {dataset.Revision}");
            WriteReport(reportPath, messages);
            return new AvatarObservationStorageSelfTestResult(true, reportPath, string.Join(" ", messages));
        }
        catch (Exception ex)
        {
            timer.Stop();
            messages.Add($"FAIL: {ex}");
            WriteReport(reportPath, messages);
            return new AvatarObservationStorageSelfTestResult(false, reportPath, ex.Message);
        }
        finally
        {
            if (!preserveArtifacts)
            {
                TryDeleteSession(root, session);
            }
        }
    }

    private static AvatarObservationCapture CreateCapture(
        string profileFolder,
        string requestId,
        double reconstructionConfidence,
        double captureQuality,
        double geometryOffset)
    {
        var vertices = new List<FaceMeshLandmarkPoint>(1_600);
        var canonical = new List<FaceMeshLandmarkPoint>(1_600);
        var topology = new List<MeshTopologyEdge>(1_599);
        for (var index = 0; index < 1_600; index++)
        {
            var x = index % 40;
            var y = index / 40;
            vertices.Add(new FaceMeshLandmarkPoint { Index = index, X = 30d + x + geometryOffset, Y = 25d + y, Z = Math.Sin(index * 0.03d) });
            canonical.Add(new FaceMeshLandmarkPoint { Index = index, X = x * 0.02d + geometryOffset, Y = y * 0.02d, Z = Math.Sin(index * 0.03d) * 0.02d });
            if (index > 0)
            {
                topology.Add(new MeshTopologyEdge
                {
                    FromIndex = index - 1,
                    ToIndex = index,
                    Role = "surface",
                    Source = "storage self-test",
                    ConfidencePercent = 100d
                });
            }
        }

        var capturedAt = DateTime.UtcNow;
        return new AvatarObservationCapture(
            profileFolder,
            SubjectId,
            "Storage Smoke",
            new ThreeDdfaReconstructionSnapshot
            {
                RequestId = requestId,
                CapturedAtUtc = capturedAt,
                ReconstructionConfidencePercent = reconstructionConfidence,
                ARotationAroundXDegrees = 1d,
                BRotationAroundYDegrees = 2d,
                CRotationAroundZDegrees = 1d,
                TrustDecision = "self-test trusted",
                DenseVertexCount = vertices.Count,
                Vertices = vertices,
                CanonicalIdentityVertices = canonical,
                TopologyEdges = topology,
                SparseLandmarks = vertices.Take(68).ToList(),
                CameraMatrixCoefficients = [1d, 0d, 0d, 1d],
                ShapeCoefficients = Enumerable.Range(0, 40).Select(index => index * 0.001d).ToList(),
                ExpressionCoefficients = Enumerable.Range(0, 10).Select(index => index * 0.002d).ToList()
            },
            CreateFrame(geometryOffset),
            new AvatarCaptureQualityAssessment
            {
                Label = "strong",
                ScorePercent = captureQuality,
                StrongEnoughForAvatarLearning = true,
                EyeEvidenceScorePercent = 95d,
                MouthEvidenceScorePercent = 95d,
                StabilityScorePercent = captureQuality,
                FaceWidthPercent = 36d,
                FaceHeightPercent = 58d
            },
            new FaceFrameGeometry
            {
                HasFace = true,
                CapturedAtUtc = capturedAt,
                XHorizontalPercent = 50d,
                YVerticalPercent = 50d,
                RelativeDistanceScale = 1d,
                ApparentDistanceUnits = 1d
            });
    }

    private static BitmapSource CreateFrame(double offset)
    {
        const int width = 128;
        const int height = 128;
        var pixels = new byte[width * height * 4];
        var variant = (byte)Math.Clamp(Math.Round(offset * 1_000d), 0d, 255d);
        for (var index = 0; index < width * height; index++)
        {
            pixels[index * 4] = (byte)(index % width);
            pixels[index * 4 + 1] = (byte)(index / width);
            pixels[index * 4 + 2] = variant;
            pixels[index * 4 + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96d, 96d, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WriteReport(string path, IEnumerable<string> lines)
    {
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void TryDeleteSession(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record AvatarObservationStorageSelfTestResult(bool Succeeded, string ReportPath, string Detail);
