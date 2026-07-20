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
        var objectFolder = AvatarStorageLayout.GetTopologyObjectFolder(storageRoot);
        Directory.CreateDirectory(objectFolder);
        var temporaryPath = Path.Combine(objectFolder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(edges.Count);
                foreach (var edge in edges)
                {
                    writer.Write(edge.FromIndex);
                    writer.Write(edge.ToIndex);
                    writer.Write(edge.Role ?? "");
                    writer.Write(edge.Source ?? "");
                    writer.Write(edge.LengthPercent);
                    writer.Write(edge.ConfidencePercent);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            return AvatarStorageLayout.PromoteContentAddressedObject(temporaryPath, objectFolder, ".avtop");
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
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
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException($"Avatar topology has an unknown signature: {path}");
        }

        var version = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (version != Version || count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException($"Avatar topology header is invalid: {path}");
        }

        var edges = new List<MeshTopologyEdge>(count);
        for (var index = 0; index < count; index++)
        {
            edges.Add(new MeshTopologyEdge
            {
                FromIndex = reader.ReadInt32(),
                ToIndex = reader.ReadInt32(),
                Role = reader.ReadString(),
                Source = reader.ReadString(),
                LengthPercent = reader.ReadDouble(),
                ConfidencePercent = reader.ReadDouble()
            });
        }

        return edges;
    }
}
