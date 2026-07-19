using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewStore
{
    public const string JsonFileName = "measurement_face_preview.json";
    public const string HtmlFileName = "measurement_face_preview.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public MeasurementFacePreviewFiles Write(string folder, MeasurementFacePreviewModel preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, JsonFileName);
        var htmlPath = Path.Combine(folder, HtmlFileName);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(preview, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(preview), Encoding.UTF8);
        return new MeasurementFacePreviewFiles(jsonPath, htmlPath);
    }

    private static string BuildHtml(MeasurementFacePreviewModel preview)
    {
        var title = $"{preview.SubjectDisplayName} measurement face preview";
        var warnings = preview.Warnings.Count == 0
            ? "<li>No preview warnings.</li>"
            : string.Concat(preview.Warnings.Select(warning => $"<li>{Escape(warning)}</li>"));
        var metrics = string.Concat(preview.Metrics.Select(metric =>
            $"<tr><th>{Escape(metric.Key)}</th><td>{Format(metric.Value)}</td></tr>"));
        var surfaceEvidence = BuildSurfaceEvidenceTable(preview.SurfaceEvidence);
        var surfacePatchGeometry = BuildSurfacePatchGeometryTable(preview.SurfacePatches);
        var poseBuckets = BuildPoseBucketTable(preview.PoseBuckets);
        var poseConsistency = BuildPoseConsistencyTable(preview.PoseBucketConsistency);
        var previewStage = preview.CanRender
            ? BuildPreviewStage(preview)
            : BuildPausedPreviewStage(preview);

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="10">
<title>{{Escape(title)}}</title>
<style>
:root {
  color-scheme: dark;
  --bg: #050b10;
  --panel: #0b141c;
  --line: #28435b;
  --text: #e7f6ff;
  --muted: #9db7c9;
  --face: #65c8ff;
  --eye: #8ff2c5;
  --mouth: #ff9fbd;
  --jaw: #ffd166;
  --nose: #d9e8ff;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font: 14px/1.45 "Segoe UI", Arial, sans-serif;
}
main {
  display: grid;
  grid-template-columns: minmax(360px, 1fr) minmax(320px, 420px);
  gap: 16px;
  padding: 16px;
}
h1 {
  margin: 0 0 4px;
  font-size: 22px;
}
.subtle { color: var(--muted); }
.stage, .panel {
  border: 1px solid var(--line);
  background: var(--panel);
  border-radius: 6px;
}
.stage {
  min-height: 560px;
  display: grid;
  grid-template-rows: minmax(420px, 1fr) auto;
  gap: 12px;
  overflow: hidden;
  padding: 12px;
}
.viewer {
  position: relative;
  min-height: 420px;
}
canvas {
  display: block;
  width: 100%;
  height: 100%;
  min-height: 420px;
  border: 1px solid #193149;
  background: #061019;
  cursor: grab;
  touch-action: none;
}
canvas[data-dragging="true"] {
  cursor: grabbing;
}
.viewer-overlay {
  position: absolute;
  left: 12px;
  top: 12px;
  color: var(--muted);
  background: rgba(5, 11, 16, 0.76);
  border: 1px solid #193149;
  padding: 8px 10px;
  max-width: min(520px, calc(100% - 24px));
  pointer-events: none;
}
.legend {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 12px;
  color: var(--muted);
  font-size: 12px;
}
.swatch {
  display: inline-block;
  width: 18px;
  height: 3px;
  margin-right: 6px;
  vertical-align: middle;
}
svg {
  width: min(92vw, 780px);
  height: auto;
  justify-self: center;
}
polyline {
  fill: none;
  stroke-width: 3;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.face { stroke: var(--face); }
.eye { stroke: var(--eye); }
.mouth, .mouth-opening { stroke: var(--mouth); }
.jaw, .jaw-droop { stroke: var(--jaw); }
.nose { stroke: var(--nose); }
.brow { stroke: #b5f0ff; }
.forehead { stroke: #9db7c9; }
.cheek { stroke: var(--face); }
.surface-patch, .surface-triangle {
  stroke-width: 1.2;
}
.point { fill: #dcefff; opacity: 0.72; }
.panel { padding: 14px; }
table {
  width: 100%;
  border-collapse: collapse;
}
th, td {
  border-bottom: 1px solid #1c3042;
  padding: 6px 0;
  vertical-align: top;
}
th {
  text-align: left;
  color: var(--muted);
  font-weight: 600;
}
.surface-cell {
  min-width: 110px;
}
.bar {
  display: block;
  height: 8px;
  margin-top: 3px;
  background: #102538;
  border: 1px solid #24455f;
}
.bar > span {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, #65c8ff, #8ff2c5);
}
.bar.depth > span {
  background: linear-gradient(90deg, #ffd166, #ff9fbd);
}
ul {
  margin: 8px 0 0;
  padding-left: 18px;
}
.disabled {
  max-width: 480px;
  color: var(--muted);
  text-align: center;
  padding: 24px;
}
.disabled strong {
  display: block;
  color: var(--text);
  font-size: 16px;
  margin-bottom: 8px;
}
@media (max-width: 900px) {
  main { grid-template-columns: 1fr; }
}
</style>
</head>
<body>
<main>
  <section class="stage" aria-label="Measurement face wireframe">
    {{previewStage}}
  </section>
  <aside class="panel">
    <h1>{{Escape(title)}}</h1>
    <div class="subtle">{{Escape(preview.RenderDecision)}} | confidence {{Format(preview.ConfidencePercent)}}%</div>
    <div class="subtle">Live report auto-refreshes every 10 seconds from the latest saved measurement snapshot.</div>
    <p>{{Escape(preview.StoragePolicy)}}</p>
    <p>{{Escape(preview.SafetyBoundary)}}</p>
    <table>
      <tr><th>Subject</th><td>{{Escape(preview.SubjectDisplayName)}} ({{Escape(preview.SubjectId)}})</td></tr>
      <tr><th>Accepted samples</th><td>{{preview.AcceptedSamples.ToString(CultureInfo.InvariantCulture)}} / {{preview.ObservedSamples.ToString(CultureInfo.InvariantCulture)}}</td></tr>
      <tr><th>Accepted weight</th><td>{{Format(preview.AcceptedSampleWeight)}}</td></tr>
      <tr><th>Geometry provenance</th><td>{{Escape(preview.GeometryProvenance)}}</td></tr>
      <tr><th>Measured contribution</th><td>{{Format(preview.MeasurementContributionPercent)}}%</td></tr>
      <tr><th>Template prior contribution</th><td>{{Format(preview.TemplatePriorContributionPercent)}}%</td></tr>
      <tr><th>Subject gate</th><td>{{Escape(preview.SubjectGate.GateDecision)}} - {{Escape(preview.SubjectGate.Reason)}}</td></tr>
      {{metrics}}
    </table>
    <h2>Surface Confidence</h2>
    {{surfaceEvidence}}
    <h2>Surface Patch Geometry</h2>
    {{surfacePatchGeometry}}
    <h2>Pose Buckets</h2>
    {{poseBuckets}}
    <h2>Pose Consistency</h2>
    {{poseConsistency}}
    <h2>Warnings</h2>
    <ul>{{warnings}}</ul>
  </aside>
</main>
</body>
</html>
""";
    }

    private static string BuildPausedPreviewStage(MeasurementFacePreviewModel preview)
    {
        var isSubjectGateAccepted = string.Equals(
            preview.SubjectGate.GateDecision,
            "accepted",
            StringComparison.OrdinalIgnoreCase);
        var title = isSubjectGateAccepted
            ? preview.AcceptedSamples <= 0 ? "Preview waiting for measurements" : "Preview paused"
            : $"Confirm {preview.SubjectDisplayName}";
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(preview.RenderDecision))
        {
            details.Add(preview.RenderDecision);
        }

        if (!isSubjectGateAccepted)
        {
            details.Add($"Check 'This is {preview.SubjectDisplayName}' only when {preview.SubjectDisplayName} is in front of the camera.");
        }

        if (preview.AcceptedSamples <= 0)
        {
            details.Add("Start Avatar Capture and keep a strong face lock until accepted measurements arrive.");
        }

        if (!string.IsNullOrWhiteSpace(preview.SubjectGate.Reason))
        {
            details.Add($"Subject gate: {preview.SubjectGate.Reason}");
        }

        return $"<div class=\"disabled\"><strong>{Escape(title)}</strong>{Escape(string.Join(" ", details))}</div>";
    }

    private static string BuildPreviewStage(MeasurementFacePreviewModel preview)
    {
        var sceneJson = JsonSerializer.Serialize(new PreviewScene(
            preview.Points,
            preview.Polylines,
            preview.SurfacePatches,
            preview.PoseBuckets,
            preview.SurfaceEvidence,
            preview.MeasurementContributionPercent,
            preview.TemplatePriorContributionPercent,
            preview.GeometryProvenance));
        var coveredPoseBuckets = preview.PoseBuckets.Count(static bucket => bucket.RequiredForAvatarCoverage && bucket.SampleCount > 0);
        var requiredPoseBuckets = preview.PoseBuckets.Count(static bucket => bucket.RequiredForAvatarCoverage);
        var poseSummary = requiredPoseBuckets > 0
            ? $"Pose buckets {coveredPoseBuckets.ToString(CultureInfo.InvariantCulture)}/{requiredPoseBuckets.ToString(CultureInfo.InvariantCulture)}"
            : "Pose buckets waiting";
        var poseConsistencySummary = $"Pose consistency {preview.PoseBucketConsistency.HealthPercent:0.#}% - {preview.PoseBucketConsistency.Status}";
        var weakestSurface = preview.SurfaceEvidence
            .OrderBy(static surface => surface.OverallConfidencePercent)
            .FirstOrDefault();
        var surfaceSummary = weakestSurface is null
            ? "Surface evidence waiting"
            : $"Weakest surface {weakestSurface.Label}: {weakestSurface.Status} ({weakestSurface.OverallConfidencePercent:0.#}%)";
        var surfacePatchSummary = preview.SurfacePatches.Count == 0
            ? "Surface mesh waiting"
            : $"Surface mesh {preview.SurfacePatches.Count.ToString(CultureInfo.InvariantCulture)} patches / {preview.SurfacePatches.Sum(static patch => patch.TriangleCount).ToString(CultureInfo.InvariantCulture)} triangles | normal consistency {preview.SurfacePatches.Average(static patch => patch.NormalConsistencyPercent):0.#}%";
        return $$"""
<div class="viewer">
  <canvas id="face3d" aria-label="Interactive 3D measurement face construction" title="Drag to rotate. Use the mouse wheel to zoom. Double-click to reset."></canvas>
  <div class="viewer-overlay">
    <strong>{{Escape(preview.GeometryProvenance)}}</strong><br>
    Measured {{Format(preview.MeasurementContributionPercent)}}% | scaffold {{Format(preview.TemplatePriorContributionPercent)}}%<br>
    {{Escape(poseSummary)}}<br>
    {{Escape(poseConsistencySummary)}}<br>
    {{Escape(surfaceSummary)}}<br>
    {{Escape(surfacePatchSummary)}}
  </div>
</div>
<div class="legend">
  <span><span class="swatch" style="background:var(--face)"></span>face</span>
  <span><span class="swatch" style="background:var(--eye)"></span>eyes</span>
  <span><span class="swatch" style="background:var(--mouth)"></span>lips</span>
  <span><span class="swatch" style="background:var(--jaw)"></span>jaw</span>
  <span><span class="swatch" style="background:var(--nose)"></span>nose</span>
</div>
<details>
  <summary>2D outline fallback</summary>
  {{BuildSvg(preview)}}
</details>
<script type="application/json" id="face-preview-scene">{{sceneJson}}</script>
<script>
(() => {
  const canvas = document.getElementById('face3d');
  const dataNode = document.getElementById('face-preview-scene');
  if (!canvas || !dataNode) return;
  const scene = JSON.parse(dataNode.textContent || '{}');
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  const colors = {
    face: '#65c8ff',
    eye: '#8ff2c5',
    mouth: '#ff9fbd',
    'mouth-opening': '#ff9fbd',
    jaw: '#ffd166',
    'jaw-droop': '#ffd166',
    nose: '#d9e8ff',
    brow: '#b5f0ff',
    forehead: '#9db7c9',
    cheek: '#65c8ff'
  };
  const points = new Map((scene.Points || []).map(point => [point.Id, point]));
  const storageKey = `AvatarBuilder.FacePreview3D.${scene.GeometryProvenance || 'default'}`;
  const defaultView = { yaw: -0.34, pitch: -0.08, zoom: 1 };
  let rotation = { ...defaultView };
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) || 'null');
    if (stored && Number.isFinite(stored.yaw) && Number.isFinite(stored.pitch)) {
      rotation = {
        yaw: stored.yaw,
        pitch: stored.pitch,
        zoom: Number.isFinite(stored.zoom) ? Math.max(0.55, Math.min(2.2, stored.zoom)) : 1
      };
    }
  } catch {}
  let dragging = false;
  let last = null;

  canvas.addEventListener('pointerdown', event => {
    dragging = true;
    last = { x: event.clientX, y: event.clientY };
    canvas.dataset.dragging = 'true';
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', event => {
    if (!dragging || !last) return;
    rotation.yaw += (event.clientX - last.x) * 0.008;
    rotation.pitch = Math.max(-0.9, Math.min(0.9, rotation.pitch + (event.clientY - last.y) * 0.006));
    last = { x: event.clientX, y: event.clientY };
    saveView();
    draw();
  });
  canvas.addEventListener('pointerup', releaseDrag);
  canvas.addEventListener('pointercancel', releaseDrag);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    rotation.zoom = Math.max(0.55, Math.min(2.2, rotation.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));
    saveView();
    draw();
  }, { passive: false });
  canvas.addEventListener('dblclick', () => {
    rotation = { ...defaultView };
    saveView();
    draw();
  });

  const resize = () => {
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(320, Math.round(rect.width));
    const height = Math.max(360, Math.round(rect.height));
    const scale = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    draw();
  };
  new ResizeObserver(resize).observe(canvas);
  resize();

  function project(point) {
    const yaw = rotation.yaw;
    const pitch = rotation.pitch;
    const cosY = Math.cos(yaw);
    const sinY = Math.sin(yaw);
    const cosP = Math.cos(pitch);
    const sinP = Math.sin(pitch);
    const x1 = point.X * cosY + point.Z * sinY;
    const z1 = -point.X * sinY + point.Z * cosY;
    const y1 = point.Y * cosP - z1 * sinP;
    const z2 = point.Y * sinP + z1 * cosP;
    const rect = canvas.getBoundingClientRect();
    const depth = 1.45 + z2;
    const zoom = Math.min(rect.width, rect.height) * 0.82 * rotation.zoom / Math.max(0.35, depth);
    return {
      x: rect.width * 0.5 + x1 * zoom,
      y: rect.height * 0.52 + y1 * zoom,
      z: z2,
      scale: Math.max(0.35, Math.min(1.8, 1 / Math.max(0.35, depth)))
    };
  }

  function releaseDrag() {
    dragging = false;
    last = null;
    delete canvas.dataset.dragging;
  }

  function saveView() {
    try { localStorage.setItem(storageKey, JSON.stringify(rotation)); } catch {}
  }

  function draw() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);
    const background = ctx.createLinearGradient(0, 0, 0, rect.height);
    background.addColorStop(0, '#061019');
    background.addColorStop(1, '#02070b');
    ctx.fillStyle = background;
    ctx.fillRect(0, 0, rect.width, rect.height);
    drawGrid(rect);

    const lines = (scene.Polylines || []).map(line => {
      const projected = (line.PointIds || [])
        .map(id => points.get(id))
        .filter(Boolean)
        .map(project);
      const depth = projected.length ? projected.reduce((sum, point) => sum + point.z, 0) / projected.length : 0;
      return { line, projected, depth };
    }).filter(item => item.projected.length >= 2)
      .sort((a, b) => a.depth - b.depth);

    const patches = (scene.SurfacePatches || []).map(patch => {
      const boundary = (patch.PointIds || [])
        .map(id => points.get(id))
        .filter(Boolean)
        .map(project);
      const triangles = (patch.Triangles || [])
        .map(triangle => (triangle.PointIds || [])
          .map(id => points.get(id))
          .filter(Boolean)
          .map(project))
        .filter(projected => projected.length === 3);
      const depthPoints = triangles.length ? triangles.flat() : boundary;
      const depth = depthPoints.length ? depthPoints.reduce((sum, point) => sum + point.z, 0) / depthPoints.length : 0;
      return { patch, boundary, triangles, depth };
    }).filter(item => item.triangles.length > 0)
      .sort((a, b) => a.depth - b.depth);

    for (const item of patches) {
      const color = colors[item.patch.Role] || '#dcefff';
      const confidence = Number.isFinite(item.patch.ConfidencePercent) ? item.patch.ConfidencePercent : 50;
      const opacity = Number.isFinite(item.patch.FillOpacity) ? item.patch.FillOpacity : 0.12;
      ctx.save();
      ctx.globalAlpha = Math.max(0.04, Math.min(0.28, opacity * confidence / 72));
      ctx.fillStyle = color;
      ctx.strokeStyle = color;
      ctx.lineWidth = 1;
      for (const triangle of item.triangles) {
        ctx.beginPath();
        ctx.moveTo(triangle[0].x, triangle[0].y);
        ctx.lineTo(triangle[1].x, triangle[1].y);
        ctx.lineTo(triangle[2].x, triangle[2].y);
        ctx.closePath();
        ctx.fill();
        ctx.globalAlpha = Math.max(0.05, Math.min(0.16, opacity * confidence / 90));
        ctx.stroke();
        ctx.globalAlpha = Math.max(0.04, Math.min(0.28, opacity * confidence / 72));
      }
      if (item.boundary.length >= 3) {
        ctx.globalAlpha = Math.max(0.08, Math.min(0.34, opacity * confidence / 52));
        ctx.beginPath();
        ctx.moveTo(item.boundary[0].x, item.boundary[0].y);
        for (let index = 1; index < item.boundary.length; index++) {
          ctx.lineTo(item.boundary[index].x, item.boundary[index].y);
        }
        ctx.closePath();
        ctx.stroke();
      }
      ctx.restore();
    }

    for (const item of lines) {
      const color = colors[item.line.Role] || '#dcefff';
      const confidence = Number.isFinite(item.line.ConfidencePercent) ? item.line.ConfidencePercent : 50;
      const templateHeavy = (item.line.Provenance || '').toLowerCase().includes('template');
      ctx.save();
      ctx.globalAlpha = Math.max(0.26, Math.min(0.95, confidence / 100));
      ctx.strokeStyle = color;
      ctx.lineWidth = templateHeavy ? 1.4 : 2.6;
      if (templateHeavy) ctx.setLineDash([7, 6]);
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.beginPath();
      ctx.moveTo(item.projected[0].x, item.projected[0].y);
      for (let index = 1; index < item.projected.length; index++) {
        ctx.lineTo(item.projected[index].x, item.projected[index].y);
      }
      ctx.stroke();
      ctx.restore();
    }

    const projectedPoints = Array.from(points.values())
      .map(point => ({ source: point, projected: project(point) }))
      .sort((a, b) => a.projected.z - b.projected.z);
    for (const item of projectedPoints) {
      const confidence = Number.isFinite(item.source.ConfidencePercent) ? item.source.ConfidencePercent : 50;
      ctx.globalAlpha = Math.max(0.28, Math.min(0.8, confidence / 120));
      ctx.fillStyle = colors[item.source.Role] || '#dcefff';
      const radius = Math.max(1.2, 2.4 * item.projected.scale);
      ctx.beginPath();
      ctx.arc(item.projected.x, item.projected.y, radius, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  function drawGrid(rect) {
    ctx.save();
    ctx.strokeStyle = '#132638';
    ctx.lineWidth = 1;
    for (let x = rect.width * 0.16; x <= rect.width * 0.84; x += rect.width * 0.085) {
      ctx.beginPath();
      ctx.moveTo(x, rect.height * 0.12);
      ctx.lineTo(x, rect.height * 0.88);
      ctx.stroke();
    }
    for (let y = rect.height * 0.14; y <= rect.height * 0.88; y += rect.height * 0.09) {
      ctx.beginPath();
      ctx.moveTo(rect.width * 0.12, y);
      ctx.lineTo(rect.width * 0.88, y);
      ctx.stroke();
    }
    ctx.restore();
  }
})();
</script>
""";
    }

    private static string BuildSvg(MeasurementFacePreviewModel preview)
    {
        var pointsById = preview.Points.ToDictionary(static point => point.Id, StringComparer.OrdinalIgnoreCase);
        var patches = new StringBuilder();
        foreach (var patch in preview.SurfacePatches)
        {
            var color = RoleColor(patch.Role);
            var opacity = Math.Clamp(patch.FillOpacity * Math.Clamp(patch.ConfidencePercent, 0d, 100d) / 72d, 0.04d, 0.28d);
            foreach (var triangle in patch.Triangles)
            {
                var coordinates = triangle.PointIds
                    .Where(pointsById.ContainsKey)
                    .Select(id => Project(pointsById[id]))
                    .ToList();
                if (coordinates.Count != 3)
                {
                    continue;
                }

                patches.AppendLine(
                    $"<polygon class=\"surface-triangle {EscapeAttribute(patch.Role)}\" points=\"{string.Join(" ", coordinates)}\" fill=\"{color}\" stroke=\"{color}\" opacity=\"{Format(opacity)}\"><title>{Escape(patch.Label)} | {Escape(patch.Provenance)} | confidence {Format(patch.ConfidencePercent)}%</title></polygon>");
            }
        }

        var polylines = new StringBuilder();
        foreach (var line in preview.Polylines)
        {
            var coordinates = line.PointIds
                .Where(pointsById.ContainsKey)
                .Select(id => Project(pointsById[id]))
                .ToList();
            if (coordinates.Count < 2)
            {
                continue;
            }

            polylines.AppendLine(
                $"<polyline class=\"{EscapeAttribute(line.Role)}\" points=\"{string.Join(" ", coordinates)}\"><title>{Escape(line.Label)} | {Escape(line.Provenance)} | confidence {Format(line.ConfidencePercent)}%</title></polyline>");
        }

        var points = string.Concat(preview.Points.Select(point =>
        {
            var (x, y) = ProjectTuple(point);
            return $"<circle class=\"point\" cx=\"{Format(x)}\" cy=\"{Format(y)}\" r=\"2.4\"><title>{Escape(point.Label)}: {Escape(point.Id)} | {Escape(point.Provenance)} | confidence {Format(point.ConfidencePercent)}%</title></circle>";
        }));

        return $$"""
<svg viewBox="0 0 900 900" role="img" aria-label="Measurement-only face wireframe">
  <rect x="0" y="0" width="900" height="900" fill="#061019"/>
  <line x1="450" y1="120" x2="450" y2="780" stroke="#193149" stroke-width="1"/>
  <line x1="170" y1="450" x2="730" y2="450" stroke="#193149" stroke-width="1"/>
  {{patches}}
  {{polylines}}
  {{points}}
</svg>
""";
    }

    private static string BuildPoseBucketTable(IReadOnlyList<MeasurementFacePreviewPoseBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return "<p class=\"subtle\">No pose bucket measurements yet.</p>";
        }

        var rows = string.Concat(buckets.Select(bucket =>
        {
            var role = bucket.PrimaryNeutralReference
                ? "neutral reference"
                : bucket.RequiredForAvatarCoverage ? "pose coverage" : "optional";
            return $"<tr><th>{Escape(bucket.Label)}</th><td>{Escape(bucket.Status)}</td><td>{bucket.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{Format(bucket.CoveragePercent)}%</td><td>{Format(bucket.HeadPitchDegrees)}</td><td>{Format(bucket.HeadYawDegrees)}</td><td>{Format(bucket.HeadRollDegrees)}</td><td>{Escape(role)}</td></tr>";
        }));
        return $"<table><tr><th>Pose</th><th>Status</th><th>Samples</th><th>Coverage</th><th>A</th><th>B</th><th>C</th><th>Use</th></tr>{rows}</table>";
    }

    private static string BuildSurfaceEvidenceTable(IReadOnlyList<MeasurementFacePreviewSurfaceEvidence> regions)
    {
        if (regions.Count == 0)
        {
            return "<p class=\"subtle\">Surface confidence is waiting for renderable model evidence.</p>";
        }

        var rows = string.Concat(regions
            .OrderBy(static region => region.OverallConfidencePercent)
            .Select(region =>
            {
                var poseBuckets = region.SupportingPoseBuckets.Count == 0
                    ? ""
                    : string.Join(", ", region.SupportingPoseBuckets.Select(Escape));
                return $$"""
<tr>
  <th>{{Escape(region.Label)}}</th>
  <td>{{Escape(region.Status)}}</td>
  <td class="surface-cell">{{Format(region.OverallConfidencePercent)}}%<span class="bar"><span style="width:{{Format(region.OverallConfidencePercent)}}%"></span></span></td>
  <td class="surface-cell">{{Format(region.FrontEvidencePercent)}}%<span class="bar"><span style="width:{{Format(region.FrontEvidencePercent)}}%"></span></span></td>
  <td class="surface-cell">{{Format(region.DepthEvidencePercent)}}%<span class="bar depth"><span style="width:{{Format(region.DepthEvidencePercent)}}%"></span></span></td>
  <td class="surface-cell">{{Format(region.DepthProfileCoveragePercent)}}%<span class="bar depth"><span style="width:{{Format(region.DepthProfileCoveragePercent)}}%"></span></span><span class="subtle">stable {{Format(region.DepthStabilityPercent)}}% | range {{Format(region.DepthRange)}} | std {{Format(region.AverageDepthStandardDeviation)}}</span></td>
  <td>{{Escape(region.EvidenceBasis)}}<br><span class="subtle">{{Escape(region.NextCaptureHint)}}</span><br><span class="subtle">{{poseBuckets}}</span></td>
</tr>
""";
            }));
        return $"<table><tr><th>Region</th><th>Status</th><th>Overall</th><th>Front</th><th>Depth</th><th>Z profile</th><th>Evidence and next capture</th></tr>{rows}</table>";
    }

    private static string BuildSurfacePatchGeometryTable(IReadOnlyList<MeasurementFacePreviewSurfacePatch> patches)
    {
        if (patches.Count == 0)
        {
            return "<p class=\"subtle\">Measured surface patches are waiting for stable learned Z evidence.</p>";
        }

        var rows = string.Concat(patches
            .OrderBy(static patch => patch.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static patch => patch.Label, StringComparer.OrdinalIgnoreCase)
            .Select(patch =>
            {
                var normal = $"{Format(patch.AverageNormalX)}, {Format(patch.AverageNormalY)}, {Format(patch.AverageNormalZ)}";
                var consistency = Math.Clamp(patch.NormalConsistencyPercent, 0d, 100d);
                return $$"""
<tr>
  <th>{{Escape(patch.Label)}}</th>
  <td>{{Escape(patch.Role)}}</td>
  <td>{{Escape(patch.GeometryStatus)}}<br><span class="subtle">{{Escape(patch.GeometryFinding)}}</span></td>
  <td class="surface-cell">{{Format(patch.GeometryHealthPercent)}}%<span class="bar"><span style="width:{{Format(Math.Clamp(patch.GeometryHealthPercent, 0d, 100d))}}%"></span></span></td>
  <td>{{patch.TriangleCount.ToString(CultureInfo.InvariantCulture)}}</td>
  <td>{{Format(patch.SurfaceArea)}}</td>
  <td>{{Format(patch.AverageTriangleArea)}}</td>
  <td>{{Format(patch.DepthRelief)}}</td>
  <td>{{Escape(normal)}}</td>
  <td class="surface-cell">{{Format(consistency)}}%<span class="bar"><span style="width:{{Format(consistency)}}%"></span></span></td>
</tr>
""";
            }));
        return $"<table><tr><th>Patch</th><th>Role</th><th>Status</th><th>Health</th><th>Triangles</th><th>Area</th><th>Avg tri</th><th>Z relief</th><th>Normal XYZ</th><th>Normal consistency</th></tr>{rows}</table>";
    }

    private static string BuildPoseConsistencyTable(PersonalFacePoseBucketConsistencyReport report)
    {
        if (report.Comparisons.Count == 0)
        {
            return $"<p class=\"subtle\">{Escape(report.Status)}</p>";
        }

        var rows = string.Concat(report.Comparisons.Select(comparison =>
            $"<tr><th>{Escape(comparison.Label)}</th><td>{Escape(comparison.Status)}</td><td>{Format(comparison.PoseAxisHealthPercent)}%</td><td>{Escape(comparison.PoseAxisReason)}</td><td>{Format(comparison.DriftScorePercent)}%</td><td>{Format(comparison.EyeMidlineXToFaceWidthDelta)}</td><td>{Format(comparison.MouthCenterXToFaceWidthDelta)}</td><td>{Format(comparison.EyeToMouthXOffsetToFaceWidthDelta)}</td><td>{Format(comparison.InterEyeDistanceToFaceWidthDelta)}</td><td>{Format(comparison.MouthWidthToFaceWidthDelta)}</td><td>{Format(comparison.EyeMidlineYToFaceHeightDelta)}</td><td>{Format(comparison.MouthCenterYToFaceHeightDelta)}</td></tr>"));
        return $"<p class=\"subtle\">{Escape(report.Status)} | health {Format(report.HealthPercent)}%</p><table><tr><th>Pose</th><th>Status</th><th>Axis</th><th>Axis reason</th><th>Drift</th><th>Eye X</th><th>Mouth X</th><th>Eye-mouth X</th><th>Eye spacing</th><th>Mouth width</th><th>Eye height</th><th>Mouth height</th></tr>{rows}</table>";
    }

    private static string Project(MeasurementFacePreviewPoint point)
    {
        var (x, y) = ProjectTuple(point);
        return $"{Format(x)},{Format(y)}";
    }

    private static (double X, double Y) ProjectTuple(MeasurementFacePreviewPoint point)
    {
        const double center = 450d;
        const double scale = 780d;
        var depthLift = Math.Clamp(point.Z, -0.25d, 0.25d) * 46d;
        return (center + point.X * scale, center + point.Y * scale - depthLift);
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string EscapeAttribute(string value)
    {
        var cleaned = new string(value
            .Where(static character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "line" : cleaned;
    }

    private static string RoleColor(string role)
    {
        return role switch
        {
            "face" => "#65c8ff",
            "eye" => "#8ff2c5",
            "mouth" or "mouth-opening" => "#ff9fbd",
            "jaw" or "jaw-droop" => "#ffd166",
            "nose" => "#d9e8ff",
            "brow" => "#b5f0ff",
            "forehead" => "#9db7c9",
            "cheek" => "#65c8ff",
            _ => "#dcefff"
        };
    }

    private static string Format(double? value)
    {
        return value is double number
            ? Format(number)
            : "";
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

internal sealed record PreviewScene(
    IReadOnlyList<MeasurementFacePreviewPoint> Points,
    IReadOnlyList<MeasurementFacePreviewPolyline> Polylines,
    IReadOnlyList<MeasurementFacePreviewSurfacePatch> SurfacePatches,
    IReadOnlyList<MeasurementFacePreviewPoseBucket> PoseBuckets,
    IReadOnlyList<MeasurementFacePreviewSurfaceEvidence> SurfaceEvidence,
    double MeasurementContributionPercent,
    double TemplatePriorContributionPercent,
    string GeometryProvenance);

public sealed record MeasurementFacePreviewFiles(string JsonPath, string HtmlPath);
