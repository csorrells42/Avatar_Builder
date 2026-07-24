using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Maintains short-lived multi-person tracks and persists only face memories
/// supported by a sustained, coherent encounter. Raw frames are never stored.
/// Avatar linkage is a separate, explicit operation.
/// </summary>
public sealed class PersonIdentityMemory : IDisposable
{
	private const int MinimumRetentionObservations = 12;

	private const int MaximumFacesPerObservation = 8;

	private const int MaximumPrototypesPerIdentity = 12;

	private const double KnownIdentitySimilarity = 0.50d;

	private const double DuplicatePreventionSimilarity = 0.44d;

	private const double MinimumMatchMargin = 0.06d;

	private const double TemporaryTrackSimilarity = 0.42d;

	private static readonly TimeSpan MinimumRetentionDuration =
		TimeSpan.FromSeconds(8);

	private static readonly TimeSpan TemporaryTrackLifetime =
		TimeSpan.FromSeconds(20);

	private static readonly TimeSpan NewEncounterGap =
		TimeSpan.FromMinutes(2);

	private static readonly TimeSpan PersistenceInterval =
		TimeSpan.FromSeconds(15);

	private static readonly TimeSpan ActiveIdentityMaximumAge =
		TimeSpan.FromSeconds(3);

	private readonly object _stateLock = new();

	private readonly object _inferenceLock = new();

	private readonly PersonIdentityMemoryStore _store = new();

	private readonly OpenCv.OpenCvYuNetFaceDetector _faceDetector = new();

	private readonly SFaceEmbeddingExtractor? _embeddingExtractor;

	private readonly List<PersonIdentityRecord> _rememberedPeople = [];

	private readonly List<TrackState> _activeTracks = [];

	private readonly HashSet<string> _dirtyIdentityIds =
		new(StringComparer.OrdinalIgnoreCase);

	private string _outputFolder = "";

	private DateTime _lastSavedAtUtc = DateTime.MinValue;

	private PersonIdentitySnapshot _latestSnapshot =
		PersonIdentitySnapshot.Waiting;

	private string _initializationStatus;

	private bool _disposed;

	public event EventHandler<PersonIdentitySnapshot>? SnapshotChanged;

	public bool IsAvailable => _embeddingExtractor is not null
		&& _faceDetector.IsAvailable;

	public string Status
	{
		get
		{
			lock (_stateLock)
			{
				return _latestSnapshot == PersonIdentitySnapshot.Waiting
					? _initializationStatus
					: _latestSnapshot.Status;
			}
		}
	}

	public PersonIdentitySnapshot LatestSnapshot
	{
		get
		{
			lock (_stateLock)
			{
				return _latestSnapshot;
			}
		}
	}

	public IReadOnlyList<PersonIdentityReviewItem> GetIdentityReviewItems()
	{
		lock (_stateLock)
		{
			return _rememberedPeople
				.OrderByDescending(person => person.LastSeenAtUtc)
				.Select(person => new PersonIdentityReviewItem(
					person.Id,
					person.DisplayName,
					string.IsNullOrWhiteSpace(_outputFolder)
						? ""
						: _store.GetContextPhotoPath(
							_outputFolder,
							person.Id),
					person.IsRegisteredUser,
					NormalizePermission(person.PermissionLevel),
					person.AvatarProfileId,
					person.FirstSeenAtUtc,
					person.LastSeenAtUtc,
					person.ObservationCount,
					person.EncounterCount))
				.ToArray();
		}
	}

