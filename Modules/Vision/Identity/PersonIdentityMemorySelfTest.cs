using System;
using System.IO;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed record PersonIdentityMemorySelfTestResult(
	bool Succeeded,
	string Detail);

public static class PersonIdentityMemorySelfTest
{
	public static PersonIdentityMemorySelfTestResult Run()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"AvatarBuilderIdentitySelfTest",
			Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			using var memory = new PersonIdentityMemory(
				initializeModels: false);
			memory.ConfigureOutputFolder(root);
			float[] passerbyEmbedding = CreateEmbedding(2);
			DateTime now = DateTime.UtcNow;

			for (int index = 0; index < 6; index++)
			{
				memory.ObserveEmbeddingFrameForSelfTest(
					[passerbyEmbedding],
					now.AddSeconds(-40d + index * 0.6d));
			}
			PersonIdentitySnapshot expired =
				memory.ObserveEmbeddingFrameForSelfTest(
					[],
					now.AddSeconds(-16d));
			if (expired.RememberedIdentityCount != 0)
			{
				return Fail(
					"A brief passerby was incorrectly persisted.");
			}

			float[] firstEmbedding = CreateEmbedding(0);
			float[] secondEmbedding = CreateEmbedding(1);
			PersonIdentitySnapshot retained =
				PersonIdentitySnapshot.Waiting;
			for (int index = 0; index < 12; index++)
			{
				retained = memory.ObserveEmbeddingFrameForSelfTest(
					[firstEmbedding, secondEmbedding],
					now.AddSeconds(-9d + index * (9d / 11d)));
			}
			if (retained.RememberedIdentityCount != 2
				|| retained.People.Count != 2
				|| retained.People.Any(person => !person.IsRemembered)
				|| string.Equals(
					retained.People[0].IdentityId,
					retained.People[1].IdentityId,
					StringComparison.OrdinalIgnoreCase))
			{
				return Fail(
					"Two sustained people were not remembered independently.");
			}

			PersonIdentitySnapshot recognizedFirst =
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(0, 3)],
					now);
			if (recognizedFirst.People.Count != 1
				|| !recognizedFirst.People[0].IsRemembered)
			{
				return Fail(
					"The first retained person was not recognized again.");
			}
			AvatarIdentityAuthorization first =
				memory.AuthorizeAvatarCapture(
					"profile-a",
					"First Test Person");
			memory.ObserveEmbeddingFrameForSelfTest(
				[CreateNearbyEmbedding(1, 4)],
				now);
			AvatarIdentityAuthorization sharedProfile =
				memory.AuthorizeAvatarCapture(
					"profile-a",
					"Second Test Person");
			AvatarIdentityAuthorization second =
				memory.AuthorizeAvatarCapture(
					"profile-b",
					"Second Test Person");
			memory.ObserveEmbeddingFrameForSelfTest(
				[CreateNearbyEmbedding(0, 5)],
				now);
			AvatarIdentityAuthorization duplicatePerson =
				memory.AuthorizeAvatarCapture(
					"profile-c",
					"Duplicate First Person");
			if (!first.Allowed
				|| sharedProfile.Allowed
				|| !second.Allowed
				|| duplicatePerson.Allowed
				|| !string.Equals(
					sharedProfile.ExistingAvatarProfileId,
					"profile-a",
					StringComparison.Ordinal)
				|| !string.Equals(
					duplicatePerson.ExistingAvatarProfileId,
					"profile-a",
					StringComparison.Ordinal))
			{
				return Fail(
					"One-person/one-avatar or one-avatar/one-person linking failed.");
			}

			string storePath =
				new PersonIdentityMemoryStore().GetPath(root);
			if (!File.Exists(storePath))
			{
				return Fail(
					"Retained face memory was not saved.");
			}
			using var reloaded = new PersonIdentityMemory(
				initializeModels: false);
			reloaded.ConfigureOutputFolder(root);
			if (reloaded.LatestSnapshot.RememberedIdentityCount != 2)
			{
				return Fail(
					"Two retained people did not survive a store reload.");
			}
			return new PersonIdentityMemorySelfTestResult(
				true,
				"PASS: brief sightings expired without persistence; " +
				"two sustained people were retained and reloaded; " +
				"explicit avatar linking enforced a one-to-one mapping.");
		}
		catch (Exception ex)
		{
			return Fail(ex.Message);
		}
		finally
		{
			try
			{
				string fullRoot = Path.GetFullPath(root);
				string expectedParent = Path.GetFullPath(Path.Combine(
					Path.GetTempPath(),
					"AvatarBuilderIdentitySelfTest"));
				if (fullRoot.StartsWith(
					expectedParent + Path.DirectorySeparatorChar,
					StringComparison.OrdinalIgnoreCase)
					&& Directory.Exists(fullRoot))
				{
					Directory.Delete(fullRoot, recursive: true);
				}
			}
			catch
			{
			}
		}
	}

	private static float[] CreateEmbedding(int primaryIndex)
	{
		float[] embedding =
			new float[SFaceEmbeddingExtractor.ExpectedEmbeddingLength];
		embedding[primaryIndex] = 1f;
		return embedding;
	}

	private static float[] CreateNearbyEmbedding(
		int primaryIndex,
		int secondaryIndex)
	{
		float[] embedding = CreateEmbedding(primaryIndex);
		embedding[primaryIndex] = 0.995f;
		embedding[secondaryIndex] = 0.1f;
		double norm = Math.Sqrt(
			embedding[primaryIndex] * embedding[primaryIndex]
			+ embedding[secondaryIndex] * embedding[secondaryIndex]);
		embedding[primaryIndex] /= (float)norm;
		embedding[secondaryIndex] /= (float)norm;
		return embedding;
	}

	private static PersonIdentityMemorySelfTestResult Fail(string detail)
	{
		return new PersonIdentityMemorySelfTestResult(
			false,
			"FAIL: " + detail);
	}
}
