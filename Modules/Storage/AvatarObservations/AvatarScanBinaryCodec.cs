using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarScanBinaryCodec
{
	private static readonly byte[] Magic = "AVSCAN01"u8.ToArray();

	private const int Version = 2;

	public static string WriteObject(string storageRoot, AvatarReconstructionSnapshot snapshot)
	{
		string scanObjectFolder = AvatarStorageLayout.GetScanObjectFolder(storageRoot);
		Directory.CreateDirectory(scanObjectFolder);
		string text = Path.Combine(scanObjectFolder, $".{Guid.NewGuid():N}.tmp");
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan))
			{
				using BinaryWriter binaryWriter = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: true);
				binaryWriter.Write(Magic);
				binaryWriter.Write(2);
				WriteDensePoints(binaryWriter, snapshot.Vertices);
				WriteDensePoints(binaryWriter, snapshot.CanonicalIdentityVertices);
				WriteIndexedPoints(binaryWriter, snapshot.SparseLandmarks);
				WriteDoubles(binaryWriter, snapshot.CameraMatrixCoefficients);
				WriteDoubles(binaryWriter, snapshot.ShapeCoefficients);
				WriteDoubles(binaryWriter, snapshot.ExpressionCoefficients);
				WriteDoubles(binaryWriter, snapshot.PoseCoefficients);
				WriteIndexedPoints(binaryWriter, snapshot.ObservedLandmarks);
				binaryWriter.Write(snapshot.SourceFrameWidthPixels);
				binaryWriter.Write(snapshot.SourceFrameHeightPixels);
				WriteFaceBox(binaryWriter, snapshot.InputFaceBox);
				binaryWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			return AvatarStorageLayout.PromoteContentAddressedObject(text, scanObjectFolder, ".avscan");
		}
		catch
		{
			TryDelete(text);
			throw;
		}
	}

	public static AvatarScanGeometry Read(string path)
	{
		using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
		if (!((ReadOnlySpan<byte>)binaryReader.ReadBytes(Magic.Length).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)Magic))
		{
			throw new InvalidDataException("Avatar scan has an unknown signature: " + path);
		}
		int num = binaryReader.ReadInt32();
		if ((num < 1 || num > 2) ? true : false)
		{
			throw new InvalidDataException($"Avatar scan version {num} is not supported: {path}");
		}
		AvatarScanGeometry avatarScanGeometry = new AvatarScanGeometry
		{
			Vertices = ReadDensePoints(binaryReader),
			CanonicalIdentityVertices = ReadDensePoints(binaryReader),
			SparseLandmarks = ReadIndexedPoints(binaryReader),
			CameraMatrixCoefficients = ReadDoubles(binaryReader),
			ShapeCoefficients = ReadDoubles(binaryReader),
			ExpressionCoefficients = ReadDoubles(binaryReader)
		};
		if (num == 1)
		{
			return avatarScanGeometry;
		}
		return avatarScanGeometry with
		{
			PoseCoefficients = ReadDoubles(binaryReader),
			ObservedLandmarks = ReadIndexedPoints(binaryReader),
			SourceFrameWidthPixels = binaryReader.ReadInt32(),
			SourceFrameHeightPixels = binaryReader.ReadInt32(),
			InputFaceBox = ReadFaceBox(binaryReader)
		};
	}

	private static void WriteDensePoints(BinaryWriter writer, IReadOnlyList<FaceMeshLandmarkPoint> points)
	{
		writer.Write(points.Count);
		for (int i = 0; i < points.Count; i++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = points[i];
			if (faceMeshLandmarkPoint.Index != i)
			{
				throw new InvalidDataException($"Dense scan point {i} had non-canonical index {faceMeshLandmarkPoint.Index}.");
			}
			writer.Write((float)faceMeshLandmarkPoint.X);
			writer.Write((float)faceMeshLandmarkPoint.Y);
			writer.Write((float)faceMeshLandmarkPoint.Z);
		}
	}

	private static List<FaceMeshLandmarkPoint> ReadDensePoints(BinaryReader reader)
	{
		int num = ReadCount(reader, 1000000, "dense point");
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = i,
				X = reader.ReadSingle(),
				Y = reader.ReadSingle(),
				Z = reader.ReadSingle()
			});
		}
		return list;
	}

	private static void WriteIndexedPoints(BinaryWriter writer, IReadOnlyList<FaceMeshLandmarkPoint> points)
	{
		writer.Write(points.Count);
		foreach (FaceMeshLandmarkPoint point in points)
		{
			writer.Write(point.Index);
			writer.Write((float)point.X);
			writer.Write((float)point.Y);
			writer.Write((float)point.Z);
		}
	}

	private static List<FaceMeshLandmarkPoint> ReadIndexedPoints(BinaryReader reader)
	{
		int num = ReadCount(reader, 100000, "indexed point");
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = reader.ReadInt32(),
				X = reader.ReadSingle(),
				Y = reader.ReadSingle(),
				Z = reader.ReadSingle()
			});
		}
		return list;
	}

	private static void WriteDoubles(BinaryWriter writer, IReadOnlyList<double> values)
	{
		writer.Write(values.Count);
		foreach (double value in values)
		{
			writer.Write(double.IsFinite(value) ? value : 0.0);
		}
	}

	private static List<double> ReadDoubles(BinaryReader reader)
	{
		int num = ReadCount(reader, 10000, "coefficient");
		List<double> list = new List<double>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(reader.ReadDouble());
		}
		return list;
	}

	private static void WriteFaceBox(BinaryWriter writer, ReconstructionInputFaceBox? faceBox)
	{
		writer.Write(faceBox != null);
		if (faceBox != null)
		{
			writer.Write(faceBox.Left);
			writer.Write(faceBox.Top);
			writer.Write(faceBox.Right);
			writer.Write(faceBox.Bottom);
			writer.Write(faceBox.Normalized);
			writer.Write(faceBox.Confidence);
		}
	}

	private static ReconstructionInputFaceBox? ReadFaceBox(BinaryReader reader)
	{
		if (!reader.ReadBoolean())
		{
			return null;
		}
		return new ReconstructionInputFaceBox
		{
			Left = reader.ReadDouble(),
			Top = reader.ReadDouble(),
			Right = reader.ReadDouble(),
			Bottom = reader.ReadDouble(),
			Normalized = reader.ReadBoolean(),
			Confidence = reader.ReadDouble()
		};
	}

	private static int ReadCount(BinaryReader reader, int maximum, string label)
	{
		int num = reader.ReadInt32();
		if (num < 0 || num > maximum)
		{
			throw new InvalidDataException($"Avatar scan {label} count {num} is invalid.");
		}
		return num;
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
