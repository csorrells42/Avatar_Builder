using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarTopologyBinaryCodec
{
	private static readonly byte[] Magic = "AVTOP001"u8.ToArray();

	private const int Version = 1;

	public static string WriteObject(string storageRoot, IReadOnlyList<MeshTopologyEdge> edges)
	{
		string topologyObjectFolder = AvatarStorageLayout.GetTopologyObjectFolder(storageRoot);
		Directory.CreateDirectory(topologyObjectFolder);
		string text = Path.Combine(topologyObjectFolder, $".{Guid.NewGuid():N}.tmp");
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				using BinaryWriter binaryWriter = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: true);
				binaryWriter.Write(Magic);
				binaryWriter.Write(1);
				binaryWriter.Write(edges.Count);
				foreach (MeshTopologyEdge edge in edges)
				{
					binaryWriter.Write(edge.FromIndex);
					binaryWriter.Write(edge.ToIndex);
					binaryWriter.Write(edge.Role ?? "");
					binaryWriter.Write(edge.Source ?? "");
					binaryWriter.Write(edge.LengthPercent);
					binaryWriter.Write(edge.ConfidencePercent);
				}
				binaryWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			return AvatarStorageLayout.PromoteContentAddressedObject(text, topologyObjectFolder, ".avtop");
		}
		catch
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			throw;
		}
	}

	public static List<MeshTopologyEdge> Read(string path)
	{
		using FileStream input = File.OpenRead(path);
		using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
		if (!((ReadOnlySpan<byte>)binaryReader.ReadBytes(Magic.Length).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)Magic))
		{
			throw new InvalidDataException("Avatar topology has an unknown signature: " + path);
		}
		int num = binaryReader.ReadInt32();
		int num2 = binaryReader.ReadInt32();
		if (num != 1 || num2 < 0 || num2 > 1000000)
		{
			throw new InvalidDataException("Avatar topology header is invalid: " + path);
		}
		List<MeshTopologyEdge> list = new List<MeshTopologyEdge>(num2);
		for (int i = 0; i < num2; i++)
		{
			list.Add(new MeshTopologyEdge
			{
				FromIndex = binaryReader.ReadInt32(),
				ToIndex = binaryReader.ReadInt32(),
				Role = binaryReader.ReadString(),
				Source = binaryReader.ReadString(),
				LengthPercent = binaryReader.ReadDouble(),
				ConfidencePercent = binaryReader.ReadDouble()
			});
		}
		return list;
	}
}
