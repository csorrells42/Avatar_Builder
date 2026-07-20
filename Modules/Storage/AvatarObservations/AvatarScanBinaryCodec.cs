using System.IO;
using System.Text;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarScanBinaryCodec
{
    private static readonly byte[] Magic = "AVSCAN01"u8.ToArray();
    private const int Version = 1;

    public static string WriteObject(string storageRoot, ThreeDdfaReconstructionSnapshot snapshot)
    {
        var objectFolder = AvatarStorageLayout.GetScanObjectFolder(storageRoot);
        Directory.CreateDirectory(objectFolder);
        var temporaryPath = Path.Combine(objectFolder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1 << 20,
                       FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                WriteDensePoints(writer, snapshot.Vertices);
                WriteDensePoints(writer, snapshot.CanonicalIdentityVertices);
                WriteIndexedPoints(writer, snapshot.SparseLandmarks);
                WriteDoubles(writer, snapshot.CameraMatrixCoefficients);
                WriteDoubles(writer, snapshot.ShapeCoefficients);
                WriteDoubles(writer, snapshot.ExpressionCoefficients);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            return AvatarStorageLayout.PromoteContentAddressedObject(temporaryPath, objectFolder, ".avscan");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static AvatarScanGeometry Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException($"Avatar scan has an unknown signature: {path}");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"Avatar scan version {version} is not supported: {path}");
        }

        return new AvatarScanGeometry
        {
            Vertices = ReadDensePoints(reader),
            CanonicalIdentityVertices = ReadDensePoints(reader),
            SparseLandmarks = ReadIndexedPoints(reader),
            CameraMatrixCoefficients = ReadDoubles(reader),
            ShapeCoefficients = ReadDoubles(reader),
            ExpressionCoefficients = ReadDoubles(reader)
        };
    }

    private static void WriteDensePoints(BinaryWriter writer, IReadOnlyList<FaceMeshLandmarkPoint> points)
    {
        writer.Write(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point.Index != index)
            {
                throw new InvalidDataException($"Dense scan point {index} had non-canonical index {point.Index}.");
            }

            writer.Write((float)point.X);
            writer.Write((float)point.Y);
            writer.Write((float)point.Z);
        }
    }

    private static List<FaceMeshLandmarkPoint> ReadDensePoints(BinaryReader reader)
    {
        var count = ReadCount(reader, 1_000_000, "dense point");
        var points = new List<FaceMeshLandmarkPoint>(count);
        for (var index = 0; index < count; index++)
        {
            points.Add(new FaceMeshLandmarkPoint
            {
                Index = index,
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle()
            });
        }

        return points;
    }

    private static void WriteIndexedPoints(BinaryWriter writer, IReadOnlyList<FaceMeshLandmarkPoint> points)
    {
        writer.Write(points.Count);
        foreach (var point in points)
        {
            writer.Write(point.Index);
            writer.Write((float)point.X);
            writer.Write((float)point.Y);
            writer.Write((float)point.Z);
        }
    }

    private static List<FaceMeshLandmarkPoint> ReadIndexedPoints(BinaryReader reader)
    {
        var count = ReadCount(reader, 100_000, "indexed point");
        var points = new List<FaceMeshLandmarkPoint>(count);
        for (var index = 0; index < count; index++)
        {
            points.Add(new FaceMeshLandmarkPoint
            {
                Index = reader.ReadInt32(),
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle()
            });
        }

        return points;
    }

    private static void WriteDoubles(BinaryWriter writer, IReadOnlyList<double> values)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            writer.Write(double.IsFinite(value) ? value : 0d);
        }
    }

    private static List<double> ReadDoubles(BinaryReader reader)
    {
        var count = ReadCount(reader, 10_000, "coefficient");
        var values = new List<double>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(reader.ReadDouble());
        }

        return values;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string label)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"Avatar scan {label} count {count} is invalid.");
        }

        return count;
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

public sealed class AvatarScanGeometry
{
    public List<FaceMeshLandmarkPoint> Vertices { get; init; } = [];

    public List<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = [];

    public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = [];

    public List<double> CameraMatrixCoefficients { get; init; } = [];

    public List<double> ShapeCoefficients { get; init; } = [];

    public List<double> ExpressionCoefficients { get; init; } = [];
}
