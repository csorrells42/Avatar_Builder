using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshStore
{
    public const string JsonFileName = "last_10_good_features.json";
    public const string HtmlFileName = "last_10_good_features.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LastGoodFeatureMeshFiles Write(string folder, LastGoodFeatureMeshReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        report = PrepareReport(report);
        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, JsonFileName);
        var htmlPath = Path.Combine(folder, HtmlFileName);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        return new LastGoodFeatureMeshFiles(jsonPath, htmlPath);
    }

    public static string GetHtmlPath(string folder)
    {
        return Path.Combine(folder, HtmlFileName);
    }

    private static LastGoodFeatureMeshReport PrepareReport(LastGoodFeatureMeshReport report)
    {
        var samples = report.Samples
            .TakeLast(5)
            .Select(RemoveThreeDdfaPayload)
            .ToList();

        return new LastGoodFeatureMeshReport
        {
            SchemaVersion = report.SchemaVersion,
            CreatedAtUtc = report.CreatedAtUtc,
            SubjectId = report.SubjectId,
            SubjectDisplayName = report.SubjectDisplayName,
            StoragePolicy = report.StoragePolicy,
            AvatarModelProgressHtmlPath = report.AvatarModelProgressHtmlPath,
            HeadLockedStability = LastGoodFeatureMeshStabilityAnalyzer.Analyze(samples),
            ReconstructionLane = report.ReconstructionLane,
            Samples = samples
        };
    }

    private static LastGoodFeatureMeshSample RemoveThreeDdfaPayload(LastGoodFeatureMeshSample sample)
    {
        var json = JsonSerializer.Serialize(sample, JsonOptions);
        var copy = JsonSerializer.Deserialize<LastGoodFeatureMeshSample>(json, JsonOptions) ?? sample;
        copy.ThreeDdfaFullResolution = null;
        return copy;
    }

    private static string BuildHtml(LastGoodFeatureMeshReport report)
    {
        var sceneJson = JsonSerializer.Serialize(report, JsonOptions);
        var sampleRows = report.Samples.Count == 0
            ? "<tr><td colspan=\"5\" class=\"muted\">No good MediaPipe feature-lock samples have been captured yet.</td></tr>"
            : string.Concat(report.Samples.Select((sample, index) =>
                $"<tr data-sample-row=\"{index}\"><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))}</td><td>{FormatSamplePointCounts(sample)}</td><td>{sample.OverallQualityPercent:0.#}%</td><td>{sample.HeadPitchDegrees:0.#}/{sample.HeadYawDegrees:0.#}/{sample.HeadRollDegrees:0.#}</td><td>{H(sample.Source)}</td></tr>"));
        var stabilityRows = BuildStabilityRows(report.HeadLockedStability);
        var stabilityFindings = report.HeadLockedStability.Findings.Count == 0
            ? "<li>No head-locked stability findings.</li>"
            : string.Concat(report.HeadLockedStability.Findings.Select(finding => $"<li>{H(finding)}</li>"));
        var avatarModelProgressLink = string.IsNullOrWhiteSpace(report.AvatarModelProgressHtmlPath)
            ? "<p class=\"muted\">Avatar Model Progress will appear after the avatar output folder is initialized.</p>"
            : $"<p><a href=\"{H(report.AvatarModelProgressHtmlPath)}\">Open Avatar Model Progress</a></p>";

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MediaPipe Last 5 Feature Locks</title>
<style>
:root {
  color-scheme: dark;
  --bg: #050b10;
  --panel: #0b141c;
  --line: #28435b;
  --text: #e7f6ff;
  --muted: #9db7c9;
  --point: #6f8da3;
  --surface: #2f6c8f;
  --three-ddfa: #66d9ff;
  --face: #65c8ff;
  --eye: #8ff2c5;
  --brow: #c9f7a3;
  --mouth: #ff9fbd;
  --jaw: #ffd166;
  --nose: #d9e8ff;
  --cheek: #c7a6ff;
  --forehead: #9db7c9;
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
  grid-template-columns: minmax(420px, 1fr) minmax(360px, 520px);
  gap: 16px;
  padding: 16px;
}
h1 { margin: 0 0 4px; font-size: 22px; }
h2 { margin: 18px 0 8px; font-size: 17px; }
.stage, .panel {
  border: 1px solid var(--line);
  background: var(--panel);
  border-radius: 6px;
}
.stage {
  min-height: 620px;
  display: grid;
  grid-template-rows: minmax(480px, 1fr) auto;
  gap: 12px;
  padding: 12px;
}
.viewer {
  position: relative;
  min-height: 480px;
}
canvas {
  width: 100%;
  height: 100%;
  min-height: 480px;
  display: block;
  border: 1px solid #193149;
  background: #061019;
  cursor: grab;
  touch-action: none;
}
canvas[data-dragging="true"] { cursor: grabbing; }
.overlay {
  position: absolute;
  left: 12px;
  top: 12px;
  max-width: min(620px, calc(100% - 24px));
  padding: 8px 10px;
  color: var(--muted);
  background: rgba(5, 11, 16, 0.78);
  border: 1px solid #193149;
  pointer-events: none;
}
.panel { padding: 14px; overflow-wrap: anywhere; }
.muted { color: var(--muted); }
.view-controls { display: flex; flex-wrap: wrap; gap: 8px; }
.view-controls button { min-width: 112px; }
.legend { display: flex; flex-wrap: wrap; gap: 8px 12px; color: var(--muted); font-size: 12px; }
.swatch { display: inline-block; width: 18px; height: 3px; margin-right: 6px; vertical-align: middle; }
button {
  background: #102033;
  color: var(--text);
  border: 1px solid #37506a;
  padding: 8px 10px;
  min-height: 34px;
}
button[aria-pressed="true"] {
  background: #1c405c;
  border-color: #65c8ff;
}
button:disabled {
  opacity: 0.42;
  cursor: not-allowed;
}
button[data-refresh-paused="true"] {
  background: #4a2630;
  border-color: #ff9fbd;
}
.sample-buttons { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
table { width: 100%; border-collapse: collapse; }
td, th { border-bottom: 1px solid #1c3042; padding: 6px 4px; text-align: left; vertical-align: top; }
th { color: var(--muted); font-weight: 600; }
tr[data-sample-row] { cursor: pointer; }
tr[data-active="true"] { background: #102033; }
@media (max-width: 980px) {
  main { grid-template-columns: 1fr; }
}
</style>
</head>
<body>
<main>
  <section class="stage" aria-label="MediaPipe last five feature-lock meshes">
    <div class="viewer">
      <canvas id="mesh3d" aria-label="Interactive 3D dense landmark points" title="Drag to rotate. Use the mouse wheel to zoom. Double-click to reset."></canvas>
      <div class="overlay" id="sampleOverlay">Waiting for MediaPipe feature-lock samples.</div>
    </div>
    <div class="view-controls" aria-label="Mesh display controls">
      <button type="button" id="toggleAutoRefresh" aria-pressed="true">Pause Updates</button>
      <button type="button" id="selectMediaPipeView" aria-pressed="false">Show MediaPipe Wireframe</button>
      <button type="button" id="togglePoints" aria-pressed="true">Points</button>
      <button type="button" id="toggleSurface" aria-pressed="true">Wireframe</button>
      <button type="button" id="toggleFeatures" aria-pressed="true">Features</button>
      <button type="button" id="toggleGhosts" aria-pressed="false">Ghost Last 5</button>
      <button type="button" id="toggleHeadLock" aria-pressed="true">Head Lock</button>
      <button type="button" id="toggleAxes" aria-pressed="true">Frame Axes</button>
    </div>
    <div class="legend">
      <span><span class="swatch" style="background:var(--point)"></span>all mesh points</span>
      <span><span class="swatch" style="background:var(--surface)"></span>MediaPipe face mesh</span>
      <span><span class="swatch" style="background:var(--face)"></span>face</span>
      <span><span class="swatch" style="background:var(--eye)"></span>eyes</span>
      <span><span class="swatch" style="background:var(--brow)"></span>brows</span>
      <span><span class="swatch" style="background:var(--mouth)"></span>lips</span>
      <span><span class="swatch" style="background:var(--jaw)"></span>jaw</span>
      <span><span class="swatch" style="background:var(--nose)"></span>nose</span>
      <span><span class="swatch" style="background:var(--cheek)"></span>cheeks</span>
      <span><span class="swatch" style="background:var(--forehead)"></span>forehead</span>
      <span><span class="swatch" style="background:#f4f7ff"></span>head-anchored axes</span>
    </div>
  </section>
  <aside class="panel">
    <h1>MediaPipe Last 5 Feature Locks</h1>
    <div class="muted" id="refreshStatus">Auto-refreshes every 30 seconds. Use Pause Updates to freeze this viewer for review. Last updated {{H(report.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}}.</div>
    <p>{{H(report.StoragePolicy)}}</p>
    <h2>Review Links</h2>
    {{avatarModelProgressLink}}
    <h2>Two-Lane Tracking</h2>
    <table>
      <tr><th>Fast tracking lane</th><td>{{H(report.ReconstructionLane.FastTrackingStatus)}}</td></tr>
      <tr><th>Avatar reconstruction lane</th><td>{{H(report.ReconstructionLane.AvatarReconstructionStatus)}}</td></tr>
      <tr><th>Trust decision</th><td>{{H(report.ReconstructionLane.TrustDecision)}}</td></tr>
      <tr><th>Learning impact</th><td>{{H(report.ReconstructionLane.LearningImpact)}}</td></tr>
    </table>
    <table>
      <tr><th>Subject</th><td>{{H(report.SubjectDisplayName)}} ({{H(report.SubjectId)}})</td></tr>
      <tr><th>Samples</th><td>{{report.Samples.Count.ToString(CultureInfo.InvariantCulture)}} / 5</td></tr>
    </table>
    <h2>Head-Locked Stability</h2>
    <table>
      <tr><th>Status</th><td>{{H(report.HeadLockedStability.Status)}}</td></tr>
      <tr><th>Health</th><td>{{report.HeadLockedStability.HealthPercent.ToString("0.#", CultureInfo.InvariantCulture)}}%</td></tr>
      <tr><th>Head-locked samples</th><td>{{report.HeadLockedStability.HeadLockedSampleCount.ToString(CultureInfo.InvariantCulture)}} / {{report.HeadLockedStability.SampleCount.ToString(CultureInfo.InvariantCulture)}}</td></tr>
      <tr><th>Worst drift</th><td>{{report.HeadLockedStability.WorstFeatureDriftPercent.ToString("0.#", CultureInfo.InvariantCulture)}}%</td></tr>
      <tr><th>B head-turn lock</th><td>{{H(report.HeadLockedStability.YawStatus)}}</td></tr>
      <tr><th>B health</th><td>{{FormatYawHealth(report.HeadLockedStability)}}; range {{report.HeadLockedStability.YawRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}} deg; left/right {{report.HeadLockedStability.YawLeftSampleCount.ToString(CultureInfo.InvariantCulture)}} / {{report.HeadLockedStability.YawRightSampleCount.ToString(CultureInfo.InvariantCulture)}}; compared features {{report.HeadLockedStability.YawComparedFeatureCount.ToString(CultureInfo.InvariantCulture)}}</td></tr>
      <tr><th>A tilt lock</th><td>{{H(report.HeadLockedStability.AStatus)}}</td></tr>
      <tr><th>A health</th><td>{{FormatAxisHealth(report.HeadLockedStability.AHealthPercent)}}; range {{report.HeadLockedStability.ARangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}} deg; negative/positive {{report.HeadLockedStability.ANegativeSampleCount.ToString(CultureInfo.InvariantCulture)}} / {{report.HeadLockedStability.APositiveSampleCount.ToString(CultureInfo.InvariantCulture)}}; compared features {{report.HeadLockedStability.AComparedFeatureCount.ToString(CultureInfo.InvariantCulture)}}</td></tr>
      <tr><th>C tilt lock</th><td>{{H(report.HeadLockedStability.CStatus)}}</td></tr>
      <tr><th>C health</th><td>{{FormatAxisHealth(report.HeadLockedStability.CHealthPercent)}}; range {{report.HeadLockedStability.CRangeDegrees.ToString("0.#", CultureInfo.InvariantCulture)}} deg; negative/positive {{report.HeadLockedStability.CNegativeSampleCount.ToString(CultureInfo.InvariantCulture)}} / {{report.HeadLockedStability.CPositiveSampleCount.ToString(CultureInfo.InvariantCulture)}}; compared features {{report.HeadLockedStability.CComparedFeatureCount.ToString(CultureInfo.InvariantCulture)}}</td></tr>
      <tr><th>Z distance lock</th><td>{{H(report.HeadLockedStability.ZStatus)}}</td></tr>
      <tr><th>Z health</th><td>{{FormatAxisHealth(report.HeadLockedStability.ZHealthPercent)}}; face-scale range {{report.HeadLockedStability.ZFaceScaleRangePercent.ToString("0.#", CultureInfo.InvariantCulture)}}%; close/far {{report.HeadLockedStability.ZCloseSampleCount.ToString(CultureInfo.InvariantCulture)}} / {{report.HeadLockedStability.ZFarSampleCount.ToString(CultureInfo.InvariantCulture)}}; compared features {{report.HeadLockedStability.ZComparedFeatureCount.ToString(CultureInfo.InvariantCulture)}}</td></tr>
    </table>
    <table>
      <tr><th>Feature</th><th>Status</th><th>Samples</th><th>Max drift</th><th>Avg drift</th></tr>
      {{stabilityRows}}
    </table>
    <ul>{{stabilityFindings}}</ul>
    <h2>B Head-Turn Findings</h2>
    <ul>{{BuildYawFindings(report.HeadLockedStability)}}</ul>
    <h2>A Tilt Findings</h2>
    <ul>{{BuildFindings(report.HeadLockedStability.AFindings, "No A-axis tilt findings.")}}</ul>
    <h2>C Tilt Findings</h2>
    <ul>{{BuildFindings(report.HeadLockedStability.CFindings, "No C-axis tilt findings.")}}</ul>
    <h2>Z Distance Findings</h2>
    <ul>{{BuildFindings(report.HeadLockedStability.ZFindings, "No Z distance findings.")}}</ul>
    <div class="sample-buttons" id="sampleButtons"></div>
    <h2>MediaPipe Samples</h2>
    <table>
      <tr><th>Time</th><th>Points</th><th>Quality</th><th>A/B/C</th><th>Source</th></tr>
      {{sampleRows}}
    </table>
    <h2>Selected Sample</h2>
    <table id="sampleDetails"></table>
  </aside>
</main>
<script type="application/json" id="meshReport">{{sceneJson}}</script>
<script>
(() => {
  const report = JSON.parse(document.getElementById('meshReport')?.textContent || '{}');
  const samples = report.samples || report.Samples || [];
  const canvas = document.getElementById('mesh3d');
  const overlay = document.getElementById('sampleOverlay');
  const details = document.getElementById('sampleDetails');
  const buttons = document.getElementById('sampleButtons');
  const selectThreeDdfaView = document.getElementById('selectThreeDdfaView');
  const selectMediaPipeView = document.getElementById('selectMediaPipeView');
  const togglePoints = document.getElementById('togglePoints');
  const toggleSurface = document.getElementById('toggleSurface');
  const toggleFeatures = document.getElementById('toggleFeatures');
  const toggleGhosts = document.getElementById('toggleGhosts');
  const toggleHeadLock = document.getElementById('toggleHeadLock');
  const toggleAxes = document.getElementById('toggleAxes');
  const toggleAutoRefresh = document.getElementById('toggleAutoRefresh');
  const refreshStatus = document.getElementById('refreshStatus');
  const ctx = canvas?.getContext('2d');
  if (!canvas || !ctx) return;

  const colors = {
    point: '#6f8da3',
    surface: '#2f6c8f',
    threeDdfa: '#66d9ff',
    face: '#65c8ff',
    eye: '#8ff2c5',
    brow: '#c9f7a3',
    mouth: '#ff9fbd',
    'mouth-opening': '#ff9fbd',
    jaw: '#ffd166',
    nose: '#d9e8ff',
    cheek: '#c7a6ff',
    forehead: '#9db7c9'
  };
  const defaultView = { yaw: -0.42, pitch: -0.12, zoom: 0.56 };
  let rotation = { ...defaultView };
  const view = { points: true, surface: true, features: true, ghosts: false, headLock: true, axes: true };
  let activeIndex = findDefaultSampleIndex();
  let activeMeshMode = preferredMeshModeForSample(samples[activeIndex]);
  let dragging = false;
  let last = null;
  let refreshTimer = null;

  bindAutoRefresh();
  bindMeshModeButtons();
  bindToggle(togglePoints, 'points');
  bindToggle(toggleSurface, 'surface');
  bindToggle(toggleFeatures, 'features');
  bindToggle(toggleGhosts, 'ghosts');
  bindToggle(toggleHeadLock, 'headLock');
  bindToggle(toggleAxes, 'axes');

  if (buttons) {
    samples.forEach((sample, index) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = `#${index + 1} ${formatTime(sample.capturedAtUtc || sample.CapturedAtUtc)}`;
      button.addEventListener('click', () => {
        activeIndex = index;
        activeMeshMode = preferredMeshModeForSample(samples[activeIndex]);
        syncMeshModeButtons();
        draw();
      });
      buttons.appendChild(button);
    });
  }

  document.querySelectorAll('[data-sample-row]').forEach(row => {
    row.addEventListener('click', () => {
      activeIndex = Number(row.getAttribute('data-sample-row'));
      activeMeshMode = preferredMeshModeForSample(samples[activeIndex]);
      syncMeshModeButtons();
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
    rotation.yaw += (event.clientX - last.x) * 0.008;
    rotation.pitch = Math.max(-1.0, Math.min(1.0, rotation.pitch + (event.clientY - last.y) * 0.006));
    last = { x: event.clientX, y: event.clientY };
    draw();
  });
  canvas.addEventListener('pointerup', releaseDrag);
  canvas.addEventListener('pointercancel', releaseDrag);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    rotation.zoom = Math.max(0.45, Math.min(2.8, rotation.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));
    draw();
  }, { passive: false });
  canvas.addEventListener('dblclick', () => {
    rotation = { ...defaultView };
    draw();
  });

  new ResizeObserver(resize).observe(canvas);
  resize();

  function bindAutoRefresh() {
    if (!toggleAutoRefresh) return;
    const key = 'episodeMonitorLast10AutoRefreshPaused';
    const isPaused = () => {
      try { return window.localStorage?.getItem(key) === 'true'; }
      catch { return false; }
    };
    const setPaused = paused => {
      try { window.localStorage?.setItem(key, paused ? 'true' : 'false'); }
      catch { /* local file storage may be unavailable; the button still works for this page. */ }
      toggleAutoRefresh.textContent = paused ? 'Resume Updates' : 'Pause Updates';
      toggleAutoRefresh.setAttribute('aria-pressed', paused ? 'false' : 'true');
      toggleAutoRefresh.dataset.refreshPaused = paused ? 'true' : 'false';
      if (refreshStatus) {
        refreshStatus.textContent = paused
          ? 'Updates paused. This viewer will not reload until you click Resume Updates.'
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
    toggleAutoRefresh.addEventListener('click', () => setPaused(!isPaused()));
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

  function bindMeshModeButtons() {
    selectThreeDdfaView?.addEventListener('click', () => setMeshMode('3ddfa'));
    selectMediaPipeView?.addEventListener('click', () => setMeshMode('mediapipe'));
    syncMeshModeButtons();
  }

  function setMeshMode(mode) {
    if (mode === '3ddfa' && !sampleHasThreeDdfa(samples[activeIndex])) {
      return;
    }

    activeMeshMode = mode;
    syncMeshModeButtons();
    draw();
  }

  function syncMeshModeButtons() {
    const hasThreeDdfa = sampleHasThreeDdfa(samples[activeIndex]);
    if (activeMeshMode === '3ddfa' && !hasThreeDdfa) {
      activeMeshMode = 'mediapipe';
    }

    if (selectThreeDdfaView) {
      selectThreeDdfaView.disabled = !hasThreeDdfa;
      selectThreeDdfaView.setAttribute('aria-pressed', activeMeshMode === '3ddfa' ? 'true' : 'false');
      selectThreeDdfaView.title = hasThreeDdfa ? 'Show the 3DDFA full-resolution dense mesh for this sample.' : '3DDFA full-resolution mesh has not attached to this sample yet.';
    }

    if (selectMediaPipeView) {
      selectMediaPipeView.setAttribute('aria-pressed', activeMeshMode === 'mediapipe' ? 'true' : 'false');
    }
  }

  function findDefaultSampleIndex() {
    for (let index = samples.length - 1; index >= 0; index--) {
      if (sampleHasThreeDdfa(samples[index])) {
        return index;
      }
    }

    return samples.length > 0 ? samples.length - 1 : -1;
  }

  function preferredMeshModeForSample(sample) {
    return sampleHasThreeDdfa(sample) ? '3ddfa' : 'mediapipe';
  }

  function sampleHasThreeDdfa(sample) {
    const threeDdfa = getThreeDdfa(sample);
    const vertices = threeDdfa ? (threeDdfa.vertices || threeDdfa.Vertices || []) : [];
    return vertices.length > 0;
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

  function chooseMesh(sample) {
    const threeDdfa = sample.threeDdfaFullResolution || sample.ThreeDdfaFullResolution || null;
    const vertices = threeDdfa ? (threeDdfa.vertices || threeDdfa.Vertices || []) : [];
    if (activeMeshMode === '3ddfa' && vertices.length > 0) {
      const edges = threeDdfa.topologyEdges || threeDdfa.TopologyEdges || [];
      const denseCount = threeDdfa.denseVertexCount ?? threeDdfa.DenseVertexCount ?? vertices.length;
      return {
        source: '3ddfa',
        label: `3DDFA full-res dense mesh (${denseCount} vertices)`,
        points: vertices,
        edges,
        groups: [],
        snapshot: threeDdfa,
        headLockSupported: false,
        warning: ''
      };
    }

    return {
      source: 'mediapipe',
      label: sample.denseMeshTopology || sample.DenseMeshTopology || 'MediaPipe dense mesh',
      points: sample.points || sample.Points || [],
      edges: sample.wireframeEdges || sample.WireframeEdges || [],
      groups: sample.featureGroups || sample.FeatureGroups || [],
      snapshot: null,
      headLockSupported: true,
      warning: activeMeshMode === '3ddfa' ? '3DDFA full mesh is not available for this selected sample; showing MediaPipe wireframe instead' : ''
    };
  }

  function draw() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);
    ctx.fillStyle = '#061019';
    ctx.fillRect(0, 0, rect.width, rect.height);
    drawGrid(rect);

    const sample = samples[activeIndex];
    document.querySelectorAll('[data-sample-row]').forEach(row => row.dataset.active = row.getAttribute('data-sample-row') === String(activeIndex));
    if (buttons) {
      [...buttons.children].forEach((button, index) => button.setAttribute('aria-pressed', index === activeIndex ? 'true' : 'false'));
    }
    syncMeshModeButtons();

    if (!sample) {
      overlay.textContent = 'Waiting for MediaPipe feature-lock samples with good eye and mouth feature lock.';
      if (details) details.innerHTML = '<tr><td class="muted">No sample selected.</td></tr>';
      return;
    }

    if (view.ghosts) {
      drawGhostSamples(rect, activeIndex);
    }

    const mesh = chooseMesh(sample);
    const points = mesh.points;
    const groups = mesh.groups;
    const edges = mesh.edges;
    const normalizedSample = normalize(points, sample, mesh);
    const normalized = normalizedSample.points;
    const byIndex = new Map(normalized.map(point => [point.index, point]));
    const featureIndexes = new Set(groups.flatMap(group => group.landmarkIndices || group.LandmarkIndices || []));

    if (view.surface) {
      drawEdges(edges.filter(isSurfaceEdge), byIndex, rect, 0.74, 0.85);
    }

    if (view.points) {
      const projected = normalized.map(point => ({
        point,
        role: featureIndexes.has(point.index) ? 'feature' : 'point',
        projected: project(point, rect)
      })).sort((a, b) => a.projected.z - b.projected.z);

      for (const item of projected) {
        ctx.globalAlpha = item.role === 'feature' ? 0.62 : 0.34;
        ctx.fillStyle = mesh.source === '3ddfa' ? colors.threeDdfa : item.role === 'feature' ? '#dcefff' : colors.point;
        const radius = mesh.source === '3ddfa' ? 0.85 : item.role === 'feature' ? 2.0 : 1.25;
        ctx.beginPath();
        ctx.arc(item.projected.x, item.projected.y, radius * item.projected.scale, 0, Math.PI * 2);
        ctx.fill();
      }
    }

    if (view.features) {
      const lineItems = groups.map(group => {
        const indices = group.landmarkIndices || group.LandmarkIndices || [];
        const projectedLine = indices.map(index => byIndex.get(index)).filter(Boolean).map(point => project(point, rect));
        const depth = projectedLine.length ? projectedLine.reduce((sum, point) => sum + point.z, 0) / projectedLine.length : 0;
        return { group, projectedLine, depth };
      }).filter(item => item.projectedLine.length >= 2)
        .sort((a, b) => a.depth - b.depth);

      for (const item of lineItems) {
        const role = item.group.role || item.group.Role || 'point';
        const closed = item.group.closed ?? item.group.Closed;
        const confidence = item.group.confidencePercent ?? item.group.ConfidencePercent ?? 70;
        ctx.save();
        ctx.globalAlpha = Math.max(0.35, Math.min(0.95, confidence / 100));
        ctx.strokeStyle = colors[role] || '#dcefff';
        ctx.lineWidth = role === 'face' ? 1.6 : 2.5;
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.beginPath();
        ctx.moveTo(item.projectedLine[0].x, item.projectedLine[0].y);
        for (let i = 1; i < item.projectedLine.length; i++) {
          ctx.lineTo(item.projectedLine[i].x, item.projectedLine[i].y);
        }
        if (closed) ctx.closePath();
        ctx.stroke();
        ctx.restore();
      }
    }

    if (view.axes && normalizedSample.frame) {
      drawHeadAxes(normalizedSample.frame, rect);
    }

    ctx.globalAlpha = 1;
    const stability = report.headLockedStability || report.HeadLockedStability || {};
    overlay.innerHTML = `<strong>${escapeHtml(mesh.label)}</strong><br>${formatTime(sample.capturedAtUtc || sample.CapturedAtUtc)} | ${points.length} points | ${edges.length} edges<br>quality ${formatNumber(sample.overallQualityPercent ?? sample.OverallQualityPercent)}% | eyes ${formatNumber(sample.eyeQualityPercent ?? sample.EyeQualityPercent)}% | brows ${formatNumber(sample.browQualityPercent ?? sample.BrowQualityPercent)}% | mouth ${formatNumber(sample.mouthQualityPercent ?? sample.MouthQualityPercent)}%<br>X/Y ${formatNumber(sample.xHorizontalPercent ?? sample.XHorizontalPercent)}% / ${formatNumber(sample.yVerticalPercent ?? sample.YVerticalPercent)}% | selected A/B/C ${formatNumber(sample.headPitchDegrees ?? sample.HeadPitchDegrees)}/${formatNumber(sample.headYawDegrees ?? sample.HeadYawDegrees)}/${formatNumber(sample.headRollDegrees ?? sample.HeadRollDegrees)}<br>Z ${formatApparentZ(sample)} | q ${formatNumber(sample.zConfidencePercent ?? sample.ZConfidencePercent)}% | ${escapeHtml(sample.rotationSource || sample.RotationSource || 'pose source waiting')}<br>B ${escapeHtml(stability.yawStatus || stability.YawStatus || 'waiting')} | A ${escapeHtml(stability.aStatus || stability.AStatus || 'waiting')} | C ${escapeHtml(stability.cStatus || stability.CStatus || 'waiting')}<br>Z lock ${escapeHtml(stability.zStatus || stability.ZStatus || 'waiting')} | scale range ${formatNumber(stability.zFaceScaleRangePercent ?? stability.ZFaceScaleRangePercent)}%<br>viewer ${escapeHtml(normalizedSample.mode)}${normalizedSample.warning ? ` | ${escapeHtml(normalizedSample.warning)}` : ''}${mesh.warning ? `<br>${escapeHtml(mesh.warning)}` : ''}`;
    if (details) {
      const anchorSummary = summarizeAnchors(normalizedSample.points, groups);
      details.innerHTML = `
        <tr><th>Good feature reason</th><td>${escapeHtml(sample.goodFeatureReason || sample.GoodFeatureReason || '')}</td></tr>
        <tr><th>Capture quality</th><td>${escapeHtml(sample.captureQualityLabel || sample.CaptureQualityLabel || '')} ${formatNumber(sample.captureQualityScorePercent ?? sample.CaptureQualityScorePercent)}%</td></tr>
        <tr><th>XYZABC pose</th><td>X ${formatNumber(sample.xHorizontalPercent ?? sample.XHorizontalPercent)}%, Y ${formatNumber(sample.yVerticalPercent ?? sample.YVerticalPercent)}%, Z ${formatApparentZ(sample)}, A/B/C ${formatNumber(sample.headPitchDegrees ?? sample.HeadPitchDegrees)}/${formatNumber(sample.headYawDegrees ?? sample.HeadYawDegrees)}/${formatNumber(sample.headRollDegrees ?? sample.HeadRollDegrees)}</td></tr>
        <tr><th>Pose sources</th><td>rotation ${escapeHtml(sample.rotationSource || sample.RotationSource || 'waiting')}; distance ${escapeHtml(sample.distanceSource || sample.DistanceSource || 'waiting')}; reference ${escapeHtml(sample.referenceScaleSource || sample.ReferenceScaleSource || 'waiting')}; Z ${escapeHtml(sample.zEstimateKind || sample.ZEstimateKind || 'waiting')} ${escapeHtml(sample.zQualityLabel || sample.ZQualityLabel || '')} (${formatNumber(sample.zConfidencePercent ?? sample.ZConfidencePercent)}%)</td></tr>
        <tr><th>Head lock</th><td>${escapeHtml(normalizedSample.mode)}${normalizedSample.frame ? `, eye scale ${formatRatio(normalizedSample.frame.interEyeDistance)}` : ''}${normalizedSample.warning ? `, ${escapeHtml(normalizedSample.warning)}` : ''}</td></tr>
        <tr><th>Head-local anchors</th><td>${escapeHtml(anchorSummary)}</td></tr>
        <tr><th>Reliability</th><td>face ${formatNumber(sample.faceReliabilityPercent ?? sample.FaceReliabilityPercent)}%, continuity ${formatNumber(sample.faceContinuityPercent ?? sample.FaceContinuityPercent)}%, eyes ${formatNumber(sample.eyeReliabilityPercent ?? sample.EyeReliabilityPercent)}%, mouth ${formatNumber(sample.mouthReliabilityPercent ?? sample.MouthReliabilityPercent)}%</td></tr>
        <tr><th>Brow tracking</th><td>height ${formatRatio(sample.averageBrowHeightRatio ?? sample.AverageBrowHeightRatio)}, left/right ${formatRatio(sample.leftBrowHeightRatio ?? sample.LeftBrowHeightRatio)} / ${formatRatio(sample.rightBrowHeightRatio ?? sample.RightBrowHeightRatio)}, asymmetry ${formatNumber(sample.browAsymmetryPercent ?? sample.BrowAsymmetryPercent)}%, q ${formatNumber(sample.browQualityPercent ?? sample.BrowQualityPercent)}%</td></tr>
        <tr><th>Artifact flags</th><td>one-eye artifact ${formatBool(sample.possibleOneEyeArtifact ?? sample.PossibleOneEyeArtifact)}, eye reconstructed ${formatBool((sample.leftEyeReconstructed ?? sample.LeftEyeReconstructed) || (sample.rightEyeReconstructed ?? sample.RightEyeReconstructed))}, mouth reconstructed ${formatBool(sample.mouthReconstructed ?? sample.MouthReconstructed)}, eye suppressed ${formatBool(sample.eyeArtifactSuppressed ?? sample.EyeArtifactSuppressed)}</td></tr>
        <tr><th>Wireframe</th><td>${escapeHtml(mesh.label)}: ${edges.filter(isSurfaceEdge).length} surface edges, ${edges.filter(edge => !isSurfaceEdge(edge)).length} feature edges</td></tr>
        <tr><th>Feature groups</th><td>${groups.map(group => escapeHtml(group.label || group.Label || group.id || group.Id)).join(', ')}</td></tr>`;
    }
  }

  function drawGhostSamples(rect, selectedIndex) {
    samples.forEach((sample, index) => {
      if (index === selectedIndex) return;
      const points = sample.points || sample.Points || [];
      const edges = sample.wireframeEdges || sample.WireframeEdges || [];
      const normalized = normalize(points, sample).points;
      const byIndex = new Map(normalized.map(point => [point.index, point]));
      drawEdges(edges.filter(isSurfaceEdge), byIndex, rect, 0.13, 0.45);
    });
  }

  function drawEdges(edges, byIndex, rect, alphaScale, widthScale) {
    const projectedEdges = edges.map(edge => {
      const from = byIndex.get(edge.fromIndex ?? edge.FromIndex);
      const to = byIndex.get(edge.toIndex ?? edge.ToIndex);
      if (!from || !to) return null;
      const a = project(from, rect);
      const b = project(to, rect);
      return { edge, a, b, depth: (a.z + b.z) / 2 };
    }).filter(Boolean).sort((a, b) => a.depth - b.depth);

    for (const item of projectedEdges) {
      const role = item.edge.role || item.edge.Role || 'surface';
      const source = item.edge.source || item.edge.Source || '';
      const confidence = item.edge.confidencePercent ?? item.edge.ConfidencePercent ?? 70;
      ctx.save();
      ctx.globalAlpha = Math.max(0.04, Math.min(0.9, confidence / 100 * alphaScale));
      ctx.strokeStyle = source === '3ddfa-full-resolution-topology' ? colors.threeDdfa : colors[role] || colors.surface;
      ctx.lineWidth = Math.max(0.35, (source === '3ddfa-full-resolution-topology' ? 0.42 : role === 'surface' ? 0.75 : 1.4) * widthScale);
      ctx.lineCap = 'round';
      ctx.beginPath();
      ctx.moveTo(item.a.x, item.a.y);
      ctx.lineTo(item.b.x, item.b.y);
      ctx.stroke();
      ctx.restore();
    }
  }

  function drawHeadAxes(frame, rect) {
    const origin = project({ index: -1, x: 0, y: 0, z: 0 }, rect);
    const axes = [
      { label: 'X', color: '#65c8ff', end: { index: -2, x: 0.22, y: 0, z: 0 } },
      { label: 'Y', color: '#ffd166', end: { index: -3, x: 0, y: 0.22, z: 0 } },
      { label: 'Z', color: '#f4f7ff', end: { index: -4, x: 0, y: 0, z: 0.22 } }
    ];

    ctx.save();
    ctx.lineWidth = 2;
    ctx.font = '12px Segoe UI, Arial, sans-serif';
    for (const axis of axes) {
      const end = project(axis.end, rect);
      ctx.globalAlpha = 0.88;
      ctx.strokeStyle = axis.color;
      ctx.fillStyle = axis.color;
      ctx.beginPath();
      ctx.moveTo(origin.x, origin.y);
      ctx.lineTo(end.x, end.y);
      ctx.stroke();
      ctx.fillText(axis.label, end.x + 5, end.y + 4);
    }
    ctx.restore();
  }

  function isSurfaceEdge(edge) {
    const role = edge.role || edge.Role || '';
    const source = edge.source || edge.Source || '';
    return role === 'surface' || source === 'mediapipe-face-tessellation' || source === 'curated-facial-scaffold' || source === 'adaptive-local-neighbors' || source === 'adaptive-delaunay';
  }

  function normalize(points, sample, mesh) {
    const raw = points.map(point => ({
      index: point.index ?? point.Index,
      x: point.x ?? point.X,
      y: point.y ?? point.Y,
      z: point.z ?? point.Z
    })).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z));
    if (raw.length === 0) return { points: [], mode: 'waiting for mesh points', frame: null, warning: 'no usable points' };
    if (view.headLock && mesh?.headLockSupported !== false) {
      const headLocked = normalizeHeadLocked(raw, sample);
      if (headLocked) {
        return headLocked;
      }
    }

    if (mesh?.source === '3ddfa') {
      return normalizeByBounds(raw, '3DDFA full-res face-bounds view', '3DDFA vertices are already reconstructed face-space; MediaPipe head-lock anchors are not used');
    }

    return normalizeByBounds(raw, view.headLock ? 'camera-bounds fallback' : 'camera-bounds view', view.headLock ? 'head anchors unavailable' : '');
  }

  function normalizeByBounds(raw, mode, warning) {
    let minX = Number.POSITIVE_INFINITY;
    let maxX = Number.NEGATIVE_INFINITY;
    let minY = Number.POSITIVE_INFINITY;
    let maxY = Number.NEGATIVE_INFINITY;
    let minZ = Number.POSITIVE_INFINITY;
    let maxZ = Number.NEGATIVE_INFINITY;
    for (const point of raw) {
      if (point.x < minX) minX = point.x;
      if (point.x > maxX) maxX = point.x;
      if (point.y < minY) minY = point.y;
      if (point.y > maxY) maxY = point.y;
      if (point.z < minZ) minZ = point.z;
      if (point.z > maxZ) maxZ = point.z;
    }
    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;
    const centerZ = (minZ + maxZ) / 2;
    const scale = 1 / Math.max(0.001, maxX - minX, maxY - minY);
    return {
      points: raw.map(point => ({
        index: point.index,
        x: (point.x - centerX) * scale,
        y: (point.y - centerY) * scale,
        z: (point.z - centerZ) * scale * 0.62
      })),
      mode,
      frame: null,
      warning
    };
  }

  function normalizeHeadLocked(raw, sample) {
    const byIndex = new Map(raw.map(point => [point.index, point]));
    const groups = sample?.featureGroups || sample?.FeatureGroups || [];
    const leftEye = centerOfGroup(groups, byIndex, 'left_eye') || centerOfIndices(byIndex, [33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7]);
    const rightEye = centerOfGroup(groups, byIndex, 'right_eye') || centerOfIndices(byIndex, [362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382]);
    const chin = byIndex.get(152) || centerOfGroup(groups, byIndex, 'jaw') || centerOfIndices(byIndex, [152, 148, 176, 149, 150]);
    if (!leftEye || !rightEye || !chin) {
      return null;
    }

    const eyeMid = multiply(add(leftEye, rightEye), 0.5);
    let xAxis = subtract(rightEye, leftEye);
    const interEyeDistance = length(xAxis);
    if (interEyeDistance < 0.0001) {
      return null;
    }

    xAxis = normalizeVector(xAxis);
    let yAxis = subtract(chin, eyeMid);
    yAxis = subtract(yAxis, multiply(xAxis, dot(yAxis, xAxis)));
    if (length(yAxis) < 0.0001) {
      return null;
    }

    yAxis = normalizeVector(yAxis);
    let zAxis = normalizeVector(cross(xAxis, yAxis));
    if (length(zAxis) < 0.0001) {
      return null;
    }

    yAxis = normalizeVector(cross(zAxis, xAxis));
    const faceHeight = distance(eyeMid, chin);
    const scale = Math.max(0.0001, Math.max(interEyeDistance * 2.35, faceHeight * 1.28));
    return {
      points: raw.map(point => {
        const relative = subtract(point, eyeMid);
        return {
          index: point.index,
          x: dot(relative, xAxis) / scale,
          y: dot(relative, yAxis) / scale,
          z: dot(relative, zAxis) / scale
        };
      }),
      mode: 'head-locked view',
      frame: {
        interEyeDistance,
        faceHeight,
        scale,
        xAxis,
        yAxis,
        zAxis
      },
      warning: ''
    };
  }

  function summarizeAnchors(points, groups) {
    const byIndex = new Map(points.map(point => [point.index, point]));
    const interesting = ['left_eye', 'right_eye', 'left_brow', 'right_brow', 'nose_bridge', 'nose_base', 'forehead', 'left_cheek', 'right_cheek', 'outer_lip', 'inner_lip', 'jaw'];
    const summaries = [];
    for (const id of interesting) {
      const center = centerOfGroup(groups, byIndex, id);
      if (!center) continue;
      summaries.push(`${labelForAnchor(id)} (${center.x.toFixed(3)}, ${center.y.toFixed(3)}, ${center.z.toFixed(3)})`);
    }

    return summaries.length === 0 ? 'waiting for feature groups' : summaries.join('; ');
  }

  function centerOfGroup(groups, byIndex, id) {
    const group = groups.find(item => String(item.id || item.Id || '').toLowerCase() === id);
    if (!group) return null;
    return centerOfIndices(byIndex, group.landmarkIndices || group.LandmarkIndices || []);
  }

  function centerOfIndices(byIndex, indices) {
    const values = indices.map(index => byIndex.get(index)).filter(Boolean);
    if (values.length === 0) return null;
    return {
      x: values.reduce((sum, point) => sum + point.x, 0) / values.length,
      y: values.reduce((sum, point) => sum + point.y, 0) / values.length,
      z: values.reduce((sum, point) => sum + point.z, 0) / values.length
    };
  }

  function labelForAnchor(id) {
    return id.replace(/_/g, ' ');
  }

  function add(a, b) {
    return { x: a.x + b.x, y: a.y + b.y, z: a.z + b.z };
  }

  function subtract(a, b) {
    return { x: a.x - b.x, y: a.y - b.y, z: a.z - b.z };
  }

  function multiply(a, scale) {
    return { x: a.x * scale, y: a.y * scale, z: a.z * scale };
  }

  function dot(a, b) {
    return a.x * b.x + a.y * b.y + a.z * b.z;
  }

  function cross(a, b) {
    return {
      x: a.y * b.z - a.z * b.y,
      y: a.z * b.x - a.x * b.z,
      z: a.x * b.y - a.y * b.x
    };
  }

  function length(a) {
    return Math.sqrt(dot(a, a));
  }

  function distance(a, b) {
    return length(subtract(a, b));
  }

  function normalizeVector(a) {
    const size = Math.max(0.000001, length(a));
    return multiply(a, 1 / size);
  }

  function project(point, rect) {
    const cosY = Math.cos(rotation.yaw);
    const sinY = Math.sin(rotation.yaw);
    const cosP = Math.cos(rotation.pitch);
    const sinP = Math.sin(rotation.pitch);
    const x1 = point.x * cosY + point.z * sinY;
    const z1 = -point.x * sinY + point.z * cosY;
    const y1 = point.y * cosP - z1 * sinP;
    const z2 = point.y * sinP + z1 * cosP;
    const depth = 1.6 + z2;
    const zoom = Math.min(rect.width, rect.height) * 0.62 * rotation.zoom / Math.max(0.35, depth);
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

  function formatBool(value) {
    return value ? 'yes' : 'no';
  }

  function formatRatio(value) {
    return Number.isFinite(Number(value)) ? `${(Number(value) * 100).toFixed(1).replace(/\.0$/, '')}%` : '--';
  }

  function getThreeDdfa(sample) {
    if (!sample) {
      return null;
    }

    return sample.threeDdfaFullResolution || sample.ThreeDdfaFullResolution || null;
  }

  function formatCompactThreeDdfa(sample) {
    const threeDdfa = getThreeDdfa(sample);
    if (!threeDdfa) {
      return '3DDFA full-res waiting for an async reconstruction match';
    }

    return `3DDFA full-res A/B/C ${formatNumber(threeDdfa.aRotationAroundXDegrees ?? threeDdfa.ARotationAroundXDegrees)}/${formatNumber(threeDdfa.bRotationAroundYDegrees ?? threeDdfa.BRotationAroundYDegrees)}/${formatNumber(threeDdfa.cRotationAroundZDegrees ?? threeDdfa.CRotationAroundZDegrees)} | vertices ${formatInteger(threeDdfa.vertexCount ?? threeDdfa.VertexCount ?? threeDdfa.denseVertexCount ?? threeDdfa.DenseVertexCount)} | edges ${formatInteger(threeDdfa.edgeCount ?? threeDdfa.EdgeCount)} | confidence ${formatNumber(threeDdfa.reconstructionConfidencePercent ?? threeDdfa.ReconstructionConfidencePercent)}%`;
  }

  function formatThreeDdfaDetails(sample) {
    const threeDdfa = getThreeDdfa(sample);
    if (!threeDdfa) {
      return '<span class="muted">Waiting for the 3DDFA async lane to attach a full-resolution reconstruction to this sample.</span>';
    }

    const warnings = threeDdfa.warnings || threeDdfa.Warnings || [];
    const capturedAt = threeDdfa.capturedAtUtc || threeDdfa.CapturedAtUtc;
    const stride = threeDdfa.denseSampleStride ?? threeDdfa.DenseSampleStride;
    const source = threeDdfa.source || threeDdfa.Source || '3DDFA_V2 ONNX';
    const poseSource = threeDdfa.poseSource || threeDdfa.PoseSource || '3DDFA pose solver';
    const warningText = warnings.length > 0
      ? `<span class="muted">warnings: ${warnings.map(escapeHtml).join('; ')}</span>`
      : '<span class="muted">no 3DDFA warnings</span>';
    return `${escapeHtml(source)} captured ${formatTime(capturedAt)}; vertices ${formatInteger(threeDdfa.vertexCount ?? threeDdfa.VertexCount ?? threeDdfa.denseVertexCount ?? threeDdfa.DenseVertexCount)}, topology edges ${formatInteger(threeDdfa.edgeCount ?? threeDdfa.EdgeCount)}, stride ${formatInteger(stride)}; A/B/C ${formatNumber(threeDdfa.aRotationAroundXDegrees ?? threeDdfa.ARotationAroundXDegrees)}/${formatNumber(threeDdfa.bRotationAroundYDegrees ?? threeDdfa.BRotationAroundYDegrees)}/${formatNumber(threeDdfa.cRotationAroundZDegrees ?? threeDdfa.CRotationAroundZDegrees)} from ${escapeHtml(poseSource)}; confidence ${formatNumber(threeDdfa.reconstructionConfidencePercent ?? threeDdfa.ReconstructionConfidencePercent)}%. ${warningText}`;
  }

  function formatApparentZ(sample) {
    const apparent = sample.apparentDistanceUnits ?? sample.ApparentDistanceUnits;
    const relative = sample.relativeDistanceScale ?? sample.RelativeDistanceScale;
    const inches = sample.distanceInches ?? sample.DistanceInches;
    const parts = [];
    if (Number.isFinite(Number(apparent))) parts.push(`${formatNumber(apparent)} apparent`);
    if (Number.isFinite(Number(relative))) parts.push(`${formatNumber(relative)}x ref`);
    if (Number.isFinite(Number(inches))) parts.push(`${formatNumber(inches)} in`);
    return parts.length ? parts.join(', ') : 'waiting';
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

    private static string H(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string BuildStabilityRows(LastGoodFeatureMeshStabilityReport stability)
    {
        if (stability.Features.Count == 0)
        {
            return "<tr><td colspan=\"5\" class=\"muted\">Head-locked feature stability is waiting for more samples.</td></tr>";
        }

        return string.Concat(stability.Features.Select(feature =>
            $"<tr><td>{H(feature.Label)}</td><td>{H(feature.Status)}</td><td>{feature.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{feature.MaximumDriftPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td><td>{feature.AverageDriftPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</td></tr>"));
    }

    private static string FormatSamplePointCounts(LastGoodFeatureMeshSample sample)
    {
        return sample.PointCount.ToString("n0", CultureInfo.InvariantCulture);
    }

    private static string BuildYawFindings(LastGoodFeatureMeshStabilityReport stability)
    {
        return BuildFindings(stability.YawFindings, "No B-axis head-turn findings.");
    }

    private static string BuildFindings(IReadOnlyList<string> findings, string empty)
    {
        return findings.Count == 0
            ? $"<li>{H(empty)}</li>"
            : string.Concat(findings.Select(finding => $"<li>{H(finding)}</li>"));
    }

    private static string FormatYawHealth(LastGoodFeatureMeshStabilityReport stability)
    {
        return FormatAxisHealth(stability.YawHealthPercent);
    }

    private static string FormatAxisHealth(double healthPercent)
    {
        return healthPercent <= 0d
            ? "waiting"
            : $"{healthPercent.ToString("0.#", CultureInfo.InvariantCulture)}%";
    }
}

public sealed record LastGoodFeatureMeshFiles(string JsonPath, string HtmlPath);
