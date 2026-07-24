using Vortice.D3DCompiler;

if (args.Length != 4)
{
	Console.Error.WriteLine(
		"Usage: ShaderCompiler <input.hlsl> <entry-point> <profile> <output.cso>");
	return 2;
}

string sourcePath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[3]);
string source = File.ReadAllText(sourcePath);
byte[] bytecode = Compiler.Compile(
	source,
	args[1],
	Path.GetFileName(sourcePath),
	args[2],
	ShaderFlags.OptimizationLevel3).ToArray();
Directory.CreateDirectory(
	Path.GetDirectoryName(outputPath)
	?? throw new InvalidOperationException("Shader output directory is missing."));
File.WriteAllBytes(outputPath, bytecode);
return 0;
