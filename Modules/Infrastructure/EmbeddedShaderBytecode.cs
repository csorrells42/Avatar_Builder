using System;
using System.IO;
using System.Reflection;

namespace AvatarBuilder.Modules.Infrastructure;

internal static class EmbeddedShaderBytecode
{
	public static byte[] Load(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		Assembly assembly = typeof(EmbeddedShaderBytecode).Assembly;
		string resourceName = "AvatarBuilder.Shaders.Compiled." + fileName;
		using Stream stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException(
				$"Embedded shader bytecode '{fileName}' is missing.");
		byte[] bytecode = new byte[checked((int)stream.Length)];
		stream.ReadExactly(bytecode);
		return bytecode;
	}
}
