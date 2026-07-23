using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Storage.AvatarObservations.Review;
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
		string fullPath = Path.GetFullPath(outputFolder);
		Directory.CreateDirectory(fullPath);
		string text = Path.Combine(fullPath, $"run-{Guid.NewGuid():N}");
		string text2 = Path.Combine(text, "AvatarSystem", "People", "storage-smoke");
		string text3 = Path.Combine(fullPath, "avatar-storage-self-test.txt");
		List<string> list = new List<string>();
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			AvatarObservationRepository avatarObservationRepository = new AvatarObservationRepository();
			AvatarObservationWriteResult avatarObservationWriteResult = avatarObservationRepository.SaveCapture(CreateCapture(text2, "first", 82.0, 80.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L));
			Require(avatarObservationWriteResult.Accepted && !avatarObservationWriteResult.ReplacedExisting, "First useful observation was not accepted.");
			string topologyObjectPath = avatarObservationRepository.ReadDataset(text2, "storage-smoke", "Storage Smoke").Observations.Single().TopologyObjectPath;
			Require(!avatarObservationRepository.SaveCapture(CreateCapture(text2, "first", 82.0, 80.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L)).Accepted, "Duplicate request ID was stored twice.");
			AvatarObservationWriteResult avatarObservationWriteResult2 = avatarObservationRepository.SaveCapture(CreateCapture(text2, "replacement", 99.0, 98.0, 0.02, "3ddfa-v2-onnx-reconstruction", 0L));
			Require(avatarObservationWriteResult2.Accepted && avatarObservationWriteResult2.ReplacedExisting, "Higher-quality near duplicate did not replace weaker evidence.");
			AvatarObservation? acceptedObservation = avatarObservationWriteResult2.AcceptedObservation;
			Require((object)acceptedObservation != null && acceptedObservation.CanonicalIdentityVertices.Count == 1600, "Accepted incremental geometry was not returned to the worker.");
			AvatarObservation? replacedObservation = avatarObservationWriteResult2.ReplacedObservation;
			Require((object)replacedObservation != null && replacedObservation.CanonicalIdentityVertices.Count == 1600, "Replaced incremental geometry was deleted before the worker received it.");
			Require(!avatarObservationRepository.SaveCapture(CreateCapture(text2, "lower", 70.0, 65.0, 0.01, "3ddfa-v2-onnx-reconstruction", 0L)).Accepted, "Lower-value near duplicate was retained.");
			AvatarObservationDataset avatarObservationDataset = avatarObservationRepository.ReadDataset(text2, "storage-smoke", "Storage Smoke");
			Require(avatarObservationDataset.Observations.Count == 1, "Retained observation count was not bounded by replacement.");
			Require(avatarObservationDataset.AcceptedObservationCount == 2, "Accepted lifetime count was incorrect.");
			Require(avatarObservationDataset.RejectedObservationCount == 1, "Rejected lifetime count was incorrect.");
			Require(avatarObservationDataset.Revision == 2, "Catalog revision did not track accepted writes.");
			AvatarObservation avatarObservation = avatarObservationDataset.Observations[0];
			Require(avatarObservation.TopologyObjectPath == topologyObjectPath, "Matching 3DDFA topology was serialized more than once.");
			AvatarObservation avatarObservation2 = avatarObservationRepository.LoadObservation(avatarObservationDataset, avatarObservation);
			string imagePath = avatarObservationRepository.GetImagePath(avatarObservationDataset, avatarObservation);
			Require(avatarObservation2.Vertices.Count == 1600, "Dense scan binary round-trip lost vertices.");
			Require(avatarObservation2.CanonicalIdentityVertices.Count == 1600, "Canonical scan binary round-trip lost vertices.");
			Require(avatarObservation2.ObservedLandmarks.Count == 478, "Binary scan round-trip lost independent MediaPipe landmarks.");
			Require(avatarObservation2.PoseCoefficients.Count == 6, "Binary scan round-trip lost DECA pose coefficients.");
			Require(avatarObservation2.SourceFrameWidthPixels == 640 && avatarObservation2.SourceFrameHeightPixels == 480, "Binary scan round-trip lost source dimensions.");
			Require(avatarObservation2.InputFaceBox != null, "Binary scan round-trip lost the reconstruction input face box.");
			Require(avatarObservationDataset.DenseTopologyEdges.Count == 1599, "Topology binary round-trip lost edges.");
			Require(!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath), "Paired source photo was not retained.");
			Require(File.Exists(AvatarStorageLayout.GetDatabasePath(avatarObservationDataset.StorageRoot)), "SQLite catalog was not created.");
			Require(Path.GetFileNameWithoutExtension(imagePath) == avatarObservation.ImageSha256, "Photo content hash did not match its object name.");
			AvatarModel avatarModel = AvatarModelBuilder.Build(avatarObservationDataset, avatarObservationRepository);
			AvatarModelStore avatarModelStore = new AvatarModelStore();
			string path = avatarModelStore.Write(text2, avatarModel);
			string htmlPath = AvatarModelStore.GetHtmlPath(text2);
			Require(avatarModel.RecentSamples.Single().SourceImageUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase), "Model report did not link the paired source photo.");
			Require(File.Exists(path), "Avatar model JSON was not written.");
			Require(File.Exists(htmlPath), "Avatar model viewer was not written.");
			VerifyIncrementalModelUpdate(avatarObservationRepository, text);
			VerifyLegacyArchiveMigration(avatarObservationRepository, text);
			VerifyRejectedCandidatesReachIdentityFinalizer(avatarObservationRepository, text);
			VerifyRecurrentBatchSelectsLowestDelta(avatarObservationRepository, text);
			VerifyStandardModelCheckpointIsolation(avatarObservationRepository, text);
			AvatarDataReviewServer avatarDataReviewServer = new AvatarDataReviewServer(avatarObservationRepository);
			try
			{
				Uri baseAddress = avatarDataReviewServer.StartOrUpdate(text2, "storage-smoke", "Storage Smoke");
				using HttpClient httpClient = new HttpClient
				{
					BaseAddress = baseAddress
				};
				string result = httpClient.GetStringAsync("").GetAwaiter().GetResult();
				Require(result.Contains("Review FLAME Data", StringComparison.Ordinal), "FLAME data review page did not load.");
				Require(result.Contains("Current Recurrent FLAME Identity", StringComparison.Ordinal), "Avatar data review page did not identify the recurrent identity view.");
				Require(result.Contains("Saved Frame With Projected Fit", StringComparison.Ordinal), "Avatar data review page did not expose paired image review.");
				Require(result.Contains("y: (centerY - point.y) * scale", StringComparison.Ordinal), "FLAME model-space Y was not converted to the browser's downward-positive canvas axis.");
				AvatarDataReviewManifest? avatarDataReviewManifest = JsonSerializer.Deserialize<AvatarDataReviewManifest>(httpClient.GetStringAsync("api/manifest").GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				Require((object)avatarDataReviewManifest != null && avatarDataReviewManifest.StoredScanCount == 1, "Avatar data review manifest did not list the retained scan.");
				AvatarDataReviewScanSummary avatarDataReviewScanSummary = avatarDataReviewManifest?.Scans.Single() ?? throw new InvalidOperationException("Avatar data review manifest was empty.");
				AvatarDataReviewScan? avatarDataReviewScan = JsonSerializer.Deserialize<AvatarDataReviewScan>(httpClient.GetStringAsync("api/scans/" + avatarDataReviewScanSummary.ObservationId).GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				Require((object)avatarDataReviewScan != null && avatarDataReviewScan.Vertices.Count == 1600, "Avatar data review endpoint did not load the selected dense scan.");
				Require((object)avatarDataReviewScan != null && avatarDataReviewScan.CanonicalIdentityVertices.Count == 1600, "Avatar data review endpoint did not load canonical XYZ geometry.");
				AvatarDataReviewIdentityModel avatarDataReviewIdentityModel = JsonSerializer.Deserialize<AvatarDataReviewIdentityModel>(httpClient.GetStringAsync("api/model").GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				int condition;
				if ((object)avatarDataReviewIdentityModel != null && !avatarDataReviewIdentityModel.HasMappedIdentity)
				{
					IReadOnlyList<FaceMeshLandmarkPoint> vertices = avatarDataReviewIdentityModel.Vertices;
					if (vertices != null)
					{
						condition = ((vertices.Count == 0) ? 1 : 0);
						goto IL_067e;
					}
				}
				condition = 0;
				goto IL_067e;
				IL_0773:
				int condition2;
				Require((byte)condition2 != 0, "Avatar data review endpoint did not expose the accumulated mapped identity.");
				List<MeshTopologyEdge>? list2 = JsonSerializer.Deserialize<List<MeshTopologyEdge>>(httpClient.GetStringAsync("api/topologies/" + avatarDataReviewScanSummary.TopologySha256).GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				Require(list2 != null && list2.Count == 1599, "Avatar data review endpoint did not load the selected topology.");
				Require(httpClient.GetByteArrayAsync("api/scans/" + avatarDataReviewScanSummary.ObservationId + "/image").GetAwaiter().GetResult()
					.Length != 0, "Avatar data review endpoint did not load the paired source photo.");
				goto end_IL_04e8;
				IL_067e:
				Require((byte)condition != 0, "Avatar data review endpoint substituted a generic template for an unmapped identity.");
				AvatarModel model = AvatarModelBuilder.ApplyIdentityMapping(avatarModel, new AvatarIdentityMappingUpdate
				{
					Accepted = true,
					Status = "Storage smoke personalized identity mapping accepted.",
					FrameCount = 5,
					IterationCount = 3,
					InitialLandmarkRmsePercent = 8.0,
					FinalLandmarkRmsePercent = 3.0,
					ImprovementPercent = 62.5,
					GenericIdentityDisplacementPercent = 2.0,
					ShapeCoefficients = avatarObservation2.ShapeCoefficients,
					CanonicalIdentityVertices = avatarObservation2.CanonicalIdentityVertices
				});
				avatarModelStore.Write(text2, model);
				avatarDataReviewIdentityModel = JsonSerializer.Deserialize<AvatarDataReviewIdentityModel>(httpClient.GetStringAsync("api/model").GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				if ((object)avatarDataReviewIdentityModel != null && avatarDataReviewIdentityModel.HasMappedIdentity)
				{
					IReadOnlyList<FaceMeshLandmarkPoint> vertices = avatarDataReviewIdentityModel.Vertices;
					if (vertices != null && vertices.Count == 1600)
					{
						condition2 = ((avatarDataReviewIdentityModel.FrameCount == 5) ? 1 : 0);
						goto IL_0773;
					}
				}
				condition2 = 0;
				goto IL_0773;
				end_IL_04e8:;
			}
			finally
			{
				avatarDataReviewServer.DisposeAsync().AsTask().GetAwaiter()
					.GetResult();
			}
			Require(avatarObservationRepository.ResetProfile(text2, "storage-smoke") == 1, "Profile reset did not remove the retained observation.");
			Require(avatarObservationRepository.ReadDataset(text2, "storage-smoke", "Storage Smoke").Observations.Count == 0, "Profile reset left catalog observations behind.");
			Require(!File.Exists(imagePath), "Profile reset left an unreferenced paired photo behind.");
			List<AvatarObservationBatchEventArgs> completedBatches = new List<AvatarObservationBatchEventArgs>();
			TaskCompletionSource finalizerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource releaseFinalizer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			int activeFinalizers = 0;
			int maximumConcurrentFinalizers = 0;
			List<AvatarObservationWorkerCompletedEventArgs> completedWorkers = new List<AvatarObservationWorkerCompletedEventArgs>();
			List<AvatarObservationWorkerStartedEventArgs> startedWorkers = new List<AvatarObservationWorkerStartedEventArgs>();
			CountdownEvent completed = new CountdownEvent(2);
			try
			{
				CountdownEvent workersCompleted = new CountdownEvent(2);
				try
				{
					AvatarObservationStorageService avatarObservationStorageService = new AvatarObservationStorageService(avatarObservationRepository, async delegate(AvatarObservationBatch _, CancellationToken cancellationToken)
					{
						int val = Interlocked.Increment(ref activeFinalizers);
						maximumConcurrentFinalizers = Math.Max(maximumConcurrentFinalizers, val);
						finalizerStarted.TrySetResult();
						try
						{
							await releaseFinalizer.Task.WaitAsync(cancellationToken);
						}
						finally
						{
							Interlocked.Decrement(ref activeFinalizers);
						}
					});
					try
					{
						avatarObservationStorageService.BatchCompleted += delegate(object? _, AvatarObservationBatchEventArgs eventArgs)
						{
							lock (completedBatches)
							{
								completedBatches.Add(eventArgs);
							}
							completed.Signal();
						};
						avatarObservationStorageService.WorkerCompleted += delegate(object? _, AvatarObservationWorkerCompletedEventArgs eventArgs)
						{
							lock (completedWorkers)
							{
								completedWorkers.Add(eventArgs);
							}
							workersCompleted.Signal();
						};
						avatarObservationStorageService.WorkerStarted += delegate(object? _, AvatarObservationWorkerStartedEventArgs eventArgs)
						{
							lock (startedWorkers)
							{
								startedWorkers.Add(eventArgs);
							}
						};
						for (int num = 0; num < 5; num++)
						{
							Require(avatarObservationStorageService.AddCandidate(CreateCapture(text2, $"background-{num}", 94.0, 92.0, 0.08 + (double)num * 0.001, "3ddfa-v2-onnx-reconstruction", 0L)) == ((num == 4) ? AvatarObservationBatchAdmission.Launched : AvatarObservationBatchAdmission.Buffered), "The first five-candidate batch did not launch exactly once.");
						}
						Require(finalizerStarted.Task.Wait(TimeSpan.FromSeconds(10L)), "The single-flight batch worker did not reach model finalization.");
						for (int num2 = 0; num2 < 5; num2++)
						{
							Require(avatarObservationStorageService.AddCandidate(CreateCapture(text2, $"busy-{num2}", 99.0, 99.0, 0.2 + (double)num2 * 0.001, "3ddfa-v2-onnx-reconstruction", 0L)) == (AvatarObservationBatchAdmission)((num2 == 4) ? 2 : 0), "The waiting batch was not retained intact while the first worker was busy.");
						}
						Require(avatarObservationStorageService.PendingCandidateCount == 5, "The full waiting batch was not preserved in memory.");
						Require(!avatarObservationStorageService.CanAcceptCandidate, "Dense capture was not paused after the waiting batch filled.");
						Require(avatarObservationStorageService.AddCandidate(CreateCapture(text2, "ignored-while-full", 100.0, 100.0, 0.3, "3ddfa-v2-onnx-reconstruction", 0L)) == AvatarObservationBatchAdmission.IgnoredWaitingBatch, "A candidate entered memory after the bounded waiting batch was full.");
						Require(avatarObservationStorageService.PendingCandidateCount == 5, "A newer candidate displaced paid-for waiting evidence.");
						Require(avatarObservationStorageService.IgnoredCandidateCount == 1, "The paused-capture guard counter was incorrect.");
						releaseFinalizer.TrySetResult();
						Require(completed.Wait(TimeSpan.FromSeconds(15L)), "The active and held batches did not complete in time.");
						Require(workersCompleted.Wait(TimeSpan.FromSeconds(5L)), "Both detached workers did not publish final durations.");
					}
					finally
					{
						releaseFinalizer.TrySetResult();
						avatarObservationStorageService.DisposeAsync().AsTask().GetAwaiter()
							.GetResult();
					}
				}
				finally
				{
					if (workersCompleted != null)
					{
						((IDisposable)workersCompleted).Dispose();
					}
				}
			}
			finally
			{
				if (completed != null)
				{
					((IDisposable)completed).Dispose();
				}
			}
			Require(completedBatches.Count == 2, "The held batch was not handed to a new worker after the first worker exited.");
			Require(completedBatches.All((AvatarObservationBatchEventArgs batch) => batch.FinalizationError == null), "Single-flight model finalization failed.");
			Require(completedBatches.Any((AvatarObservationBatchEventArgs batch) => batch.Batch.AcceptedCount > 0), "Single-flight batch worker did not persist useful evidence.");
			Require(maximumConcurrentFinalizers == 1, "More than one storage/model finalizer ran concurrently.");
			Require(completedWorkers.Count == 2, "A worker processed more than one detached five-candidate batch.");
			Require(completedWorkers.All((AvatarObservationWorkerCompletedEventArgs worker) => worker.Duration.Ticks > 0), "A detached worker did not publish its duration.");
			Require(startedWorkers.Count == 2, "The scheduler did not publish both isolated worker starts.");
			TimeSpan? waitAfterPreviousWorker = startedWorkers[1].WaitAfterPreviousWorker;
			Require(waitAfterPreviousWorker.HasValue && waitAfterPreviousWorker.GetValueOrDefault().Ticks >= 0, "The next-worker wait was not measured after the first worker died.");
			Require(avatarObservationRepository.ReadDataset(text2, "storage-smoke", "Storage Smoke").Observations.Count >= 1, "Single-flight batch worker produced an empty catalog.");
			Require(avatarObservationRepository.ResetProfile(text2, "storage-smoke") >= 1, "Final batch-worker cleanup did not remove its observations.");
			stopwatch.Stop();
			list.Add("PASS: SQLite catalog, binary scan, topology, JPEG, ranking, replacement, exact incremental model math, truthful recurrent-identity review, guarded legacy-image migration, backend-specific deletion, independently retained Standard Model checkpoints, checkpoint exclusion from legacy AvatarModelBuilder averaging, reopen, isolated five-candidate workers, one held in-memory batch, pause-on-full persistence, five-result convergence-minimum selection, on-demand all-scan review, model build, and reset checks passed.");
			list.Add($"Elapsed: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
			list.Add($"Catalog revision before reset: {avatarObservationDataset.Revision}");
			WriteReport(text3, list);
			return new AvatarObservationStorageSelfTestResult(Succeeded: true, text3, string.Join(" ", list));
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			list.Add($"FAIL: {ex}");
			WriteReport(text3, list);
			return new AvatarObservationStorageSelfTestResult(Succeeded: false, text3, ex.Message);
		}
		finally
		{
			if (!preserveArtifacts)
			{
				TryDeleteSession(fullPath, text);
			}
		}
	}

	private static AvatarObservationCapture CreateCapture(string profileFolder, string requestId, double reconstructionConfidence, double captureQuality, double geometryOffset, string backendId = "3ddfa-v2-onnx-reconstruction", long modelSequenceNumber = 0L, double modelCoefficientDeltaRms = 0.0, bool pinnedStillConverged = false, int pinnedStillPassCount = 0)
	{
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(1600);
		List<FaceMeshLandmarkPoint> list2 = new List<FaceMeshLandmarkPoint>(1600);
		List<MeshTopologyEdge> list3 = new List<MeshTopologyEdge>(1599);
		for (int i = 0; i < 1600; i++)
		{
			int num = i % 40;
			int num2 = i / 40;
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = i,
				X = 30.0 + (double)num + geometryOffset,
				Y = 25.0 + (double)num2,
				Z = Math.Sin((double)i * 0.03)
			});
			list2.Add(new FaceMeshLandmarkPoint
			{
				Index = i,
				X = (double)num * 0.02 + geometryOffset,
				Y = (double)num2 * 0.02,
				Z = Math.Sin((double)i * 0.03) * 0.02
			});
			if (i > 0)
			{
				list3.Add(new MeshTopologyEdge
				{
					FromIndex = i - 1,
					ToIndex = i,
					Role = "surface",
					Source = "storage self-test",
					ConfidencePercent = 100.0
				});
			}
		}
		DateTime utcNow = DateTime.UtcNow;
		return new AvatarObservationCapture(profileFolder, "storage-smoke", "Storage Smoke", new AvatarReconstructionSnapshot
		{
			BackendId = backendId,
			RequestId = requestId,
			CapturedAtUtc = utcNow,
			ReconstructionConfidencePercent = reconstructionConfidence,
			CurrentModelSequenceNumber = modelSequenceNumber,
			CurrentModelCoefficientDeltaRms = modelCoefficientDeltaRms,
			PinnedStillConverged = pinnedStillConverged,
			PinnedStillPassCount = pinnedStillPassCount,
			PinnedStillStablePassCount = (pinnedStillConverged ? 2 : 0),
			ARotationAroundXDegrees = 1.0,
			BRotationAroundYDegrees = 2.0,
			CRotationAroundZDegrees = 1.0,
			TrustDecision = "self-test trusted",
			DenseVertexCount = list.Count,
			Vertices = list,
			CanonicalIdentityVertices = list2,
			TopologyEdges = list3,
			SparseLandmarks = list.Take(68).ToList(),
			CameraMatrixCoefficients = new global::_003C_003Ez__ReadOnlyArray<double>(new double[3] { 1.0, 0.0, 0.0 }),
			ShapeCoefficients = (from index in Enumerable.Range(0, 40)
				select (double)index * 0.001).ToList(),
			ExpressionCoefficients = (from index in Enumerable.Range(0, 10)
				select (double)index * 0.002).ToList(),
			PoseCoefficients = (from index in Enumerable.Range(0, 6)
				select (double)index * 0.003).ToList(),
			SourceFrameWidthPixels = 640,
			SourceFrameHeightPixels = 480,
			InputFaceBox = new ReconstructionInputFaceBox
			{
				Left = 0.2,
				Top = 0.1,
				Right = 0.8,
				Bottom = 0.9,
				Normalized = true,
				Confidence = 0.98
			},
			ObservedLandmarks = (from index in Enumerable.Range(0, 478)
				select new FaceMeshLandmarkPoint
				{
					Index = index,
					X = 0.25 + (double)(index % 24) * 0.02,
					Y = 0.2 + (double)(index / 24) * 0.025,
					Z = Math.Sin((double)index * 0.02) * 0.01
				}).ToList()
		}, CreateFrame(geometryOffset), new AvatarCaptureQualityAssessment
		{
			Label = "strong",
			ScorePercent = captureQuality,
			StrongEnoughForAvatarLearning = true,
			EyeEvidenceScorePercent = 95.0,
			MouthEvidenceScorePercent = 95.0,
			StabilityScorePercent = captureQuality,
			FaceWidthPercent = 36.0,
			FaceHeightPercent = 58.0
		}, new FaceFrameGeometry
		{
			HasFace = true,
			CapturedAtUtc = utcNow,
			XHorizontalPercent = 50.0,
			YVerticalPercent = 50.0,
			RelativeDistanceScale = 1.0,
			ApparentDistanceUnits = 1.0
		});
	}

	private static void VerifyIncrementalModelUpdate(AvatarObservationRepository repository, string session)
	{
		string profileFolder = Path.Combine(session, "incremental-model", "storage-smoke");
		AvatarObservationWriteResult avatarObservationWriteResult = repository.SaveCapture(CreateCapture(profileFolder, "incremental-first", 84.0, 82.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L));
		Require((object)avatarObservationWriteResult.AcceptedObservation != null, "First incremental write did not return its accepted geometry.");
		AvatarObservationDataset observationSet = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke");
		AvatarModel avatarModel = AvatarModelBuilder.UpdateIncrementally(observationSet, repository, AvatarModelBuilder.CreateWaiting(observationSet), new global::_003C_003Ez__ReadOnlySingleElementList<AvatarObservationWriteResult>(avatarObservationWriteResult));
		AvatarModel full = AvatarModelBuilder.Build(observationSet, repository);
		RequireModelsEqual(avatarModel, full, "first accepted batch");
		avatarModel = AvatarModelBuilder.ApplyIdentityMapping(avatarModel, new AvatarIdentityMappingUpdate
		{
			Accepted = true,
			Status = "self-test mapped identity accepted",
			FrameCount = 5,
			IterationCount = 12,
			InitialLandmarkRmsePercent = 8.0,
			FinalLandmarkRmsePercent = 4.0,
			ImprovementPercent = 50.0,
			GenericIdentityDisplacementPercent = 2.0,
			ShapeCoefficients = avatarObservationWriteResult.AcceptedObservation.ShapeCoefficients,
			CanonicalIdentityVertices = avatarObservationWriteResult.AcceptedObservation.CanonicalIdentityVertices
		});
		Require(avatarModel.Identity.MappedDenseVertices.Count == 1600, "Accepted identity mapping did not attach canonical geometry.");
		AvatarObservationWriteResult avatarObservationWriteResult2 = repository.SaveCapture(CreateCapture(profileFolder, "incremental-replacement", 99.0, 99.0, 0.02, "3ddfa-v2-onnx-reconstruction", 0L));
		Require(avatarObservationWriteResult2.Accepted && avatarObservationWriteResult2.ReplacedExisting, "Incremental replacement test did not replace its baseline.");
		AvatarObservationDataset observationSet2 = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke");
		avatarModel = AvatarModelBuilder.UpdateIncrementally(observationSet2, repository, avatarModel, new global::_003C_003Ez__ReadOnlySingleElementList<AvatarObservationWriteResult>(avatarObservationWriteResult2));
		full = AvatarModelBuilder.Build(observationSet2, repository);
		RequireModelsEqual(avatarModel, full, "replacement batch");
		Require(avatarModel.Identity.MappedDenseVertices.Count == 1600, "Incremental update discarded the accepted mapped identity.");
		repository.ResetProfile(profileFolder, "storage-smoke");
	}

	private static void VerifyRejectedCandidatesReachIdentityFinalizer(AvatarObservationRepository repository, string session)
	{
		string profileFolder = Path.Combine(session, "identity-evidence-finalizer", "storage-smoke");
		Require(repository.SaveCapture(CreateCapture(profileFolder, "identity-seed", 99.0, 99.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L)).Accepted, "Identity-evidence finalizer fixture was not retained.");
		ManualResetEventSlim finalized = new ManualResetEventSlim();
		try
		{
			AvatarObservationBatch finalizedBatch = null;
			AvatarObservationStorageService avatarObservationStorageService = new AvatarObservationStorageService(repository, delegate(AvatarObservationBatch batch, CancellationToken _)
			{
				finalizedBatch = batch;
				finalized.Set();
				return Task.CompletedTask;
			});
			try
			{
				for (int num = 0; num < 5; num++)
				{
					avatarObservationStorageService.AddCandidate(CreateCapture(profileFolder, $"identity-rejected-{num}", 70.0, 65.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L));
				}
				Require(finalized.Wait(TimeSpan.FromSeconds(10L)), "A fully rejected batch never reached identity finalization.");
				int condition;
				if ((object)finalizedBatch != null)
				{
					IReadOnlyList<AvatarObservationCapture> candidates = finalizedBatch.Candidates;
					if (candidates != null && candidates.Count == 5)
					{
						condition = ((finalizedBatch.AcceptedCount == 0) ? 1 : 0);
						goto IL_0146;
					}
				}
				condition = 0;
				goto IL_0146;
				IL_0146:
				Require((byte)condition != 0, "Rejected near-duplicate evidence was not preserved as an isolated five-frame identity batch.");
			}
			finally
			{
				avatarObservationStorageService.DisposeAsync().AsTask().GetAwaiter()
					.GetResult();
				repository.ResetProfile(profileFolder, "storage-smoke");
			}
		}
		finally
		{
			if (finalized != null)
			{
				((IDisposable)finalized).Dispose();
			}
		}
	}

	private static void VerifyRecurrentBatchSelectsLowestDelta(AvatarObservationRepository repository, string session)
	{
		string profileFolder = Path.Combine(session, "recurrent-minimum", "storage-smoke");
		ManualResetEventSlim completed = new ManualResetEventSlim();
		try
		{
			AvatarObservationBatch completedBatch = null;
			AvatarObservationStorageService avatarObservationStorageService = new AvatarObservationStorageService(repository);
			avatarObservationStorageService.BatchCompleted += delegate(object? _, AvatarObservationBatchEventArgs eventArgs)
			{
				completedBatch = eventArgs.Batch;
				completed.Set();
			};
			try
			{
				double[] array = new double[5] { 0.18, 0.12, 0.06, 0.09, 0.11 };
				for (int num = 0; num < array.Length; num++)
				{
					avatarObservationStorageService.AddCandidate(CreateCapture(profileFolder, $"recurrent-{num}", 54.0, 90.0, (double)num * 0.001, "deca-flame-recurrent-v4", 100 + num, array[num]));
				}
				Require(completed.Wait(TimeSpan.FromSeconds(10L)), "The recurrent minimum-selection batch did not complete.");
				int condition;
				if ((object)completedBatch != null)
				{
					IReadOnlyList<AvatarObservationCapture> candidates = completedBatch.Candidates;
					if (candidates != null && candidates.Count == 5)
					{
						IReadOnlyList<AvatarObservationWriteResult> results = completedBatch.Results;
						if (results != null && results.Count == 1)
						{
							condition = ((completedBatch.AcceptedCount == 1) ? 1 : 0);
							goto IL_0137;
						}
					}
				}
				condition = 0;
				goto IL_0137;
				IL_0137:
				Require((byte)condition != 0, "The recurrent worker did not persist exactly one minimum from its five-result window.");
				AvatarObservation avatarObservation = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke").Observations.Single();
				Require(Math.Abs(avatarObservation.ModelCoefficientDeltaRms - 0.06) <= 1E-09, "The recurrent worker did not retain the smallest coefficient delta.");
				Require(avatarObservation.ModelSequenceNumber == 102, "The recurrent worker did not retain the model sequence associated with the smallest delta.");
			}
			finally
			{
				avatarObservationStorageService.DisposeAsync().AsTask().GetAwaiter()
					.GetResult();
				repository.ResetProfile(profileFolder, "storage-smoke");
			}
		}
		finally
		{
			if (completed != null)
			{
				((IDisposable)completed).Dispose();
			}
		}
	}

	private static void VerifyStandardModelCheckpointIsolation(AvatarObservationRepository repository, string session)
	{
		string profileFolder = Path.Combine(session, "standard-model-checkpoint-isolation", "storage-smoke");
		try
		{
			Require(repository.SaveCapture(CreateCapture(profileFolder, "recurrent-lineage-control", 92.0, 91.0, 0.1, "deca-flame-recurrent-v4", 40L, 0.04)).Accepted, "The recurrent control observation was not retained.");
			AvatarObservationWriteResult avatarObservationWriteResult = repository.SaveCapture(CreateCapture(profileFolder, "first-standard-model-checkpoint", 48.0, 42.0, 4.5, "deca-flame-standard-model-checkpoint-v1", 41L, 0.008, pinnedStillConverged: true, 7));
			Require(avatarObservationWriteResult.Accepted && !avatarObservationWriteResult.ReplacedExisting, "A Standard Model checkpoint was not retained independently from the recurrent lane.");
			AvatarObservationWriteResult avatarObservationWriteResult2 = repository.SaveCapture(CreateCapture(profileFolder, "second-standard-model-checkpoint", 44.0, 40.0, -3.75, "deca-flame-standard-model-checkpoint-v1", 42L, 0.006, pinnedStillConverged: false, 12));
			Require(avatarObservationWriteResult2.Accepted && !avatarObservationWriteResult2.ReplacedExisting, "A later Standard Model checkpoint replaced or rejected an earlier checkpoint.");
			AvatarObservationDataset avatarObservationDataset = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke");
			AvatarObservationDataset avatarObservationDataset2 = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke", includeDenseTopology: true, "deca-flame-recurrent-v4");
			AvatarObservationDataset avatarObservationDataset3 = repository.ReadDataset(profileFolder, "storage-smoke", "Storage Smoke", includeDenseTopology: true, "deca-flame-standard-model-checkpoint-v1");
			Require(avatarObservationDataset.Observations.Count == 3, "The mixed catalog did not retain one recurrent observation and two independent Standard Model checkpoints.");
			Require(avatarObservationDataset2.Observations.Count == 1, "Backend filtering did not isolate the recurrent observation.");
			Require(avatarObservationDataset3.Observations.Count == 2, "Backend filtering did not isolate both Standard Model checkpoints.");
			Require(avatarObservationDataset3.Observations.All((AvatarObservation observation) => string.Equals(observation.BackendId, "deca-flame-standard-model-checkpoint-v1", StringComparison.Ordinal) && observation.IdentityUse.Contains("Standard Model checkpoint", StringComparison.Ordinal)), "A retained checkpoint lost its Standard Model lineage metadata.");
			AvatarModel full = AvatarModelBuilder.Build(avatarObservationDataset2, repository);
			AvatarModel avatarModel = AvatarModelBuilder.Build(avatarObservationDataset, repository);
			RequireModelsEqual(avatarModel, full, "Standard Model checkpoint exclusion");
			Require(avatarModel.Identity.SampleCount == 1 && avatarModel.Expression.SampleCount == 1 && avatarModel.PoseCoverage.TotalSampleCount == 1 && avatarModel.RecentSamples.Count == 1, "Standard Model checkpoints entered AvatarModelBuilder's legacy averaging, expression, coverage, or recent-sample path.");
			AvatarModel avatarModel2 = AvatarModelBuilder.Build(avatarObservationDataset3, repository);
			Require(avatarModel2.Identity.SampleCount == 0 && avatarModel2.Identity.MeanDenseVertices.Count == 0 && avatarModel2.RecentSamples.Count == 0, "AvatarModelBuilder treated Standard Model checkpoints as legacy averaged identity observations.");
		}
		finally
		{
			repository.ResetProfile(profileFolder, "storage-smoke");
		}
	}

	private static void VerifyLegacyArchiveMigration(AvatarObservationRepository repository, string session)
	{
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = session;
		buffer[1] = "legacy-migration";
		buffer[2] = "AvatarSystem";
		buffer[3] = "People";
		buffer[4] = "storage-smoke";
		string text = Path.Combine(buffer);
		Require((object)repository.SaveCapture(CreateCapture(text, "legacy-migration-source", 95.0, 95.0, 0.0, "3ddfa-v2-onnx-reconstruction", 0L)).AcceptedObservation != null, "Legacy migration fixture was not retained.");
		AvatarObservation avatarObservation = repository.SaveCapture(CreateCapture(text, "deca-migration-control", 96.0, 96.0, 0.5, "deca-flame-recurrent-v4", 0L)).AcceptedObservation ?? throw new InvalidOperationException("DECA migration control was not retained.");
		AvatarObservationDataset avatarObservationDataset = repository.ReadDataset(text, "storage-smoke", "Storage Smoke", includeDenseTopology: false, "3ddfa-v2-onnx-reconstruction");
		string path = repository.GetImagePath(avatarObservationDataset, avatarObservationDataset.Observations.Single()) ?? throw new InvalidOperationException("Legacy migration fixture image was missing.");
		string path2 = Path.Combine(text, "avatar_model.json");
		Directory.CreateDirectory(text);
		File.WriteAllText(path2, "legacy-model");
		AvatarDataReviewServer avatarDataReviewServer = new AvatarDataReviewServer(repository);
		try
		{
			Uri baseAddress = avatarDataReviewServer.StartOrUpdate(text, "storage-smoke", "Storage Smoke", "deca-flame-recurrent-v4");
			using HttpClient httpClient = new HttpClient
			{
				BaseAddress = baseAddress
			};
			AvatarDataReviewManifest avatarDataReviewManifest = JsonSerializer.Deserialize<AvatarDataReviewManifest>(httpClient.GetStringAsync("api/manifest").GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
			AvatarDataReviewScanSummary? obj = avatarDataReviewManifest?.Scans.SingleOrDefault() ?? throw new InvalidOperationException("Backend-specific review returned no DECA observation.");
			Require(avatarDataReviewManifest.StoredScanCount == 1, "Backend-specific review did not isolate the DECA observation.");
			Require(string.Equals(obj.Source, avatarObservation.Source, StringComparison.Ordinal), "Backend-specific review returned legacy scan data.");
		}
		finally
		{
			avatarDataReviewServer.DisposeAsync().AsTask().GetAwaiter()
				.GetResult();
		}
		LegacyAvatarReprocessingArchiveResult legacyAvatarReprocessingArchiveResult = new LegacyAvatarReprocessingArchive().ArchiveAndDeleteBackend(text, "storage-smoke", "Storage Smoke", "3ddfa-v2-onnx-reconstruction", repository);
		Require(legacyAvatarReprocessingArchiveResult.ArchivedImageCount == 1, "Legacy migration did not archive its exact source image.");
		Require(legacyAvatarReprocessingArchiveResult.DeletedObservationCount == 1, "Legacy migration did not remove exactly its selected backend.");
		Require(File.Exists(legacyAvatarReprocessingArchiveResult.ManifestPath), "Legacy migration did not write its reprocessing manifest.");
		Require(Directory.EnumerateFiles(Path.Combine(legacyAvatarReprocessingArchiveResult.ArchiveRoot, "Images")).Count() == 1, "Legacy migration archive image count was incorrect.");
		Require(!File.Exists(path), "Legacy migration left its old content-addressed image behind.");
		Require(!File.Exists(path2), "Legacy migration left a stale derived model behind.");
		Require(repository.ReadDataset(text, "storage-smoke", "Storage Smoke", includeDenseTopology: true, "3ddfa-v2-onnx-reconstruction").Observations.Count == 0, "Legacy migration left selected-backend observations behind.");
		Require(repository.ReadDataset(text, "storage-smoke", "Storage Smoke", includeDenseTopology: true, "deca-flame-recurrent-v4").Observations.Count == 1, "Legacy migration deleted an unrelated DECA observation.");
		Require(repository.ResetProfile(text, "storage-smoke") == 1, "Legacy migration control cleanup failed.");
	}

	private static void RequireModelsEqual(AvatarModel incremental, AvatarModel full, string stage)
	{
		Require(incremental.Identity.MeanDenseVertices.Count == full.Identity.MeanDenseVertices.Count, "Incremental model vertex count diverged after " + stage + ".");
		for (int i = 0; i < full.Identity.MeanDenseVertices.Count; i++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = incremental.Identity.MeanDenseVertices[i];
			FaceMeshLandmarkPoint faceMeshLandmarkPoint2 = full.Identity.MeanDenseVertices[i];
			Require(Math.Max(Math.Abs(faceMeshLandmarkPoint.X - faceMeshLandmarkPoint2.X), Math.Max(Math.Abs(faceMeshLandmarkPoint.Y - faceMeshLandmarkPoint2.Y), Math.Abs(faceMeshLandmarkPoint.Z - faceMeshLandmarkPoint2.Z))) <= 1E-09, $"Incremental vertex math diverged after {stage} at vertex {i}.");
		}
		Require(incremental.Identity.MeanShapeCoefficients.Count == full.Identity.MeanShapeCoefficients.Count, "Incremental shape coefficient count diverged after " + stage + ".");
		for (int j = 0; j < full.Identity.MeanShapeCoefficients.Count; j++)
		{
			Require(Math.Abs(incremental.Identity.MeanShapeCoefficients[j] - full.Identity.MeanShapeCoefficients[j]) <= 1E-09, $"Incremental shape coefficient math diverged after {stage} at coefficient {j}.");
		}
	}

	private static BitmapSource CreateFrame(double offset)
	{
		byte[] array = new byte[65536];
		byte b = (byte)Math.Clamp(Math.Round(offset * 1000.0), 0.0, 255.0);
		for (int i = 0; i < 16384; i++)
		{
			array[i * 4] = (byte)(i % 128);
			array[i * 4 + 1] = (byte)(i / 128);
			array[i * 4 + 2] = b;
			array[i * 4 + 3] = byte.MaxValue;
		}
		BitmapSource bitmapSource = BitmapSource.Create(128, 128, 96.0, 96.0, PixelFormats.Bgra32, null, array, 512);
		bitmapSource.Freeze();
		return bitmapSource;
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
			string value = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string fullPath = Path.GetFullPath(path);
			if (fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
			{
				Directory.Delete(fullPath, recursive: true);
			}
		}
		catch
		{
		}
	}
}
