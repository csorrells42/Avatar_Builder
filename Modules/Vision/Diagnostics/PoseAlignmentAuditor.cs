using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Onnx;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class PoseAlignmentAuditor
{
	public const int CurrentPoseConventionVersion = 2;

	public const string HtmlFileName = "pose_alignment_audit.html";

	public const string JsonFileName = "pose_alignment_summary.json";

	public const string CsvFileName = "pose_alignment_samples.csv";

	private const int MinimumSampleCount = 30;

	private const int MaximumSampleCount = 600;

	private const double MinimumAxisRangeDegrees = 12.0;

	private const double MinimumAbsoluteCorrelation = 0.8;

	private const double MaximumP95ErrorDegrees = 8.0;

	private const double MinimumNovelPoseDeltaDegrees = 1.25;

	private static readonly TimeSpan MaximumDuplicatePoseInterval = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan ReportWriteInterval = TimeSpan.FromSeconds(5L);

	private static readonly JsonSerializerOptions ReportJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private readonly object _sync = new object();

	private readonly List<PoseAlignmentSample> _samples = new List<PoseAlignmentSample>();

	private string _folder = "";

	private PoseAlignmentSummary _summary = PoseAlignmentSummary.Waiting;

	private DateTime _lastReportWriteAtUtc = DateTime.MinValue;

	public PoseAlignmentSummary CurrentSummary
	{
		get
		{
			lock (_sync)
			{
				return _summary;
			}
		}
	}

	public void SetOutputRoot(string outputRoot)
	{
		if (string.IsNullOrWhiteSpace(outputRoot))
		{
			return;
		}
		lock (_sync)
		{
			_folder = Path.Combine(Path.GetFullPath(outputRoot), "Benchmarks");
			Directory.CreateDirectory(_folder);
			ArchiveLegacySamples(Path.Combine(_folder, "pose_alignment_samples.csv"));
			_samples.Clear();
			_samples.AddRange(ReadSamples(Path.Combine(_folder, "pose_alignment_samples.csv")).TakeLast(600));
			_summary = BuildSummary(_samples);
			_lastReportWriteAtUtc = DateTime.MinValue;
			WriteReportLocked();
		}
	}

	public PoseAlignmentSummary Record(DateTime capturedAtUtc, PoseAngles mediaPipe, ThreeDdfaOnnxSidecarPose threeDdfa)
	{
		if (!mediaPipe.IsFinite || !double.IsFinite(threeDdfa.ARotationAroundXDegrees) || !double.IsFinite(threeDdfa.BRotationAroundYDegrees) || !double.IsFinite(threeDdfa.CRotationAroundZDegrees))
		{
			return CurrentSummary;
		}
		lock (_sync)
		{
			PoseAlignmentSample poseAlignmentSample = new PoseAlignmentSample
			{
				CapturedAtUtc = capturedAtUtc,
				PoseConventionVersion = 2,
				MediaPipeA = mediaPipe.A,
				MediaPipeB = mediaPipe.B,
				MediaPipeC = mediaPipe.C,
				ThreeDdfaA = threeDdfa.ARotationAroundXDegrees,
				ThreeDdfaB = threeDdfa.BRotationAroundYDegrees,
				ThreeDdfaC = threeDdfa.CRotationAroundZDegrees
			};
			if (!ShouldRetainSample(poseAlignmentSample))
			{
				return _summary;
			}
			_samples.Add(poseAlignmentSample);
			if (_samples.Count > 600)
			{
				_samples.RemoveRange(0, _samples.Count - 600);
			}
			_summary = BuildSummary(_samples);
			if (DateTime.UtcNow - _lastReportWriteAtUtc >= ReportWriteInterval)
			{
				WriteReportLocked();
			}
			return _summary;
		}
	}

	private bool ShouldRetainSample(PoseAlignmentSample candidate)
	{
		if (_samples.Count < 30)
		{
			return true;
		}
		List<PoseAlignmentSample> samples = _samples;
		PoseAlignmentSample poseAlignmentSample = samples[samples.Count - 1];
		if (candidate.CapturedAtUtc - poseAlignmentSample.CapturedAtUtc >= MaximumDuplicatePoseInterval)
		{
			return true;
		}
		return MaximumPoseDelta(candidate, poseAlignmentSample) >= 1.25;
	}

	private static double MaximumPoseDelta(PoseAlignmentSample current, PoseAlignmentSample previous)
	{
		return new double[6]
		{
			Math.Abs(current.MediaPipeA - previous.MediaPipeA),
			Math.Abs(current.MediaPipeB - previous.MediaPipeB),
			Math.Abs(current.MediaPipeC - previous.MediaPipeC),
			Math.Abs(current.ThreeDdfaA - previous.ThreeDdfaA),
			Math.Abs(current.ThreeDdfaB - previous.ThreeDdfaB),
			Math.Abs(current.ThreeDdfaC - previous.ThreeDdfaC)
		}.Max();
	}

	public string GetHtmlPath()
	{
		lock (_sync)
		{
			return string.IsNullOrWhiteSpace(_folder) ? "" : Path.Combine(_folder, "pose_alignment_audit.html");
		}
	}

	public void EnsureReport()
	{
		lock (_sync)
		{
			WriteReportLocked();
		}
	}

	private static PoseAlignmentSummary BuildSummary(IReadOnlyList<PoseAlignmentSample> samples)
	{
		PoseAxisAlignment poseAxisAlignment = FitAxis("A around X", samples.Select((PoseAlignmentSample sample) => (MediaPipeA: sample.MediaPipeA, ThreeDdfaA: sample.ThreeDdfaA)).ToList());
		PoseAxisAlignment poseAxisAlignment2 = FitAxis("B around Y", samples.Select((PoseAlignmentSample sample) => (MediaPipeB: sample.MediaPipeB, ThreeDdfaB: sample.ThreeDdfaB)).ToList());
		PoseAxisAlignment poseAxisAlignment3 = FitAxis("C around Z", samples.Select((PoseAlignmentSample sample) => (MediaPipeC: sample.MediaPipeC, ThreeDdfaC: sample.ThreeDdfaC)).ToList());
		PoseAxisAlignment[] array = new PoseAxisAlignment[3] { poseAxisAlignment, poseAxisAlignment2, poseAxisAlignment3 };
		bool flag = array.All((PoseAxisAlignment axis) => axis.Ready);
		string guidance = (flag ? "A/B/C agreement is measured and within tolerance. The comparison is ready for diagnostic review." : BuildGuidance(array, samples.Count));
		return new PoseAlignmentSummary
		{
			UpdatedAtUtc = DateTime.UtcNow,
			PoseConventionVersion = 2,
			SampleCount = samples.Count,
			ReadyForComparison = flag,
			Status = (flag ? "aligned" : "collecting alignment evidence"),
			Guidance = guidance,
			A = poseAxisAlignment,
			B = poseAxisAlignment2,
			C = poseAxisAlignment3
		};
	}

	private static PoseAxisAlignment FitAxis(string name, IReadOnlyList<(double X, double Y)> pairs)
	{
		if (pairs.Count == 0)
		{
			return new PoseAxisAlignment
			{
				Name = name,
				Status = "waiting for paired samples"
			};
		}
		double meanX = pairs.Average(((double X, double Y) pair) => pair.X);
		double meanY = pairs.Average(((double X, double Y) pair) => pair.Y);
		double num = pairs.Sum(((double X, double Y) pair) => Math.Pow(pair.X - meanX, 2.0));
		double num2 = pairs.Sum(((double X, double Y) pair) => Math.Pow(pair.Y - meanY, 2.0));
		double num3 = pairs.Sum(((double X, double Y) pair) => (pair.X - meanX) * (pair.Y - meanY));
		double scale = ((num <= 1E-06) ? 0.0 : (num3 / num));
		double offset = meanY - scale * meanX;
		double value = ((num <= 1E-06 || num2 <= 1E-06) ? 0.0 : (num3 / Math.Sqrt(num * num2)));
		List<double> list = pairs.Select(((double X, double Y) pair) => Math.Abs(scale * pair.X + offset - pair.Y)).Order().ToList();
		double num4 = pairs.Max(((double X, double Y) pair) => pair.X) - pairs.Min(((double X, double Y) pair) => pair.X);
		double num5 = pairs.Max(((double X, double Y) pair) => pair.Y) - pairs.Min(((double X, double Y) pair) => pair.Y);
		double num6 = Percentile(list, 0.95);
		double value2 = list.Average();
		bool num7 = pairs.Count >= 30;
		bool flag = num4 >= 12.0 && num5 >= 12.0;
		bool flag2 = Math.Abs(value) >= 0.8;
		bool flag3 = num6 <= 8.0;
		double num8 = Math.Abs(scale);
		bool flag4 = num8 >= 0.35 && num8 <= 2.75;
		bool ready = num7 && flag && flag2 && flag3 && flag4;
		string status = ((!num7) ? $"need {30 - pairs.Count} more paired samples" : ((!flag) ? "need more movement around this axis" : ((!flag2 || !flag4) ? "the packages do not yet track this axis consistently" : ((!flag3) ? "residual disagreement is above tolerance" : "aligned within tolerance"))));
		return new PoseAxisAlignment
		{
			Name = name,
			SampleCount = pairs.Count,
			Scale = Round(scale),
			OffsetDegrees = Round(offset),
			Correlation = Round(value),
			MediaPipeRangeDegrees = Round(num4),
			ThreeDdfaRangeDegrees = Round(num5),
			MeanAbsoluteErrorDegrees = Round(value2),
			P95AbsoluteErrorDegrees = Round(num6),
			Ready = ready,
			Status = status
		};
	}

	private static string BuildGuidance(IReadOnlyList<PoseAxisAlignment> axes, int sampleCount)
	{
		if (sampleCount < 30)
		{
			return $"Keep your face visible and slowly move through A, B, and C. {30 - sampleCount} more same-frame pairs are needed before alignment can be judged.";
		}
		IEnumerable<string> values = from axis in axes
			where !axis.Ready
			select axis.Name + ": " + axis.Status;
		return "Alignment is not ready: " + string.Join("; ", values) + ".";
	}

	private void WriteSamplesLocked()
	{
		if (string.IsNullOrWhiteSpace(_folder))
		{
			return;
		}
		string path = Path.Combine(_folder, "pose_alignment_samples.csv");
		StringBuilder stringBuilder = new StringBuilder("capturedAtUtc,poseConventionVersion,mediaPipeA,mediaPipeB,mediaPipeC,threeDdfaA,threeDdfaB,threeDdfaC\n");
		foreach (PoseAlignmentSample sample in _samples)
		{
			InlineArray8<string> buffer = default(InlineArray8<string>);
			buffer[0] = sample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture);
			buffer[1] = sample.PoseConventionVersion.ToString(CultureInfo.InvariantCulture);
			buffer[2] = Number(sample.MediaPipeA);
			buffer[3] = Number(sample.MediaPipeB);
			buffer[4] = Number(sample.MediaPipeC);
			buffer[5] = Number(sample.ThreeDdfaA);
			buffer[6] = Number(sample.ThreeDdfaB);
			buffer[7] = Number(sample.ThreeDdfaC);
			stringBuilder.AppendLine(string.Join(",", (ReadOnlySpan<string?>)buffer));
		}
		AtomicTextFileWriter.WriteAllText(path, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private void WriteReportLocked()
	{
		if (!string.IsNullOrWhiteSpace(_folder))
		{
			Directory.CreateDirectory(_folder);
			string contents = JsonSerializer.Serialize(_summary, ReportJsonOptions);
			WriteSamplesLocked();
			AtomicTextFileWriter.WriteAllText(Path.Combine(_folder, "pose_alignment_summary.json"), contents, Encoding.UTF8);
			AtomicTextFileWriter.WriteAllText(Path.Combine(_folder, "pose_alignment_audit.html"), BuildHtml(_summary, _samples.TakeLast(60).ToList()), Encoding.UTF8);
			_lastReportWriteAtUtc = DateTime.UtcNow;
		}
	}

	private static IReadOnlyList<PoseAlignmentSample> ReadSamples(string path)
	{
		if (!File.Exists(path))
		{
			return Array.Empty<PoseAlignmentSample>();
		}
		List<PoseAlignmentSample> list = new List<PoseAlignmentSample>();
		foreach (string item in File.ReadLines(path).Skip(1))
		{
			string[] array = item.Split(',');
			if (array.Length == 8 && DateTime.TryParse(array[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) && int.TryParse(array[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2) && result2 == 2 && TryNumbers(array.Skip(2), out double[] values))
			{
				list.Add(new PoseAlignmentSample
				{
					CapturedAtUtc = result,
					PoseConventionVersion = result2,
					MediaPipeA = values[0],
					MediaPipeB = values[1],
					MediaPipeC = values[2],
					ThreeDdfaA = values[3],
					ThreeDdfaB = values[4],
					ThreeDdfaC = values[5]
				});
			}
		}
		return list;
	}

	private static void ArchiveLegacySamples(string path)
	{
		if (File.Exists(path) && !(File.ReadLines(path).FirstOrDefault() ?? string.Empty).Contains("poseConventionVersion", StringComparison.OrdinalIgnoreCase))
		{
			string? path2 = Path.GetDirectoryName(path) ?? string.Empty;
			string text = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
			string destFileName = Path.Combine(path2, "pose_alignment_samples.legacy-v1-" + text + ".csv");
			File.Move(path, destFileName);
		}
	}

	private static bool TryNumbers(IEnumerable<string> parts, out double[] values)
	{
		values = parts.Select((string part) => (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) ? double.NaN : result).ToArray();
		if (values.Length == 6)
		{
			return values.All(double.IsFinite);
		}
		return false;
	}

	private static string BuildHtml(PoseAlignmentSummary summary, IReadOnlyList<PoseAlignmentSample> recent)
	{
		string value = string.Concat(new PoseAxisAlignment[3] { summary.A, summary.B, summary.C }.Select((PoseAxisAlignment axis) => $"<tr><td>{H(axis.Name)}</td><td>{axis.SampleCount}</td><td>{axis.MediaPipeRangeDegrees:0.#} / {axis.ThreeDdfaRangeDegrees:0.#}</td><td>{axis.Scale:0.###} x + {axis.OffsetDegrees:0.###}</td><td>{axis.Correlation:0.###}</td><td>{axis.MeanAbsoluteErrorDegrees:0.##} / {axis.P95AbsoluteErrorDegrees:0.##}</td><td class=\"{(axis.Ready ? "good" : "warn")}\">{H(axis.Status)}</td></tr>"));
		string value2 = string.Concat(recent.OrderByDescending((PoseAlignmentSample sample) => sample.CapturedAtUtc).Select(delegate(PoseAlignmentSample sample)
		{
			double num = summary.A.Scale * sample.MediaPipeA + summary.A.OffsetDegrees;
			double num2 = summary.B.Scale * sample.MediaPipeB + summary.B.OffsetDegrees;
			double num3 = summary.C.Scale * sample.MediaPipeC + summary.C.OffsetDegrees;
			return $"<tr><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{sample.MediaPipeA:0.#}/{sample.MediaPipeB:0.#}/{sample.MediaPipeC:0.#}</td><td>{num:0.#}/{num2:0.#}/{num3:0.#}</td><td>{sample.ThreeDdfaA:0.#}/{sample.ThreeDdfaB:0.#}/{sample.ThreeDdfaC:0.#}</td><td>{Math.Abs(num - sample.ThreeDdfaA):0.#}/{Math.Abs(num2 - sample.ThreeDdfaB):0.#}/{Math.Abs(num3 - sample.ThreeDdfaC):0.#}</td></tr>";
		}));
		return $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"refresh\" content=\"5\"><title>A/B/C Alignment Audit</title><style>:root{{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--good:#80e0a4;--warn:#ffd27a}}*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}}main{{max-width:1300px;margin:auto;padding:18px}}.panel{{border:1px solid var(--line);background:var(--panel);padding:14px;margin:0 0 14px;border-radius:6px}}.good{{color:var(--good)}}.warn{{color:var(--warn)}}.muted{{color:var(--muted)}}h1{{margin:0 0 6px;font-size:24px}}table{{width:100%;border-collapse:collapse}}td,th{{border-bottom:1px solid #1c3042;padding:7px 5px;text-align:left;vertical-align:top}}th{{color:var(--muted)}}code{{color:#b9d7ef}}</style></head><body><main><section class=\"panel\"><h1>A/B/C Alignment Audit</h1><p class=\"{(summary.ReadyForComparison ? "good" : "warn")}\"><strong>{H(summary.Status)}</strong></p><p>{H(summary.Guidance)}</p><p class=\"muted\">Pose convention v{summary.PoseConventionVersion} | {summary.SampleCount} exact-frame MediaPipe/3DDFA pairs. MediaPipe is transformed to 3DDFA with the measured equation shown below; raw samples remain in <code>{"pose_alignment_samples.csv"}</code>.</p></section><section class=\"panel\"><table><tr><th>Axis</th><th>Pairs</th><th>Motion range MP / 3DDFA</th><th>Measured transform</th><th>Correlation</th><th>Error mean / p95</th><th>Decision</th></tr>{value}</table></section><section class=\"panel\"><h2>Recent Exact-Frame Pairs</h2><table><tr><th>Time</th><th>MediaPipe raw A/B/C</th><th>MediaPipe calibrated A/B/C</th><th>3DDFA A/B/C</th><th>Absolute error A/B/C</th></tr>{value2}</table></section></main></body></html>";
	}

	private static double Percentile(IReadOnlyList<double> sorted, double fraction)
	{
		if (sorted.Count == 0)
		{
			return 0.0;
		}
		double num = (double)(sorted.Count - 1) * fraction;
		int num2 = (int)Math.Floor(num);
		int num3 = (int)Math.Ceiling(num);
		if (num2 != num3)
		{
			return sorted[num2] + (sorted[num3] - sorted[num2]) * (num - (double)num2);
		}
		return sorted[num2];
	}

	private static double Round(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return Math.Round(value, 6);
	}

	private static string Number(double value)
	{
		return value.ToString("0.######", CultureInfo.InvariantCulture);
	}

	private static string H(string? value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}
}
