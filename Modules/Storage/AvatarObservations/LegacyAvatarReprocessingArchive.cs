using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class LegacyAvatarReprocessingArchive
{
	private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	public LegacyAvatarReprocessingArchiveResult ArchiveAndDeleteBackend(string profileFolder, string subjectId, string subjectDisplayName, string backendId, AvatarObservationRepository repository)
	{
		string fullPath = Path.GetFullPath(profileFolder);
		string path = Directory.GetParent(fullPath)?.Parent?.FullName ?? throw new InvalidOperationException("Profile folder has no AvatarSystem parent: " + profileFolder);
		AvatarObservationDataset avatarObservationDataset = repository.ReadDataset(fullPath, subjectId, subjectDisplayName, includeDenseTopology: false, backendId);
		if (avatarObservationDataset.Observations.Count == 0)
		{
			return new LegacyAvatarReprocessingArchiveResult(0, 0, "", "", 0L);
		}
		string text = Path.Combine(path, "Reprocessing", "Legacy3Ddfa", subjectId);
		string text2 = Path.Combine(text, "Images");
		Directory.CreateDirectory(text2);
		List<string> list = new List<string>(avatarObservationDataset.Observations.Count + 1) { "ObservationId,CapturedAtUtc,SubjectId,SubjectDisplayName,OriginalBackendId,OriginalSource,ImageFile,ImageSha256,SampleQualityPercent,EyeQualityPercent,MouthQualityPercent,BrowQualityPercent,StabilityQualityPercent,XHorizontalPercent,YVerticalPercent,RelativeDistanceScale,ApparentDistanceUnits,FaceWidthPercent,FaceHeightPercent,LegacyARotation,LegacyBRotation,LegacyCRotation,PoseBucket,TrustDecision" };
		long num = 0L;
		int num2 = 0;
		foreach (AvatarObservation item in avatarObservationDataset.Observations.OrderBy((AvatarObservation item) => item.CapturedAtUtc))
		{
			string text3 = repository.GetImagePath(avatarObservationDataset, item) ?? throw new FileNotFoundException("Cannot remove legacy observation " + item.ObservationId + "; its source image is missing.");
			string text4 = Path.GetExtension(text3);
			if (string.IsNullOrWhiteSpace(text4))
			{
				text4 = ".jpg";
			}
			string value = ((item.ImageSha256.Length >= 12) ? item.ImageSha256.Substring(0, 12) : item.ObservationId.Substring(0, Math.Min(12, item.ObservationId.Length)));
			string text5 = $"{item.CapturedAtUtc:yyyyMMdd-HHmmssfff}-{value}{text4.ToLowerInvariant()}";
			string text6 = Path.Combine(text2, text5);
			if (!File.Exists(text6))
			{
				File.Copy(text3, text6, overwrite: false);
			}
			if (!string.Equals(AvatarStorageLayout.ComputeSha256(text6), item.ImageSha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Archived image verification failed: " + text6);
			}
			num2++;
			num += new FileInfo(text6).Length;
			list.Add(CreateManifestRow(item, subjectId, subjectDisplayName, text5));
		}
		string text7 = Path.Combine(text, "reprocessing_frames.csv");
		AtomicTextFileWriter.WriteAllText(text7, string.Join(Environment.NewLine, list) + Environment.NewLine, Utf8WithoutBom);
		if (num2 != avatarObservationDataset.Observations.Count)
		{
			throw new InvalidDataException("The image archive count did not match the legacy observation count.");
		}
		int num3 = repository.DeleteBackend(fullPath, subjectId, backendId);
		if (num3 != avatarObservationDataset.Observations.Count)
		{
			throw new InvalidDataException($"Archived {avatarObservationDataset.Observations.Count} observations but removed {num3} catalog rows.");
		}
		DeleteLegacyDerivedFiles(fullPath);
		PruneEmptyObjectFolders(avatarObservationDataset.StorageRoot);
		return new LegacyAvatarReprocessingArchiveResult(num3, num2, text, text7, num);
	}

	private static string CreateManifestRow(AvatarObservation observation, string subjectId, string subjectDisplayName, string imageFile)
	{
		return string.Join(",", new string[24]
		{
			Csv(observation.ObservationId),
			Csv(observation.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
			Csv(subjectId),
			Csv(subjectDisplayName),
			Csv(observation.BackendId),
			Csv(observation.Source),
			Csv(Path.Combine("Images", imageFile).Replace(Path.DirectorySeparatorChar, '/')),
			Csv(observation.ImageSha256),
			Number(observation.SampleQualityPercent),
			Number(observation.EyeQualityPercent),
			Number(observation.MouthQualityPercent),
			Number(observation.BrowQualityPercent),
			Number(observation.StabilityQualityPercent),
			Number(observation.XHorizontalPercent),
			Number(observation.YVerticalPercent),
			Number(observation.RelativeDistanceScale),
			Number(observation.ApparentDistanceUnits),
			Number(observation.FaceWidthPercent),
			Number(observation.FaceHeightPercent),
			Number(observation.ARotationAroundXDegrees),
			Number(observation.BRotationAroundYDegrees),
			Number(observation.CRotationAroundZDegrees),
			Csv(observation.PoseBucket),
			Csv(observation.TrustDecision)
		});
	}

	private static string Csv(string value)
	{
		return "\"" + value.Replace("\"", "\"\"") + "\"";
	}

	private static string Number(double? value)
	{
		return value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
	}

	private static void DeleteLegacyDerivedFiles(string profileFolder)
	{
		string[] array = new string[9] { "avatar_model.json", "avatar_model_history.jsonl", "avatar_model_history_latest.json", "avatar_model_history_recent.json", "avatar_model_progress.html", "avatar_model_regression.html", "avatar_system.html", "avatar_system.json", "last_5_3ddfa_reconstructions.html" };
		foreach (string path in array)
		{
			string path2 = Path.Combine(profileFolder, path);
			if (File.Exists(path2))
			{
				File.Delete(path2);
			}
		}
		string text = Path.Combine(profileFolder, "Benchmarks");
		array = new string[3] { "pose_alignment_audit.html", "pose_alignment_samples.csv", "pose_alignment_summary.json" };
		foreach (string path3 in array)
		{
			string path4 = Path.Combine(text, path3);
			if (File.Exists(path4))
			{
				File.Delete(path4);
			}
		}
		if (Directory.Exists(text) && !Directory.EnumerateFileSystemEntries(text).Any())
		{
			Directory.Delete(text);
		}
	}

	private static void PruneEmptyObjectFolders(string storageRoot)
	{
		string path = Path.Combine(storageRoot, "Objects");
		if (!Directory.Exists(path))
		{
			return;
		}
		foreach (string item in from text in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
			orderby text.Length descending
			select text)
		{
			if (!Directory.EnumerateFileSystemEntries(item).Any())
			{
				Directory.Delete(item);
			}
		}
	}
}
