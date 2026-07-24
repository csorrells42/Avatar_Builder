using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Vision.Identity;

internal sealed class PersonIdentityMemoryStore
{
	private const string RootFolderName = "AvatarSystem";

	private const string MemoryFolderName = "IdentityMemory";

	private const string MemoryFileName = "person_identity_memory.sqlite";

	public List<PersonIdentityRecord> Load(string outputFolder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		string path = GetPath(outputFolder);
		if (!File.Exists(path))
		{
			return [];
		}
		try
		{
			using SqliteConnection connection = Open(path);
			EnsureSchema(connection);
			var people = new List<PersonIdentityRecord>();
			var byId = new Dictionary<string, PersonIdentityRecord>(
				StringComparer.OrdinalIgnoreCase);
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText =
					"""
					SELECT id, display_name, avatar_profile_id,
						is_registered_user, permission_level,
						first_seen_utc, last_seen_utc,
						observation_count, encounter_count
					FROM people
					ORDER BY first_seen_utc, id;
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				while (reader.Read())
				{
					var person = new PersonIdentityRecord
					{
						Id = reader.GetString(0).Trim(),
						DisplayName = reader.GetString(1).Trim(),
						AvatarProfileId = reader.GetString(2).Trim(),
						IsRegisteredUser = reader.GetInt32(3) != 0,
						PermissionLevel = NormalizePermission(
							reader.GetString(4)),
						FirstSeenAtUtc = ReadUtc(reader.GetString(5)),
						LastSeenAtUtc = ReadUtc(reader.GetString(6)),
						ObservationCount = Math.Max(1, reader.GetInt32(7)),
						EncounterCount = Math.Max(1, reader.GetInt32(8))
					};
					if (string.IsNullOrWhiteSpace(person.Id)
						|| byId.ContainsKey(person.Id))
					{
						continue;
					}
					byId.Add(person.Id, person);
					people.Add(person);
				}
			}
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText =
					"""
					SELECT person_id, embedding
					FROM person_prototypes
					ORDER BY person_id, ordinal;
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				while (reader.Read())
				{
					if (!byId.TryGetValue(
						reader.GetString(0),
						out PersonIdentityRecord? person))
					{
						continue;
					}
					float[] embedding = ReadEmbedding((byte[])reader[1]);
					if (embedding.Length
						== SFaceEmbeddingExtractor.ExpectedEmbeddingLength)
					{
						person.Prototypes.Add(embedding);
					}
				}
			}
			return people
				.Where(person => person.Prototypes.Count > 0)
				.ToList();
		}
		catch
		{
			return [];
		}
	}

	public void Upsert(
		string outputFolder,
		IReadOnlyCollection<PersonIdentityRecord> changedPeople)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		if (changedPeople.Count == 0)
		{
			return;
		}
		string path = GetPath(outputFolder);
		Directory.CreateDirectory(
			Path.GetDirectoryName(path)
			?? Path.Combine(outputFolder, RootFolderName));
		using SqliteConnection connection = Open(path);
		EnsureSchema(connection);
		using SqliteTransaction transaction = connection.BeginTransaction();
		foreach (PersonIdentityRecord person in changedPeople)
		{
			UpsertPerson(connection, transaction, person);
		}
		transaction.Commit();
	}

	public string GetPath(string outputFolder)
	{
		return Path.Combine(
			outputFolder,
			RootFolderName,
			MemoryFolderName,
			MemoryFileName);
	}

	public string GetContextPhotoPath(
		string outputFolder,
		string identityId)
	{
		return Path.Combine(
			outputFolder,
			RootFolderName,
			MemoryFolderName,
			"ContextPhotos",
			identityId + ".jpg");
	}

	public void SaveContextPhoto(
		string outputFolder,
		string identityId,
		ReadOnlySpan<byte> jpegBytes)
	{
		if (jpegBytes.IsEmpty)
		{
			return;
		}
		string path = GetContextPhotoPath(outputFolder, identityId);
		Directory.CreateDirectory(
			Path.GetDirectoryName(path)
			?? throw new InvalidOperationException(
				"Identity context-photo directory is missing."));
		using FileStream stream = new(
			path,
			FileMode.Create,
			FileAccess.Write,
			FileShare.Read);
		stream.Write(jpegBytes);
		stream.Flush(flushToDisk: true);
	}

	private static SqliteConnection Open(string path)
	{
		var connection = new SqliteConnection(
			new SqliteConnectionStringBuilder
			{
				DataSource = path,
				Mode = SqliteOpenMode.ReadWriteCreate,
				Cache = SqliteCacheMode.Private,
				Pooling = true
			}.ToString());
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"""
			PRAGMA journal_mode=WAL;
			PRAGMA synchronous=NORMAL;
			PRAGMA foreign_keys=ON;
			PRAGMA busy_timeout=1000;
			""";
		command.ExecuteNonQuery();
		return connection;
	}

	private static void EnsureSchema(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS people (
				id TEXT PRIMARY KEY NOT NULL,
				display_name TEXT NOT NULL,
				avatar_profile_id TEXT NOT NULL,
				is_registered_user INTEGER NOT NULL,
				permission_level TEXT NOT NULL,
				first_seen_utc TEXT NOT NULL,
				last_seen_utc TEXT NOT NULL,
				observation_count INTEGER NOT NULL,
				encounter_count INTEGER NOT NULL
			);
			CREATE TABLE IF NOT EXISTS person_prototypes (
				person_id TEXT NOT NULL,
				ordinal INTEGER NOT NULL,
				embedding BLOB NOT NULL,
				PRIMARY KEY (person_id, ordinal),
				FOREIGN KEY (person_id)
					REFERENCES people(id) ON DELETE CASCADE
			);
			""";
		command.ExecuteNonQuery();
	}

	private static void UpsertPerson(
		SqliteConnection connection,
		SqliteTransaction transaction,
		PersonIdentityRecord person)
	{
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"""
				INSERT INTO people (
					id, display_name, avatar_profile_id,
					is_registered_user, permission_level,
					first_seen_utc, last_seen_utc,
					observation_count, encounter_count)
				VALUES (
					$id, $display_name, $avatar_profile_id,
					$is_registered_user, $permission_level,
					$first_seen_utc, $last_seen_utc,
					$observation_count, $encounter_count)
				ON CONFLICT(id) DO UPDATE SET
					display_name = excluded.display_name,
					avatar_profile_id = excluded.avatar_profile_id,
					is_registered_user = excluded.is_registered_user,
					permission_level = excluded.permission_level,
					first_seen_utc = excluded.first_seen_utc,
					last_seen_utc = excluded.last_seen_utc,
					observation_count = excluded.observation_count,
					encounter_count = excluded.encounter_count;
				""";
			command.Parameters.AddWithValue("$id", person.Id.Trim());
			command.Parameters.AddWithValue(
				"$display_name",
				person.DisplayName.Trim());
			command.Parameters.AddWithValue(
				"$avatar_profile_id",
				person.AvatarProfileId.Trim());
			command.Parameters.AddWithValue(
				"$is_registered_user",
				person.IsRegisteredUser ? 1 : 0);
			command.Parameters.AddWithValue(
				"$permission_level",
				NormalizePermission(person.PermissionLevel));
			command.Parameters.AddWithValue(
				"$first_seen_utc",
				WriteUtc(person.FirstSeenAtUtc));
			command.Parameters.AddWithValue(
				"$last_seen_utc",
				WriteUtc(person.LastSeenAtUtc));
			command.Parameters.AddWithValue(
				"$observation_count",
				Math.Max(1, person.ObservationCount));
			command.Parameters.AddWithValue(
				"$encounter_count",
				Math.Max(1, person.EncounterCount));
			command.ExecuteNonQuery();
		}
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"DELETE FROM person_prototypes WHERE person_id = $person_id;";
			command.Parameters.AddWithValue("$person_id", person.Id.Trim());
			command.ExecuteNonQuery();
		}
		for (int ordinal = 0;
			ordinal < person.Prototypes.Count;
			ordinal++)
		{
			float[] embedding = person.Prototypes[ordinal];
			if (!IsUsableEmbedding(embedding))
			{
				continue;
			}
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText =
				"""
				INSERT INTO person_prototypes (
					person_id, ordinal, embedding)
				VALUES ($person_id, $ordinal, $embedding);
				""";
			command.Parameters.AddWithValue("$person_id", person.Id.Trim());
			command.Parameters.AddWithValue("$ordinal", ordinal);
			command.Parameters.Add(
				"$embedding",
				SqliteType.Blob).Value = WriteEmbedding(embedding);
			command.ExecuteNonQuery();
		}
	}

	private static bool IsUsableEmbedding(float[]? embedding)
	{
		return embedding is
			{ Length: SFaceEmbeddingExtractor.ExpectedEmbeddingLength }
			&& embedding.All(float.IsFinite);
	}

	private static byte[] WriteEmbedding(float[] embedding)
	{
		byte[] bytes = new byte[embedding.Length * sizeof(float)];
		Buffer.BlockCopy(
			embedding,
			0,
			bytes,
			0,
			bytes.Length);
		return bytes;
	}

	private static float[] ReadEmbedding(byte[] bytes)
	{
		if (bytes.Length
			!= SFaceEmbeddingExtractor.ExpectedEmbeddingLength
				* sizeof(float))
		{
			return [];
		}
		float[] embedding =
			new float[SFaceEmbeddingExtractor.ExpectedEmbeddingLength];
		Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
		if (!embedding.All(float.IsFinite))
		{
			return [];
		}
		double squaredNorm = 0d;
		foreach (float value in embedding)
		{
			squaredNorm += value * value;
		}
		double norm = Math.Sqrt(squaredNorm);
		if (!double.IsFinite(norm) || norm < 1e-8d)
		{
			return [];
		}
		float inverseNorm = (float)(1d / norm);
		for (int index = 0; index < embedding.Length; index++)
		{
			embedding[index] *= inverseNorm;
		}
		return embedding;
	}

	private static string WriteUtc(DateTime value)
	{
		return (value == default ? DateTime.UtcNow : value)
			.ToUniversalTime()
			.ToString("O");
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

	private static DateTime ReadUtc(string value)
	{
		return DateTime.TryParse(
			value,
			null,
			System.Globalization.DateTimeStyles.RoundtripKind,
			out DateTime parsed)
			? parsed.ToUniversalTime()
			: DateTime.UtcNow;
	}
}
