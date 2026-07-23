using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelStore
{
	private readonly record struct FileStamp(bool Exists, long Length, long LastWriteTimeUtcTicks)
	{
		public static FileStamp Read(string path)
		{
			if (!File.Exists(path))
			{
				return default(FileStamp);
			}
			FileInfo fileInfo = new FileInfo(path);
			return new FileStamp(Exists: true, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
		}
	}

	public const string JsonFileName = "avatar_model.json";

	public const string HtmlFileName = "avatar_model_progress.html";

	private const string ViewerVersion = "avatar-model-viewer-v4-multiframe-identity-mapping";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly object _cacheLock = new object();

	private string _cachedPath = "";

	private FileStamp _cachedStamp;

	private AvatarModel? _cachedModel;

	public string Write(string folder, AvatarModel model)
	{
		Directory.CreateDirectory(folder);
		string jsonPath = GetJsonPath(folder);
		AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
		UpdateCache(jsonPath, model);
		WriteViewer(folder, model);
		return jsonPath;
	}

	public string WriteViewer(string folder, AvatarModel model)
	{
		Directory.CreateDirectory(folder);
		string htmlPath = GetHtmlPath(folder);
		AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(model), Encoding.UTF8);
		return htmlPath;
	}

	public string EnsureViewer(string folder, AvatarModel model)
	{
		string htmlPath = GetHtmlPath(folder);
		if (!IsCurrentViewer(htmlPath))
		{
			return WriteViewer(folder, model);
		}
		return htmlPath;
	}

	public AvatarModel? Read(string folder)
	{
		string jsonPath = GetJsonPath(folder);
		lock (_cacheLock)
		{
			FileStamp fileStamp = FileStamp.Read(jsonPath);
			if (!fileStamp.Exists)
			{
				ClearCache();
				return null;
			}
			if (_cachedModel != null && string.Equals(_cachedPath, jsonPath, StringComparison.OrdinalIgnoreCase) && _cachedStamp == fileStamp)
			{
				return _cachedModel;
			}
			try
			{
				AvatarModel avatarModel = JsonSerializer.Deserialize<AvatarModel>(File.ReadAllText(jsonPath), JsonOptions);
				if (avatarModel != null)
				{
					UpdateCache(jsonPath, avatarModel);
				}
				return avatarModel;
			}
			catch
			{
				ClearCache();
				return null;
			}
		}
	}

	public static string GetJsonPath(string folder)
	{
		return Path.Combine(folder, "avatar_model.json");
	}

	public static string GetHtmlPath(string folder)
	{
		return Path.Combine(folder, "avatar_model_progress.html");
	}

	private static string BuildHtml(AvatarModel model)
	{
		string value = JsonSerializer.Serialize(CreateViewerModel(model), JsonOptions);
		string value2 = ((model.Findings.Count == 0) ? "<li>No model findings yet.</li>" : string.Concat(model.Findings.Select((string finding) => "<li>" + H(finding) + "</li>")));
		string value3 = ((model.Identity.RegionConfidence.Count == 0) ? "<tr><td colspan=\"3\" class=\"muted\">Waiting for region confidence.</td></tr>" : string.Concat(model.Identity.RegionConfidence.Select((AvatarRegionConfidence region) => $"<tr><td>{H(region.Region)}</td><td>{region.ConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{H(region.Basis)}</td></tr>")));
		string value4 = ((model.Expression.Buckets.Count == 0) ? "<tr><td colspan=\"4\" class=\"muted\">Waiting for expression samples.</td></tr>" : string.Concat(model.Expression.Buckets.Select((AvatarExpressionBucket bucket) => $"<tr><td>{H(bucket.Name)}</td><td>{bucket.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{bucket.AverageEnergyPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{H(bucket.Meaning)}</td></tr>")));
		string value5 = ((model.RecentSamples.Count == 0) ? "<tr><td colspan=\"7\" class=\"muted\">Waiting for accepted reconstruction observations.</td></tr>" : string.Concat(model.RecentSamples.Select(delegate(AvatarModelSampleSummary sample)
		{
			string value6 = (string.IsNullOrWhiteSpace(sample.SourceImageUri) ? "<span class=\"muted\">Unavailable</span>" : ("<a href=\"" + H(sample.SourceImageUri) + "\">Photo</a>"));
			return $"<tr><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{sample.WeightPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{sample.ReconstructionConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{sample.ARotationAroundXDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.BRotationAroundYDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.CRotationAroundZDegrees.ToString("0.#", CultureInfo.InvariantCulture)}</td><td>{sample.VertexCount.ToString("n0", CultureInfo.InvariantCulture)}</td><td>{value6}</td><td>{H(sample.IdentityUse)}</td></tr>";
		})));
		return $"<!doctype html>\r\n<html lang=\"en\">\r\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"avatar-builder-viewer\" content=\"{"avatar-model-viewer-v4-multiframe-identity-mapping"}\">\n<meta http-equiv=\"refresh\" content=\"30\">\n<title>Avatar Model Progress</title>\r\n<style>\r\n:root{{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--mesh:#66d9ff;--edge:#1f6f90;--good:#80e0a4;--warn:#ffd27a}}\r\n*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}}main{{display:grid;grid-template-columns:minmax(420px,1fr) minmax(360px,520px);gap:16px;padding:16px}}.stage,.panel{{border:1px solid var(--line);background:var(--panel);border-radius:6px}}.stage{{min-height:620px;padding:12px;display:grid;grid-template-rows:minmax(500px,1fr) auto;gap:12px}}.viewer{{position:relative;min-height:500px}}canvas{{width:100%;height:100%;min-height:500px;display:block;border:1px solid #193149;background:#061019;cursor:grab;touch-action:none}}canvas[data-dragging=true]{{cursor:grabbing}}.overlay{{position:absolute;left:12px;top:12px;max-width:min(640px,calc(100% - 24px));padding:8px 10px;background:rgba(5,11,16,.78);border:1px solid #193149;color:var(--muted);pointer-events:none}}.controls{{display:flex;flex-wrap:wrap;gap:8px}}.panel{{padding:14px;overflow-wrap:anywhere}}h1{{margin:0 0 4px;font-size:22px}}h2{{margin:18px 0 8px;font-size:17px}}.muted{{color:var(--muted)}}.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:8px}}.metric{{background:#07121c;border:1px solid #1d2c38;padding:10px}}.label{{color:var(--muted);font-size:12px;text-transform:uppercase}}.value{{font-size:18px;font-weight:700}}.good{{color:var(--good)}}.warn{{color:var(--warn)}}button,.button{{background:#102033;color:var(--text);border:1px solid #37506a;padding:8px 10px;min-height:34px;text-decoration:none;display:inline-flex;align-items:center}}button[aria-pressed=true]{{background:#1c405c;border-color:#65c8ff}}table{{width:100%;border-collapse:collapse}}td,th{{border-bottom:1px solid #1c3042;padding:6px 4px;text-align:left;vertical-align:top}}th{{color:var(--muted);font-weight:600}}@media(max-width:980px){{main{{grid-template-columns:1fr}}}}\r\n</style>\r\n</head>\r\n<body>\r\n<main>\r\n  <section class=\"stage\" aria-label=\"Accumulated avatar model\">\r\n    <div class=\"viewer\">\r\n      <canvas id=\"avatarModelCanvas\" title=\"Drag to rotate. Mouse wheel zooms. Double-click resets.\"></canvas>\r\n      <div class=\"overlay\" id=\"avatarOverlay\">Waiting for avatar model data.</div>\r\n    </div>\r\n    <div class=\"controls\">\r\n      <button type=\"button\" id=\"togglePoints\" aria-pressed=\"true\">Points</button>\r\n      <button type=\"button\" id=\"toggleEdges\" aria-pressed=\"true\">Wireframe</button>\r\n      <button type=\"button\" id=\"toggleAutoRotate\" aria-pressed=\"false\">Auto Rotate</button>\r\n      <button type=\"button\" id=\"resetView\">Reset View</button>\r\n      <a class=\"button\" href=\"{"avatar_model_regression.html"}\">Open Regression Audit</a>\r\n    </div>\r\n  </section>\r\n  <aside class=\"panel\">\r\n    <h1>Avatar Model Progress</h1>\r\n    <p class=\"muted\">Auto-refreshes every 30 seconds. Last updated {H(model.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}.</p>\r\n    <p>{H(model.Status)}</p>\r\n    <p class=\"muted\">{H(model.StoragePolicy)}</p>\r\n    <div class=\"grid\">\r\n      <div class=\"metric\"><div class=\"label\">Identity samples</div><div class=\"value\">{model.Identity.SampleCount.ToString(CultureInfo.InvariantCulture)}</div></div>\n      <div class=\"metric\"><div class=\"label\">Convergence</div><div class=\"value\">{model.Convergence.ScorePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Maturity</div><div class=\"value\">{H(model.Convergence.Label)}</div></div>\n      <div class=\"metric\"><div class=\"label\">Identity confidence</div><div class=\"value\">{model.Identity.ConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Dense vertices</div><div class=\"value\">{model.Identity.DenseVertexCount.ToString("n0", CultureInfo.InvariantCulture)}</div></div>\n      <div class=\"metric\"><div class=\"label\">Identity fit RMSE</div><div class=\"value\">{model.Identity.MappingFinalLandmarkRmsePercent.ToString("0.##", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Last fit improvement</div><div class=\"value\">{model.Identity.MappingImprovementPercent.ToString("0.##", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Mapped from generic</div><div class=\"value\">{model.Identity.GenericIdentityDisplacementPercent.ToString("0.##", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Pose coverage</div><div class=\"value\">{model.PoseCoverage.CoveragePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Expression samples</div><div class=\"value\">{model.Expression.SampleCount.ToString(CultureInfo.InvariantCulture)}</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Expression energy</div><div class=\"value\">{model.Expression.ExpressionEnergyPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div></div>\n      <div class=\"metric\"><div class=\"label\">Storage revision</div><div class=\"value\">{model.SourceObservationRevision.ToString(CultureInfo.InvariantCulture)}</div></div>\n    </div>\n    <p class=\"muted\">{H(model.Convergence.Basis)}</p>\n    <h2>Coverage</h2>\r\n    <table>\r\n      <tr><th>Summary</th><td>{H(model.PoseCoverage.Summary)}</td></tr>\r\n      <tr><th>B turns</th><td>left {model.PoseCoverage.LeftBTurnSampleCount}, right {model.PoseCoverage.RightBTurnSampleCount}, range {model.PoseCoverage.BRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)} deg</td></tr>\r\n      <tr><th>A tilt</th><td>negative {model.PoseCoverage.NegativeATiltSampleCount}, positive {model.PoseCoverage.PositiveATiltSampleCount}, range {model.PoseCoverage.ARangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)} deg</td></tr>\r\n      <tr><th>C tilt</th><td>negative {model.PoseCoverage.NegativeCTiltSampleCount}, positive {model.PoseCoverage.PositiveCTiltSampleCount}, range {model.PoseCoverage.CRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)} deg</td></tr>\r\n      <tr><th>Z scale</th><td>close {model.PoseCoverage.CloseZSampleCount}, far {model.PoseCoverage.FarZSampleCount}, range {model.PoseCoverage.ZScaleRangePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td></tr>\r\n    </table>\r\n    <h2>Findings</h2>\r\n    <ul>{value2}</ul>\r\n    <h2>Region Confidence</h2>\r\n    <table><tr><th>Region</th><th>Confidence</th><th>Basis</th></tr>{value3}</table>\r\n    <h2>Expression Model</h2>\r\n    <table><tr><th>Bucket</th><th>Samples</th><th>Energy</th><th>Meaning</th></tr>{value4}</table>\r\n    <h2>Recent Stored Observations</h2>\r\n    <table><tr><th>Time</th><th>Weight</th><th>Reconstruction</th><th>A/B/C</th><th>Vertices</th><th>Photo</th><th>Use</th></tr>{value5}</table>\n  </aside>\r\n</main>\r\n<script type=\"application/json\" id=\"avatarModelJson\">{value}</script>\r\n<script>\r\n(() => {{\r\n  const model = JSON.parse(document.getElementById('avatarModelJson')?.textContent || '{{}}');\r\n  const identity = model.identity || model.Identity || {{}};\r\n  const mappedPoints = identity.mappedDenseVertices || identity.MappedDenseVertices || [];\n  const averagedPoints = identity.meanDenseVertices || identity.MeanDenseVertices || [];\n  const points = mappedPoints.length ? mappedPoints : averagedPoints;\n  const edges = identity.topologyEdges || identity.TopologyEdges || [];\r\n  const canvas = document.getElementById('avatarModelCanvas');\r\n  const overlay = document.getElementById('avatarOverlay');\r\n  const ctx = canvas?.getContext('2d');\r\n  if (!canvas || !ctx) return;\r\n  const view = {{ points: true, edges: true, autoRotate: false, yaw: -0.35, pitch: -0.10, zoom: 0.82 }};\r\n  let dragging = false;\r\n  let last = null;\r\n  let animation = null;\r\n\r\n  document.getElementById('togglePoints')?.addEventListener('click', event => toggle(event.currentTarget, 'points'));\r\n  document.getElementById('toggleEdges')?.addEventListener('click', event => toggle(event.currentTarget, 'edges'));\r\n  document.getElementById('toggleAutoRotate')?.addEventListener('click', event => {{\r\n    toggle(event.currentTarget, 'autoRotate');\r\n    schedule();\r\n  }});\r\n  document.getElementById('resetView')?.addEventListener('click', () => {{\r\n    view.yaw = -0.35;\r\n    view.pitch = -0.10;\r\n    view.zoom = 0.82;\r\n    draw();\r\n  }});\r\n  canvas.addEventListener('pointerdown', event => {{\r\n    dragging = true;\r\n    last = {{ x: event.clientX, y: event.clientY }};\r\n    canvas.dataset.dragging = 'true';\r\n    canvas.setPointerCapture(event.pointerId);\r\n  }});\r\n  canvas.addEventListener('pointermove', event => {{\r\n    if (!dragging || !last) return;\r\n    view.yaw += (event.clientX - last.x) * 0.008;\r\n    view.pitch = Math.max(-1.1, Math.min(1.1, view.pitch + (event.clientY - last.y) * 0.006));\r\n    last = {{ x: event.clientX, y: event.clientY }};\r\n    draw();\r\n  }});\r\n  canvas.addEventListener('pointerup', release);\r\n  canvas.addEventListener('pointercancel', release);\r\n  canvas.addEventListener('wheel', event => {{\r\n    event.preventDefault();\r\n    view.zoom = Math.max(0.42, Math.min(3.2, view.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));\r\n    draw();\r\n  }}, {{ passive: false }});\r\n  canvas.addEventListener('dblclick', () => {{\r\n    view.yaw = -0.35;\r\n    view.pitch = -0.10;\r\n    view.zoom = 0.82;\r\n    draw();\r\n  }});\r\n  new ResizeObserver(resize).observe(canvas);\r\n  resize();\r\n\r\n  function toggle(button, key) {{\r\n    view[key] = !view[key];\r\n    button.setAttribute('aria-pressed', view[key] ? 'true' : 'false');\r\n    draw();\r\n  }}\r\n\r\n  function release() {{\r\n    dragging = false;\r\n    last = null;\r\n    delete canvas.dataset.dragging;\r\n  }}\r\n\r\n  function resize() {{\r\n    const rect = canvas.getBoundingClientRect();\r\n    const width = Math.max(360, Math.round(rect.width));\r\n    const height = Math.max(420, Math.round(rect.height));\r\n    const scale = window.devicePixelRatio || 1;\r\n    canvas.width = Math.round(width * scale);\r\n    canvas.height = Math.round(height * scale);\r\n    ctx.setTransform(scale, 0, 0, scale, 0, 0);\r\n    draw();\r\n  }}\r\n\r\n  function schedule() {{\r\n    if (animation) cancelAnimationFrame(animation);\r\n    if (!view.autoRotate) {{\r\n      animation = null;\r\n      return;\r\n    }}\n\n    const step = () => {{\n      view.yaw += 0.006;\r\n      draw();\r\n      animation = requestAnimationFrame(step);\r\n    }};\r\n    animation = requestAnimationFrame(step);\r\n  }}\r\n\r\n  function draw() {{\n    const rect = canvas.getBoundingClientRect();\r\n    ctx.clearRect(0, 0, rect.width, rect.height);\r\n    ctx.fillStyle = '#061019';\r\n    ctx.fillRect(0, 0, rect.width, rect.height);\r\n    drawGrid(rect);\r\n    const normalized = normalize(points);\r\n    const byIndex = new Map(normalized.map(point => [point.index, point]));\r\n    if (view.edges) drawEdges(edges, byIndex, rect);\r\n    if (view.points) drawPoints(normalized, rect);\r\n    if (overlay) {{\n      const fullVertexCount = identity.fullDenseVertexCount ?? identity.FullDenseVertexCount ?? points.length;\n      const fullEdgeCount = identity.fullTopologyEdgeCount ?? identity.FullTopologyEdgeCount ?? edges.length;\n      const mappingStatus = identity.mappingStatus ?? identity.MappingStatus ?? 'identity mapping waiting';\n      const geometryLabel = mappedPoints.length ? 'current recurrent model' : 'raw observation accumulator';\n      overlay.innerHTML = `<strong>${{escape(model.subjectDisplayName || model.SubjectDisplayName || 'Avatar model')}}</strong><br>${{escape(model.status || model.Status || 'waiting')}}<br>${{Number(fullVertexCount).toLocaleString()}} ${{geometryLabel}} vertices | ${{Number(fullEdgeCount).toLocaleString()}} topology edges<br>${{points.length.toLocaleString()}} display points | ${{edges.length.toLocaleString()}} display edges<br>identity confidence ${{format(identity.confidencePercent ?? identity.ConfidencePercent)}}% | samples ${{identity.sampleCount ?? identity.SampleCount ?? 0}}<br>fit ${{format(identity.mappingFinalLandmarkRmsePercent ?? identity.MappingFinalLandmarkRmsePercent)}}% RMSE | improvement ${{format(identity.mappingImprovementPercent ?? identity.MappingImprovementPercent)}}%<br>${{escape(mappingStatus)}}<br>${{escape((model.poseCoverage || model.PoseCoverage || {{}}).summary || (model.poseCoverage || model.PoseCoverage || {{}}).Summary || 'coverage waiting')}}`;\n    }}\n  }}\r\n\r\n  function normalize(rawPoints) {{\r\n    const raw = rawPoints.map(point => ({{\r\n      index: point.index ?? point.Index,\r\n      x: point.x ?? point.X,\r\n      y: point.y ?? point.Y,\r\n      z: point.z ?? point.Z\r\n    }})).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z));\r\n    if (raw.length === 0) return [];\r\n    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;\r\n    for (const point of raw) {{\r\n      minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x);\r\n      minY = Math.min(minY, point.y); maxY = Math.max(maxY, point.y);\r\n      minZ = Math.min(minZ, point.z); maxZ = Math.max(maxZ, point.z);\r\n    }}\r\n    const centerX = (minX + maxX) / 2;\r\n    const centerY = (minY + maxY) / 2;\r\n    const centerZ = (minZ + maxZ) / 2;\r\n    const scale = 1 / Math.max(0.001, maxX - minX, maxY - minY);\r\n    return raw.map(point => ({{\r\n      index: point.index,\r\n      x: (point.x - centerX) * scale,\r\n      y: (point.y - centerY) * scale,\r\n      z: (point.z - centerZ) * scale * 0.70\r\n    }}));\r\n  }}\r\n\r\n  function drawPoints(normalized, rect) {{\r\n    const projected = normalized.map(point => ({{ point, projected: project(point, rect) }})).sort((a, b) => a.projected.z - b.projected.z);\r\n    for (const item of projected) {{\r\n      ctx.globalAlpha = 0.42;\r\n      ctx.fillStyle = '#66d9ff';\r\n      ctx.beginPath();\r\n      ctx.arc(item.projected.x, item.projected.y, Math.max(0.45, 0.95 * item.projected.scale), 0, Math.PI * 2);\r\n      ctx.fill();\r\n    }}\r\n    ctx.globalAlpha = 1;\r\n  }}\r\n\r\n  function drawEdges(rawEdges, byIndex, rect) {{\r\n    const projected = rawEdges.map(edge => {{\r\n      const from = byIndex.get(edge.fromIndex ?? edge.FromIndex);\r\n      const to = byIndex.get(edge.toIndex ?? edge.ToIndex);\r\n      if (!from || !to) return null;\r\n      const a = project(from, rect);\r\n      const b = project(to, rect);\r\n      return {{ a, b, z: (a.z + b.z) / 2 }};\r\n    }}).filter(Boolean).sort((a, b) => a.z - b.z);\r\n    for (const edge of projected) {{\r\n      ctx.globalAlpha = 0.20;\r\n      ctx.strokeStyle = '#66d9ff';\r\n      ctx.lineWidth = Math.max(0.28, 0.46 * ((edge.a.scale + edge.b.scale) / 2));\r\n      ctx.beginPath();\r\n      ctx.moveTo(edge.a.x, edge.a.y);\r\n      ctx.lineTo(edge.b.x, edge.b.y);\r\n      ctx.stroke();\r\n    }}\r\n    ctx.globalAlpha = 1;\r\n  }}\r\n\r\n  function project(point, rect) {{\r\n    const cosY = Math.cos(view.yaw);\r\n    const sinY = Math.sin(view.yaw);\r\n    const cosP = Math.cos(view.pitch);\r\n    const sinP = Math.sin(view.pitch);\r\n    const x1 = point.x * cosY + point.z * sinY;\r\n    const z1 = -point.x * sinY + point.z * cosY;\r\n    const y1 = point.y * cosP - z1 * sinP;\r\n    const z2 = point.y * sinP + z1 * cosP;\r\n    const depth = 1.7 + z2;\r\n    const zoom = Math.min(rect.width, rect.height) * 0.70 * view.zoom / Math.max(0.35, depth);\r\n    return {{\r\n      x: rect.width * 0.5 + x1 * zoom,\r\n      // Canonical backend geometry is Y-up; browser canvas coordinates are Y-down.\n      y: rect.height * 0.52 - y1 * zoom,\n      z: z2,\r\n      scale: Math.max(0.35, Math.min(1.8, 1 / Math.max(0.35, depth)))\r\n    }};\r\n  }}\r\n\r\n  function drawGrid(rect) {{\r\n    ctx.save();\r\n    ctx.strokeStyle = '#132638';\r\n    ctx.lineWidth = 1;\r\n    for (let x = rect.width * 0.14; x <= rect.width * 0.86; x += rect.width * 0.09) {{\r\n      ctx.beginPath(); ctx.moveTo(x, rect.height * 0.12); ctx.lineTo(x, rect.height * 0.88); ctx.stroke();\r\n    }}\r\n    for (let y = rect.height * 0.14; y <= rect.height * 0.88; y += rect.height * 0.09) {{\r\n      ctx.beginPath(); ctx.moveTo(rect.width * 0.12, y); ctx.lineTo(rect.width * 0.88, y); ctx.stroke();\r\n    }}\r\n    ctx.restore();\r\n  }}\r\n\r\n  function format(value) {{\r\n    return Number.isFinite(Number(value)) ? Number(value).toFixed(1).replace(/\\.0$/, '') : '--';\r\n  }}\r\n\r\n  function escape(value) {{\r\n    return String(value ?? '').replace(/[&<>\"']/g, char => ({{ '&': '&amp;', '<': '&lt;', '>': '&gt;', '\"': '&quot;', \"'\": '&#39;' }}[char]));\r\n  }}\r\n}})();\r\n</script>\r\n</body>\r\n</html>";
	}

	private static object CreateViewerModel(AvatarModel model)
	{
		List<MeshTopologyEdge> topologyEdges = model.Identity.TopologyEdges.Where((MeshTopologyEdge _, int index) => index % 16 == 0).ToList();
		List<FaceMeshLandmarkPoint> list = ((model.Identity.MappedDenseVertices.Count > 0) ? model.Identity.MappedDenseVertices : model.Identity.MeanDenseVertices);
		List<FaceMeshLandmarkPoint> list2 = list.ToList();
		return new
		{
			SubjectDisplayName = model.SubjectDisplayName,
			Status = model.Status,
			PoseCoverage = model.PoseCoverage,
			Identity = new
			{
				ConfidencePercent = model.Identity.ConfidencePercent,
				SampleCount = model.Identity.SampleCount,
				MappingStatus = model.Identity.MappingStatus,
				MappingFinalLandmarkRmsePercent = model.Identity.MappingFinalLandmarkRmsePercent,
				MappingImprovementPercent = model.Identity.MappingImprovementPercent,
				MeanDenseVertices = list2,
				MappedDenseVertices = ((model.Identity.MappedDenseVertices.Count > 0) ? list2 : new List<FaceMeshLandmarkPoint>()),
				TopologyEdges = topologyEdges,
				FullDenseVertexCount = list.Count,
				FullTopologyEdgeCount = model.Identity.TopologyEdges.Count
			}
		};
	}

	private static bool IsCurrentViewer(string path)
	{
		if (!File.Exists(path))
		{
			return false;
		}
		try
		{
			using StreamReader streamReader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			char[] array = new char[2048];
			int num = streamReader.ReadBlock(array, 0, array.Length);
			return num > 0 && new string(array, 0, num).Contains("avatar-model-viewer-v4-multiframe-identity-mapping", StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private void UpdateCache(string path, AvatarModel model)
	{
		lock (_cacheLock)
		{
			_cachedPath = path;
			_cachedStamp = FileStamp.Read(path);
			_cachedModel = model;
		}
	}

	private void ClearCache()
	{
		_cachedPath = "";
		_cachedStamp = default(FileStamp);
		_cachedModel = null;
	}

	private static string H(string? value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}
}
