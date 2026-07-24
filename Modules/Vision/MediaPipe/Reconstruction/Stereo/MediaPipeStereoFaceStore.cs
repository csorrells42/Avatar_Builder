using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public static class MediaPipeStereoFaceStore
{
	public const string FolderName = "StereoFaceGeometry";

	public const string StateFileName = "stereo-face-state.bin";

	public const string ViewerFileName = "stereo-face-viewer.html";

	public const string RawViewerFileName = "stereo-face-raw-evidence-viewer.html";

	public const string ProbabilityViewerFileName = "stereo-probability-face-viewer.html";

	private const int MaximumVertexCount = 478;

	private const int MaximumDenseVertexCount = 65536;

	private const int MaximumRawPointBinCount = 1000000;

	public static string GetFolder(string profileFolder)
	{
		return Path.Combine(profileFolder, "StereoFaceGeometry");
	}

	public static string GetStatePath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), StateFileName);
	}

	public static string GetViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-viewer.html");
	}

	public static string GetRawViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-raw-evidence-viewer.html");
	}

	public static string GetProbabilityViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-probability-face-viewer.html");
	}

	public static MediaPipeStereoFaceState? ReadState(string profileFolder)
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

	public static void WriteData(string profileFolder, MediaPipeStereoFaceState state)
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

	public static void WriteViewers(string profileFolder, MediaPipeStereoFaceState state, MediaPipeStereoFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetViewerPath(profileFolder), MediaPipeStereoFaceViewerPage.Build(model), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetRawViewerPath(profileFolder), MediaPipeStereoRawEvidenceViewerPage.Build(state), Encoding.UTF8);
	}

	public static void WriteProbabilityFace(string profileFolder, MediaPipeStereoProbabilityFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetProbabilityViewerPath(profileFolder), MediaPipeStereoProbabilityFaceViewerPage.Build(model), Encoding.UTF8);
	}

	private static MediaPipeStereoFaceState ReadState(BinaryReader reader)
	{
		string subjectId = reader.ReadString();
		string subjectDisplayName = reader.ReadString();
		string calibrationId = reader.ReadString();
		long updatedAtTicks = reader.ReadInt64();
		if (updatedAtTicks < DateTime.MinValue.Ticks || updatedAtTicks > DateTime.MaxValue.Ticks)
		{
			throw new InvalidDataException("Invalid stereo face update timestamp.");
		}

		long acceptedFrameCount = reader.ReadInt64();
		long rejectedFrameCount = reader.ReadInt64();
		long directObservationCount = reader.ReadInt64();
		long rejectedPointCount = reader.ReadInt64();
		long denseObservationCount = reader.ReadInt64();
		long rejectedDensePointCount = reader.ReadInt64();
		long rawTriangulatedObservationCount = reader.ReadInt64();
		long rawUnstoredObservationCount = reader.ReadInt64();
		double baselineInches = reader.ReadDouble();

		int vertexCount = ReadCount(reader, MaximumVertexCount, "stereo vertex");
		MediaPipeStereoVertexAccumulatorState[] vertices = new MediaPipeStereoVertexAccumulatorState[vertexCount];
		for (int index = 0; index < vertexCount; index++)
		{
			vertices[index] = new MediaPipeStereoVertexAccumulatorState
			{
				Index = reader.ReadInt32(),
				TotalWeight = reader.ReadDouble(),
				MeanXInches = reader.ReadDouble(),
				MeanYInches = reader.ReadDouble(),
				MeanZInches = reader.ReadDouble(),
				M2X = reader.ReadDouble(),
				M2Y = reader.ReadDouble(),
				M2Z = reader.ReadDouble(),
				WeightedResidualSum = reader.ReadDouble(),
				DirectObservationCount = reader.ReadInt64(),
				RejectedObservationCount = reader.ReadInt64()
			};
		}

		int denseVertexCount = ReadCount(reader, MaximumDenseVertexCount, "dense stereo vertex");
		MediaPipeStereoDenseVertexAccumulatorState[] denseVertices = new MediaPipeStereoDenseVertexAccumulatorState[denseVertexCount];
		for (int index = 0; index < denseVertexCount; index++)
		{
			denseVertices[index] = new MediaPipeStereoDenseVertexAccumulatorState
			{
				SampleIndex = reader.ReadInt32(),
				TriangleIndex = reader.ReadInt32(),
				IsExpressionSurface = reader.ReadBoolean(),
				TotalWeight = reader.ReadDouble(),
				MeanXInches = reader.ReadDouble(),
				MeanYInches = reader.ReadDouble(),
				MeanZInches = reader.ReadDouble(),
				M2X = reader.ReadDouble(),
				M2Y = reader.ReadDouble(),
				M2Z = reader.ReadDouble(),
				WeightedResidualSum = reader.ReadDouble(),
				DirectObservationCount = reader.ReadInt64(),
				RejectedObservationCount = reader.ReadInt64()
			};
		}

		int rawPointBinCount = ReadCount(reader, MaximumRawPointBinCount, "raw stereo point bin");
		MediaPipeStereoRawPointBinState[] rawPointBins = new MediaPipeStereoRawPointBinState[rawPointBinCount];
		for (int index = 0; index < rawPointBinCount; index++)
		{
			rawPointBins[index] = new MediaPipeStereoRawPointBinState
			{
				BinX = reader.ReadInt32(),
				BinY = reader.ReadInt32(),
				BinZ = reader.ReadInt32(),
				MeanXInches = reader.ReadDouble(),
				MeanYInches = reader.ReadDouble(),
				MeanZInches = reader.ReadDouble(),
				ObservationCount = reader.ReadInt64(),
				AcceptedObservationCount = reader.ReadInt64()
			};
		}

		if (reader.BaseStream.Position != reader.BaseStream.Length)
		{
			throw new InvalidDataException("Unexpected data after stereo face state.");
		}

		return new MediaPipeStereoFaceState
		{
			SubjectId = subjectId,
			SubjectDisplayName = subjectDisplayName,
			CalibrationId = calibrationId,
			UpdatedAtUtc = new DateTime(updatedAtTicks, DateTimeKind.Utc),
			AcceptedFrameCount = acceptedFrameCount,
			RejectedFrameCount = rejectedFrameCount,
			DirectObservationCount = directObservationCount,
			RejectedPointCount = rejectedPointCount,
			DenseObservationCount = denseObservationCount,
			RejectedDensePointCount = rejectedDensePointCount,
			RawTriangulatedObservationCount = rawTriangulatedObservationCount,
			RawUnstoredObservationCount = rawUnstoredObservationCount,
			BaselineInches = baselineInches,
			VertexAccumulators = vertices,
			DenseVertexAccumulators = denseVertices,
			RawPointBins = rawPointBins
		};
	}

	private static void WriteState(BinaryWriter writer, MediaPipeStereoFaceState state)
	{
		writer.Write(state.SubjectId);
		writer.Write(state.SubjectDisplayName);
		writer.Write(state.CalibrationId);
		writer.Write(state.UpdatedAtUtc.Ticks);
		writer.Write(state.AcceptedFrameCount);
		writer.Write(state.RejectedFrameCount);
		writer.Write(state.DirectObservationCount);
		writer.Write(state.RejectedPointCount);
		writer.Write(state.DenseObservationCount);
		writer.Write(state.RejectedDensePointCount);
		writer.Write(state.RawTriangulatedObservationCount);
		writer.Write(state.RawUnstoredObservationCount);
		writer.Write(state.BaselineInches);

		IReadOnlyList<MediaPipeStereoVertexAccumulatorState> vertices = state.VertexAccumulators;
		writer.Write(vertices.Count);
		for (int index = 0; index < vertices.Count; index++)
		{
			MediaPipeStereoVertexAccumulatorState vertex = vertices[index];
			writer.Write(vertex.Index);
			writer.Write(vertex.TotalWeight);
			writer.Write(vertex.MeanXInches);
			writer.Write(vertex.MeanYInches);
			writer.Write(vertex.MeanZInches);
			writer.Write(vertex.M2X);
			writer.Write(vertex.M2Y);
			writer.Write(vertex.M2Z);
			writer.Write(vertex.WeightedResidualSum);
			writer.Write(vertex.DirectObservationCount);
			writer.Write(vertex.RejectedObservationCount);
		}

		IReadOnlyList<MediaPipeStereoDenseVertexAccumulatorState> denseVertices = state.DenseVertexAccumulators;
		writer.Write(denseVertices.Count);
		for (int index = 0; index < denseVertices.Count; index++)
		{
			MediaPipeStereoDenseVertexAccumulatorState vertex = denseVertices[index];
			writer.Write(vertex.SampleIndex);
			writer.Write(vertex.TriangleIndex);
			writer.Write(vertex.IsExpressionSurface);
			writer.Write(vertex.TotalWeight);
			writer.Write(vertex.MeanXInches);
			writer.Write(vertex.MeanYInches);
			writer.Write(vertex.MeanZInches);
			writer.Write(vertex.M2X);
			writer.Write(vertex.M2Y);
			writer.Write(vertex.M2Z);
			writer.Write(vertex.WeightedResidualSum);
			writer.Write(vertex.DirectObservationCount);
			writer.Write(vertex.RejectedObservationCount);
		}

		IReadOnlyList<MediaPipeStereoRawPointBinState> rawPointBins = state.RawPointBins;
		writer.Write(rawPointBins.Count);
		for (int index = 0; index < rawPointBins.Count; index++)
		{
			MediaPipeStereoRawPointBinState pointBin = rawPointBins[index];
			writer.Write(pointBin.BinX);
			writer.Write(pointBin.BinY);
			writer.Write(pointBin.BinZ);
			writer.Write(pointBin.MeanXInches);
			writer.Write(pointBin.MeanYInches);
			writer.Write(pointBin.MeanZInches);
			writer.Write(pointBin.ObservationCount);
			writer.Write(pointBin.AcceptedObservationCount);
		}
	}

	private static int ReadCount(BinaryReader reader, int maximumCount, string name)
	{
		int count = reader.ReadInt32();
		if (count < 0 || count > maximumCount)
		{
			throw new InvalidDataException($"Invalid {name} count.");
		}
		return count;
	}
}
