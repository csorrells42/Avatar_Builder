using System.IO;
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Vision.MediaPipe;

var geometryResult = MediaPipeBrowGeometrySelfTest.Run();
Console.WriteLine(geometryResult.Detail);
if (!geometryResult.Succeeded)
{
    return 1;
}

var outputFolder = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "AvatarBuilderStorageSelfTest");
var result = AvatarObservationStorageSelfTest.Run(
    outputFolder,
    args.Any(static argument => string.Equals(argument, "--keep", StringComparison.OrdinalIgnoreCase)));
Console.WriteLine(result.Detail);
Console.WriteLine(result.ReportPath);
return result.Succeeded ? 0 : 1;
