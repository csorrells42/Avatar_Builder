using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using AvatarBuilder.Modules.Vision.Reconstruction;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationRepository
{
    private static readonly ConcurrentDictionary<string, object> DatabaseLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AvatarObservationWriteResult SaveCapture(AvatarObservationCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var storageRoot = AvatarStorageLayout.GetStorageRoot(capture.ProfileFolder);
        Directory.CreateDirectory(storageRoot);
        var databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
        lock (DatabaseLocks.GetOrAdd(databasePath, static _ => new object()))
        {
            using var connection = Open(databasePath);
            EnsureSchema(connection);
            var existing = ReadObservations(connection, capture.SubjectId);
            if (!string.IsNullOrWhiteSpace(capture.Reconstruction.RequestId)
                && existing.Any(item => string.Equals(item.RequestId, capture.Reconstruction.RequestId, StringComparison.Ordinal)))
            {
                var state = ReadProfileState(connection, capture.SubjectId);
                return new AvatarObservationWriteResult(
                    false,
                    false,
                    "",
                    "The reconstruction request was already stored.",
                    state.Revision,
                    existing.Count);
            }

            var decision = AvatarObservationRanker.Rank(capture, existing);
            if (!decision.Accepted || decision.Candidate is null)
            {
                var state = IncrementRejected(connection, capture.SubjectId, capture.SubjectDisplayName, decision.Reason);
                return new AvatarObservationWriteResult(
                    false,
                    false,
                    "",
                    decision.Reason,
                    state.Revision,
                    existing.Count);
            }

            string? scanPath = null;
            string? imagePath = null;
            string? topologyPath = null;
            AvatarObservation? candidate = null;
            ProfileState committedState;
            try
            {
                scanPath = AvatarScanBinaryCodec.WriteObject(storageRoot, capture.Reconstruction);
                imagePath = AvatarObservationImageCodec.WriteObject(storageRoot, capture.SourceFrame);
                topologyPath = FindReusableTopologyPath(storageRoot, capture.Reconstruction, existing)
                    ?? AvatarTopologyBinaryCodec.WriteObject(storageRoot, capture.Reconstruction.TopologyEdges);
                candidate = decision.Candidate with
                {
                    ScanObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, scanPath),
                    ImageObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, imagePath),
                    TopologyObjectPath = AvatarStorageLayout.ToRelativePath(storageRoot, topologyPath),
                    ScanSha256 = Path.GetFileNameWithoutExtension(scanPath),
                    ImageSha256 = Path.GetFileNameWithoutExtension(imagePath),
                    TopologySha256 = Path.GetFileNameWithoutExtension(topologyPath)
                };
                committedState = CommitAccepted(
                    connection,
                    capture.SubjectId,
                    capture.SubjectDisplayName,
                    candidate,
                    decision.Replacement,
                    decision.Reason);
            }
            catch
            {
                TryDeleteUnreferencedFullPath(connection, storageRoot, scanPath);
                TryDeleteUnreferencedFullPath(connection, storageRoot, imagePath);
                TryDeleteUnreferencedFullPath(connection, storageRoot, topologyPath);
                throw;
            }

            if (decision.Replacement is not null)
            {
                TryDeleteUnreferencedObjects(connection, storageRoot, decision.Replacement);
            }

            return new AvatarObservationWriteResult(
                true,
                decision.Replacement is not null,
                candidate.ObservationId,
                decision.Reason,
                committedState.Revision,
                existing.Count + (decision.Replacement is null ? 1 : 0));
        }
    }

    public int ResetProfile(string profileFolder, string subjectId)
    {
        var storageRoot = AvatarStorageLayout.GetStorageRoot(profileFolder);
        Directory.CreateDirectory(storageRoot);
        var databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
        lock (DatabaseLocks.GetOrAdd(databasePath, static _ => new object()))
        {
            using var connection = Open(databasePath);
            EnsureSchema(connection);
            var observations = ReadObservations(connection, subjectId);
            using (var transaction = connection.BeginTransaction())
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM observations WHERE subject_id=$subject;";
                delete.Parameters.AddWithValue("$subject", subjectId);
                delete.ExecuteNonQuery();

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE profiles
                    SET revision=revision+1,
                        accepted_count=0,
                        rejected_count=0,
                        last_decision='Profile storage reset by the user.',
                        updated_at_utc=$updated
                    WHERE subject_id=$subject;
                    """;
                update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$subject", subjectId);
                update.ExecuteNonQuery();
                transaction.Commit();
            }

            foreach (var observation in observations)
            {
                TryDeleteUnreferencedObjects(connection, storageRoot, observation);
            }

            return observations.Count;
        }
    }

    public AvatarObservationDataset ReadDataset(
        string profileFolder,
        string subjectId,
        string subjectDisplayName)
    {
        var storageRoot = AvatarStorageLayout.GetStorageRoot(profileFolder);
        Directory.CreateDirectory(storageRoot);
        var databasePath = AvatarStorageLayout.GetDatabasePath(storageRoot);
        lock (DatabaseLocks.GetOrAdd(databasePath, static _ => new object()))
        {
            using var connection = Open(databasePath);
            EnsureSchema(connection);
            var observations = ReadObservations(connection, subjectId);
            var state = ReadProfileState(connection, subjectId);
            var topologyPath = observations
                .OrderByDescending(static item => item.CapturedAtUtc)
                .Select(static item => item.TopologyObjectPath)
                .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
            IReadOnlyList<MeshTopologyEdge> topology = [];
            if (!string.IsNullOrWhiteSpace(topologyPath))
            {
                var fullPath = AvatarStorageLayout.ResolveObjectPath(storageRoot, topologyPath);
                if (File.Exists(fullPath))
                {
                    topology = AvatarTopologyBinaryCodec.Read(fullPath);
                }
            }

            return new AvatarObservationDataset
            {
                StorageRoot = storageRoot,
                SubjectId = subjectId,
                SubjectDisplayName = string.IsNullOrWhiteSpace(state.DisplayName)
                    ? subjectDisplayName
                    : state.DisplayName,
                Revision = state.Revision,
                AcceptedObservationCount = state.AcceptedCount,
                RejectedObservationCount = state.RejectedCount,
                UpdatedAtUtc = state.UpdatedAtUtc,
                DenseTopologyEdges = topology,
                Observations = observations
            };
        }
    }

    public AvatarObservation LoadObservation(AvatarObservationDataset dataset, AvatarObservation metadata)
    {
        var path = AvatarStorageLayout.ResolveObjectPath(dataset.StorageRoot, metadata.ScanObjectPath);
        var geometry = AvatarScanBinaryCodec.Read(path);
        return metadata with
        {
            Vertices = geometry.Vertices,
            CanonicalIdentityVertices = geometry.CanonicalIdentityVertices,
            SparseLandmarks = geometry.SparseLandmarks,
            CameraMatrixCoefficients = geometry.CameraMatrixCoefficients,
            ShapeCoefficients = geometry.ShapeCoefficients,
            ExpressionCoefficients = geometry.ExpressionCoefficients
        };
    }

    public string? GetImagePath(AvatarObservationDataset dataset, AvatarObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.ImageObjectPath))
        {
            return null;
        }

        var path = AvatarStorageLayout.ResolveObjectPath(dataset.StorageRoot, observation.ImageObjectPath);
        return File.Exists(path) ? path : null;
    }

    private static string? FindReusableTopologyPath(
        string storageRoot,
        ThreeDdfaReconstructionSnapshot reconstruction,
        IReadOnlyList<AvatarObservation> existing)
    {
        var relativePath = existing
            .Where(item => item.DenseVertexCount == reconstruction.Vertices.Count)
            .OrderByDescending(static item => item.CapturedAtUtc)
            .Select(static item => item.TopologyObjectPath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var fullPath = AvatarStorageLayout.ResolveObjectPath(storageRoot, relativePath);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            CREATE TABLE IF NOT EXISTS storage_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO storage_meta(key, value) VALUES('schema_version', '1')
                ON CONFLICT(key) DO NOTHING;
            CREATE TABLE IF NOT EXISTS profiles (
                subject_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                accepted_count INTEGER NOT NULL DEFAULT 0,
                rejected_count INTEGER NOT NULL DEFAULT 0,
                last_decision TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS observations (
                observation_id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                sample_id TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                reconstruction_confidence REAL NOT NULL,
                sample_quality REAL NOT NULL,
                eye_quality REAL NOT NULL,
                mouth_quality REAL NOT NULL,
                brow_quality REAL NOT NULL,
                stability_quality REAL NOT NULL,
                a_rotation REAL NOT NULL,
                b_rotation REAL NOT NULL,
                c_rotation REAL NOT NULL,
                x_horizontal REAL NOT NULL,
                y_vertical REAL NOT NULL,
                relative_distance REAL NULL,
                apparent_distance REAL NULL,
                face_width REAL NULL,
                face_height REAL NULL,
                identity_weight REAL NOT NULL,
                expression_weight REAL NOT NULL,
                identity_score REAL NOT NULL,
                animation_score REAL NOT NULL,
                coverage_score REAL NOT NULL,
                retention_score REAL NOT NULL,
                expression_energy REAL NOT NULL,
                pose_bucket TEXT NOT NULL,
                identity_use TEXT NOT NULL,
                trust_decision TEXT NOT NULL,
                scan_object_path TEXT NOT NULL,
                image_object_path TEXT NOT NULL,
                topology_object_path TEXT NOT NULL,
                scan_sha256 TEXT NOT NULL,
                image_sha256 TEXT NOT NULL,
                topology_sha256 TEXT NOT NULL,
                dense_vertex_count INTEGER NOT NULL,
                canonical_vertex_count INTEGER NOT NULL,
                shape_coefficients BLOB NOT NULL,
                expression_coefficients BLOB NOT NULL,
                warnings_json TEXT NOT NULL,
                FOREIGN KEY(subject_id) REFERENCES profiles(subject_id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_observation_request
                ON observations(subject_id, request_id) WHERE request_id <> '';
            CREATE INDEX IF NOT EXISTS ix_observations_subject_time
                ON observations(subject_id, captured_at_utc);
            CREATE INDEX IF NOT EXISTS ix_observations_subject_bucket_score
                ON observations(subject_id, pose_bucket, retention_score);
            """;
        command.ExecuteNonQuery();
    }

    private static List<AvatarObservation> ReadObservations(SqliteConnection connection, string subjectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM observations WHERE subject_id=$subject ORDER BY captured_at_utc;";
        command.Parameters.AddWithValue("$subject", subjectId);
        using var reader = command.ExecuteReader();
        var observations = new List<AvatarObservation>();
        while (reader.Read())
        {
            observations.Add(ReadObservation(reader));
        }

        return observations;
    }

    private static AvatarObservation ReadObservation(SqliteDataReader reader)
    {
        return new AvatarObservation
        {
            ObservationId = Text(reader, "observation_id"),
            RequestId = Text(reader, "request_id"),
            SampleId = Text(reader, "sample_id"),
            CapturedAtUtc = DateTime.Parse(Text(reader, "captured_at_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Source = Text(reader, "source"),
            ReconstructionConfidencePercent = Number(reader, "reconstruction_confidence"),
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
            Warnings = JsonSerializer.Deserialize<List<string>>(Text(reader, "warnings_json"), JsonOptions) ?? []
        };
    }

    private static ProfileState CommitAccepted(
        SqliteConnection connection,
        string subjectId,
        string displayName,
        AvatarObservation candidate,
        AvatarObservation? replacement,
        string decision)
    {
        using var transaction = connection.BeginTransaction();
        UpsertProfile(connection, transaction, subjectId, displayName);
        if (replacement is not null)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM observations WHERE observation_id=$id;";
            delete.Parameters.AddWithValue("$id", replacement.ObservationId);
            delete.ExecuteNonQuery();
        }

        InsertObservation(connection, transaction, subjectId, candidate);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE profiles
                SET display_name=$display,
                    revision=revision+1,
                    accepted_count=accepted_count+1,
                    last_decision=$decision,
                    updated_at_utc=$updated
                WHERE subject_id=$subject;
                """;
            update.Parameters.AddWithValue("$display", displayName);
            update.Parameters.AddWithValue("$decision", decision);
            update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$subject", subjectId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return ReadProfileState(connection, subjectId);
    }

    private static ProfileState IncrementRejected(
        SqliteConnection connection,
        string subjectId,
        string displayName,
        string decision)
    {
        using var transaction = connection.BeginTransaction();
        UpsertProfile(connection, transaction, subjectId, displayName);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE profiles
            SET display_name=$display,
                rejected_count=rejected_count+1,
                last_decision=$decision,
                updated_at_utc=$updated
            WHERE subject_id=$subject;
            """;
        update.Parameters.AddWithValue("$display", displayName);
        update.Parameters.AddWithValue("$decision", decision);
        update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$subject", subjectId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return ReadProfileState(connection, subjectId);
    }

    private static void UpsertProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subjectId,
        string displayName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO profiles(subject_id, display_name, updated_at_utc)
            VALUES($subject, $display, $updated)
            ON CONFLICT(subject_id) DO UPDATE SET display_name=excluded.display_name;
            """;
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$display", displayName);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void InsertObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subjectId,
        AvatarObservation item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observations(
                observation_id, subject_id, request_id, sample_id, captured_at_utc, source,
                reconstruction_confidence, sample_quality, eye_quality, mouth_quality, brow_quality, stability_quality,
                a_rotation, b_rotation, c_rotation, x_horizontal, y_vertical, relative_distance, apparent_distance,
                face_width, face_height, identity_weight, expression_weight, identity_score, animation_score,
                coverage_score, retention_score, expression_energy, pose_bucket, identity_use, trust_decision,
                scan_object_path, image_object_path, topology_object_path, scan_sha256, image_sha256, topology_sha256,
                dense_vertex_count, canonical_vertex_count, shape_coefficients, expression_coefficients, warnings_json)
            VALUES(
                $observation, $subject, $request, $sample, $captured, $source,
                $reconstruction, $quality, $eye, $mouth, $brow, $stability,
                $a, $b, $c, $x, $y, $relative, $apparent,
                $faceWidth, $faceHeight, $identityWeight, $expressionWeight, $identityScore, $animationScore,
                $coverageScore, $retentionScore, $expressionEnergy, $bucket, $identityUse, $trust,
                $scanPath, $imagePath, $topologyPath, $scanHash, $imageHash, $topologyHash,
                $denseCount, $canonicalCount, $shape, $expression, $warnings);
            """;
        Add(command, "$observation", item.ObservationId);
        Add(command, "$subject", subjectId);
        Add(command, "$request", item.RequestId);
        Add(command, "$sample", item.SampleId);
        Add(command, "$captured", item.CapturedAtUtc.ToString("O"));
        Add(command, "$source", item.Source);
        Add(command, "$reconstruction", item.ReconstructionConfidencePercent);
        Add(command, "$quality", item.SampleQualityPercent);
        Add(command, "$eye", item.EyeQualityPercent);
        Add(command, "$mouth", item.MouthQualityPercent);
        Add(command, "$brow", item.BrowQualityPercent);
        Add(command, "$stability", item.StabilityQualityPercent);
        Add(command, "$a", item.ARotationAroundXDegrees);
        Add(command, "$b", item.BRotationAroundYDegrees);
        Add(command, "$c", item.CRotationAroundZDegrees);
        Add(command, "$x", item.XHorizontalPercent);
        Add(command, "$y", item.YVerticalPercent);
        Add(command, "$relative", item.RelativeDistanceScale);
        Add(command, "$apparent", item.ApparentDistanceUnits);
        Add(command, "$faceWidth", item.FaceWidthPercent);
        Add(command, "$faceHeight", item.FaceHeightPercent);
        Add(command, "$identityWeight", item.IdentityWeightPercent);
        Add(command, "$expressionWeight", item.ExpressionWeightPercent);
        Add(command, "$identityScore", item.IdentityScorePercent);
        Add(command, "$animationScore", item.AnimationScorePercent);
        Add(command, "$coverageScore", item.CoverageScorePercent);
        Add(command, "$retentionScore", item.RetentionScorePercent);
        Add(command, "$expressionEnergy", item.ExpressionEnergyPercent);
        Add(command, "$bucket", item.PoseBucket);
        Add(command, "$identityUse", item.IdentityUse);
        Add(command, "$trust", item.TrustDecision);
        Add(command, "$scanPath", item.ScanObjectPath);
        Add(command, "$imagePath", item.ImageObjectPath);
        Add(command, "$topologyPath", item.TopologyObjectPath);
        Add(command, "$scanHash", item.ScanSha256);
        Add(command, "$imageHash", item.ImageSha256);
        Add(command, "$topologyHash", item.TopologySha256);
        Add(command, "$denseCount", item.DenseVertexCount);
        Add(command, "$canonicalCount", item.CanonicalVertexCount);
        Add(command, "$shape", DoublesToBytes(item.ShapeCoefficients));
        Add(command, "$expression", DoublesToBytes(item.ExpressionCoefficients));
        Add(command, "$warnings", JsonSerializer.Serialize(item.Warnings, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static ProfileState ReadProfileState(SqliteConnection connection, string subjectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM profiles WHERE subject_id=$subject;";
        command.Parameters.AddWithValue("$subject", subjectId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ProfileState();
        }

        return new ProfileState
        {
            DisplayName = Text(reader, "display_name"),
            Revision = reader.GetInt64(reader.GetOrdinal("revision")),
            AcceptedCount = reader.GetInt64(reader.GetOrdinal("accepted_count")),
            RejectedCount = reader.GetInt64(reader.GetOrdinal("rejected_count")),
            UpdatedAtUtc = DateTime.Parse(Text(reader, "updated_at_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    private static void TryDeleteUnreferencedObjects(
        SqliteConnection connection,
        string storageRoot,
        AvatarObservation observation)
    {
        TryDeleteUnreferencedPath(connection, storageRoot, observation.ScanObjectPath);
        TryDeleteUnreferencedPath(connection, storageRoot, observation.ImageObjectPath);
        TryDeleteUnreferencedPath(connection, storageRoot, observation.TopologyObjectPath);
    }

    private static void TryDeleteUnreferencedFullPath(
        SqliteConnection connection,
        string storageRoot,
        string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        TryDeleteUnreferencedPath(
            connection,
            storageRoot,
            AvatarStorageLayout.ToRelativePath(storageRoot, fullPath));
    }

    private static void TryDeleteUnreferencedPath(SqliteConnection connection, string storageRoot, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM observations
                    WHERE scan_object_path=$path OR image_object_path=$path OR topology_object_path=$path);
                """;
            command.Parameters.AddWithValue("$path", relativePath);
            if (Convert.ToInt32(command.ExecuteScalar()) != 0)
            {
                return;
            }

            var fullPath = AvatarStorageLayout.ResolveObjectPath(storageRoot, relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Immutable-object cleanup is opportunistic. Never invalidate a committed catalog row.
        }
    }

    private static byte[] DoublesToBytes(IReadOnlyList<double> values)
    {
        var bytes = new byte[values.Count * sizeof(double)];
        for (var index = 0; index < values.Count; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(double), sizeof(double)), values[index]);
        }

        return bytes;
    }

    private static List<double> BytesToDoubles(byte[] bytes)
    {
        if (bytes.Length % sizeof(double) != 0)
        {
            throw new InvalidDataException("Stored coefficient data was not aligned to 64-bit values.");
        }

        var values = new List<double>(bytes.Length / sizeof(double));
        for (var offset = 0; offset < bytes.Length; offset += sizeof(double))
        {
            values.Add(BitConverter.ToDouble(bytes, offset));
        }

        return values;
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string Text(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));

    private static double Number(SqliteDataReader reader, string name) => reader.GetDouble(reader.GetOrdinal(name));

    private static int Integer(SqliteDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));

    private static double? NullableNumber(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private sealed class ProfileState
    {
        public string DisplayName { get; init; } = "";

        public long Revision { get; init; }

        public long AcceptedCount { get; init; }

        public long RejectedCount { get; init; }

        public DateTime UpdatedAtUtc { get; init; }
    }
}
