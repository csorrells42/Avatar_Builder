using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodThreeDdfaStore
{
    private const string LegacyJsonFileName = "last_5_3ddfa_reconstructions.json";
    public const string HtmlFileName = "last_5_3ddfa_reconstructions.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Write(string folder, LastGoodThreeDdfaReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        report = PrepareReport(report);
        Directory.CreateDirectory(folder);
        var htmlPath = Path.Combine(folder, HtmlFileName);
        AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        TryDeleteLegacyJson(folder);
        return htmlPath;
    }

    public static string GetHtmlPath(string folder)
    {
        return Path.Combine(folder, HtmlFileName);
    }

    private static void TryDeleteLegacyJson(string folder)
    {
        try
        {
            var path = Path.Combine(folder, LegacyJsonFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The HTML viewer is authoritative; a locked legacy cache can be removed next time.
        }
    }

    public static IReadOnlyList<ThreeDdfaReconstructionSnapshot> CreateSamples(
        AvatarModelObservationSet observationSet,
        IReadOnlyList<ThreeDdfaReconstructionSnapshot>? currentSamples = null)
    {
        ArgumentNullException.ThrowIfNull(observationSet);

        var persisted = observationSet.Observations
            .Where(static observation => observation.Vertices.Count > 0)
            .Select(static observation => new ThreeDdfaReconstructionSnapshot
        {
            RequestId = observation.RequestId,
            CapturedAtUtc = observation.CapturedAtUtc,
            Source = observation.Source,
            DenseVertexCount = observation.Vertices.Count,
            DenseSampleStride = 1,
            ReconstructionConfidencePercent = observation.ReconstructionConfidencePercent,
            ARotationAroundXDegrees = observation.ARotationAroundXDegrees,
            BRotationAroundYDegrees = observation.BRotationAroundYDegrees,
            CRotationAroundZDegrees = observation.CRotationAroundZDegrees,
            PoseSource = "3DDFA_V2 ONNX",
            TrustDecision = observation.TrustDecision,
            Vertices = observation.Vertices,
            CameraMatrixCoefficients = observation.CameraMatrixCoefficients,
            ShapeCoefficients = observation.ShapeCoefficients,
            ExpressionCoefficients = observation.ExpressionCoefficients,
            Warnings = observation.Warnings
        });
        var combined = currentSamples is { Count: > 0 }
            ? persisted.Concat(currentSamples)
            : persisted;

        return combined
            .GroupBy(CreateSampleKey, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(sample => sample.CapturedAtUtc).First())
            .OrderBy(static sample => sample.CapturedAtUtc)
            .TakeLast(AvatarModelObservationStore.MaxReviewGeometryCount)
            .ToList();
    }

    public static List<MeshTopologyEdge> SelectSharedTopology(
        AvatarModelObservationSet observationSet,
        IReadOnlyList<ThreeDdfaReconstructionSnapshot>? currentSamples = null)
    {
        ArgumentNullException.ThrowIfNull(observationSet);

        IReadOnlyList<MeshTopologyEdge> best = observationSet.DenseTopologyEdges;
        if (currentSamples is not null)
        {
            foreach (var sample in currentSamples)
            {
                if (sample.TopologyEdges.Count > best.Count)
                {
                    best = sample.TopologyEdges;
                }
            }
        }

        return best.ToList();
    }

    private static LastGoodThreeDdfaReport PrepareReport(LastGoodThreeDdfaReport report)
    {
        IReadOnlyList<MeshTopologyEdge> topology = report.DenseTopologyEdges;
        foreach (var sample in report.Samples)
        {
            if (sample.TopologyEdges.Count > topology.Count)
            {
                topology = sample.TopologyEdges;
            }
        }

        return new LastGoodThreeDdfaReport
        {
            SchemaVersion = "last-good-3ddfa-v2",
            CreatedAtUtc = report.CreatedAtUtc,
            SubjectId = report.SubjectId,
            SubjectDisplayName = report.SubjectDisplayName,
            StoragePolicy = report.StoragePolicy,
            AvatarModelProgressHtmlPath = report.AvatarModelProgressHtmlPath,
            ReconstructionLane = report.ReconstructionLane,
            DenseTopologyEdges = topology.ToList(),
            Samples = report.Samples
                .TakeLast(AvatarModelObservationStore.MaxReviewGeometryCount)
                .Select(CopyWithoutTopology)
                .ToList()
        };
    }

    private static ThreeDdfaReconstructionSnapshot CopyWithoutTopology(ThreeDdfaReconstructionSnapshot sample)
    {
        return new ThreeDdfaReconstructionSnapshot
        {
            RequestId = sample.RequestId,
            CapturedAtUtc = sample.CapturedAtUtc,
            Source = sample.Source,
            CoordinateSpace = sample.CoordinateSpace,
            DenseVertexCount = sample.DenseVertexCount,
            DenseSampleStride = sample.DenseSampleStride,
            ReconstructionConfidencePercent = sample.ReconstructionConfidencePercent,
            ARotationAroundXDegrees = sample.ARotationAroundXDegrees,
            BRotationAroundYDegrees = sample.BRotationAroundYDegrees,
            CRotationAroundZDegrees = sample.CRotationAroundZDegrees,
            PoseSource = sample.PoseSource,
            TrustDecision = sample.TrustDecision,
            Vertices = sample.Vertices,
            SparseLandmarks = sample.SparseLandmarks,
            CameraMatrixCoefficients = sample.CameraMatrixCoefficients,
            ShapeCoefficients = sample.ShapeCoefficients,
            ExpressionCoefficients = sample.ExpressionCoefficients,
            Warnings = sample.Warnings
        };
    }

    private static string CreateSampleKey(ThreeDdfaReconstructionSnapshot sample)
    {
        return string.IsNullOrWhiteSpace(sample.RequestId)
            ? $"{sample.Source}|{sample.CapturedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"
            : sample.RequestId;
    }

    private static string BuildHtml(LastGoodThreeDdfaReport report)
    {
        var sceneJson = JsonSerializer.Serialize(report, JsonOptions);
        var sampleRows = report.Samples.Count == 0
            ? "<tr><td colspan=\"5\" class=\"muted\">No 3DDFA dense reconstructions have been captured yet.</td></tr>"
            : string.Concat(report.Samples.Select((sample, index) =>
                $"<tr data-sample-row=\"{index}\"><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))}</td><td>{sample.VertexCount.ToString("n0", CultureInfo.InvariantCulture)}</td><td>{sample.ReconstructionConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{sample.ARotationAroundXDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.BRotationAroundYDegrees.ToString("0.#", CultureInfo.InvariantCulture)} / {sample.CRotationAroundZDegrees.ToString("0.#", CultureInfo.InvariantCulture)}</td><td>{H(sample.Source)}</td></tr>"));
        var avatarModelProgressLink = string.IsNullOrWhiteSpace(report.AvatarModelProgressHtmlPath)
            ? "<p class=\"muted\">Avatar Model Progress will appear after the avatar output folder is initialized.</p>"
            : $"<p><a href=\"{H(report.AvatarModelProgressHtmlPath)}\">Open Avatar Model Progress</a></p>";

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>3DDFA Last 5 Dense Reconstructions</title>
<style>
:root{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--mesh:#66d9ff;--edge:#1f6f90;--good:#80e0a4;--warn:#ffd27a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}main{display:grid;grid-template-columns:minmax(420px,1fr) minmax(360px,520px);gap:16px;padding:16px}.stage,.panel{border:1px solid var(--line);background:var(--panel);border-radius:6px}.stage{min-height:620px;padding:12px;display:grid;grid-template-rows:minmax(500px,1fr) auto;gap:12px}.viewer{position:relative;min-height:500px}canvas{width:100%;height:100%;min-height:500px;display:block;border:1px solid #193149;background:#061019;cursor:grab;touch-action:none}canvas[data-dragging=true]{cursor:grabbing}.overlay{position:absolute;left:12px;top:12px;max-width:min(640px,calc(100% - 24px));padding:8px 10px;background:rgba(5,11,16,.78);border:1px solid #193149;color:var(--muted);pointer-events:none}.controls,.sample-buttons{display:flex;flex-wrap:wrap;gap:8px}.panel{padding:14px;overflow-wrap:anywhere}h1{margin:0 0 4px;font-size:22px}h2{margin:18px 0 8px;font-size:17px}.muted{color:var(--muted)}button{background:#102033;color:var(--text);border:1px solid #37506a;padding:8px 10px;min-height:34px}button[aria-pressed=true]{background:#1c405c;border-color:#65c8ff}button[data-refresh-paused=true]{background:#4a2630;border-color:#ff9fbd}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #1c3042;padding:6px 4px;text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}tr[data-sample-row]{cursor:pointer}tr[data-active=true]{background:#102033}@media(max-width:980px){main{grid-template-columns:1fr} }
</style>
</head>
<body>
<main>
  <section class="stage" aria-label="3DDFA dense reconstruction viewer">
    <div class="viewer">
      <canvas id="mesh3d" title="Drag to rotate. Use the mouse wheel to zoom. Double-click to reset."></canvas>
      <div class="overlay" id="sampleOverlay">Waiting for 3DDFA dense reconstructions.</div>
    </div>
    <div class="controls">
      <button type="button" id="toggleAutoRefresh" aria-pressed="true">Pause Updates</button>
      <button type="button" id="togglePoints" aria-pressed="true">Points</button>
      <button type="button" id="toggleSurface" aria-pressed="true">Wireframe</button>
      <button type="button" id="resetView">Reset View</button>
    </div>
  </section>
  <aside class="panel">
    <h1>3DDFA Last 5 Dense Reconstructions</h1>
    <p class="muted" id="refreshStatus">Auto-refreshes every 30 seconds. Use Pause Updates to freeze this viewer for review. Last updated {{H(report.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}}.</p>
    <p>{{H(report.StoragePolicy)}}</p>
    <h2>Review Links</h2>
    {{avatarModelProgressLink}}
    <h2>3DDFA Lane</h2>
    <table>
      <tr><th>Subject</th><td>{{H(report.SubjectDisplayName)}} ({{H(report.SubjectId)}})</td></tr>
      <tr><th>Samples</th><td>{{report.Samples.Count.ToString(CultureInfo.InvariantCulture)}} / 5</td></tr>
      <tr><th>Status</th><td>{{H(report.ReconstructionLane.AvatarReconstructionStatus)}}</td></tr>
      <tr><th>Trust</th><td>{{H(report.ReconstructionLane.TrustDecision)}}</td></tr>
    </table>
    <div class="sample-buttons" id="sampleButtons"></div>
    <h2>Samples</h2>
    <table>
      <tr><th>Time</th><th>Vertices</th><th>Confidence</th><th>A/B/C</th><th>Source</th></tr>
      {{sampleRows}}
    </table>
    <h2>Selected Reconstruction</h2>
    <table id="sampleDetails"></table>
  </aside>
</main>
<script type="application/json" id="meshReport">{{sceneJson}}</script>
<script>
(() => {
  const report = JSON.parse(document.getElementById('meshReport')?.textContent || '{}');
  const samples = report.samples || report.Samples || [];
  const sharedEdges = report.denseTopologyEdges || report.DenseTopologyEdges || [];
  const canvas = document.getElementById('mesh3d');
  const overlay = document.getElementById('sampleOverlay');
  const details = document.getElementById('sampleDetails');
  const buttons = document.getElementById('sampleButtons');
  const ctx = canvas?.getContext('2d');
  if (!canvas || !ctx) return;

  const view = { points: true, surface: true, yaw: -0.38, pitch: -0.10, zoom: 0.82 };
  let activeIndex = samples.length > 0 ? samples.length - 1 : -1;
  let dragging = false;
  let last = null;
  let refreshTimer = null;

  bindAutoRefresh();
  bindToggle(document.getElementById('togglePoints'), 'points');
  bindToggle(document.getElementById('toggleSurface'), 'surface');
  document.getElementById('resetView')?.addEventListener('click', () => {
    view.yaw = -0.38;
    view.pitch = -0.10;
    view.zoom = 0.82;
    draw();
  });

  samples.forEach((sample, index) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = `#${index + 1} ${formatTime(sample.capturedAtUtc || sample.CapturedAtUtc)}`;
    button.addEventListener('click', () => {
      activeIndex = index;
      draw();
    });
    buttons?.appendChild(button);
  });

  document.querySelectorAll('[data-sample-row]').forEach(row => {
    row.addEventListener('click', () => {
      activeIndex = Number(row.getAttribute('data-sample-row'));
      draw();
    });
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
  canvas.addEventListener('pointerup', releaseDrag);
  canvas.addEventListener('pointercancel', releaseDrag);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    view.zoom = Math.max(0.42, Math.min(3.2, view.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));
    draw();
  }, { passive: false });
  canvas.addEventListener('dblclick', () => {
    view.yaw = -0.38;
    view.pitch = -0.10;
    view.zoom = 0.82;
    draw();
  });

  new ResizeObserver(resize).observe(canvas);
  resize();

  function bindAutoRefresh() {
    const toggle = document.getElementById('toggleAutoRefresh');
    const status = document.getElementById('refreshStatus');
    if (!toggle) return;
    const key = 'avatarBuilderLast5ThreeDdfaAutoRefreshPaused';
    const isPaused = () => {
      try { return window.localStorage?.getItem(key) === 'true'; }
      catch { return false; }
    };
    const setPaused = paused => {
      try { window.localStorage?.setItem(key, paused ? 'true' : 'false'); }
      catch { }
      toggle.textContent = paused ? 'Resume Updates' : 'Pause Updates';
      toggle.setAttribute('aria-pressed', paused ? 'false' : 'true');
      toggle.dataset.refreshPaused = paused ? 'true' : 'false';
      if (status) {
        status.textContent = paused
          ? 'Updates paused. This 3DDFA viewer will not reload until you click Resume Updates.'
          : 'Auto-refreshes every 30 seconds. Use Pause Updates to freeze this viewer for review.';
      }
      if (refreshTimer) {
        clearTimeout(refreshTimer);
        refreshTimer = null;
      }
      if (!paused) {
        refreshTimer = setTimeout(() => window.location.reload(), 30000);
      }
    };
    toggle.addEventListener('click', () => setPaused(!isPaused()));
    setPaused(isPaused());
  }

  function bindToggle(button, key) {
    if (!button) return;
    button.addEventListener('click', () => {
      view[key] = !view[key];
      button.setAttribute('aria-pressed', view[key] ? 'true' : 'false');
      draw();
    });
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

  function draw() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);
    ctx.fillStyle = '#061019';
    ctx.fillRect(0, 0, rect.width, rect.height);
    drawGrid(rect);

    document.querySelectorAll('[data-sample-row]').forEach(row => {
      row.dataset.active = row.getAttribute('data-sample-row') === String(activeIndex);
    });
    if (buttons) {
      [...buttons.children].forEach((button, index) => button.setAttribute('aria-pressed', index === activeIndex ? 'true' : 'false'));
    }

    const sample = samples[activeIndex];
    if (!sample) {
      overlay.textContent = 'Waiting for 3DDFA dense reconstructions.';
      if (details) details.innerHTML = '<tr><td class="muted">No sample selected.</td></tr>';
      return;
    }

    const vertices = sample.vertices || sample.Vertices || [];
    const sampleEdges = sample.topologyEdges || sample.TopologyEdges || [];
    const edges = sampleEdges.length > 0 ? sampleEdges : sharedEdges;
    const normalized = normalize(vertices);
    const byIndex = new Map(normalized.map(point => [point.index, point]));

    if (view.surface) {
      drawEdges(edges, byIndex, rect);
    }
    if (view.points) {
      drawPoints(normalized, rect);
    }

    const confidence = sample.reconstructionConfidencePercent ?? sample.ReconstructionConfidencePercent;
    overlay.innerHTML = `<strong>3DDFA dense reconstruction</strong><br>${formatTime(sample.capturedAtUtc || sample.CapturedAtUtc)} | ${vertices.length.toLocaleString()} vertices | ${edges.length.toLocaleString()} edges<br>A/B/C ${formatNumber(sample.aRotationAroundXDegrees ?? sample.ARotationAroundXDegrees)} / ${formatNumber(sample.bRotationAroundYDegrees ?? sample.BRotationAroundYDegrees)} / ${formatNumber(sample.cRotationAroundZDegrees ?? sample.CRotationAroundZDegrees)} deg<br>confidence ${formatNumber(confidence)}% | ${escapeHtml(sample.trustDecision || sample.TrustDecision || '')}`;

    if (details) {
      const warnings = sample.warnings || sample.Warnings || [];
      details.innerHTML = `
        <tr><th>Source</th><td>${escapeHtml(sample.source || sample.Source || '3DDFA_V2 ONNX')}</td></tr>
        <tr><th>Pose source</th><td>${escapeHtml(sample.poseSource || sample.PoseSource || '3DDFA_V2 ONNX')}</td></tr>
        <tr><th>Dense mesh</th><td>${vertices.length.toLocaleString()} vertices, ${edges.length.toLocaleString()} topology edges, stride ${formatInteger(sample.denseSampleStride ?? sample.DenseSampleStride)}</td></tr>
        <tr><th>A/B/C</th><td>${formatNumber(sample.aRotationAroundXDegrees ?? sample.ARotationAroundXDegrees)} / ${formatNumber(sample.bRotationAroundYDegrees ?? sample.BRotationAroundYDegrees)} / ${formatNumber(sample.cRotationAroundZDegrees ?? sample.CRotationAroundZDegrees)} deg</td></tr>
        <tr><th>Trust</th><td>${escapeHtml(sample.trustDecision || sample.TrustDecision || '')}</td></tr>
        <tr><th>Warnings</th><td>${warnings.length ? warnings.map(escapeHtml).join('; ') : '<span class="muted">No 3DDFA warnings.</span>'}</td></tr>`;
    }
  }

  function normalize(points) {
    const raw = points.map(point => ({
      index: point.index ?? point.Index,
      x: point.x ?? point.X,
      y: point.y ?? point.Y,
      z: point.z ?? point.Z
    })).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z));
    if (raw.length === 0) return [];
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const point of raw) {
      minX = Math.min(minX, point.x);
      maxX = Math.max(maxX, point.x);
      minY = Math.min(minY, point.y);
      maxY = Math.max(maxY, point.y);
      minZ = Math.min(minZ, point.z);
      maxZ = Math.max(maxZ, point.z);
    }
    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;
    const centerZ = (minZ + maxZ) / 2;
    const scale = 1 / Math.max(0.001, maxX - minX, maxY - minY);
    return raw.map(point => ({
      index: point.index,
      x: (point.x - centerX) * scale,
      y: (point.y - centerY) * scale,
      z: (point.z - centerZ) * scale * 0.62
    }));
  }

  function drawEdges(edges, byIndex, rect) {
    const projected = edges.map(edge => {
      const from = byIndex.get(edge.fromIndex ?? edge.FromIndex);
      const to = byIndex.get(edge.toIndex ?? edge.ToIndex);
      if (!from || !to) return null;
      const a = project(from, rect);
      const b = project(to, rect);
      return { a, b, depth: (a.z + b.z) / 2 };
    }).filter(Boolean).sort((a, b) => a.depth - b.depth);
    ctx.save();
    ctx.strokeStyle = '#66d9ff';
    ctx.lineWidth = 0.36;
    ctx.globalAlpha = 0.34;
    for (const edge of projected) {
      ctx.beginPath();
      ctx.moveTo(edge.a.x, edge.a.y);
      ctx.lineTo(edge.b.x, edge.b.y);
      ctx.stroke();
    }
    ctx.restore();
  }

  function drawPoints(points, rect) {
    const projected = points.map(point => ({ point, projected: project(point, rect) })).sort((a, b) => a.projected.z - b.projected.z);
    ctx.save();
    ctx.fillStyle = '#66d9ff';
    ctx.globalAlpha = 0.72;
    for (const item of projected) {
      const radius = 0.78 * item.projected.scale;
      ctx.beginPath();
      ctx.arc(item.projected.x, item.projected.y, radius, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
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
    const depth = 1.6 + z2;
    const zoom = Math.min(rect.width, rect.height) * 0.76 * view.zoom / Math.max(0.35, depth);
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
    for (let x = rect.width * 0.14; x <= rect.width * 0.86; x += rect.width * 0.12) {
      ctx.beginPath();
      ctx.moveTo(x, rect.height * 0.12);
      ctx.lineTo(x, rect.height * 0.88);
      ctx.stroke();
    }
    for (let y = rect.height * 0.14; y <= rect.height * 0.88; y += rect.height * 0.12) {
      ctx.beginPath();
      ctx.moveTo(rect.width * 0.12, y);
      ctx.lineTo(rect.width * 0.88, y);
      ctx.stroke();
    }
    ctx.restore();
  }

  function releaseDrag() {
    dragging = false;
    last = null;
    delete canvas.dataset.dragging;
  }

  function formatTime(value) {
    const date = value ? new Date(value) : null;
    return date && !Number.isNaN(date.getTime()) ? date.toLocaleTimeString() : '--';
  }

  function formatNumber(value) {
    return Number.isFinite(Number(value)) ? Number(value).toFixed(1).replace(/\.0$/, '') : '--';
  }

  function formatInteger(value) {
    return Number.isFinite(Number(value)) ? Math.round(Number(value)).toLocaleString() : '--';
  }

  function escapeHtml(value) {
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
