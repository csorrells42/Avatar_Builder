using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public static class MediaPipeNormalizedFaceStore
{
	public const string FolderName = "MediaPipeGeometry";

	public const string StateFileName = "reconstruction-state.bin";

	public const string ViewerFileName = "normalized-face-viewer.html";

	public static string GetFolder(string profileFolder)
	{
		return Path.Combine(profileFolder, "MediaPipeGeometry");
	}

	public static string GetStatePath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), StateFileName);
	}

	public static string GetViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "normalized-face-viewer.html");
	}

	public static MediaPipeNormalizedFaceState? ReadState(string profileFolder)
	{
		string statePath = GetStatePath(profileFolder);
		if (!File.Exists(statePath))
		{
			return null;
		}
		try
		{
			using FileStream input = new FileStream(
				statePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 65536,
				FileOptions.SequentialScan);
			using BinaryReader reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
			return ReadState(reader);
		}
		catch (EndOfStreamException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}

	public static void WriteData(string profileFolder, MediaPipeNormalizedFaceState state)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(state, "state");
		string folder = GetFolder(profileFolder);
		Directory.CreateDirectory(folder);
		string statePath = GetStatePath(profileFolder);
		string temporaryPath = statePath + ".tmp";
		try
		{
			using (FileStream output = new FileStream(
				temporaryPath,
				FileMode.Create,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 65536,
				FileOptions.SequentialScan))
			using (BinaryWriter writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: false))
			{
				WriteState(writer, state);
			}
			File.Move(temporaryPath, statePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	public static void WriteViewer(string profileFolder, MediaPipeNormalizedFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetViewerPath(profileFolder), MediaPipeNormalizedFaceViewerPage.Build(model), Encoding.UTF8);
	}

	public static void Delete(string profileFolder)
	{
		string folder = GetFolder(profileFolder);
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	private static MediaPipeNormalizedFaceState ReadState(BinaryReader reader)
	{
		string subjectId = reader.ReadString();
		string subjectDisplayName = reader.ReadString();
		DateTime updatedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
		long acceptedFrameCount = reader.ReadInt64();
		long rejectedFrameCount = reader.ReadInt64();
		long directLandmarkObservationCount = reader.ReadInt64();
		long hiddenLandmarkRejectionCount = reader.ReadInt64();
		long silhouetteObservationCount = reader.ReadInt64();
		double minimumARotationDegrees = reader.ReadDouble();
		double maximumARotationDegrees = reader.ReadDouble();
		double minimumBRotationDegrees = reader.ReadDouble();
		double maximumBRotationDegrees = reader.ReadDouble();
		double minimumCRotationDegrees = reader.ReadDouble();
		double maximumCRotationDegrees = reader.ReadDouble();

		int vertexCount = reader.ReadInt32();
		if (vertexCount < 0 || vertexCount > 478)
		{
			throw new InvalidDataException("Invalid MediaPipe vertex count.");
		}
		MediaPipeVertexAccumulatorState[] vertices = new MediaPipeVertexAccumulatorState[vertexCount];
		for (int index = 0; index < vertexCount; index++)
		{
			vertices[index] = new MediaPipeVertexAccumulatorState
			{
				Index = reader.ReadInt32(),
				M00 = reader.ReadDouble(),
				M01 = reader.ReadDouble(),
				M02 = reader.ReadDouble(),
				M11 = reader.ReadDouble(),
				M12 = reader.ReadDouble(),
				M22 = reader.ReadDouble(),
				B0 = reader.ReadDouble(),
				B1 = reader.ReadDouble(),
				B2 = reader.ReadDouble(),
				SumMeasurementSquares = reader.ReadDouble(),
				TotalWeight = reader.ReadDouble(),
				DirectObservationCount = reader.ReadInt64(),
				RejectedHiddenObservationCount = reader.ReadInt64(),
				MinimumYawDegrees = reader.ReadDouble(),
				MaximumYawDegrees = reader.ReadDouble(),
				MinimumPitchDegrees = reader.ReadDouble(),
				MaximumPitchDegrees = reader.ReadDouble()
			};
		}

		int silhouetteCount = reader.ReadInt32();
		if (silhouetteCount < 0 || silhouetteCount > 4096)
		{
			throw new InvalidDataException("Invalid MediaPipe silhouette count.");
		}
		MediaPipeSilhouetteProfileState[] silhouettes = new MediaPipeSilhouetteProfileState[silhouetteCount];
		for (int silhouetteIndex = 0; silhouetteIndex < silhouetteCount; silhouetteIndex++)
		{
			int yawBinDegrees = reader.ReadInt32();
			int pitchBinDegrees = reader.ReadInt32();
			string cameraId = reader.ReadString();
			long frameCount = reader.ReadInt64();
			int bandCount = reader.ReadInt32();
			if (bandCount < 0 || bandCount > 18)
			{
				throw new InvalidDataException("Invalid MediaPipe silhouette band count.");
			}
			MediaPipeSilhouetteBandState[] bands = new MediaPipeSilhouetteBandState[bandCount];
			for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
			{
				bands[bandIndex] = new MediaPipeSilhouetteBandState
				{
					BandIndex = reader.ReadInt32(),
					ObservationCount = reader.ReadInt64(),
					MinimumSupportMean = reader.ReadDouble(),
					MaximumSupportMean = reader.ReadDouble(),
					MinimumSupportM2 = reader.ReadDouble(),
					MaximumSupportM2 = reader.ReadDouble()
				};
			}
			silhouettes[silhouetteIndex] = new MediaPipeSilhouetteProfileState
			{
				YawBinDegrees = yawBinDegrees,
				PitchBinDegrees = pitchBinDegrees,
				CameraId = cameraId,
				FrameCount = frameCount,
				Bands = bands
			};
		}

		if (reader.BaseStream.Position != reader.BaseStream.Length)
		{
			throw new InvalidDataException("Unexpected data after MediaPipe reconstruction state.");
		}

		return new MediaPipeNormalizedFaceState
		{
			SubjectId = subjectId,
			SubjectDisplayName = subjectDisplayName,
			UpdatedAtUtc = updatedAtUtc,
			AcceptedFrameCount = acceptedFrameCount,
			RejectedFrameCount = rejectedFrameCount,
			DirectLandmarkObservationCount = directLandmarkObservationCount,
			HiddenLandmarkRejectionCount = hiddenLandmarkRejectionCount,
			SilhouetteObservationCount = silhouetteObservationCount,
			MinimumARotationDegrees = minimumARotationDegrees,
			MaximumARotationDegrees = maximumARotationDegrees,
			MinimumBRotationDegrees = minimumBRotationDegrees,
			MaximumBRotationDegrees = maximumBRotationDegrees,
			MinimumCRotationDegrees = minimumCRotationDegrees,
			MaximumCRotationDegrees = maximumCRotationDegrees,
			VertexAccumulators = vertices,
			SilhouetteAccumulators = silhouettes
		};
	}

	private static void WriteState(BinaryWriter writer, MediaPipeNormalizedFaceState state)
	{
		writer.Write(state.SubjectId);
		writer.Write(state.SubjectDisplayName);
		writer.Write(state.UpdatedAtUtc.Ticks);
		writer.Write(state.AcceptedFrameCount);
		writer.Write(state.RejectedFrameCount);
		writer.Write(state.DirectLandmarkObservationCount);
		writer.Write(state.HiddenLandmarkRejectionCount);
		writer.Write(state.SilhouetteObservationCount);
		writer.Write(state.MinimumARotationDegrees);
		writer.Write(state.MaximumARotationDegrees);
		writer.Write(state.MinimumBRotationDegrees);
		writer.Write(state.MaximumBRotationDegrees);
		writer.Write(state.MinimumCRotationDegrees);
		writer.Write(state.MaximumCRotationDegrees);

		IReadOnlyList<MediaPipeVertexAccumulatorState> vertices = state.VertexAccumulators;
		writer.Write(vertices.Count);
		for (int index = 0; index < vertices.Count; index++)
		{
			MediaPipeVertexAccumulatorState vertex = vertices[index];
			writer.Write(vertex.Index);
			writer.Write(vertex.M00);
			writer.Write(vertex.M01);
			writer.Write(vertex.M02);
			writer.Write(vertex.M11);
			writer.Write(vertex.M12);
			writer.Write(vertex.M22);
			writer.Write(vertex.B0);
			writer.Write(vertex.B1);
			writer.Write(vertex.B2);
			writer.Write(vertex.SumMeasurementSquares);
			writer.Write(vertex.TotalWeight);
			writer.Write(vertex.DirectObservationCount);
			writer.Write(vertex.RejectedHiddenObservationCount);
			writer.Write(vertex.MinimumYawDegrees);
			writer.Write(vertex.MaximumYawDegrees);
			writer.Write(vertex.MinimumPitchDegrees);
			writer.Write(vertex.MaximumPitchDegrees);
		}

		IReadOnlyList<MediaPipeSilhouetteProfileState> silhouettes = state.SilhouetteAccumulators;
		writer.Write(silhouettes.Count);
		for (int silhouetteIndex = 0; silhouetteIndex < silhouettes.Count; silhouetteIndex++)
		{
			MediaPipeSilhouetteProfileState silhouette = silhouettes[silhouetteIndex];
			writer.Write(silhouette.YawBinDegrees);
			writer.Write(silhouette.PitchBinDegrees);
			writer.Write(silhouette.CameraId);
			writer.Write(silhouette.FrameCount);
			IReadOnlyList<MediaPipeSilhouetteBandState> bands = silhouette.Bands;
			writer.Write(bands.Count);
			for (int bandIndex = 0; bandIndex < bands.Count; bandIndex++)
			{
				MediaPipeSilhouetteBandState band = bands[bandIndex];
				writer.Write(band.BandIndex);
				writer.Write(band.ObservationCount);
				writer.Write(band.MinimumSupportMean);
				writer.Write(band.MaximumSupportMean);
				writer.Write(band.MinimumSupportM2);
				writer.Write(band.MaximumSupportM2);
			}
		}
	}
}
