using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class VisionBenchmarkRecorder : IDisposable
{
	private const int MaximumPendingSampleCount = 64;

	private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10L);

	private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private readonly object _pendingLock = new object();

	private readonly Dictionary<string, VisionPipelineDiagnostics> _pending = new Dictionary<string, VisionPipelineDiagnostics>(StringComparer.Ordinal);

	private readonly object _folderLock = new object();

	private readonly object _flushLock = new object();

	private readonly Timer _timer;

	private string _benchmarkFolder = "";

	private int _flushRunning;

	private bool _disposed;

	public VisionBenchmarkRecorder()
	{
		_timer = new Timer(delegate
		{
			QueueFlush();
		}, null, FlushInterval, FlushInterval);
	}

	public void SetOutputRoot(string outputRoot)
	{
		if (string.IsNullOrWhiteSpace(outputRoot))
		{
			return;
		}
		FlushPending();
		lock (_folderLock)
		{
			_benchmarkFolder = Path.Combine(Path.GetFullPath(outputRoot), "Benchmarks");
		}
	}

	public void Record(VisionPipelineDiagnostics diagnostics)
	{
		if (_disposed || diagnostics == VisionPipelineDiagnostics.None || diagnostics.EndToEndMilliseconds <= 0.0)
		{
			return;
		}
		string key = diagnostics.Backend + "\0" + diagnostics.Mode;
		lock (_pendingLock)
		{
			if (_pending.Count < 64 && !_pending.ContainsKey(key))
			{
				_pending.Add(key, diagnostics);
			}
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_timer.Dispose();
			FlushPending();
		}
	}

	private void QueueFlush()
	{
		if (_disposed || !HasPendingSamples() || Interlocked.Exchange(ref _flushRunning, 1) != 0)
		{
			return;
		}
		Task.Run(delegate
		{
			try
			{
				FlushPending();
			}
			finally
			{
				Interlocked.Exchange(ref _flushRunning, 0);
			}
		});
	}

	private void FlushPending()
	{
		lock (_flushLock)
		{
			FlushPendingLocked();
		}
	}

	private void FlushPendingLocked()
	{
		string benchmarkFolder;
		lock (_folderLock)
		{
			benchmarkFolder = _benchmarkFolder;
		}
		if (string.IsNullOrWhiteSpace(benchmarkFolder) || !HasPendingSamples())
		{
			return;
		}
		KeyValuePair<string, VisionPipelineDiagnostics>[] array;
		lock (_pendingLock)
		{
			array = _pending.ToArray();
			_pending.Clear();
		}
		if (array.Length == 0)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(benchmarkFolder);
			foreach (IGrouping<string, VisionPipelineDiagnostics> item in from pair in array
				select pair.Value into sample
				group sample by sample.CapturedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
			{
				string text = Path.Combine(benchmarkFolder, "vision-pipeline-" + item.Key + ".csv");
				bool flag = !File.Exists(text) || new FileInfo(text).Length == 0;
				using StreamWriter streamWriter = new StreamWriter(text, append: true, Utf8WithoutBom);
				if (flag)
				{
					streamWriter.WriteLine("capturedAtUtc,backend,mode,sourceWidth,sourceHeight,inputWidth,inputHeight,payloadBytes,hasFace,clientPrepareMs,decodeMs,faceBoxMs,inferenceMs,parametersMs,sparseMs,denseMs,poseMs,serializeMs,sidecarTotalMs,roundTripMs,parseMs,endToEndMs,status");
				}
				foreach (VisionPipelineDiagnostics item2 in item)
				{
					streamWriter.WriteLine(ToCsv(item2));
				}
			}
		}
		catch
		{
			lock (_pendingLock)
			{
				KeyValuePair<string, VisionPipelineDiagnostics>[] array2 = array;
				for (int num = 0; num < array2.Length; num++)
				{
					KeyValuePair<string, VisionPipelineDiagnostics> keyValuePair = array2[num];
					if (_pending.Count < 64)
					{
						_pending.TryAdd(keyValuePair.Key, keyValuePair.Value);
						continue;
					}
					break;
				}
			}
		}
	}

	private bool HasPendingSamples()
	{
		lock (_pendingLock)
		{
			return _pending.Count > 0;
		}
	}

	private static string ToCsv(VisionPipelineDiagnostics sample)
	{
		_003C_003Ey__InlineArray23<string> buffer = default(_003C_003Ey__InlineArray23<string>);
		buffer[0] = Csv(sample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));
		buffer[1] = Csv(sample.Backend);
		buffer[2] = Csv(sample.Mode);
		buffer[3] = sample.SourceWidth.ToString(CultureInfo.InvariantCulture);
		buffer[4] = sample.SourceHeight.ToString(CultureInfo.InvariantCulture);
		buffer[5] = sample.InputWidth.ToString(CultureInfo.InvariantCulture);
		buffer[6] = sample.InputHeight.ToString(CultureInfo.InvariantCulture);
		buffer[7] = sample.EncodedPayloadBytes.ToString(CultureInfo.InvariantCulture);
		buffer[8] = (sample.HasFace ? "true" : "false");
		buffer[9] = Number(sample.ClientPrepareMilliseconds);
		buffer[10] = Stage(sample, "decode");
		buffer[11] = Stage(sample, "face_box", "faceBox");
		buffer[12] = Stage(sample, "inference");
		buffer[13] = Stage(sample, "parameters");
		buffer[14] = Stage(sample, "sparse");
		buffer[15] = Stage(sample, "dense");
		buffer[16] = Stage(sample, "pose");
		buffer[17] = Stage(sample, "serialize");
		buffer[18] = Stage(sample, "total");
		buffer[19] = Number(sample.SidecarRoundTripMilliseconds);
		buffer[20] = Number(sample.ClientParseMilliseconds);
		buffer[21] = Number(sample.EndToEndMilliseconds);
		buffer[22] = Csv(sample.Status);
		return string.Join(",", (ReadOnlySpan<string?>)buffer);
	}

	private static string Stage(VisionPipelineDiagnostics sample, params string[] names)
	{
		foreach (string key in names)
		{
			if (sample.SidecarStagesMilliseconds.TryGetValue(key, out var value))
			{
				return Number(value);
			}
		}
		return "";
	}

	private static string Number(double value)
	{
		if (!double.IsFinite(value))
		{
			return "";
		}
		return value.ToString("0.####", CultureInfo.InvariantCulture);
	}

	private static string Csv(string value)
	{
		return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
	}
}
