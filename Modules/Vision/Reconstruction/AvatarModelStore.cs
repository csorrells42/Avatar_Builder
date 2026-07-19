using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelStore
{
    public const string JsonFileName = "avatar_model.json";
    public const string HtmlFileName = "avatar_model_progress.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Write(string folder, AvatarModel model)
    {
        Directory.CreateDirectory(folder);
        var jsonPath = GetJsonPath(folder);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(folder), BuildHtml(model), Encoding.UTF8);
        return jsonPath;
    }

    public static string GetJsonPath(string folder)
    {
        return Path.Combine(folder, JsonFileName);
    }

    public static string GetHtmlPath(string folder)
    {
        return Path.Combine(folder, HtmlFileName);
    }

    private static string BuildHtml(AvatarModel model)
    {
        var sceneJson = JsonSerializer.Serialize(model, JsonOptions);
        var findings = model.Findings.Count == 0
            ? "<li>No model findings yet.</li>"
            : string.Concat(model.Findings.Select(finding => $"<li>{H(finding)}</li>"));
        var regions = model.Identity.RegionConfidence.Count == 0
            ? "<tr><td colspan=\"3\" class=\"muted\">Waiting for region confidence.</td></tr>"
            : string.Concat(model.Identity.RegionConfidence.Select(region =>
                $"<tr><td>{H(region.Region)}</td><td>{region.ConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{H(region.Basis)}</td></tr>"));
        var buckets = model.Expression.Buckets.Count == 0
            ? "<tr><td colspan=\"4\" class=\"muted\">Waiting for expression samples.</td></tr>"
            : string.Concat(model.Expression.Buckets.Select(bucket =>
                $"<tr><td>{H(bucket.Name)}</td><td>{bucket.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{bucket.AverageEnergyPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{H(bucket.Meaning)}</td></tr>"));
        var sampleRows = model.RecentSamples.Count == 0
            ? "<tr><td colspan=\"6\" class=\"muted\">Waiting for accepted 3DDFA observations.</td></tr>"
            : string.Concat(model.RecentSamples.Select(sample =>
                $"<tr><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{sample.WeightPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{sample.ReconstructionConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{sample.ARotationAroundXDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.BRotationAroundYDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.CRotationAroundZDegrees.ToString("0.#", CultureInfo.InvariantCulture)}</td><td>{sample.VertexCount.ToString("n0", CultureInfo.InvariantCulture)}</td><td>{H(sample.IdentityUse)}</td></tr>"));

        return $$$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="30">
<title>Avatar Model Progress</title>
<style>
:root{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--mesh:#66d9ff;--edge:#1f6f90;--good:#80e0a4;--warn:#ffd27a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}main{display:grid;grid-template-columns:minmax(420px,1fr) minmax(360px,520px);gap:16px;padding:16px}.stage,.panel{border:1px solid var(--line);background:var(--panel);border-radius:6px}.stage{min-height:620px;padding:12px;display:grid;grid-template-rows:minmax(500px,1fr) auto;gap:12px}.viewer{position:relative;min-height:500px}canvas{width:100%;height:100%;min-height:500px;display:block;border:1px solid #193149;background:#061019;cursor:grab;touch-action:none}canvas[data-dragging=true]{cursor:grabbing}.overlay{position:absolute;left:12px;top:12px;max-width:min(640px,calc(100% - 24px));padding:8px 10px;background:rgba(5,11,16,.78);border:1px solid #193149;color:var(--muted);pointer-events:none}.controls{display:flex;flex-wrap:wrap;gap:8px}.panel{padding:14px;overflow-wrap:anywhere}h1{margin:0 0 4px;font-size:22px}h2{margin:18px 0 8px;font-size:17px}.muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:8px}.metric{background:#07121c;border:1px solid #1d2c38;padding:10px}.label{color:var(--muted);font-size:12px;text-transform:uppercase}.value{font-size:18px;font-weight:700}.good{color:var(--good)}.warn{color:var(--warn)}button,.button{background:#102033;color:var(--text);border:1px solid #37506a;padding:8px 10px;min-height:34px;text-decoration:none;display:inline-flex;align-items:center}button[aria-pressed=true]{background:#1c405c;border-color:#65c8ff}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #1c3042;padding:6px 4px;text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}@media(max-width:980px){main{grid-template-columns:1fr}}
</style>
</head>
<body>
<main>
  <section class="stage" aria-label="Accumulated avatar model">
    <div class="viewer">
      <canvas id="avatarModelCanvas" title="Drag to rotate. Mouse wheel zooms. Double-click resets."></canvas>
      <div class="overlay" id="avatarOverlay">Waiting for avatar model data.</div>
    </div>
    <div class="controls">
      <button type="button" id="togglePoints" aria-pressed="true">Points</button>
      <button type="button" id="toggleEdges" aria-pressed="true">Wireframe</button>
      <button type="button" id="toggleAutoRotate" aria-pressed="false">Auto Rotate</button>
      <button type="button" id="resetView">Reset View</button>
      <a class="button" href="{{{AvatarModelHistoryStore.HtmlFileName}}}">Open Regression Audit</a>
    </div>
  </section>
  <aside class="panel">
    <h1>Avatar Model Progress</h1>
    <p class="muted">Auto-refreshes every 30 seconds. Last updated {{{H(model.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}}}.</p>
    <p>{{{H(model.Status)}}}</p>
    <p class="muted">{{{H(model.StoragePolicy)}}}</p>
    <div class="grid">
      <div class="metric"><div class="label">Identity samples</div><div class="value">{{{model.Identity.SampleCount.ToString(CultureInfo.InvariantCulture)}}}</div></div>
      <div class="metric"><div class="label">Identity confidence</div><div class="value">{{{model.Identity.ConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div></div>
      <div class="metric"><div class="label">Dense vertices</div><div class="value">{{{model.Identity.DenseVertexCount.ToString("n0", CultureInfo.InvariantCulture)}}}</div></div>
      <div class="metric"><div class="label">Pose coverage</div><div class="value">{{{model.PoseCoverage.CoveragePercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div></div>
      <div class="metric"><div class="label">Expression samples</div><div class="value">{{{model.Expression.SampleCount.ToString(CultureInfo.InvariantCulture)}}}</div></div>
      <div class="metric"><div class="label">Expression energy</div><div class="value">{{{model.Expression.ExpressionEnergyPercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div></div>
    </div>
    <h2>Coverage</h2>
    <table>
      <tr><th>Summary</th><td>{{{H(model.PoseCoverage.Summary)}}}</td></tr>
      <tr><th>B turns</th><td>left {{{model.PoseCoverage.LeftBTurnSampleCount}}}, right {{{model.PoseCoverage.RightBTurnSampleCount}}}, range {{{model.PoseCoverage.BRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}}} deg</td></tr>
      <tr><th>A tilt</th><td>negative {{{model.PoseCoverage.NegativeATiltSampleCount}}}, positive {{{model.PoseCoverage.PositiveATiltSampleCount}}}, range {{{model.PoseCoverage.ARangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}}} deg</td></tr>
      <tr><th>C tilt</th><td>negative {{{model.PoseCoverage.NegativeCTiltSampleCount}}}, positive {{{model.PoseCoverage.PositiveCTiltSampleCount}}}, range {{{model.PoseCoverage.CRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}}} deg</td></tr>
      <tr><th>Z scale</th><td>close {{{model.PoseCoverage.CloseZSampleCount}}}, far {{{model.PoseCoverage.FarZSampleCount}}}, range {{{model.PoseCoverage.ZScaleRangePercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</td></tr>
    </table>
    <h2>Findings</h2>
    <ul>{{{findings}}}</ul>
    <h2>Region Confidence</h2>
    <table><tr><th>Region</th><th>Confidence</th><th>Basis</th></tr>{{{regions}}}</table>
    <h2>Expression Model</h2>
    <table><tr><th>Bucket</th><th>Samples</th><th>Energy</th><th>Meaning</th></tr>{{{buckets}}}</table>
    <h2>Recent Stored Observations</h2>
    <table><tr><th>Time</th><th>Weight</th><th>3DDFA</th><th>A/B/C</th><th>Vertices</th><th>Use</th></tr>{{{sampleRows}}}</table>
  </aside>
</main>
<script type="application/json" id="avatarModelJson">{{{sceneJson}}}</script>
<script>
(() => {
  const model = JSON.parse(document.getElementById('avatarModelJson')?.textContent || '{}');
  const identity = model.identity || model.Identity || {};
  const points = identity.meanDenseVertices || identity.MeanDenseVertices || [];
  const edges = identity.topologyEdges || identity.TopologyEdges || [];
  const canvas = document.getElementById('avatarModelCanvas');
  const overlay = document.getElementById('avatarOverlay');
  const ctx = canvas?.getContext('2d');
  if (!canvas || !ctx) return;
  const view = { points: true, edges: true, autoRotate: false, yaw: -0.35, pitch: -0.10, zoom: 0.82 };
  let dragging = false;
  let last = null;
  let animation = null;

  document.getElementById('togglePoints')?.addEventListener('click', event => toggle(event.currentTarget, 'points'));
  document.getElementById('toggleEdges')?.addEventListener('click', event => toggle(event.currentTarget, 'edges'));
  document.getElementById('toggleAutoRotate')?.addEventListener('click', event => {
    toggle(event.currentTarget, 'autoRotate');
    schedule();
  });
  document.getElementById('resetView')?.addEventListener('click', () => {
    view.yaw = -0.35;
    view.pitch = -0.10;
    view.zoom = 0.82;
    draw();
  });
  canvas.addEventListener('pointerdown', event => {
    dragging = true;
    last = { x: event.clientX, y: event.clientY };
    canvas.dataset.dragging = 'true';
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', event => {
    if (!dragging || !last) return;
    view.yaw += (event.clientX - last.x) * 0.008;
    view.pitch = Math.max(-1.1, Math.min(1.1, view.pitch + (event.clientY - last.y) * 0.006));
    last = { x: event.clientX, y: event.clientY };
    draw();
  });
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    view.zoom = Math.max(0.42, Math.min(3.2, view.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));
    draw();
  }, { passive: false });
  canvas.addEventListener('dblclick', () => {
    view.yaw = -0.35;
    view.pitch = -0.10;
    view.zoom = 0.82;
    draw();
  });
  new ResizeObserver(resize).observe(canvas);
  resize();

  function toggle(button, key) {
    view[key] = !view[key];
    button.setAttribute('aria-pressed', view[key] ? 'true' : 'false');
    draw();
  }

  function release() {
    dragging = false;
    last = null;
    delete canvas.dataset.dragging;
  }

  function resize() {
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(360, Math.round(rect.width));
    const height = Math.max(420, Math.round(rect.height));
    const scale = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    draw();
  }

  function schedule() {
    if (animation) cancelAnimationFrame(animation);
    if (!view.autoRotate) {
      animation = null;
      return;
    }

    const step = () => {
      view.yaw += 0.006;
      draw();
      animation = requestAnimationFrame(step);
    };
    animation = requestAnimationFrame(step);
  }

  function draw() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);
    ctx.fillStyle = '#061019';
    ctx.fillRect(0, 0, rect.width, rect.height);
    drawGrid(rect);
    const normalized = normalize(points);
    const byIndex = new Map(normalized.map(point => [point.index, point]));
    if (view.edges) drawEdges(edges, byIndex, rect);
    if (view.points) drawPoints(normalized, rect);
    if (overlay) {
      overlay.innerHTML = `<strong>${escape(model.subjectDisplayName || model.SubjectDisplayName || 'Avatar model')}</strong><br>${escape(model.status || model.Status || 'waiting')}<br>${points.length.toLocaleString()} averaged vertices | ${edges.length.toLocaleString()} topology edges<br>identity confidence ${format(identity.confidencePercent ?? identity.ConfidencePercent)}% | samples ${identity.sampleCount ?? identity.SampleCount ?? 0}<br>${escape((model.poseCoverage || model.PoseCoverage || {}).summary || (model.poseCoverage || model.PoseCoverage || {}).Summary || 'coverage waiting')}`;
    }
  }

  function normalize(rawPoints) {
    const raw = rawPoints.map(point => ({
      index: point.index ?? point.Index,
      x: point.x ?? point.X,
      y: point.y ?? point.Y,
      z: point.z ?? point.Z
    })).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z));
    if (raw.length === 0) return [];
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const point of raw) {
      minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x);
      minY = Math.min(minY, point.y); maxY = Math.max(maxY, point.y);
      minZ = Math.min(minZ, point.z); maxZ = Math.max(maxZ, point.z);
    }
    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;
    const centerZ = (minZ + maxZ) / 2;
    const scale = 1 / Math.max(0.001, maxX - minX, maxY - minY);
    return raw.map(point => ({
      index: point.index,
      x: (point.x - centerX) * scale,
      y: (point.y - centerY) * scale,
      z: (point.z - centerZ) * scale * 0.70
    }));
  }

  function drawPoints(normalized, rect) {
    const projected = normalized.map(point => ({ point, projected: project(point, rect) })).sort((a, b) => a.projected.z - b.projected.z);
    for (const item of projected) {
      ctx.globalAlpha = 0.42;
      ctx.fillStyle = '#66d9ff';
      ctx.beginPath();
      ctx.arc(item.projected.x, item.projected.y, Math.max(0.45, 0.95 * item.projected.scale), 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  function drawEdges(rawEdges, byIndex, rect) {
    const projected = rawEdges.map(edge => {
      const from = byIndex.get(edge.fromIndex ?? edge.FromIndex);
      const to = byIndex.get(edge.toIndex ?? edge.ToIndex);
      if (!from || !to) return null;
      const a = project(from, rect);
      const b = project(to, rect);
      return { a, b, z: (a.z + b.z) / 2 };
    }).filter(Boolean).sort((a, b) => a.z - b.z);
    for (const edge of projected) {
      ctx.globalAlpha = 0.20;
      ctx.strokeStyle = '#66d9ff';
      ctx.lineWidth = Math.max(0.28, 0.46 * ((edge.a.scale + edge.b.scale) / 2));
      ctx.beginPath();
      ctx.moveTo(edge.a.x, edge.a.y);
      ctx.lineTo(edge.b.x, edge.b.y);
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  function project(point, rect) {
    const cosY = Math.cos(view.yaw);
    const sinY = Math.sin(view.yaw);
    const cosP = Math.cos(view.pitch);
    const sinP = Math.sin(view.pitch);
    const x1 = point.x * cosY + point.z * sinY;
    const z1 = -point.x * sinY + point.z * cosY;
    const y1 = point.y * cosP - z1 * sinP;
    const z2 = point.y * sinP + z1 * cosP;
    const depth = 1.7 + z2;
    const zoom = Math.min(rect.width, rect.height) * 0.70 * view.zoom / Math.max(0.35, depth);
    return {
      x: rect.width * 0.5 + x1 * zoom,
      y: rect.height * 0.52 + y1 * zoom,
      z: z2,
      scale: Math.max(0.35, Math.min(1.8, 1 / Math.max(0.35, depth)))
    };
  }

  function drawGrid(rect) {
    ctx.save();
    ctx.strokeStyle = '#132638';
    ctx.lineWidth = 1;
    for (let x = rect.width * 0.14; x <= rect.width * 0.86; x += rect.width * 0.09) {
      ctx.beginPath(); ctx.moveTo(x, rect.height * 0.12); ctx.lineTo(x, rect.height * 0.88); ctx.stroke();
    }
    for (let y = rect.height * 0.14; y <= rect.height * 0.88; y += rect.height * 0.09) {
      ctx.beginPath(); ctx.moveTo(rect.width * 0.12, y); ctx.lineTo(rect.width * 0.88, y); ctx.stroke();
    }
    ctx.restore();
  }

  function format(value) {
    return Number.isFinite(Number(value)) ? Number(value).toFixed(1).replace(/\.0$/, '') : '--';
  }

  function escape(value) {
    return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
  }
})();
</script>
</body>
</html>
""";
    }

    private static string H(string? value)
    {
        return WebUtility.HtmlEncode(value ?? "");
    }
}
