using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations.Review;

public sealed class AvatarDataReviewServer : IAsyncDisposable
{
	private sealed record ReviewContext(string ProfileFolder, string SubjectId, string SubjectDisplayName, string? BackendId);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly AvatarObservationRepository _repository;

	private readonly AvatarModelStore _modelStore = new AvatarModelStore();

	private readonly object _gate = new object();

	private readonly HashSet<Task> _clientTasks = new HashSet<Task>();

	private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();

	private readonly string _sessionToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

	private TcpListener? _listener;

	private CancellationTokenSource? _cancellation;

	private Task? _acceptLoopTask;

	private ReviewContext? _context;

	private int _port;

	private bool _disposed;

	public AvatarDataReviewServer(AvatarObservationRepository repository)
	{
		_repository = repository ?? throw new ArgumentNullException("repository");
	}

	public Uri StartOrUpdate(string profileFolder, string subjectId, string subjectDisplayName, string? backendId = null)
	{
		ReviewContext context = new ReviewContext(Path.GetFullPath(profileFolder), subjectId, subjectDisplayName, backendId);
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_context = context;
			if (_listener == null)
			{
				_cancellation = new CancellationTokenSource();
				_listener = new TcpListener(IPAddress.Loopback, 0);
				_listener.Start();
				_port = ((IPEndPoint)_listener.LocalEndpoint).Port;
				_acceptLoopTask = Task.Run(() => AcceptLoopAsync(_listener, _cancellation.Token));
			}
			return BuildReviewUri();
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task acceptLoopTask;
		TcpClient[] array;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_cancellation?.Cancel();
			_listener?.Stop();
			acceptLoopTask = _acceptLoopTask;
			array = _clients.ToArray();
		}
		TcpClient[] array2 = array;
		foreach (TcpClient tcpClient in array2)
		{
			try
			{
				tcpClient.Dispose();
			}
			catch
			{
			}
		}
		await IgnoreShutdownException(acceptLoopTask, TimeSpan.FromSeconds(2L)).ConfigureAwait(continueOnCapturedContext: false);
		Task[] tasks;
		lock (_gate)
		{
			tasks = _clientTasks.ToArray();
		}
		await IgnoreShutdownException(Task.WhenAll(tasks), TimeSpan.FromSeconds(2L)).ConfigureAwait(continueOnCapturedContext: false);
		lock (_gate)
		{
			_cancellation?.Dispose();
			_cancellation = null;
			_listener = null;
			_acceptLoopTask = null;
			_clientTasks.Clear();
			_clients.Clear();
		}
	}

	private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				Task task = HandleClientSafelyAsync(client, cancellationToken);
				lock (_gate)
				{
					if (_disposed)
					{
						client.Dispose();
						break;
					}
					_clients.Add(client);
					_clientTasks.Add(task);
				}
				task.ContinueWith(delegate(Task completedTask)
				{
					lock (_gate)
					{
						_clientTasks.Remove(completedTask);
						_clients.Remove(client);
					}
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (SocketException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		{
			try
			{
				client.NoDelay = true;
				await HandleClientAsync(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			catch (IOException)
			{
			}
			catch (SocketException)
			{
			}
			catch (Exception ex4)
			{
				try
				{
					await WriteTextResponseAsync(client.GetStream(), HttpStatusCode.InternalServerError, "text/plain; charset=utf-8", "Avatar data review failed: " + ex4.Message, "no-store", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch
				{
				}
			}
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		NetworkStream stream = client.GetStream();
		using StreamReader reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
		string text = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		string[] requestParts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		if (requestParts.Length < 2 || !string.Equals(requestParts[0], "GET", StringComparison.OrdinalIgnoreCase))
		{
			await WriteTextResponseAsync(stream, HttpStatusCode.MethodNotAllowed, "text/plain; charset=utf-8", "Only GET is supported.", "no-store", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		for (int headerCount = 0; headerCount < 100; headerCount++)
		{
			if (string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
			{
				break;
			}
		}
		string text2 = requestParts[1];
		if (Uri.TryCreate(text2, UriKind.Absolute, out Uri result))
		{
			text2 = result.PathAndQuery;
		}
		string text3 = text2.Split('?', 2)[0];
		string text4 = "/review/" + _sessionToken + "/";
		if (!text3.StartsWith(text4, StringComparison.Ordinal))
		{
			await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string text5 = Uri.UnescapeDataString(text3.Substring(text4.Length)).Trim('/');
		if (string.IsNullOrEmpty(text5) || string.Equals(text5, "index.html", StringComparison.OrdinalIgnoreCase))
		{
			await WriteTextResponseAsync(stream, HttpStatusCode.OK, "text/html; charset=utf-8", "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n<title>Review FLAME Data</title>\n<style>\n:root{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--mesh:#66d9ff;--good:#80e0a4;--warn:#ffd27a}\n*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}button,input{font:inherit}main{display:grid;grid-template-columns:minmax(620px,1fr) minmax(340px,430px);gap:12px;padding:12px;min-height:100vh}.workspace,.sidebar{border:1px solid var(--line);background:var(--panel);border-radius:6px}.workspace{padding:12px;display:grid;grid-template-rows:auto minmax(0,1fr);gap:10px}.viewer-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;min-height:620px}.viewer-panel{display:grid;grid-template-rows:auto minmax(0,1fr);gap:6px;min-width:0}.viewer-title{display:flex;align-items:center;justify-content:space-between;gap:8px}.viewer-title h2{margin:0;font-size:16px}.viewer-title span{color:var(--muted);font-size:12px}canvas{width:100%;height:100%;min-height:570px;display:block;border:1px solid #193149;background:#061019;touch-action:none}#mesh3d{cursor:grab}#mesh3d[data-dragging=true]{cursor:grabbing}.toolbar,.navigation{display:flex;flex-wrap:wrap;align-items:center;gap:7px}.toolbar{justify-content:space-between}.toolbar-group{display:flex;flex-wrap:wrap;gap:7px}button{background:#102033;color:var(--text);border:1px solid #37506a;padding:7px 10px;min-height:34px}button:hover{border-color:#65c8ff}button:disabled{opacity:.45}button[aria-pressed=true]{background:#1c405c;border-color:#65c8ff}button[data-paused=true]{background:#4a2630;border-color:#ff9fbd}.sidebar{padding:12px;display:grid;grid-template-rows:auto auto auto minmax(220px,1fr) auto;gap:10px;max-height:calc(100vh - 24px);position:sticky;top:12px}.sidebar h1{font-size:22px;margin:0}.summary{color:var(--muted)}.summary strong{color:var(--text)}input{width:100%;background:#07111a;color:var(--text);border:1px solid #37506a;padding:8px}.scan-list{overflow:auto;border:1px solid #1c3042;background:#071019}.scan-item{width:100%;display:grid;grid-template-columns:68px 1fr auto;gap:8px;text-align:left;border:0;border-bottom:1px solid #1c3042;background:transparent;padding:8px}.scan-item:hover{background:#102033}.scan-item[aria-pressed=true]{background:#17334a;box-shadow:inset 3px 0 #65c8ff}.scan-number{color:var(--muted)}.scan-main{min-width:0}.scan-main strong,.scan-main span{display:block}.scan-main span{color:var(--muted);font-size:12px}.scan-score{color:var(--good);white-space:nowrap}.details{border-top:1px solid #1c3042;padding-top:8px;max-height:240px;overflow:auto}.details table{width:100%;border-collapse:collapse}.details td,.details th{border-bottom:1px solid #1c3042;padding:5px 3px;text-align:left;vertical-align:top}.details th{width:105px;color:var(--muted);font-weight:600}.status{color:var(--muted)}.status[data-error=true]{color:#ff9a9a}@media(max-width:1150px){main{grid-template-columns:1fr}.sidebar{position:static;max-height:none}.viewer-grid{min-height:520px}.scan-list{max-height:420px}}@media(max-width:760px){.viewer-grid{grid-template-columns:1fr}.viewer-panel{min-height:480px}canvas{min-height:430px}}\n</style>\n</head>\n<body>\n<main>\n  <section class=\"workspace\" aria-label=\"Avatar scan viewers\">\n    <div class=\"toolbar\">\n      <div class=\"toolbar-group\">\n        <button type=\"button\" id=\"togglePoints\" aria-pressed=\"true\">Points</button>\n        <button type=\"button\" id=\"toggleSurface\" aria-pressed=\"true\">Wireframe</button>\n        <button type=\"button\" id=\"resetView\">Reset 3D View</button>\n      </div>\n      <div class=\"toolbar-group\">\n        <button type=\"button\" id=\"toggleUpdates\" aria-pressed=\"true\">Pause Updates</button>\n        <button type=\"button\" id=\"refreshScans\">Refresh Scans</button>\n      </div>\n    </div>\n    <div class=\"viewer-grid\">\n      <div class=\"viewer-panel\">\n        <div class=\"viewer-title\"><h2 id=\"meshTitle\">Current Recurrent FLAME Identity</h2><span id=\"meshSubtitle\">Latest complete model; drag to rotate; wheel to zoom</span></div>\n        <canvas id=\"mesh3d\"></canvas>\n      </div>\n      <div class=\"viewer-panel\">\n        <div class=\"viewer-title\"><h2>Saved Frame With Projected Fit</h2><span>Image-space fit paired with this scan</span></div>\n        <canvas id=\"photoMesh\"></canvas>\n      </div>\n    </div>\n  </section>\n  <aside class=\"sidebar\">\n    <div>\n      <h1 id=\"reviewTitle\">Review FLAME Data</h1>\n      <div class=\"summary\" id=\"summary\">Loading stored FLAME scans...</div>\n    </div>\n    <div class=\"navigation\">\n      <button type=\"button\" id=\"newerScan\">Newer</button>\n      <button type=\"button\" id=\"olderScan\">Older</button>\n      <span class=\"status\" id=\"position\">--</span>\n    </div>\n    <input id=\"scanFilter\" type=\"search\" placeholder=\"Filter by date, pose, source, or trust\" aria-label=\"Filter stored scans\">\n    <div class=\"scan-list\" id=\"scanList\" aria-label=\"Stored avatar scans\"></div>\n    <div>\n      <div class=\"status\" id=\"status\">Connecting to Avatar Builder...</div>\n      <div class=\"details\" id=\"details\"></div>\n    </div>\n  </aside>\n</main>\n<script>\n(() => {\n  const meshCanvas = document.getElementById('mesh3d');\n  const photoCanvas = document.getElementById('photoMesh');\n  const meshContext = meshCanvas.getContext('2d');\n  const photoContext = photoCanvas.getContext('2d');\n  const elements = {\n    summary: document.getElementById('summary'), position: document.getElementById('position'),\n    list: document.getElementById('scanList'), filter: document.getElementById('scanFilter'),\n    status: document.getElementById('status'), details: document.getElementById('details'),\n    newer: document.getElementById('newerScan'), older: document.getElementById('olderScan'),\n    reviewTitle: document.getElementById('reviewTitle'), meshTitle: document.getElementById('meshTitle'),\n    meshSubtitle: document.getElementById('meshSubtitle')\n  };\n  const view = { points: true, surface: true, yaw: -0.38, pitch: -0.10, zoom: 0.82 };\n  const topologyCache = new Map();\n  const imageCache = new Map();\n  let manifest = null;\n  let visibleScans = [];\n  let activeId = '';\n  let activeScan = null;\n  let activeIdentityModel = null;\n  let activeTopology = [];\n  let identityTopology = [];\n  let activeImage = null;\n  let normalizedVertices = [];\n  let normalizedByIndex = new Map();\n  let loadGeneration = 0;\n  let dragging = false;\n  let dragStart = null;\n  let drawPending = false;\n  let updateTimer = null;\n  let standardModelCheckpointReview = false;\n\n  bindToggle('togglePoints', 'points');\n  bindToggle('toggleSurface', 'surface');\n  document.getElementById('resetView').addEventListener('click', resetView);\n  document.getElementById('refreshScans').addEventListener('click', () => refreshManifest(true));\n  elements.filter.addEventListener('input', applyFilter);\n  elements.newer.addEventListener('click', () => moveSelection(-1));\n  elements.older.addEventListener('click', () => moveSelection(1));\n  bindUpdateToggle();\n  bindMeshInteraction();\n  new ResizeObserver(scheduleDraw).observe(meshCanvas);\n  new ResizeObserver(scheduleDraw).observe(photoCanvas);\n  window.addEventListener('keydown', event => {\n    if (event.target === elements.filter) return;\n    if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') { event.preventDefault(); moveSelection(-1); }\n    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') { event.preventDefault(); moveSelection(1); }\n  });\n\n  refreshManifest(false);\n\n  async function refreshManifest(keepSelection) {\n    setStatus('Reading stored scan catalog...', false);\n    try {\n      const [manifestResponse, modelResponse] = await Promise.all([\n        fetch('api/manifest', { cache: 'no-store' }),\n        fetch('api/model', { cache: 'no-store' })\n      ]);\n      if (!manifestResponse.ok) throw new Error(`Catalog request failed (${manifestResponse.status})`);\n      if (!modelResponse.ok) throw new Error(`Identity model request failed (${modelResponse.status})`);\n      manifest = await manifestResponse.json();\n      activeIdentityModel = await modelResponse.json();\n      standardModelCheckpointReview = manifest.isStandardModelCheckpointReview === true;\n      applyReviewMode();\n      if (!standardModelCheckpointReview) {\n        setReviewMesh(activeIdentityModel.vertices || [], activeIdentityModel.topologyEdges || []);\n      }\n      const identityStatus = standardModelCheckpointReview\n        ? 'select a Standard Model checkpoint to inspect its canonical 3D mesh'\n        : activeIdentityModel.hasMappedIdentity\n          ? `mapped identity ${formatNumber(activeIdentityModel.finalLandmarkRmsePercent)}% RMSE, ${formatNumber(activeIdentityModel.improvementPercent)}% improvement`\n          : 'personalized identity not learned yet';\n      elements.summary.innerHTML = `<strong>${escapeHtml(manifest.subjectDisplayName || manifest.subjectId || 'Avatar')}</strong><br>${formatInteger(manifest.storedScanCount)} retained scan${manifest.storedScanCount === 1 ? '' : 's'} | revision ${formatInteger(manifest.revision)}<br>${escapeHtml(identityStatus)}`;\n      applyFilter();\n      const desired = keepSelection && visibleScans.some(scan => scan.observationId === activeId)\n        ? activeId\n        : visibleScans[0]?.observationId || '';\n      if (desired && desired !== activeId) await selectScan(desired);\n      else if (desired) renderSelectionState();\n      else clearSelection(emptyCatalogMessage());\n      setStatus(`Catalog refreshed ${new Date().toLocaleTimeString()}.`, false);\n    } catch (error) {\n      setStatus(`Could not read stored scans: ${error.message}`, true);\n    }\n  }\n\n  function applyFilter() {\n    const query = elements.filter.value.trim().toLowerCase();\n    const scans = manifest?.scans || [];\n    visibleScans = query\n      ? scans.filter(scan => searchableText(scan).includes(query))\n      : scans.slice();\n    renderScanList();\n    if (activeId && !visibleScans.some(scan => scan.observationId === activeId)) {\n      const nextId = visibleScans[0]?.observationId || '';\n      if (nextId) selectScan(nextId);\n      else clearSelection('No scans match the current filter.');\n    } else {\n      renderSelectionState();\n    }\n  }\n\n  function searchableText(scan) {\n    const date = new Date(scan.capturedAtUtc);\n    return [date.toLocaleString(), scan.source, scan.poseBucket, scan.trustDecision,\n      `model ${formatInteger(scan.modelSequenceNumber)}`,\n      `delta ${formatNumber(scan.modelCoefficientDeltaRms, 6)}`,\n      `a ${formatNumber(scan.aRotationAroundXDegrees)}`,\n      `b ${formatNumber(scan.bRotationAroundYDegrees)}`,\n      `c ${formatNumber(scan.cRotationAroundZDegrees)}`].join(' ').toLowerCase();\n  }\n\n  function renderScanList() {\n    elements.list.replaceChildren();\n    if (visibleScans.length === 0) {\n      const empty = document.createElement('div');\n      empty.className = 'status';\n      empty.style.padding = '12px';\n      empty.textContent = manifest?.storedScanCount ? 'No scans match this filter.' : emptyCatalogMessage();\n      elements.list.appendChild(empty);\n      return;\n    }\n    const fragment = document.createDocumentFragment();\n    visibleScans.forEach((scan, index) => {\n      const button = document.createElement('button');\n      button.type = 'button';\n      button.className = 'scan-item';\n      button.dataset.scanId = scan.observationId;\n      button.setAttribute('aria-pressed', scan.observationId === activeId ? 'true' : 'false');\n      const score = scan.modelSequenceNumber > 0\n        ? `delta ${formatNumber(scan.modelCoefficientDeltaRms, 4)}`\n        : `${formatNumber(scan.sampleQualityPercent)}%`;\n      button.innerHTML = `<span class=\"scan-number\">#${formatInteger(index + 1)}</span><span class=\"scan-main\"><strong>${escapeHtml(formatDateTime(scan.capturedAtUtc))}</strong><span>${escapeHtml(scan.poseBucket || 'unclassified pose')} | A/B/C ${formatNumber(scan.aRotationAroundXDegrees)} / ${formatNumber(scan.bRotationAroundYDegrees)} / ${formatNumber(scan.cRotationAroundZDegrees)}</span></span><span class=\"scan-score\">${escapeHtml(score)}</span>`;\n      button.addEventListener('click', () => selectScan(scan.observationId));\n      fragment.appendChild(button);\n    });\n    elements.list.appendChild(fragment);\n  }\n\n  async function selectScan(observationId) {\n    const summary = visibleScans.find(scan => scan.observationId === observationId)\n      || manifest?.scans?.find(scan => scan.observationId === observationId);\n    if (!summary) return;\n    activeId = observationId;\n    activeScan = null;\n    activeTopology = [];\n    activeImage = null;\n    renderSelectionState();\n    scheduleDraw();\n    const generation = ++loadGeneration;\n    setStatus(`Loading scan ${formatDateTime(summary.capturedAtUtc)}...`, false);\n    try {\n      const scanResponse = await fetch(`api/scans/${encodeURIComponent(observationId)}`, { cache: 'force-cache' });\n      if (!scanResponse.ok) throw new Error(`Scan request failed (${scanResponse.status})`);\n      const scan = await scanResponse.json();\n      const topologyHash = scan.summary?.topologySha256 || '';\n      const [topology, image] = await Promise.all([\n        loadTopology(topologyHash),\n        loadImage(`api/scans/${encodeURIComponent(observationId)}/image`, summary.hasSourceImage)\n      ]);\n      if (generation !== loadGeneration) return;\n      activeScan = scan;\n      activeTopology = topology;\n      activeImage = image;\n      if (standardModelCheckpointReview) {\n        setReviewMesh(scan.canonicalIdentityVertices || [], topology);\n      }\n      renderDetails();\n      renderSelectionState();\n      scheduleDraw();\n      const meshName = standardModelCheckpointReview ? 'Standard Model checkpoint' : 'current recurrent identity';\n      setStatus(`Loaded selected projected scan and ${formatInteger(normalizedVertices.length)} ${meshName} vertices.`, false);\n    } catch (error) {\n      if (generation !== loadGeneration) return;\n      setStatus(`Could not load selected scan: ${error.message}`, true);\n      scheduleDraw();\n    }\n  }\n\n  async function loadTopology(hash) {\n    if (!hash) return [];\n    if (!topologyCache.has(hash)) {\n      topologyCache.set(hash, fetch(`api/topologies/${encodeURIComponent(hash)}`, { cache: 'force-cache' }).then(response => {\n        if (!response.ok) throw new Error(`Topology request failed (${response.status})`);\n        return response.json();\n      }));\n    }\n    return topologyCache.get(hash);\n  }\n\n  async function loadImage(uri, available) {\n    if (!available) return null;\n    if (!imageCache.has(uri)) {\n      imageCache.set(uri, new Promise((resolve, reject) => {\n        const image = new Image();\n        image.addEventListener('load', () => resolve(image), { once: true });\n        image.addEventListener('error', () => reject(new Error('The paired source image could not be loaded.')), { once: true });\n        image.src = uri;\n      }));\n    }\n    return imageCache.get(uri);\n  }\n\n  function moveSelection(delta) {\n    if (visibleScans.length === 0) return;\n    const current = Math.max(0, visibleScans.findIndex(scan => scan.observationId === activeId));\n    const next = Math.max(0, Math.min(visibleScans.length - 1, current + delta));\n    if (visibleScans[next].observationId !== activeId) selectScan(visibleScans[next].observationId);\n  }\n\n  function renderSelectionState() {\n    document.querySelectorAll('.scan-item').forEach(button => {\n      const active = button.dataset.scanId === activeId;\n      button.setAttribute('aria-pressed', active ? 'true' : 'false');\n      if (active) button.scrollIntoView({ block: 'nearest' });\n    });\n    const index = visibleScans.findIndex(scan => scan.observationId === activeId);\n    elements.position.textContent = index >= 0 ? `${index + 1} of ${visibleScans.length}` : `0 of ${visibleScans.length}`;\n    elements.newer.disabled = index <= 0;\n    elements.older.disabled = index < 0 || index >= visibleScans.length - 1;\n  }\n\n  function renderDetails() {\n    const scan = activeScan;\n    if (!scan) { elements.details.textContent = ''; return; }\n    const item = scan.summary;\n    const warnings = scan.warnings || [];\n    const depth = coordinateExtent(scan.canonicalIdentityVertices || [], 'z');\n    const identity = activeIdentityModel || {};\n    const identityLabel = standardModelCheckpointReview ? 'Checkpoint' : 'Current identity';\n    const identityStatus = standardModelCheckpointReview\n      ? `Standard Model checkpoint #${formatInteger(item.modelSequenceNumber)}`\n      : identity.hasMappedIdentity\n        ? identity.status || 'Latest recurrent model loaded'\n        : identity.status || 'Waiting for the first recurrent model';\n    elements.details.innerHTML = `<table>\n      <tr><th>${identityLabel}</th><td>${escapeHtml(identityStatus)}</td></tr>\n      <tr><th>Captured</th><td>${escapeHtml(formatDateTime(item.capturedAtUtc))}</td></tr>\n      <tr><th>Dense model</th><td>${formatInteger(scan.canonicalIdentityVertices?.length || scan.vertices?.length || item.denseVertexCount)} canonical XYZ vertices, ${formatInteger(activeTopology.length)} edges</td></tr>\n      <tr><th>3D depth span</th><td>${formatNumber(depth, 4)} FLAME units</td></tr>\n      <tr><th>Recurrent model</th><td>#${formatInteger(item.modelSequenceNumber)}; coefficient delta RMS ${formatNumber(item.modelCoefficientDeltaRms, 6)}</td></tr>\n      <tr><th>A / B / C</th><td>${formatNumber(item.aRotationAroundXDegrees)} / ${formatNumber(item.bRotationAroundYDegrees)} / ${formatNumber(item.cRotationAroundZDegrees)} deg</td></tr>\n      <tr><th>Measured fit</th><td>${formatNumber(item.reconstructionConfidencePercent)}%</td></tr>\n      <tr><th>Quality</th><td>${formatNumber(item.sampleQualityPercent)}% overall; eyes ${formatNumber(item.eyeQualityPercent)}%; mouth ${formatNumber(item.mouthQualityPercent)}%; brows ${formatNumber(item.browQualityPercent)}%; stability ${formatNumber(item.stabilityQualityPercent)}%</td></tr>\n      <tr><th>Pose</th><td>${escapeHtml(item.poseBucket || 'unclassified')}</td></tr>\n      <tr><th>Trust</th><td>${escapeHtml(item.trustDecision || 'not recorded')}</td></tr>\n      <tr><th>Warnings</th><td>${warnings.length ? warnings.map(escapeHtml).join('; ') : '<span class=\"status\">None</span>'}</td></tr>\n    </table>`;\n  }\n\n  function coordinateExtent(points, axis) {\n    let minimum = Infinity;\n    let maximum = -Infinity;\n    for (const point of points) {\n      const value = Number(point?.[axis]);\n      if (!Number.isFinite(value)) continue;\n      minimum = Math.min(minimum, value);\n      maximum = Math.max(maximum, value);\n    }\n    return Number.isFinite(minimum) && Number.isFinite(maximum) ? maximum - minimum : 0;\n  }\n\n  function clearSelection(message) {\n    activeId = '';\n    activeScan = null;\n    activeTopology = [];\n    activeImage = null;\n    if (standardModelCheckpointReview) setReviewMesh([], []);\n    elements.details.textContent = '';\n    renderSelectionState();\n    setStatus(message, false);\n    scheduleDraw();\n  }\n\n  function bindToggle(id, key) {\n    const button = document.getElementById(id);\n    button.addEventListener('click', () => {\n      view[key] = !view[key];\n      button.setAttribute('aria-pressed', view[key] ? 'true' : 'false');\n      scheduleDraw();\n    });\n  }\n\n  function bindUpdateToggle() {\n    const button = document.getElementById('toggleUpdates');\n    const storageKey = 'avatarBuilderReviewUpdatesPaused';\n    const isPaused = () => { try { return localStorage.getItem(storageKey) === 'true'; } catch { return false; } };\n    const apply = paused => {\n      try { localStorage.setItem(storageKey, paused ? 'true' : 'false'); } catch { }\n      button.textContent = paused ? 'Resume Updates' : 'Pause Updates';\n      button.dataset.paused = paused ? 'true' : 'false';\n      button.setAttribute('aria-pressed', paused ? 'false' : 'true');\n      if (updateTimer) clearInterval(updateTimer);\n      updateTimer = paused ? null : setInterval(() => refreshManifest(true), 30000);\n    };\n    button.addEventListener('click', () => apply(!isPaused()));\n    apply(isPaused());\n  }\n\n  function bindMeshInteraction() {\n    meshCanvas.addEventListener('pointerdown', event => {\n      dragging = true;\n      dragStart = { x: event.clientX, y: event.clientY };\n      meshCanvas.dataset.dragging = 'true';\n      meshCanvas.setPointerCapture(event.pointerId);\n    });\n    meshCanvas.addEventListener('pointermove', event => {\n      if (!dragging || !dragStart) return;\n      view.yaw += (event.clientX - dragStart.x) * 0.008;\n      view.pitch = Math.max(-1.1, Math.min(1.1, view.pitch + (event.clientY - dragStart.y) * 0.006));\n      dragStart = { x: event.clientX, y: event.clientY };\n      scheduleDraw();\n    });\n    meshCanvas.addEventListener('pointerup', releaseDrag);\n    meshCanvas.addEventListener('pointercancel', releaseDrag);\n    meshCanvas.addEventListener('wheel', event => {\n      event.preventDefault();\n      view.zoom = Math.max(0.42, Math.min(3.2, view.zoom * (event.deltaY < 0 ? 1.08 : 0.92)));\n      scheduleDraw();\n    }, { passive: false });\n    meshCanvas.addEventListener('dblclick', resetView);\n  }\n\n  function releaseDrag() {\n    dragging = false;\n    dragStart = null;\n    delete meshCanvas.dataset.dragging;\n  }\n\n  function resetView() {\n    view.yaw = -0.38; view.pitch = -0.10; view.zoom = 0.82;\n    scheduleDraw();\n  }\n\n  function scheduleDraw() {\n    if (drawPending) return;\n    drawPending = true;\n    requestAnimationFrame(() => {\n      drawPending = false;\n      drawMesh();\n      drawPhotoMesh();\n    });\n  }\n\n  function prepareCanvas(canvas, context) {\n    const rect = canvas.getBoundingClientRect();\n    const width = Math.max(320, Math.round(rect.width));\n    const height = Math.max(420, Math.round(rect.height));\n    const scale = window.devicePixelRatio || 1;\n    const pixelWidth = Math.round(width * scale);\n    const pixelHeight = Math.round(height * scale);\n    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {\n      canvas.width = pixelWidth;\n      canvas.height = pixelHeight;\n    }\n    context.setTransform(scale, 0, 0, scale, 0, 0);\n    context.clearRect(0, 0, width, height);\n    context.fillStyle = '#061019';\n    context.fillRect(0, 0, width, height);\n    return { width, height };\n  }\n\n  function drawMesh() {\n    const rect = prepareCanvas(meshCanvas, meshContext);\n    if ((!standardModelCheckpointReview && !activeIdentityModel?.hasMappedIdentity) || normalizedVertices.length === 0) {\n      const message = standardModelCheckpointReview\n        ? 'Select a Standard Model checkpoint'\n        : 'Waiting for the first completed recurrent FLAME loop';\n      drawWaiting(meshContext, rect, message);\n      return;\n    }\n    drawGrid(meshContext, rect);\n    if (view.surface) {\n      meshContext.save();\n      meshContext.strokeStyle = '#66d9ff';\n      meshContext.lineWidth = 0.45;\n      meshContext.globalAlpha = 0.38;\n      meshContext.beginPath();\n      for (const edge of identityTopology) {\n        const from = normalizedByIndex.get(edge.fromIndex);\n        const to = normalizedByIndex.get(edge.toIndex);\n        if (!from || !to) continue;\n        const a = project(from, rect);\n        const b = project(to, rect);\n        meshContext.moveTo(a.x, a.y);\n        meshContext.lineTo(b.x, b.y);\n      }\n      meshContext.stroke();\n      meshContext.restore();\n    }\n    if (view.points) {\n      meshContext.save();\n      meshContext.fillStyle = '#a9eaff';\n      meshContext.globalAlpha = 0.74;\n      meshContext.beginPath();\n      for (const point of normalizedVertices) {\n        const projected = project(point, rect);\n        const radius = Math.max(0.34, 0.66 * projected.scale);\n        meshContext.moveTo(projected.x + radius, projected.y);\n        meshContext.arc(projected.x, projected.y, radius, 0, Math.PI * 2);\n      }\n      meshContext.fill();\n      meshContext.restore();\n    }\n  }\n\n  function drawPhotoMesh() {\n    const rect = prepareCanvas(photoCanvas, photoContext);\n    if (!activeScan) { drawWaiting(photoContext, rect, 'Select a stored scan'); return; }\n    if (!activeImage) { drawWaiting(photoContext, rect, 'No paired source image'); return; }\n    const scale = Math.min(rect.width / activeImage.naturalWidth, rect.height / activeImage.naturalHeight);\n    const width = activeImage.naturalWidth * scale;\n    const height = activeImage.naturalHeight * scale;\n    const left = (rect.width - width) / 2;\n    const top = (rect.height - height) / 2;\n    photoContext.drawImage(activeImage, left, top, width, height);\n    const points = (activeScan.vertices || []).map(point => ({\n      index: point.index,\n      x: left + Number(point.x) * scale,\n      y: top + Number(point.y) * scale\n    })).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y));\n    const byIndex = new Map(points.map(point => [point.index, point]));\n    if (view.surface) {\n      photoContext.save();\n      photoContext.strokeStyle = '#66d9ff';\n      photoContext.lineWidth = 0.45;\n      photoContext.globalAlpha = 0.48;\n      photoContext.beginPath();\n      for (const edge of activeTopology) {\n        const from = byIndex.get(edge.fromIndex);\n        const to = byIndex.get(edge.toIndex);\n        if (!from || !to) continue;\n        photoContext.moveTo(from.x, from.y);\n        photoContext.lineTo(to.x, to.y);\n      }\n      photoContext.stroke();\n      photoContext.restore();\n    }\n    if (view.points) {\n      photoContext.save();\n      photoContext.fillStyle = '#b8efff';\n      photoContext.globalAlpha = 0.78;\n      photoContext.beginPath();\n      for (const point of points) {\n        const radius = 0.56;\n        photoContext.moveTo(point.x + radius, point.y);\n        photoContext.arc(point.x, point.y, radius, 0, Math.PI * 2);\n      }\n      photoContext.fill();\n      photoContext.restore();\n    }\n  }\n\n  function normalize(points) {\n    const raw = points.map(point => ({ index: point.index, x: Number(point.x), y: Number(point.y), z: Number(point.z) }))\n      .filter(point => Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z));\n    if (raw.length === 0) return [];\n    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;\n    for (const point of raw) {\n      minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x);\n      minY = Math.min(minY, point.y); maxY = Math.max(maxY, point.y);\n      minZ = Math.min(minZ, point.z); maxZ = Math.max(maxZ, point.z);\n    }\n    const centerX = (minX + maxX) / 2;\n    const centerY = (minY + maxY) / 2;\n    const centerZ = (minZ + maxZ) / 2;\n    const scale = 1 / Math.max(0.001, maxX - minX, maxY - minY);\n    return raw.map(point => ({ index: point.index, x: (point.x - centerX) * scale, y: (centerY - point.y) * scale, z: (point.z - centerZ) * scale }));\n  }\n\n  function setReviewMesh(vertices, topology) {\n    normalizedVertices = normalize(vertices || []);\n    normalizedByIndex = new Map(normalizedVertices.map(point => [point.index, point]));\n    identityTopology = topology || [];\n  }\n\n  function applyReviewMode() {\n    if (standardModelCheckpointReview) {\n      document.title = 'Review Standard Model';\n      elements.reviewTitle.textContent = 'Review Standard Model';\n      elements.meshTitle.textContent = 'Standard Model Checkpoint';\n      elements.meshSubtitle.textContent = 'Selected checkpoint; drag to rotate; wheel to zoom';\n      return;\n    }\n    document.title = 'Review FLAME Data';\n    elements.reviewTitle.textContent = 'Review FLAME Data';\n    elements.meshTitle.textContent = 'Current Recurrent FLAME Identity';\n    elements.meshSubtitle.textContent = 'Latest complete model; drag to rotate; wheel to zoom';\n  }\n\n  function emptyCatalogMessage() {\n    return standardModelCheckpointReview\n      ? 'No Standard Model checkpoints yet. Start Avatar Capture to collect one.'\n      : 'No stored FLAME scans yet. Start Avatar Capture to collect the first reconstruction.';\n  }\n\n  function project(point, rect) {\n    const cosY = Math.cos(view.yaw), sinY = Math.sin(view.yaw);\n    const cosP = Math.cos(view.pitch), sinP = Math.sin(view.pitch);\n    const x1 = point.x * cosY + point.z * sinY;\n    const z1 = -point.x * sinY + point.z * cosY;\n    const y1 = point.y * cosP - z1 * sinP;\n    const z2 = point.y * sinP + z1 * cosP;\n    const depth = 1.6 + z2;\n    const zoom = Math.min(rect.width, rect.height) * 0.76 * view.zoom / Math.max(0.35, depth);\n    return { x: rect.width * 0.5 + x1 * zoom, y: rect.height * 0.52 + y1 * zoom, z: z2, scale: Math.max(0.35, Math.min(1.8, 1 / Math.max(0.35, depth))) };\n  }\n\n  function drawGrid(context, rect) {\n    context.save();\n    context.strokeStyle = '#132638'; context.lineWidth = 1; context.beginPath();\n    for (let x = rect.width * 0.14; x <= rect.width * 0.86; x += rect.width * 0.12) { context.moveTo(x, rect.height * 0.12); context.lineTo(x, rect.height * 0.88); }\n    for (let y = rect.height * 0.14; y <= rect.height * 0.88; y += rect.height * 0.12) { context.moveTo(rect.width * 0.12, y); context.lineTo(rect.width * 0.88, y); }\n    context.stroke(); context.restore();\n  }\n\n  function drawWaiting(context, rect, message) {\n    context.fillStyle = '#9db7c9'; context.font = '14px Segoe UI, Arial, sans-serif'; context.textAlign = 'center';\n    context.fillText(message, rect.width / 2, rect.height / 2);\n  }\n\n  function setStatus(message, error) {\n    elements.status.textContent = message;\n    elements.status.dataset.error = error ? 'true' : 'false';\n  }\n\n  function formatDateTime(value) {\n    const date = value ? new Date(value) : null;\n    return date && !Number.isNaN(date.getTime()) ? date.toLocaleString() : '--';\n  }\n\n  function formatNumber(value) { return Number.isFinite(Number(value)) ? Number(value).toFixed(1).replace(/\\.0$/, '') : '--'; }\n  function formatInteger(value) { return Number.isFinite(Number(value)) ? Math.round(Number(value)).toLocaleString() : '--'; }\n  function escapeHtml(value) { return String(value ?? '').replace(/[&<>\"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '\"': '&quot;', \"'\": '&#39;' }[char])); }\n})();\n</script>\n</body>\n</html>", "no-store", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (string.Equals(text5, "favicon.ico", StringComparison.OrdinalIgnoreCase))
		{
			await WriteBytesResponseAsync(stream, HttpStatusCode.NoContent, "image/x-icon", Array.Empty<byte>(), "public, max-age=86400", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		ReviewContext context = GetContext();
		if (string.Equals(text5, "api/manifest", StringComparison.OrdinalIgnoreCase))
		{
			AvatarObservationDataset dataset = _repository.ReadDataset(context.ProfileFolder, context.SubjectId, context.SubjectDisplayName, includeDenseTopology: false, context.BackendId);
			AvatarDataReviewManifest value = CreateManifest(dataset, context.BackendId);
			await WriteJsonResponseAsync(stream, value, "no-store", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		else if (string.Equals(text5, "api/model", StringComparison.OrdinalIgnoreCase))
		{
			AvatarIdentityModel avatarIdentityModel = _modelStore.Read(context.ProfileFolder)?.Identity;
			List<FaceMeshLandmarkPoint> list = avatarIdentityModel?.MappedDenseVertices ?? new List<FaceMeshLandmarkPoint>();
			AvatarDataReviewIdentityModel value2 = new AvatarDataReviewIdentityModel(list.Count > 0, avatarIdentityModel?.MappingStatus ?? "The recurrent FLAME identity is waiting for its first completed loop.", avatarIdentityModel?.MappingUpdatedAtUtc, avatarIdentityModel?.MappingFrameCount ?? 0, avatarIdentityModel?.MappingIterationCount ?? 0, avatarIdentityModel?.MappingInitialLandmarkRmsePercent ?? 0.0, avatarIdentityModel?.MappingFinalLandmarkRmsePercent ?? 0.0, avatarIdentityModel?.MappingImprovementPercent ?? 0.0, avatarIdentityModel?.GenericIdentityDisplacementPercent ?? 0.0, list, avatarIdentityModel?.TopologyEdges ?? new List<MeshTopologyEdge>());
			await WriteJsonResponseAsync(stream, value2, "no-store", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		else if (text5.StartsWith("api/scans/", StringComparison.OrdinalIgnoreCase))
		{
			string text6 = text5.Substring("api/scans/".Length);
			bool flag = text6.EndsWith("/image", StringComparison.OrdinalIgnoreCase);
			string text7;
			if (!flag)
			{
				text7 = text6;
			}
			else
			{
				string text8 = text6;
				int length = "/image".Length;
				text7 = text8.Substring(0, text8.Length - length);
			}
			string observationId = text7;
			AvatarObservationDataset dataset2 = _repository.ReadDataset(context.ProfileFolder, context.SubjectId, context.SubjectDisplayName, includeDenseTopology: false, context.BackendId);
			AvatarObservation avatarObservation = FindObservation(dataset2, observationId);
			if ((object)avatarObservation == null)
			{
				await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (flag)
			{
				string imagePath = _repository.GetImagePath(dataset2, avatarObservation);
				if (string.IsNullOrWhiteSpace(imagePath))
				{
					await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					await WriteFileResponseAsync(stream, imagePath, "image/jpeg", "private, max-age=31536000, immutable", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			else
			{
				AvatarObservation avatarObservation2 = _repository.LoadObservation(dataset2, avatarObservation);
				AvatarDataReviewScan value3 = new AvatarDataReviewScan(CreateSummary(avatarObservation), avatarObservation2.Vertices, avatarObservation2.CanonicalIdentityVertices, avatarObservation2.Warnings);
				await WriteJsonResponseAsync(stream, value3, "private, max-age=31536000, immutable", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		else if (text5.StartsWith("api/topologies/", StringComparison.OrdinalIgnoreCase))
		{
			string topologyHash = text5.Substring("api/topologies/".Length);
			AvatarObservationDataset avatarObservationDataset = _repository.ReadDataset(context.ProfileFolder, context.SubjectId, context.SubjectDisplayName, includeDenseTopology: false, context.BackendId);
			AvatarObservation avatarObservation3 = avatarObservationDataset.Observations.FirstOrDefault((AvatarObservation item) => string.Equals(item.TopologySha256, topologyHash, StringComparison.OrdinalIgnoreCase));
			if ((object)avatarObservation3 == null)
			{
				await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			IReadOnlyList<MeshTopologyEdge> value4 = _repository.LoadTopology(avatarObservationDataset, avatarObservation3);
			await WriteJsonResponseAsync(stream, value4, "private, max-age=31536000, immutable", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		else
		{
			await WriteNotFoundAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private AvatarDataReviewManifest CreateManifest(AvatarObservationDataset dataset, string? backendId)
	{
		List<AvatarDataReviewScanSummary> list = dataset.Observations.OrderByDescending((AvatarObservation observation) => observation.CapturedAtUtc).Select(CreateSummary).ToList();
		string text = backendId ?? string.Empty;
		bool isStandardModelCheckpointReview = string.Equals(text, "deca-flame-standard-model-checkpoint-v1", StringComparison.Ordinal);
		return new AvatarDataReviewManifest(dataset.SubjectId, dataset.SubjectDisplayName, dataset.Revision, dataset.UpdatedAtUtc, list.Count, text, isStandardModelCheckpointReview, list);
	}

	private static AvatarDataReviewScanSummary CreateSummary(AvatarObservation observation)
	{
		return new AvatarDataReviewScanSummary(observation.ObservationId, observation.CapturedAtUtc, observation.Source, observation.DenseVertexCount, observation.ReconstructionConfidencePercent, observation.ModelSequenceNumber, observation.ModelCoefficientDeltaRms, observation.SampleQualityPercent, observation.EyeQualityPercent, observation.MouthQualityPercent, observation.BrowQualityPercent, observation.StabilityQualityPercent, observation.ARotationAroundXDegrees, observation.BRotationAroundYDegrees, observation.CRotationAroundZDegrees, observation.PoseBucket, observation.TrustDecision, observation.TopologySha256, !string.IsNullOrWhiteSpace(observation.ImageObjectPath));
	}

	private static AvatarObservation? FindObservation(AvatarObservationDataset dataset, string observationId)
	{
		return dataset.Observations.FirstOrDefault((AvatarObservation item) => string.Equals(item.ObservationId, observationId, StringComparison.Ordinal));
	}

	private ReviewContext GetContext()
	{
		lock (_gate)
		{
			return _context ?? throw new InvalidOperationException("No avatar profile is available for review.");
		}
	}

	private Uri BuildReviewUri()
	{
		return new Uri($"http://127.0.0.1:{_port}/review/{_sessionToken}/", UriKind.Absolute);
	}

	private static async Task WriteJsonResponseAsync<T>(NetworkStream stream, T value, string cacheControl, CancellationToken cancellationToken)
	{
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
		await WriteBytesResponseAsync(stream, HttpStatusCode.OK, "application/json; charset=utf-8", payload, cacheControl, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static Task WriteTextResponseAsync(NetworkStream stream, HttpStatusCode statusCode, string contentType, string value, string cacheControl, CancellationToken cancellationToken)
	{
		return WriteBytesResponseAsync(stream, statusCode, contentType, Encoding.UTF8.GetBytes(value), cacheControl, cancellationToken);
	}

	private static async Task WriteBytesResponseAsync(NetworkStream stream, HttpStatusCode statusCode, string contentType, byte[] payload, string cacheControl, CancellationToken cancellationToken)
	{
		await WriteHeadersAsync(stream, statusCode, contentType, payload.LongLength, cacheControl, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (payload.Length != 0)
		{
			await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static async Task WriteFileResponseAsync(NetworkStream stream, string path, string contentType, string cacheControl, CancellationToken cancellationToken)
	{
		await using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await WriteHeadersAsync(stream, HttpStatusCode.OK, contentType, file.Length, cacheControl, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task WriteHeadersAsync(NetworkStream stream, HttpStatusCode statusCode, string contentType, long contentLength, string cacheControl, CancellationToken cancellationToken)
	{
		string value = statusCode switch
		{
			HttpStatusCode.OK => "OK", 
			HttpStatusCode.NoContent => "No Content", 
			HttpStatusCode.NotFound => "Not Found", 
			HttpStatusCode.MethodNotAllowed => "Method Not Allowed", 
			_ => "Internal Server Error", 
		};
		byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)statusCode} {value}\r\nContent-Type: {contentType}\r\nContent-Length: {contentLength}\r\nCache-Control: {cacheControl}\r\n" + "Connection: close\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'self'; img-src 'self' data:; connect-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\r\n\r\n");
		await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static Task WriteNotFoundAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		return WriteTextResponseAsync(stream, HttpStatusCode.NotFound, "text/plain; charset=utf-8", "The requested avatar review resource was not found.", "no-store", cancellationToken);
	}

	private static async Task IgnoreShutdownException(Task? task, TimeSpan timeout)
	{
		if (task == null)
		{
			return;
		}
		try
		{
			await task.WaitAsync(timeout).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		catch (SocketException)
		{
		}
		catch (TimeoutException)
		{
		}
	}
}
