using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationRepository
{
	private sealed class ProfileState
	{
		public string DisplayName { get; init; } = "";

		public long Revision { get; init; }

		public long AcceptedCount { get; init; }

		public long RejectedCount { get; init; }

		public DateTime UpdatedAtUtc { get; init; }
	}

	private static readonly ConcurrentDictionary<string, object> DatabaseLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public AvatarObservationWriteResult SaveCapture(AvatarObservationCapture capture)
	{
		ArgumentNullException.ThrowIfNull(capture, "capture");
		string storageRoot = AvatarStorageLayout.GetStorageRoot(capture.ProfileFolder);
		Directory.CreateDirectory(storageRoot);
		string databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
		lock (DatabaseLocks.GetOrAdd(databasePath, (string _) => new object()))
		{
			using SqliteConnection connection = Open(databasePath);
			EnsureSchema(connection);
			List<AvatarObservation> list = ReadObservations(connection, capture.SubjectId, capture.Reconstruction.BackendId);
			if (!string.IsNullOrWhiteSpace(capture.Reconstruction.RequestId) && list.Any((AvatarObservation item) => string.Equals(item.RequestId, capture.Reconstruction.RequestId, StringComparison.Ordinal)))
			{
				ProfileState profileState = ReadProfileState(connection, capture.SubjectId);
				return new AvatarObservationWriteResult(Accepted: false, ReplacedExisting: false, "", "The reconstruction request was already stored.", profileState.Revision, list.Count);
			}
			AvatarObservationRankingDecision avatarObservationRankingDecision = AvatarObservationRanker.Rank(capture, list);
			if (!avatarObservationRankingDecision.Accepted || (object)avatarObservationRankingDecision.Candidate == null)
			{
				ProfileState profileState2 = IncrementRejected(connection, capture.SubjectId, capture.SubjectDisplayName, avatarObservationRankingDecision.Reason);
				return new AvatarObservationWriteResult(Accepted: false, ReplacedExisting: false, "", avatarObservationRankingDecision.Reason, profileState2.Revision, list.Count);
			}
			string text = null;
			string text2 = null;
			string text3 = null;
			AvatarObservation avatarObservation = null;
			AvatarObservation avatarObservation2 = null;
			AvatarObservation avatarObservation3 = null;
			ProfileState profileState3;
			try
			{
				text = AvatarScanBinaryCodec.WriteObject(storageRoot, capture.Reconstruction);
				text2 = AvatarObservationImageCodec.WriteObject(storageRoot, capture.SourceFrame);
				text3 = FindReusableTopologyPath(storageRoot, capture.Reconstruction, list) ?? AvatarTopologyBinaryCodec.WriteObject(storageRoot, capture.Reconstruction.TopologyEdges);
				avatarObservation = avatarObservationRankingDecision.Candidate with
				{
					ScanObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, text),
					ImageObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, text2),
					TopologyObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, text3),
					ScanSha256 = Path.GetFileNameWithoutExtension(text),
					ImageSha256 = Path.GetFileNameWithoutExtension(text2),
					TopologySha256 = Path.GetFileNameWithoutExtension(text3)
				};
				avatarObservation2 = AttachGeometry(avatarObservation, capture.Reconstruction);
				avatarObservation3 = (((object)avatarObservationRankingDecision.Replacement == null) ? null : LoadObservationGeometry(storageRoot, avatarObservationRankingDecision.Replacement));
				profileState3 = CommitAccepted(connection, capture.SubjectId, capture.SubjectDisplayName, avatarObservation, avatarObservationRankingDecision.Replacement, avatarObservationRankingDecision.Reason);
			}
			catch
			{
				TryDeleteUnreferencedFullPath(connection, storageRoot, text);
				TryDeleteUnreferencedFullPath(connection, storageRoot, text2);
				TryDeleteUnreferencedFullPath(connection, storageRoot, text3);
				throw;
			}
			if ((object)avatarObservationRankingDecision.Replacement != null)
			{
				TryDeleteUnreferencedObjects(connection, storageRoot, avatarObservationRankingDecision.Replacement);
			}
			return new AvatarObservationWriteResult(Accepted: true, (object)avatarObservationRankingDecision.Replacement != null, avatarObservation.ObservationId, avatarObservationRankingDecision.Reason, profileState3.Revision, list.Count + (((object)avatarObservationRankingDecision.Replacement == null) ? 1 : 0), avatarObservation2, avatarObservation3);
		}
	}

	public int ResetProfile(string profileFolder, string subjectId)
	{
		string storageRoot = AvatarStorageLayout.GetStorageRoot(profileFolder);
		Directory.CreateDirectory(storageRoot);
		string databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
		lock (DatabaseLocks.GetOrAdd(databasePath, (string _) => new object()))
		{
			using SqliteConnection sqliteConnection = Open(databasePath);
			EnsureSchema(sqliteConnection);
			List<AvatarObservation> list = ReadObservations(sqliteConnection, subjectId);
			using (SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction())
			{
				using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
				sqliteCommand.Transaction = sqliteTransaction;
				sqliteCommand.CommandText = "DELETE FROM observations WHERE subject_id=$subject;";
				sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
				sqliteCommand.ExecuteNonQuery();
				using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
				sqliteCommand2.Transaction = sqliteTransaction;
				sqliteCommand2.CommandText = "UPDATE profiles\nSET revision=revision+1,\n    accepted_count=0,\n    rejected_count=0,\n    last_decision='Profile storage reset by the user.',\n    updated_at_utc=$updated\nWHERE subject_id=$subject;";
				sqliteCommand2.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
				sqliteCommand2.Parameters.AddWithValue("$subject", subjectId);
				sqliteCommand2.ExecuteNonQuery();
				sqliteTransaction.Commit();
			}
			foreach (AvatarObservation item in list)
			{
				TryDeleteUnreferencedObjects(sqliteConnection, storageRoot, item);
			}
			return list.Count;
		}
	}

	public int DeleteBackend(string profileFolder, string subjectId, string backendId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendId, "backendId");
		string storageRoot = AvatarStorageLayout.GetStorageRoot(profileFolder);
		Directory.CreateDirectory(storageRoot);
		string databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
		lock (DatabaseLocks.GetOrAdd(databasePath, (string _) => new object()))
		{
			using SqliteConnection sqliteConnection = Open(databasePath);
			EnsureSchema(sqliteConnection);
			List<AvatarObservation> list = ReadObservations(sqliteConnection, subjectId, backendId);
			if (list.Count == 0)
			{
				return 0;
			}
			using (SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction())
			{
				using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
				sqliteCommand.Transaction = sqliteTransaction;
				sqliteCommand.CommandText = "DELETE FROM observations WHERE subject_id=$subject AND backend_id=$backend;";
				sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
				sqliteCommand.Parameters.AddWithValue("$backend", backendId);
				sqliteCommand.ExecuteNonQuery();
				using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
				sqliteCommand2.Transaction = sqliteTransaction;
				sqliteCommand2.CommandText = "UPDATE profiles\nSET revision=revision+1,\n    accepted_count=(SELECT COUNT(*) FROM observations WHERE subject_id=$subject),\n    last_decision=$decision,\n    updated_at_utc=$updated\nWHERE subject_id=$subject;";
				sqliteCommand2.Parameters.AddWithValue("$subject", subjectId);
				sqliteCommand2.Parameters.AddWithValue("$decision", "Removed obsolete " + backendId + " observations after preserving their source images for reprocessing.");
				sqliteCommand2.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
				sqliteCommand2.ExecuteNonQuery();
				sqliteTransaction.Commit();
			}
			foreach (AvatarObservation item in list)
			{
				TryDeleteUnreferencedObjects(sqliteConnection, storageRoot, item);
			}
			using SqliteCommand sqliteCommand3 = sqliteConnection.CreateCommand();
			sqliteCommand3.CommandText = "VACUUM;";
			sqliteCommand3.ExecuteNonQuery();
			return list.Count;
		}
	}

	public AvatarObservationDataset ReadDataset(string profileFolder, string subjectId, string subjectDisplayName, bool includeDenseTopology = true, string? backendId = null)
	{
		string storageRoot = AvatarStorageLayout.GetStorageRoot(profileFolder);
		Directory.CreateDirectory(storageRoot);
		string databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
		lock (DatabaseLocks.GetOrAdd(databasePath, (string _) => new object()))
		{
			using SqliteConnection connection = Open(databasePath);
			EnsureSchema(connection);
			List<AvatarObservation> list = ReadObservations(connection, subjectId, backendId);
			ProfileState profileState = ReadProfileState(connection, subjectId);
			string text = (from item in list
				orderby item.CapturedAtUtc descending
				select item.TopologyObjectPath).FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value));
			IReadOnlyList<MeshTopologyEdge> denseTopologyEdges = Array.Empty<MeshTopologyEdge>();
			if (includeDenseTopology && !string.IsNullOrWhiteSpace(text))
			{
				string path = AvatarStorageLayout.ResolveObjectPath(storageRoot, text);
				if (File.Exists(path))
				{
					denseTopologyEdges = AvatarTopologyBinaryCodec.Read(path);
				}
			}
			return new AvatarObservationDataset
			{
				StorageRoot = storageRoot,
				SubjectId = subjectId,
				SubjectDisplayName = (string.IsNullOrWhiteSpace(profileState.DisplayName) ? subjectDisplayName : profileState.DisplayName),
				Revision = profileState.Revision,
				AcceptedObservationCount = profileState.AcceptedCount,
				RejectedObservationCount = profileState.RejectedCount,
				UpdatedAtUtc = profileState.UpdatedAtUtc,
				DenseTopologyEdges = denseTopologyEdges,
				Observations = list
			};
		}
	}

	public AvatarObservation LoadObservation(AvatarObservationDataset dataset, AvatarObservation metadata)
	{
		return LoadObservationGeometry(dataset.StorageRoot, metadata);
	}

	private static AvatarObservation LoadObservationGeometry(string storageRoot, AvatarObservation metadata)
	{
		AvatarScanGeometry avatarScanGeometry = AvatarScanBinaryCodec.Read(AvatarStorageLayout.ResolveObjectPath(storageRoot, metadata.ScanObjectPath));
		return metadata with
		{
			Vertices = avatarScanGeometry.Vertices,
			CanonicalIdentityVertices = avatarScanGeometry.CanonicalIdentityVertices,
			SparseLandmarks = avatarScanGeometry.SparseLandmarks,
			CameraMatrixCoefficients = avatarScanGeometry.CameraMatrixCoefficients,
			ShapeCoefficients = avatarScanGeometry.ShapeCoefficients,
			ExpressionCoefficients = avatarScanGeometry.ExpressionCoefficients,
			PoseCoefficients = avatarScanGeometry.PoseCoefficients,
			ObservedLandmarks = avatarScanGeometry.ObservedLandmarks,
			SourceFrameWidthPixels = avatarScanGeometry.SourceFrameWidthPixels,
			SourceFrameHeightPixels = avatarScanGeometry.SourceFrameHeightPixels,
			InputFaceBox = avatarScanGeometry.InputFaceBox
		};
	}

	private static AvatarObservation AttachGeometry(AvatarObservation metadata, AvatarReconstructionSnapshot reconstruction)
	{
		return metadata with
		{
			Vertices = QuantizeStoredPoints(reconstruction.Vertices),
			CanonicalIdentityVertices = QuantizeStoredPoints(reconstruction.CanonicalIdentityVertices),
			SparseLandmarks = QuantizeStoredPoints(reconstruction.SparseLandmarks),
			CameraMatrixCoefficients = reconstruction.CameraMatrixCoefficients.ToList(),
			ShapeCoefficients = reconstruction.ShapeCoefficients.ToList(),
			ExpressionCoefficients = reconstruction.ExpressionCoefficients.ToList(),
			PoseCoefficients = reconstruction.PoseCoefficients.ToList(),
			ObservedLandmarks = QuantizeStoredPoints(reconstruction.ObservedLandmarks),
			SourceFrameWidthPixels = reconstruction.SourceFrameWidthPixels,
			SourceFrameHeightPixels = reconstruction.SourceFrameHeightPixels,
			InputFaceBox = reconstruction.InputFaceBox
		};
	}

	private static List<FaceMeshLandmarkPoint> QuantizeStoredPoints(IReadOnlyList<FaceMeshLandmarkPoint> points)
	{
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(points.Count);
		foreach (FaceMeshLandmarkPoint point in points)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = point.Index,
				X = (float)point.X,
				Y = (float)point.Y,
				Z = (float)point.Z
			});
		}
		return list;
	}

	public string? GetImagePath(AvatarObservationDataset dataset, AvatarObservation observation)
	{
		if (string.IsNullOrWhiteSpace(observation.ImageObjectPath))
		{
			return null;
		}
		string text = AvatarStorageLayout.ResolveObjectPath(dataset.StorageRoot, observation.ImageObjectPath);
		if (!File.Exists(text))
		{
			return null;
		}
		return text;
	}

	public IReadOnlyList<MeshTopologyEdge> LoadTopology(AvatarObservationDataset dataset, AvatarObservation observation)
	{
		if (string.IsNullOrWhiteSpace(observation.TopologyObjectPath))
		{
			return Array.Empty<MeshTopologyEdge>();
		}
		string path = AvatarStorageLayout.ResolveObjectPath(dataset.StorageRoot, observation.TopologyObjectPath);
		if (!File.Exists(path))
		{
			return new List<MeshTopologyEdge>();
		}
		return AvatarTopologyBinaryCodec.Read(path);
	}

	private static string? FindReusableTopologyPath(string storageRoot, AvatarReconstructionSnapshot reconstruction, IReadOnlyList<AvatarObservation> existing)
	{
		string text = (from item in existing
			where item.DenseVertexCount == reconstruction.Vertices.Count
			orderby item.CapturedAtUtc descending
			select item.TopologyObjectPath).FirstOrDefault((string path) => !string.IsNullOrWhiteSpace(path));
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string text2 = AvatarStorageLayout.ResolveObjectPath(storageRoot, text);
		if (!File.Exists(text2))
		{
			return null;
		}
		return text2;
	}

	private static SqliteConnection Open(string databasePath)
	{
		SqliteConnection sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared,
			Pooling = true
		}.ToString());
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
		sqliteCommand.ExecuteNonQuery();
		return sqliteConnection;
	}

	private static void EnsureSchema(SqliteConnection connection)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA journal_mode=WAL;\nPRAGMA synchronous=NORMAL;\nPRAGMA temp_store=MEMORY;\nCREATE TABLE IF NOT EXISTS storage_meta (\n    key TEXT PRIMARY KEY,\n    value TEXT NOT NULL\n);\nINSERT INTO storage_meta(key, value) VALUES('schema_version', '1')\n    ON CONFLICT(key) DO NOTHING;\nCREATE TABLE IF NOT EXISTS profiles (\n    subject_id TEXT PRIMARY KEY,\n    display_name TEXT NOT NULL,\n    revision INTEGER NOT NULL DEFAULT 0,\n    accepted_count INTEGER NOT NULL DEFAULT 0,\n    rejected_count INTEGER NOT NULL DEFAULT 0,\n    last_decision TEXT NOT NULL DEFAULT '',\n    updated_at_utc TEXT NOT NULL\n);\nCREATE TABLE IF NOT EXISTS observations (\n    observation_id TEXT PRIMARY KEY,\n    subject_id TEXT NOT NULL,\n    request_id TEXT NOT NULL,\n    sample_id TEXT NOT NULL,\n    captured_at_utc TEXT NOT NULL,\n    source TEXT NOT NULL,\n    backend_id TEXT NOT NULL DEFAULT '3ddfa-v2-onnx-reconstruction',\n    reconstruction_confidence REAL NOT NULL,\n    model_sequence INTEGER NOT NULL DEFAULT 0,\n    coefficient_delta_rms REAL NOT NULL DEFAULT 1000000,\n    sample_quality REAL NOT NULL,\n    eye_quality REAL NOT NULL,\n    mouth_quality REAL NOT NULL,\n    brow_quality REAL NOT NULL,\n    stability_quality REAL NOT NULL,\n    a_rotation REAL NOT NULL,\n    b_rotation REAL NOT NULL,\n    c_rotation REAL NOT NULL,\n    x_horizontal REAL NOT NULL,\n    y_vertical REAL NOT NULL,\n    relative_distance REAL NULL,\n    apparent_distance REAL NULL,\n    face_width REAL NULL,\n    face_height REAL NULL,\n    identity_weight REAL NOT NULL,\n    expression_weight REAL NOT NULL,\n    identity_score REAL NOT NULL,\n    animation_score REAL NOT NULL,\n    coverage_score REAL NOT NULL,\n    retention_score REAL NOT NULL,\n    expression_energy REAL NOT NULL,\n    pose_bucket TEXT NOT NULL,\n    identity_use TEXT NOT NULL,\n    trust_decision TEXT NOT NULL,\n    scan_object_path TEXT NOT NULL,\n    image_object_path TEXT NOT NULL,\n    topology_object_path TEXT NOT NULL,\n    scan_sha256 TEXT NOT NULL,\n    image_sha256 TEXT NOT NULL,\n    topology_sha256 TEXT NOT NULL,\n    dense_vertex_count INTEGER NOT NULL,\n    canonical_vertex_count INTEGER NOT NULL,\n    shape_coefficients BLOB NOT NULL,\n    expression_coefficients BLOB NOT NULL,\n    warnings_json TEXT NOT NULL,\n    FOREIGN KEY(subject_id) REFERENCES profiles(subject_id) ON DELETE CASCADE\n);\nCREATE UNIQUE INDEX IF NOT EXISTS ux_observation_request\n    ON observations(subject_id, request_id) WHERE request_id <> '';\nCREATE INDEX IF NOT EXISTS ix_observations_subject_time\n    ON observations(subject_id, captured_at_utc);\nCREATE INDEX IF NOT EXISTS ix_observations_subject_bucket_score\n    ON observations(subject_id, pose_bucket, retention_score);";
		sqliteCommand.ExecuteNonQuery();
		EnsureColumn(connection, "observations", "backend_id", "TEXT NOT NULL DEFAULT '3ddfa-v2-onnx-reconstruction'");
		EnsureColumn(connection, "observations", "model_sequence", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn(connection, "observations", "coefficient_delta_rms", "REAL NOT NULL DEFAULT 1000000");
	}

	private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string declaration)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA table_info(" + tableName + ");";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			if (string.Equals(sqliteDataReader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		sqliteDataReader.Close();
		using SqliteCommand sqliteCommand2 = connection.CreateCommand();
		sqliteCommand2.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};";
		sqliteCommand2.ExecuteNonQuery();
	}

	private static List<AvatarObservation> ReadObservations(SqliteConnection connection, string subjectId, string? backendId = null)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = (string.IsNullOrWhiteSpace(backendId) ? "SELECT * FROM observations WHERE subject_id=$subject ORDER BY captured_at_utc;" : "SELECT * FROM observations WHERE subject_id=$subject AND backend_id=$backend ORDER BY captured_at_utc;");
		sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
		if (!string.IsNullOrWhiteSpace(backendId))
		{
			sqliteCommand.Parameters.AddWithValue("$backend", backendId);
		}
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		List<AvatarObservation> list = new List<AvatarObservation>();
		while (sqliteDataReader.Read())
		{
			list.Add(ReadObservation(sqliteDataReader));
		}
		return list;
	}

	private static AvatarObservation ReadObservation(SqliteDataReader reader)
	{
		return new AvatarObservation
		{
			ObservationId = Text(reader, "observation_id"),
			RequestId = Text(reader, "request_id"),
			SampleId = Text(reader, "sample_id"),
			CapturedAtUtc = DateTime.Parse(Text(reader, "captured_at_utc"), null, DateTimeStyles.RoundtripKind),
			BackendId = Text(reader, "backend_id"),
			Source = Text(reader, "source"),
			ReconstructionConfidencePercent = Number(reader, "reconstruction_confidence"),
			ModelSequenceNumber = Integer64(reader, "model_sequence"),
			ModelCoefficientDeltaRms = Number(reader, "coefficient_delta_rms"),
			SampleQualityPercent = Number(reader, "sample_quality"),
			EyeQualityPercent = Number(reader, "eye_quality"),
			MouthQualityPercent = Number(reader, "mouth_quality"),
			BrowQualityPercent = Number(reader, "brow_quality"),
			StabilityQualityPercent = Number(reader, "stability_quality"),
			ARotationAroundXDegrees = Number(reader, "a_rotation"),
			BRotationAroundYDegrees = Number(reader, "b_rotation"),
			CRotationAroundZDegrees = Number(reader, "c_rotation"),
			XHorizontalPercent = Number(reader, "x_horizontal"),
			YVerticalPercent = Number(reader, "y_vertical"),
			RelativeDistanceScale = NullableNumber(reader, "relative_distance"),
			ApparentDistanceUnits = NullableNumber(reader, "apparent_distance"),
			FaceWidthPercent = NullableNumber(reader, "face_width"),
			FaceHeightPercent = NullableNumber(reader, "face_height"),
			IdentityWeightPercent = Number(reader, "identity_weight"),
			ExpressionWeightPercent = Number(reader, "expression_weight"),
			IdentityScorePercent = Number(reader, "identity_score"),
			AnimationScorePercent = Number(reader, "animation_score"),
			CoverageScorePercent = Number(reader, "coverage_score"),
			RetentionScorePercent = Number(reader, "retention_score"),
			ExpressionEnergyPercent = Number(reader, "expression_energy"),
			PoseBucket = Text(reader, "pose_bucket"),
			IdentityUse = Text(reader, "identity_use"),
			TrustDecision = Text(reader, "trust_decision"),
			ScanObjectPath = Text(reader, "scan_object_path"),
			ImageObjectPath = Text(reader, "image_object_path"),
			TopologyObjectPath = Text(reader, "topology_object_path"),
			ScanSha256 = Text(reader, "scan_sha256"),
			ImageSha256 = Text(reader, "image_sha256"),
			TopologySha256 = Text(reader, "topology_sha256"),
			DenseVertexCount = Integer(reader, "dense_vertex_count"),
			CanonicalVertexCount = Integer(reader, "canonical_vertex_count"),
			ShapeCoefficients = BytesToDoubles((byte[])reader["shape_coefficients"]),
			ExpressionCoefficients = BytesToDoubles((byte[])reader["expression_coefficients"]),
			Warnings = (JsonSerializer.Deserialize<List<string>>(Text(reader, "warnings_json"), JsonOptions) ?? new List<string>())
		};
	}

	private static ProfileState CommitAccepted(SqliteConnection connection, string subjectId, string displayName, AvatarObservation candidate, AvatarObservation? replacement, string decision)
	{
		using SqliteTransaction sqliteTransaction = connection.BeginTransaction();
		UpsertProfile(connection, sqliteTransaction, subjectId, displayName);
		if ((object)replacement != null)
		{
			using SqliteCommand sqliteCommand = connection.CreateCommand();
			sqliteCommand.Transaction = sqliteTransaction;
			sqliteCommand.CommandText = "DELETE FROM observations WHERE observation_id=$id;";
			sqliteCommand.Parameters.AddWithValue("$id", replacement.ObservationId);
			sqliteCommand.ExecuteNonQuery();
		}
		InsertObservation(connection, sqliteTransaction, subjectId, candidate);
		using (SqliteCommand sqliteCommand2 = connection.CreateCommand())
		{
			sqliteCommand2.Transaction = sqliteTransaction;
			sqliteCommand2.CommandText = "UPDATE profiles\nSET display_name=$display,\n    revision=revision+1,\n    accepted_count=accepted_count+1,\n    last_decision=$decision,\n    updated_at_utc=$updated\nWHERE subject_id=$subject;";
			sqliteCommand2.Parameters.AddWithValue("$display", displayName);
			sqliteCommand2.Parameters.AddWithValue("$decision", decision);
			sqliteCommand2.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
			sqliteCommand2.Parameters.AddWithValue("$subject", subjectId);
			sqliteCommand2.ExecuteNonQuery();
		}
		sqliteTransaction.Commit();
		return ReadProfileState(connection, subjectId);
	}

	private static ProfileState IncrementRejected(SqliteConnection connection, string subjectId, string displayName, string decision)
	{
		using SqliteTransaction sqliteTransaction = connection.BeginTransaction();
		UpsertProfile(connection, sqliteTransaction, subjectId, displayName);
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.Transaction = sqliteTransaction;
		sqliteCommand.CommandText = "UPDATE profiles\nSET display_name=$display,\n    rejected_count=rejected_count+1,\n    last_decision=$decision,\n    updated_at_utc=$updated\nWHERE subject_id=$subject;";
		sqliteCommand.Parameters.AddWithValue("$display", displayName);
		sqliteCommand.Parameters.AddWithValue("$decision", decision);
		sqliteCommand.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
		sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
		sqliteCommand.ExecuteNonQuery();
		sqliteTransaction.Commit();
		return ReadProfileState(connection, subjectId);
	}

	private static void UpsertProfile(SqliteConnection connection, SqliteTransaction transaction, string subjectId, string displayName)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.Transaction = transaction;
		sqliteCommand.CommandText = "INSERT INTO profiles(subject_id, display_name, updated_at_utc)\nVALUES($subject, $display, $updated)\nON CONFLICT(subject_id) DO UPDATE SET display_name=excluded.display_name;";
		sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
		sqliteCommand.Parameters.AddWithValue("$display", displayName);
		sqliteCommand.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
		sqliteCommand.ExecuteNonQuery();
	}

	private static void InsertObservation(SqliteConnection connection, SqliteTransaction transaction, string subjectId, AvatarObservation item)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.Transaction = transaction;
		sqliteCommand.CommandText = "INSERT INTO observations(\n    observation_id, subject_id, request_id, sample_id, captured_at_utc, source, backend_id,\n    reconstruction_confidence, model_sequence, coefficient_delta_rms,\n    sample_quality, eye_quality, mouth_quality, brow_quality, stability_quality,\n    a_rotation, b_rotation, c_rotation, x_horizontal, y_vertical, relative_distance, apparent_distance,\n    face_width, face_height, identity_weight, expression_weight, identity_score, animation_score,\n    coverage_score, retention_score, expression_energy, pose_bucket, identity_use, trust_decision,\n    scan_object_path, image_object_path, topology_object_path, scan_sha256, image_sha256, topology_sha256,\n    dense_vertex_count, canonical_vertex_count, shape_coefficients, expression_coefficients, warnings_json)\nVALUES(\n    $observation, $subject, $request, $sample, $captured, $source, $backend,\n    $reconstruction, $modelSequence, $coefficientDelta,\n    $quality, $eye, $mouth, $brow, $stability,\n    $a, $b, $c, $x, $y, $relative, $apparent,\n    $faceWidth, $faceHeight, $identityWeight, $expressionWeight, $identityScore, $animationScore,\n    $coverageScore, $retentionScore, $expressionEnergy, $bucket, $identityUse, $trust,\n    $scanPath, $imagePath, $topologyPath, $scanHash, $imageHash, $topologyHash,\n    $denseCount, $canonicalCount, $shape, $expression, $warnings);";
		Add(sqliteCommand, "$observation", item.ObservationId);
		Add(sqliteCommand, "$subject", subjectId);
		Add(sqliteCommand, "$request", item.RequestId);
		Add(sqliteCommand, "$sample", item.SampleId);
		Add(sqliteCommand, "$captured", item.CapturedAtUtc.ToString("O"));
		Add(sqliteCommand, "$source", item.Source);
		Add(sqliteCommand, "$backend", item.BackendId);
		Add(sqliteCommand, "$reconstruction", item.ReconstructionConfidencePercent);
		Add(sqliteCommand, "$modelSequence", item.ModelSequenceNumber);
		Add(sqliteCommand, "$coefficientDelta", item.ModelCoefficientDeltaRms);
		Add(sqliteCommand, "$quality", item.SampleQualityPercent);
		Add(sqliteCommand, "$eye", item.EyeQualityPercent);
		Add(sqliteCommand, "$mouth", item.MouthQualityPercent);
		Add(sqliteCommand, "$brow", item.BrowQualityPercent);
		Add(sqliteCommand, "$stability", item.StabilityQualityPercent);
		Add(sqliteCommand, "$a", item.ARotationAroundXDegrees);
		Add(sqliteCommand, "$b", item.BRotationAroundYDegrees);
		Add(sqliteCommand, "$c", item.CRotationAroundZDegrees);
		Add(sqliteCommand, "$x", item.XHorizontalPercent);
		Add(sqliteCommand, "$y", item.YVerticalPercent);
		Add(sqliteCommand, "$relative", item.RelativeDistanceScale);
		Add(sqliteCommand, "$apparent", item.ApparentDistanceUnits);
		Add(sqliteCommand, "$faceWidth", item.FaceWidthPercent);
		Add(sqliteCommand, "$faceHeight", item.FaceHeightPercent);
		Add(sqliteCommand, "$identityWeight", item.IdentityWeightPercent);
		Add(sqliteCommand, "$expressionWeight", item.ExpressionWeightPercent);
		Add(sqliteCommand, "$identityScore", item.IdentityScorePercent);
		Add(sqliteCommand, "$animationScore", item.AnimationScorePercent);
		Add(sqliteCommand, "$coverageScore", item.CoverageScorePercent);
		Add(sqliteCommand, "$retentionScore", item.RetentionScorePercent);
		Add(sqliteCommand, "$expressionEnergy", item.ExpressionEnergyPercent);
		Add(sqliteCommand, "$bucket", item.PoseBucket);
		Add(sqliteCommand, "$identityUse", item.IdentityUse);
		Add(sqliteCommand, "$trust", item.TrustDecision);
		Add(sqliteCommand, "$scanPath", item.ScanObjectPath);
		Add(sqliteCommand, "$imagePath", item.ImageObjectPath);
		Add(sqliteCommand, "$topologyPath", item.TopologyObjectPath);
		Add(sqliteCommand, "$scanHash", item.ScanSha256);
		Add(sqliteCommand, "$imageHash", item.ImageSha256);
		Add(sqliteCommand, "$topologyHash", item.TopologySha256);
		Add(sqliteCommand, "$denseCount", item.DenseVertexCount);
		Add(sqliteCommand, "$canonicalCount", item.CanonicalVertexCount);
		Add(sqliteCommand, "$shape", DoublesToBytes(item.ShapeCoefficients));
		Add(sqliteCommand, "$expression", DoublesToBytes(item.ExpressionCoefficients));
		Add(sqliteCommand, "$warnings", JsonSerializer.Serialize(item.Warnings, JsonOptions));
		sqliteCommand.ExecuteNonQuery();
	}

	private static ProfileState ReadProfileState(SqliteConnection connection, string subjectId)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = "SELECT * FROM profiles WHERE subject_id=$subject;";
		sqliteCommand.Parameters.AddWithValue("$subject", subjectId);
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		if (!sqliteDataReader.Read())
		{
			return new ProfileState();
		}
		return new ProfileState
		{
			DisplayName = Text(sqliteDataReader, "display_name"),
			Revision = sqliteDataReader.GetInt64(sqliteDataReader.GetOrdinal("revision")),
			AcceptedCount = sqliteDataReader.GetInt64(sqliteDataReader.GetOrdinal("accepted_count")),
			RejectedCount = sqliteDataReader.GetInt64(sqliteDataReader.GetOrdinal("rejected_count")),
			UpdatedAtUtc = DateTime.Parse(Text(sqliteDataReader, "updated_at_utc"), null, DateTimeStyles.RoundtripKind)
		};
	}

	private static void TryDeleteUnreferencedObjects(SqliteConnection connection, string storageRoot, AvatarObservation observation)
	{
		TryDeleteUnreferencedPath(connection, storageRoot, observation.ScanObjectPath);
		TryDeleteUnreferencedPath(connection, storageRoot, observation.ImageObjectPath);
		TryDeleteUnreferencedPath(connection, storageRoot, observation.TopologyObjectPath);
	}

	private static void TryDeleteUnreferencedFullPath(SqliteConnection connection, string storageRoot, string? fullPath)
	{
		if (!string.IsNullOrWhiteSpace(fullPath))
		{
			TryDeleteUnreferencedPath(connection, storageRoot, AvatarStorageLayout.ToRelativePath(storageRoot, fullPath));
		}
	}

	private static void TryDeleteUnreferencedPath(SqliteConnection connection, string storageRoot, string relativePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				return;
			}
			using SqliteCommand sqliteCommand = connection.CreateCommand();
			sqliteCommand.CommandText = "SELECT EXISTS(\n    SELECT 1 FROM observations\n    WHERE scan_object_path=$path OR image_object_path=$path OR topology_object_path=$path);";
			sqliteCommand.Parameters.AddWithValue("$path", relativePath);
			if (Convert.ToInt32(sqliteCommand.ExecuteScalar()) == 0)
			{
				string path = AvatarStorageLayout.ResolveObjectPath(storageRoot, relativePath);
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
		catch
		{
		}
	}

	private static byte[] DoublesToBytes(IReadOnlyList<double> values)
	{
		byte[] array = new byte[values.Count * 8];
		for (int i = 0; i < values.Count; i++)
		{
			BitConverter.TryWriteBytes(array.AsSpan(i * 8, 8), values[i]);
		}
		return array;
	}

	private static List<double> BytesToDoubles(byte[] bytes)
	{
		if (bytes.Length % 8 != 0)
		{
			throw new InvalidDataException("Stored coefficient data was not aligned to 64-bit values.");
		}
		List<double> list = new List<double>(bytes.Length / 8);
		for (int i = 0; i < bytes.Length; i += 8)
		{
			list.Add(BitConverter.ToDouble(bytes, i));
		}
		return list;
	}

	private static void Add(SqliteCommand command, string name, object? value)
	{
		command.Parameters.AddWithValue(name, value ?? DBNull.Value);
	}

	private static string Text(SqliteDataReader reader, string name)
	{
		return reader.GetString(reader.GetOrdinal(name));
	}

	private static double Number(SqliteDataReader reader, string name)
	{
		return reader.GetDouble(reader.GetOrdinal(name));
	}

	private static int Integer(SqliteDataReader reader, string name)
	{
		return reader.GetInt32(reader.GetOrdinal(name));
	}

	private static long Integer64(SqliteDataReader reader, string name)
	{
		return reader.GetInt64(reader.GetOrdinal(name));
	}

	private static double? NullableNumber(SqliteDataReader reader, string name)
	{
		int ordinal = reader.GetOrdinal(name);
		if (!reader.IsDBNull(ordinal))
		{
			return reader.GetDouble(ordinal);
		}
		return null;
	}
}
