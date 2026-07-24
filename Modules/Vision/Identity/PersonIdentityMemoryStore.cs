using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Identity;

internal sealed class PersonIdentityMemoryStore
{
	private const string RootFolderName = "AvatarSystem";

	private const string MemoryFolderName = "IdentityMemory";

	private const string MemoryFileName = "person_identity_memory.json";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	public List<PersonIdentityRecord> Load(string outputFolder)
	{
		string path = GetPath(outputFolder);
		try
		{
			if (!File.Exists(path))
			{
				return [];
			}
			List<PersonIdentityRecord>? people =
				JsonSerializer.Deserialize<List<PersonIdentityRecord>>(
					File.ReadAllText(path, Encoding.UTF8),
					JsonOptions);
			return Normalize(people ?? []);
		}
		catch
		{
			return [];
		}
	}

	public void Save(
		string outputFolder,
		IReadOnlyCollection<PersonIdentityRecord> people)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		string path = GetPath(outputFolder);
		Directory.CreateDirectory(
			Path.GetDirectoryName(path)
			?? Path.Combine(outputFolder, RootFolderName));
		AtomicTextFileWriter.WriteAllText(
			path,
			JsonSerializer.Serialize(people, JsonOptions),
			Encoding.UTF8);
	}

	public string GetPath(string outputFolder)
	{
		return Path.Combine(
			outputFolder,
			RootFolderName,
			MemoryFolderName,
			MemoryFileName);
	}

	private static List<PersonIdentityRecord> Normalize(
		IEnumerable<PersonIdentityRecord> people)
	{
		var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var normalized = new List<PersonIdentityRecord>();
		foreach (PersonIdentityRecord person in people)
		{
			string id = person.Id.Trim();
			if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
			{
				continue;
			}
			person.Id = id;
			person.DisplayName = person.DisplayName.Trim();
			person.AvatarProfileId = person.AvatarProfileId.Trim();
			person.FirstSeenAtUtc = person.FirstSeenAtUtc == default
				? DateTime.UtcNow
				: person.FirstSeenAtUtc;
			person.LastSeenAtUtc = person.LastSeenAtUtc == default
				? person.FirstSeenAtUtc
				: person.LastSeenAtUtc;
			person.ObservationCount = Math.Max(1, person.ObservationCount);
			person.EncounterCount = Math.Max(1, person.EncounterCount);
			person.Prototypes = person.Prototypes
				.Where(IsUsableEmbedding)
				.Select(NormalizeEmbedding)
				.Where(embedding =>
					embedding.Length
						== SFaceEmbeddingExtractor.ExpectedEmbeddingLength)
				.Take(12)
				.ToList();
			if (person.Prototypes.Count > 0)
			{
				normalized.Add(person);
			}
		}
		return normalized;
	}

	private static bool IsUsableEmbedding(float[]? embedding)
	{
		return embedding is { Length: SFaceEmbeddingExtractor.ExpectedEmbeddingLength }
			&& embedding.All(float.IsFinite);
	}

	private static float[] NormalizeEmbedding(float[] embedding)
	{
		double squaredNorm = 0d;
		for (int index = 0; index < embedding.Length; index++)
		{
			squaredNorm += embedding[index] * embedding[index];
		}
		double norm = Math.Sqrt(squaredNorm);
		if (norm < 1e-8d)
		{
			return [];
		}
		float inverseNorm = (float)(1d / norm);
		float[] normalized = new float[embedding.Length];
		for (int index = 0; index < embedding.Length; index++)
		{
			normalized[index] = embedding[index] * inverseNorm;
		}
		return normalized;
	}
}