	public bool UpdateIdentityReview(
		string identityId,
		string displayName,
		bool registerAsUser,
		string permissionLevel)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
		lock (_stateLock)
		{
			PersonIdentityRecord? person =
				_rememberedPeople.FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						identityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return false;
			}
			person.DisplayName = displayName.Trim();
			person.IsRegisteredUser = registerAsUser;
			person.PermissionLevel =
				registerAsUser
					? NormalizePermission(permissionLevel)
					: "Default User";
			_dirtyIdentityIds.Add(person.Id);
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			return true;
		}
	}

	public PersonIdentityMemory()
		: this(initializeModels: true)
	{
	}

	internal PersonIdentityMemory(bool initializeModels)
	{
		if (!initializeModels)
		{
			_initializationStatus = "People memory self-test";
			return;
		}
		FaceIdentityModelInfo model = FaceIdentityModelInfo.Load();
		if (!model.IsReady)
		{
			_initializationStatus = model.Status;
			return;
		}
		try
		{
			_embeddingExtractor =
				new SFaceEmbeddingExtractor(model.ModelPath);
			_initializationStatus =
				$"{_embeddingExtractor.BackendName} people memory ready";
		}
		catch (Exception ex)
		{
			_initializationStatus =
				"People memory unavailable: " + ex.Message;
		}
	}

	internal PersonIdentitySnapshot ObserveEmbeddingFrameForSelfTest(
		IReadOnlyList<float[]> embeddings,
		DateTime capturedAtUtc)
	{
		var samples = new List<FaceSample>(embeddings.Count);
		for (int index = 0; index < embeddings.Count; index++)
		{
			double left = 0.08d + index * 0.11d;
			samples.Add(new FaceSample(
				embeddings[index].ToArray(),
				0.96d,
				new PersonFaceBox(
					left,
					0.12d,
					Math.Min(0.96d, left + 0.09d),
					0.88d)));
		}
		lock (_stateLock)
		{
			_latestSnapshot = UpdateMemoryLocked(samples, capturedAtUtc);
			return _latestSnapshot;
		}
	}

	public void ConfigureOutputFolder(string outputFolder)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		lock (_stateLock)
		{
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			_outputFolder = outputFolder;
			_rememberedPeople.Clear();
			_rememberedPeople.AddRange(_store.Load(outputFolder));
			_activeTracks.Clear();
			_dirtyIdentityIds.Clear();
			_lastSavedAtUtc = DateTime.UtcNow;
			_latestSnapshot = new PersonIdentitySnapshot(
				DateTime.MinValue,
				Array.Empty<PersonIdentityObservation>(),
				_rememberedPeople.Count,
				_embeddingExtractor?.BackendName ?? "",
				_initializationStatus);
		}
	}

	public void ObserveBgra(
		byte[] bgraPixels,
		int width,
		int height,
		int stride,
		DateTime capturedAtUtc)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		SFaceEmbeddingExtractor? extractor = _embeddingExtractor;
		if (extractor is null
			|| !_faceDetector.IsAvailable
			|| width <= 0
			|| height <= 0
			|| stride < width * 4
			|| bgraPixels.Length < stride * height)
		{
			return;
		}

		lock (_inferenceLock)
		{
			using Mat bgra = Mat.FromPixelData(
				height,
				width,
				MatType.CV_8UC4,
				bgraPixels,
				stride);
			using Mat bgr = new();
			Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
			ObserveBgrLocked(
				bgr,
				extractor,
				capturedAtUtc == default
					? DateTime.UtcNow
					: capturedAtUtc);
		}
	}

	public void ObserveBgr(
		Mat bgrFrame,
		DateTime capturedAtUtc)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		SFaceEmbeddingExtractor? extractor = _embeddingExtractor;
		if (extractor is null
			|| !_faceDetector.IsAvailable
			|| bgrFrame.Empty()
			|| bgrFrame.Channels() != 3)
		{
			return;
		}
		lock (_inferenceLock)
		{
			ObserveBgrLocked(
				bgrFrame,
				extractor,
				capturedAtUtc == default
					? DateTime.UtcNow
					: capturedAtUtc);
		}
	}

	private void ObserveBgrLocked(
		Mat sourceBgr,
		SFaceEmbeddingExtractor extractor,
		DateTime capturedAtUtc)
	{
		using Mat resizedBgr = new();
		Mat observationBgr = sourceBgr;
		const int maximumObservationDimension = 960;
		int sourceDimension = Math.Max(sourceBgr.Width, sourceBgr.Height);
		if (sourceDimension > maximumObservationDimension)
		{
			double scale =
				(double)maximumObservationDimension /
				sourceDimension;
			Cv2.Resize(
				sourceBgr,
				resizedBgr,
				new Size(
					Math.Max(1, (int)Math.Round(sourceBgr.Width * scale)),
					Math.Max(1, (int)Math.Round(sourceBgr.Height * scale))));
			observationBgr = resizedBgr;
		}
		List<FaceSample> samples = [];
		IReadOnlyList<OpenCv.YuNetFaceDetection> faces =
			_faceDetector.DetectAll(observationBgr);
		foreach (OpenCv.YuNetFaceDetection face in faces
			.Where(face => face.Score >= 0.72d)
			.Take(MaximumFacesPerObservation))
		{
			if (!extractor.TryExtract(
				observationBgr,
				face,
				out float[] embedding))
			{
				continue;
			}
			samples.Add(new FaceSample(
				embedding,
				face.Score,
				ToNormalizedBox(
					face.FaceBox,
					observationBgr.Width,
					observationBgr.Height),
				CalculateContextPhotoQuality(face)));
		}
		var contextPhoto = new Lazy<byte[]>(() =>
		{
			Cv2.ImEncode(
				".jpg",
				sourceBgr,
				out byte[] jpeg,
				[(int)ImwriteFlags.JpegQuality, 92]);
			return jpeg;
		});
		PersonIdentitySnapshot snapshot;
		lock (_stateLock)
		{
			snapshot = UpdateMemoryLocked(
				samples,
				capturedAtUtc,
				() => contextPhoto.Value);
			_latestSnapshot = snapshot;
		}
		try
		{
			SnapshotChanged?.Invoke(this, snapshot);
		}
		catch
		{
		}
	}

	public string GetActiveAvatarProfileId()
	{
		lock (_stateLock)
		{
			if (!TryGetSingleActiveRememberedPersonLocked(
				DateTime.UtcNow,
				out PersonIdentityRecord? person))
			{
				return "";
			}
			return person.AvatarProfileId;
		}
	}

	public bool TryGetActiveRememberedPerson(
		out string personIdentityId,
		out string avatarProfileId)
	{
		lock (_stateLock)
		{
			if (!TryGetSingleActiveRememberedPersonLocked(
				DateTime.UtcNow,
				out PersonIdentityRecord? person))
			{
				personIdentityId = "";
				avatarProfileId = "";
				return false;
			}
			personIdentityId = person.Id;
			avatarProfileId = person.AvatarProfileId;
			return true;
		}
	}

	public AvatarIdentityAuthorization AuthorizeAvatarCapture(
		string avatarProfileId,
		string avatarDisplayName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(avatarProfileId);
		lock (_stateLock)
		{
			if (!TryGetSingleActiveRememberedPersonLocked(
				DateTime.UtcNow,
				out PersonIdentityRecord? person))
			{
				return new AvatarIdentityAuthorization(
					false,
					"",
					"",
					"Avatar capture needs one remembered face in view. " +
					"Keep one person visible until People memory says remembered.");
			}
			PersonIdentityRecord? profileOwner =
				_rememberedPeople.FirstOrDefault(candidate =>
					string.Equals(
						candidate.AvatarProfileId,
						avatarProfileId,
						StringComparison.OrdinalIgnoreCase));
			if (profileOwner is not null
				&& !ReferenceEquals(profileOwner, person))
			{
				return new AvatarIdentityAuthorization(
					false,
					person.Id,
					profileOwner.AvatarProfileId,
					"That avatar profile is already linked to a different " +
					"remembered person. Capture was not started.");
			}
			if (!string.IsNullOrWhiteSpace(person.AvatarProfileId)
				&& !string.Equals(
					person.AvatarProfileId,
					avatarProfileId,
					StringComparison.OrdinalIgnoreCase))
			{
				return new AvatarIdentityAuthorization(
					false,
					person.Id,
					person.AvatarProfileId,
					"This remembered person already owns avatar profile " +
					$"'{person.AvatarProfileId}'. A second avatar identity " +
					"was not created.");
			}
			if (string.IsNullOrWhiteSpace(person.AvatarProfileId))
			{
				person.AvatarProfileId = avatarProfileId;
				person.DisplayName = avatarDisplayName.Trim();
				if (IsOwnerProfile(avatarProfileId))
				{
					person.IsRegisteredUser = true;
					person.PermissionLevel = "Superuser";
				}
				_dirtyIdentityIds.Add(person.Id);
				SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			}
			return new AvatarIdentityAuthorization(
				true,
				person.Id,
				person.AvatarProfileId,
				"Avatar capture consent linked this remembered face to " +
				$"'{avatarDisplayName}'. This is a face-memory match, " +
				"not identity authentication.");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		lock (_stateLock)
		{
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			_activeTracks.Clear();
		}
		lock (_inferenceLock)
		{
			_embeddingExtractor?.Dispose();
			_faceDetector.Dispose();
		}
	}

	private PersonIdentitySnapshot UpdateMemoryLocked(
		IReadOnlyList<FaceSample> samples,
		DateTime capturedAtUtc,
		Func<byte[]>? contextPhotoFactory = null)
	{
		PruneExpiredTracksLocked(capturedAtUtc);
		var usedTracks = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);
		var usedIdentities = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);
		var observations = new List<PersonIdentityObservation>(samples.Count);
		bool retainedNewPerson = false;

		foreach (FaceSample sample in samples)
		{
			KnownMatch known = FindKnownMatchLocked(
				sample.Embedding,
				usedIdentities);
			TrackState track;
			PersonIdentityRecord? person = null;
			double similarity = 0d;
			bool personCreated = false;
			if (known.Person is PersonIdentityRecord recognizedPerson
				&& known.BestSimilarity >= KnownIdentitySimilarity
				&& known.BestSimilarity - known.SecondSimilarity
					>= MinimumMatchMargin)
			{
				person = recognizedPerson;
				similarity = known.BestSimilarity;
				track = FindOrCreateKnownTrackLocked(
					recognizedPerson,
					sample,
					capturedAtUtc,
					usedTracks);
			}
			else
			{
				track = FindOrCreateTemporaryTrackLocked(
					sample,
					capturedAtUtc,
					usedTracks);
				if (!string.IsNullOrWhiteSpace(track.IdentityId))
				{
					person = _rememberedPeople.FirstOrDefault(candidate =>
						string.Equals(
							candidate.Id,
							track.IdentityId,
							StringComparison.OrdinalIgnoreCase));
				}
			}

			track.Update(
				sample,
				capturedAtUtc,
				person is null ? contextPhotoFactory : null);
			usedTracks.Add(track.TrackId);
			if (person is null && IsReadyToRetain(track))
			{
				KnownMatch duplicate = FindKnownMatchLocked(
					track.Centroid,
					usedIdentities);
				if (duplicate.Person is not null
					&& duplicate.BestSimilarity
						>= DuplicatePreventionSimilarity
					&& duplicate.BestSimilarity
						- duplicate.SecondSimilarity
						>= MinimumMatchMargin)
				{
					person = duplicate.Person;
					similarity = duplicate.BestSimilarity;
				}
				else
				{
					person = RetainTrackLocked(track, capturedAtUtc);
					similarity = 1d;
					retainedNewPerson = true;
					personCreated = true;
				}
				track.IdentityId = person.Id;
			}
			if (person is not null)
			{
				track.IdentityId = person.Id;
				usedIdentities.Add(person.Id);
				if (!personCreated)
				{
					UpdateRememberedPersonLocked(
						person,
						sample,
						capturedAtUtc);
				}
				if (similarity <= 0d)
				{
					similarity = MaximumSimilarity(
						sample.Embedding,
						person.Prototypes);
				}
			}
			observations.Add(new PersonIdentityObservation(
				track.TrackId,
				person?.Id ?? "",
				person?.DisplayName ?? "",
				person?.AvatarProfileId ?? "",
				person is not null,
				Math.Clamp(similarity, 0d, 1d),
				sample.FaceBox));
		}

		SaveIfDirtyLocked(
			force: retainedNewPerson,
			capturedAtUtc);
		int rememberedInFrame =
			observations.Count(observation => observation.IsRemembered);
		int learningInFrame = observations.Count - rememberedInFrame;
		string status = FormatStatus(
			observations.Count,
			rememberedInFrame,
			learningInFrame);
		return new PersonIdentitySnapshot(
			capturedAtUtc,
			observations,
			_rememberedPeople.Count,
			_embeddingExtractor?.BackendName ?? "",
			status);
	}

	private TrackState FindOrCreateKnownTrackLocked(
		PersonIdentityRecord person,
		FaceSample sample,
		DateTime capturedAtUtc,
		ISet<string> usedTracks)
	{
		TrackState? track = _activeTracks
			.Where(candidate =>
				!usedTracks.Contains(candidate.TrackId)
				&& string.Equals(
					candidate.IdentityId,
					person.Id,
					StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(candidate =>
				IntersectionOverUnion(
					candidate.LastFaceBox,
					sample.FaceBox))
			.FirstOrDefault();
		if (track is not null)
		{
			return track;
		}
		track = new TrackState(sample, capturedAtUtc)
		{
			IdentityId = person.Id
		};
		_activeTracks.Add(track);
		return track;
	}

	private TrackState FindOrCreateTemporaryTrackLocked(
		FaceSample sample,
		DateTime capturedAtUtc,
		ISet<string> usedTracks)
	{
		TrackState? best = null;
		double bestScore = double.NegativeInfinity;
		foreach (TrackState candidate in _activeTracks)
		{
			if (usedTracks.Contains(candidate.TrackId)
				|| !string.IsNullOrWhiteSpace(candidate.IdentityId)
				|| capturedAtUtc - candidate.LastSeenAtUtc
					> TemporaryTrackLifetime)
			{
				continue;
			}
			double similarity = Cosine(
				sample.Embedding,
				candidate.Centroid);
			double overlap = IntersectionOverUnion(
				sample.FaceBox,
				candidate.LastFaceBox);
			if (similarity < TemporaryTrackSimilarity
				|| (similarity < KnownIdentitySimilarity
					&& overlap < 0.05d))
			{
				continue;
			}
			double score = similarity + overlap * 0.12d;
			if (score > bestScore)
			{
				best = candidate;
				bestScore = score;
			}
		}
		if (best is not null)
		{
			return best;
		}
		var created = new TrackState(sample, capturedAtUtc);
		_activeTracks.Add(created);
		return created;
	}

	private KnownMatch FindKnownMatchLocked(
		IReadOnlyList<float> embedding,
		ISet<string> excludedIdentityIds)
	{
		PersonIdentityRecord? bestPerson = null;
		double best = -1d;
		double second = -1d;
		foreach (PersonIdentityRecord person in _rememberedPeople)
		{
			if (excludedIdentityIds.Contains(person.Id))
			{
				continue;
			}
			double similarity = MaximumSimilarity(
				embedding,
				person.Prototypes);
			if (similarity > best)
			{
				second = best;
				best = similarity;
				bestPerson = person;
			}
			else if (similarity > second)
			{
				second = similarity;
			}
		}
		return new KnownMatch(bestPerson, best, second);
	}

	private PersonIdentityRecord RetainTrackLocked(
		TrackState track,
		DateTime capturedAtUtc)
	{
		var person = new PersonIdentityRecord
		{
			Id = "person-" + Guid.NewGuid().ToString("N"),
			DisplayName =
				$"Remembered person {_rememberedPeople.Count + 1}",
			FirstSeenAtUtc = track.FirstSeenAtUtc,
			LastSeenAtUtc = capturedAtUtc,
			ObservationCount = track.ObservationCount,
			EncounterCount = 1,
			Prototypes = track.Prototypes
				.Select(prototype => prototype.ToArray())
				.Take(MaximumPrototypesPerIdentity)
				.ToList()
		};
		if (person.Prototypes.Count == 0)
		{
			person.Prototypes.Add(track.Centroid.ToArray());
		}
		_rememberedPeople.Add(person);
		if (!string.IsNullOrWhiteSpace(_outputFolder)
			&& track.BestContextPhotoJpeg is { Length: > 0 } jpeg)
		{
			_store.SaveContextPhoto(
				_outputFolder,
				person.Id,
				jpeg);
			track.ReleaseContextPhoto();
		}
		_dirtyIdentityIds.Add(person.Id);
		return person;
	}

	private void UpdateRememberedPersonLocked(
		PersonIdentityRecord person,
		FaceSample sample,
		DateTime capturedAtUtc)
	{
		if (capturedAtUtc - person.LastSeenAtUtc >= NewEncounterGap)
		{
			person.EncounterCount++;
		}
		person.LastSeenAtUtc = capturedAtUtc;
		person.ObservationCount++;
		if (sample.DetectionScore >= 0.82d
			&& person.Prototypes.Count < MaximumPrototypesPerIdentity
			&& MaximumSimilarity(
				sample.Embedding,
				person.Prototypes) < 0.92d)
		{
			person.Prototypes.Add(sample.Embedding.ToArray());
		}
		_dirtyIdentityIds.Add(person.Id);
	}

	private bool TryGetSingleActiveRememberedPersonLocked(
		DateTime utcNow,
		[NotNullWhen(true)] out PersonIdentityRecord? person)
	{
		person = null;
		PersonIdentitySnapshot snapshot = _latestSnapshot;
		if (snapshot.CapturedAtUtc == DateTime.MinValue
			|| utcNow - snapshot.CapturedAtUtc > ActiveIdentityMaximumAge
			|| snapshot.People.Count != 1)
		{
			return false;
		}
		PersonIdentityObservation observation = snapshot.People[0];
		if (!observation.IsRemembered
			|| string.IsNullOrWhiteSpace(observation.IdentityId))
		{
			return false;
		}
		person = _rememberedPeople.FirstOrDefault(candidate =>
			string.Equals(
				candidate.Id,
				observation.IdentityId,
				StringComparison.OrdinalIgnoreCase));
		return person is not null;
	}

	private void SaveIfDirtyLocked(bool force, DateTime utcNow)
	{
		if (_dirtyIdentityIds.Count == 0
			|| string.IsNullOrWhiteSpace(_outputFolder)
			|| (!force
				&& utcNow - _lastSavedAtUtc < PersistenceInterval))
		{
			return;
		}
		List<PersonIdentityRecord> changedPeople = _rememberedPeople
			.Where(person => _dirtyIdentityIds.Contains(person.Id))
			.ToList();
		_store.Upsert(_outputFolder, changedPeople);
		_lastSavedAtUtc = utcNow;
		_dirtyIdentityIds.Clear();
	}

	private void PruneExpiredTracksLocked(DateTime capturedAtUtc)
	{
		_activeTracks.RemoveAll(track =>
			capturedAtUtc - track.LastSeenAtUtc
				> TemporaryTrackLifetime);
	}

	private static bool IsReadyToRetain(TrackState track)
	{
		return track.ObservationCount >= MinimumRetentionObservations
			&& track.LastSeenAtUtc - track.FirstSeenAtUtc
				>= MinimumRetentionDuration
			&& track.AverageDetectionScore >= 0.78d
			&& track.Prototypes.Count >= 3;
	}

	private static string FormatStatus(
		int visibleCount,
		int rememberedCount,
		int learningCount)
	{
		if (visibleCount == 0)
		{
			return "People memory: no face observed";
		}
		string people = visibleCount == 1
			? "1 face"
			: $"{visibleCount} faces";
		if (rememberedCount == 0)
		{
			return $"People memory: {people}; {learningCount} learning";
		}
		if (learningCount == 0)
		{
			return $"People memory: {people}; {rememberedCount} remembered";
		}
		return "People memory: " +
			$"{people}; {rememberedCount} remembered, " +
			$"{learningCount} learning";
	}

	private static PersonFaceBox ToNormalizedBox(
		Rect face,
		int width,
		int height)
	{
		return new PersonFaceBox(
			Math.Clamp((double)face.Left / width, 0d, 1d),
			Math.Clamp((double)face.Top / height, 0d, 1d),
			Math.Clamp((double)face.Right / width, 0d, 1d),
			Math.Clamp((double)face.Bottom / height, 0d, 1d));
	}

	private static double CalculateContextPhotoQuality(
		OpenCv.YuNetFaceDetection face)
	{
		double eyeDistance = Math.Max(
			8d,
			Math.Sqrt(
				Math.Pow(face.LeftEye.X - face.RightEye.X, 2d)
				+ Math.Pow(face.LeftEye.Y - face.RightEye.Y, 2d)));
		double eyeRoll =
			Math.Abs(face.LeftEye.Y - face.RightEye.Y) / eyeDistance;
		double eyeCenterX =
			(face.LeftEye.X + face.RightEye.X) * 0.5d;
		double noseOffset =
			Math.Abs(face.NoseTip.X - eyeCenterX) / eyeDistance;
		double mouthTilt =
			Math.Abs(
				face.LeftMouthCorner.Y
				- face.RightMouthCorner.Y) / eyeDistance;
		double frontal =
			1d
			- Math.Clamp(
				eyeRoll * 2.2d
				+ noseOffset * 1.35d
				+ mouthTilt * 0.8d,
				0d,
				1d);
		return Math.Clamp(face.Score * frontal, 0d, 1d);
	}

	private static string NormalizePermission(string? permission)
	{
		return string.Equals(
			permission?.Trim(),
			"Superuser",
			StringComparison.OrdinalIgnoreCase)
			? "Superuser"
			: "Default User";
	}

	private static bool IsOwnerProfile(string avatarProfileId)
	{
		return string.Equals(
				avatarProfileId,
				"chris",
				StringComparison.OrdinalIgnoreCase)
			|| string.Equals(
				avatarProfileId,
				"chris2",
				StringComparison.OrdinalIgnoreCase);
	}

	private static double MaximumSimilarity(
		IReadOnlyList<float> embedding,
		IEnumerable<float[]> prototypes)
	{
		double best = -1d;
		foreach (float[] prototype in prototypes)
		{
			best = Math.Max(best, Cosine(embedding, prototype));
		}
		return best;
	}

	private static double Cosine(
		IReadOnlyList<float> first,
		IReadOnlyList<float> second)
	{
		if (first.Count != second.Count || first.Count == 0)
		{
			return -1d;
		}
		double dot = 0d;
		for (int index = 0; index < first.Count; index++)
		{
			dot += first[index] * second[index];
		}
		return Math.Clamp(dot, -1d, 1d);
	}

	private static double IntersectionOverUnion(
		PersonFaceBox first,
		PersonFaceBox second)
	{
		double left = Math.Max(first.Left, second.Left);
		double top = Math.Max(first.Top, second.Top);
		double right = Math.Min(first.Right, second.Right);
		double bottom = Math.Min(first.Bottom, second.Bottom);
		double intersection =
			Math.Max(0d, right - left) *
			Math.Max(0d, bottom - top);
		double firstArea =
			Math.Max(0d, first.Right - first.Left) *
			Math.Max(0d, first.Bottom - first.Top);
		double secondArea =
			Math.Max(0d, second.Right - second.Left) *
			Math.Max(0d, second.Bottom - second.Top);
		double union = firstArea + secondArea - intersection;
		return union <= 1e-8d ? 0d : intersection / union;
	}

	private sealed class TrackState
	{
		public string TrackId { get; } =
			"track-" + Guid.NewGuid().ToString("N");

		public string IdentityId { get; set; } = "";

		public DateTime FirstSeenAtUtc { get; }

		public DateTime LastSeenAtUtc { get; private set; }

		public PersonFaceBox LastFaceBox { get; private set; }

		public int ObservationCount { get; private set; }

		public double AverageDetectionScore { get; private set; }

		public float[] Centroid { get; private set; }

		public List<float[]> Prototypes { get; } = [];

		public byte[]? BestContextPhotoJpeg { get; private set; }

		private double _bestContextPhotoQuality =
			double.NegativeInfinity;

		public TrackState(FaceSample initial, DateTime capturedAtUtc)
		{
			FirstSeenAtUtc = capturedAtUtc;
			LastSeenAtUtc = capturedAtUtc;
			LastFaceBox = initial.FaceBox;
			Centroid = initial.Embedding.ToArray();
		}

		public void Update(
			FaceSample sample,
			DateTime capturedAtUtc,
			Func<byte[]>? contextPhotoFactory)
		{
			int previousCount = ObservationCount;
			ObservationCount++;
			LastSeenAtUtc = capturedAtUtc;
			LastFaceBox = sample.FaceBox;
			AverageDetectionScore =
				(AverageDetectionScore * previousCount
					+ sample.DetectionScore) /
				ObservationCount;
			float previousWeight = previousCount;
			for (int index = 0; index < Centroid.Length; index++)
			{
				Centroid[index] =
					(Centroid[index] * previousWeight
						+ sample.Embedding[index]) /
					ObservationCount;
			}
			NormalizeInPlace(Centroid);
			if (Prototypes.Count < MaximumPrototypesPerIdentity
				&& (ObservationCount == 1
					|| ObservationCount % 3 == 0))
			{
				Prototypes.Add(sample.Embedding.ToArray());
			}
			if (contextPhotoFactory is not null
				&& sample.ContextPhotoQuality
					> _bestContextPhotoQuality + 0.005d)
			{
				byte[] candidate = contextPhotoFactory();
				if (candidate.Length > 0)
				{
					BestContextPhotoJpeg = candidate;
					_bestContextPhotoQuality =
						sample.ContextPhotoQuality;
				}
			}
		}

		public void ReleaseContextPhoto()
		{
			BestContextPhotoJpeg = null;
		}

		private static void NormalizeInPlace(float[] values)
		{
			double squaredNorm = 0d;
			for (int index = 0; index < values.Length; index++)
			{
				squaredNorm += values[index] * values[index];
			}
			double norm = Math.Sqrt(squaredNorm);
			if (norm < 1e-8d)
			{
				return;
			}
			float inverseNorm = (float)(1d / norm);
			for (int index = 0; index < values.Length; index++)
			{
				values[index] *= inverseNorm;
			}
		}
	}

	private sealed record FaceSample(
		float[] Embedding,
		double DetectionScore,
		PersonFaceBox FaceBox,
		double ContextPhotoQuality = 0d);

	private readonly record struct KnownMatch(
		PersonIdentityRecord? Person,
		double BestSimilarity,
		double SecondSimilarity);
}
