using System;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

internal static class DenseFaceWarpViewerPage
{
	public static string Build(string documentJson)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(documentJson);
		return Template.Replace("__WARP_DATA__", documentJson, StringComparison.Ordinal);
	}

	private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Measured Face and Dense Warp</title>
<style>
:root{color-scheme:dark;background:#06111a;color:#e8f5ff;font:13px/1.4 Segoe UI,Arial,sans-serif}
*{box-sizing:border-box}body{margin:0;min-height:100vh;background:#06111a}.toolbar{height:56px;display:flex;align-items:center;gap:8px;padding:8px 12px;border-bottom:1px solid #284052;background:#091722}.toolbar strong{font-size:16px;margin-right:10px}.toolbar button{height:34px;padding:0 12px;color:#dff3ff;background:#102638;border:1px solid #3876a0;border-radius:2px;cursor:pointer}.toolbar button.on{background:#176287;border-color:#63c9f5}.metrics{margin-left:auto;color:#9fdcf6;text-align:right;font-variant-numeric:tabular-nums}.evidence{height:82px;display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:1px;background:#284052;border-bottom:1px solid #284052}.metric{min-width:0;padding:8px 10px;background:#0a1721;color:#96b8ca}.metric strong{display:block;color:#e8f5ff;font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-variant-numeric:tabular-nums}.metric small{display:block;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.confidence-high{color:#62d5ff}.confidence-medium{color:#ffd166}.confidence-low{color:#758b99}.views{height:calc(100vh - 188px);display:grid;grid-template-columns:1fr 1fr;gap:1px;background:#284052}.panel{position:relative;min-width:0;background:#050d14}.panel h2{position:absolute;z-index:2;top:12px;left:14px;margin:0;font-size:14px;color:#f2fbff}.panel p{position:absolute;z-index:2;top:34px;left:14px;margin:0;color:#8fb9d0}.panel canvas{width:100%;height:100%;display:block;touch-action:none}.footer{height:50px;padding:7px 12px;border-top:1px solid #284052;color:#b9d7e7;display:flex;align-items:center;gap:22px}.good{color:#68e0a5}.warn{color:#ffd66e}@media(max-width:1050px){.evidence{grid-template-columns:repeat(3,minmax(0,1fr));height:130px}.views{height:calc(100vh - 236px)}}@media(max-width:700px){.toolbar{height:auto;min-height:56px;flex-wrap:wrap}.views{grid-template-columns:1fr;grid-template-rows:1fr 1fr}.metrics{display:none}.evidence{grid-template-columns:repeat(2,minmax(0,1fr));height:190px}.views{height:calc(100vh - 296px)}}
</style>
</head>
<body>
<div class="toolbar">
  <strong>Measured Face and Dense Warp</strong>
  <button id="mesh" class="on">Wireframe</button>
  <button id="points" class="on">Points</button>
  <button id="anchors" class="on">Trusted Controls</button>
  <button id="rays">Warp Movement</button>
  <button id="reset">Reset 3D View</button>
  <div class="metrics" id="metrics"></div>
</div>
<section class="evidence" aria-label="Face reconstruction statistics">
  <div class="metric"><span>Measured evidence</span><strong id="measured-count"></strong><small id="measured-topology"></small></div>
  <div class="metric"><span>Measured confidence</span><strong id="confidence-summary"></strong><small id="confidence-detail"></small></div>
  <div class="metric"><span>Trusted controls</span><strong id="control-count"></strong><small>MediaPipe anchors applied to 3DDFA</small></div>
  <div class="metric"><span>Dense output</span><strong id="dense-count"></strong><small id="dense-topology"></small></div>
  <div class="metric"><span>Anchor fit</span><strong id="anchor-fit"></strong><small id="anchor-improvement"></small></div>
  <div class="metric"><span>Warp safety</span><strong id="movement"></strong><small id="clamp"></small></div>
</section>
<main class="views">
  <section class="panel"><h2>Measured MediaPipe Face</h2><p>Accumulated directly observed geometry, colored by confidence</p><canvas id="measured"></canvas></section>
  <section class="panel"><h2>Dense MediaPipe-Warped 3DDFA</h2><p>Full dense topology reshaped by the measured face</p><canvas id="warped"></canvas></section>
</main>
<footer class="footer"><span id="status"></span><span><span class="confidence-high">Cyan: high evidence</span> | <span class="confidence-medium">amber: partial evidence</span> | <span class="confidence-low">gray: low evidence</span>. Drag to rotate; wheel zooms both.</span></footer>
<script>
const data=__WARP_DATA__;
const measured=Float64Array.from(data.measuredCoordinates||[]), measuredConfidence=Float64Array.from(data.measuredConfidences||[]), measuredEdges=Int32Array.from(data.measuredEdgeIndices||[]);
const warped=Float64Array.from(data.warpedCoordinates||[]), warpedEdges=Int32Array.from(data.edgeIndices||[]);
const canvases=[document.getElementById('measured'),document.getElementById('warped')];
const state={yaw:-0.18,pitch:0.04,zoom:1,mesh:true,points:true,anchors:true,rays:false};
const fmt=n=>Number(n||0).toFixed(4);
const number=n=>Number(n||0).toLocaleString(), percent=n=>`${Number(n||0).toFixed(1)}%`;
const measuredCount=Number(data.measuredVertexCount||measured.length/3), high=Number(data.highConfidenceMeasuredVertexCount||0), medium=Number(data.mediumConfidenceMeasuredVertexCount||0), low=Number(data.lowConfidenceMeasuredVertexCount||0);
document.getElementById('metrics').innerHTML=`${data.subjectDisplayName||data.subjectId||'Avatar'}<br>${new Date(data.createdAtUtc).toLocaleString()}`;
document.getElementById('measured-count').textContent=`${number(measuredCount)} points`;
document.getElementById('measured-topology').textContent=`${number(data.measuredEdgeCount||measuredEdges.length/2)} measured edges`;
document.getElementById('confidence-summary').innerHTML=`<span class="confidence-high">${percent(data.meanMeasuredConfidence*100)} mean</span>`;
document.getElementById('confidence-detail').innerHTML=`<span class="confidence-high">${number(high)} high</span> | <span class="confidence-medium">${number(medium)} medium</span> | <span class="confidence-low">${number(low)} low</span>`;
document.getElementById('control-count').textContent=`${number(data.appliedControlPointCount)} controls`;
document.getElementById('dense-count').textContent=`${number(data.sourceVertexCount)} vertices`;
document.getElementById('dense-topology').textContent=`${number(data.denseEdgeCount||warpedEdges.length/2)} dense edges`;
document.getElementById('anchor-fit').textContent=`${fmt(data.sourceAnchorRms)} \u2192 ${fmt(data.warpedAnchorRms)} RMS`;
document.getElementById('anchor-improvement').textContent=`${percent(data.anchorRmsImprovementPercent)} improvement`;
document.getElementById('movement').textContent=`${fmt(data.medianAppliedDisplacement)} median | ${fmt(data.percentile95AppliedDisplacement)} p95`;
document.getElementById('clamp').textContent=`${percent(data.safetyClampVertexPercent)} safety-clamped vertices`;
const improved=data.warpedAnchorRms<data.sourceAnchorRms;
document.getElementById('status').innerHTML=`<span class="${improved?'good':'warn'}">${data.status}</span>`;
function toggle(id,key){const b=document.getElementById(id);b.onclick=()=>{state[key]=!state[key];b.classList.toggle('on',state[key]);drawAll()}}
toggle('mesh','mesh');toggle('points','points');toggle('anchors','anchors');toggle('rays','rays');
document.getElementById('reset').onclick=()=>{state.yaw=-0.18;state.pitch=0.04;state.zoom=1;drawAll()};
function rotate(x,y,z){const cy=Math.cos(state.yaw),sy=Math.sin(state.yaw),cp=Math.cos(state.pitch),sp=Math.sin(state.pitch),x1=x*cy+z*sy,z1=-x*sy+z*cy;return [x1,y*cp-z1*sp,y*sp+z1*cp]}
function bounds(coords){let m=0;for(let i=0;i<coords.length;i++)m=Math.max(m,Math.abs(coords[i]));return Math.max(m,1)}
const extent=Math.max(bounds(measured),bounds(warped));
function sizeCanvas(c){const d=Math.max(1,Math.min(2,window.devicePixelRatio||1)),r=c.getBoundingClientRect(),w=Math.max(1,Math.round(r.width*d)),h=Math.max(1,Math.round(r.height*d));if(c.width!==w||c.height!==h){c.width=w;c.height=h}return [w,h,d]}
function project(v,w,h){const r=rotate(v[0],v[1],v[2]),s=Math.min(w,h)*0.39*state.zoom/extent;return [w/2+r[0]*s,h/2-r[1]*s,r[2]]}
function confidenceColor(value,alpha){const c=Math.max(0,Math.min(1,Number(value)||0));if(c>=.72)return `rgba(98,213,255,${alpha})`;if(c>=.35)return `rgba(255,209,102,${alpha})`;return `rgba(64,88,105,${alpha})`}
function drawMeasured(canvas){const ctx=canvas.getContext('2d'),[w,h,d]=sizeCanvas(canvas);ctx.clearRect(0,0,w,h);ctx.lineCap='round';ctx.lineJoin='round';const p=new Array(measured.length/3);for(let i=0,n=0;i<measured.length;i+=3,n++)p[n]=project([measured[i],measured[i+1],measured[i+2]],w,h);
 if(state.mesh){ctx.lineWidth=Math.max(.55*d,.65);for(let e=0;e<measuredEdges.length;e+=2){const ai=measuredEdges[e],bi=measuredEdges[e+1],a=p[ai],b=p[bi];if(!a||!b)continue;const trust=Math.min(measuredConfidence[ai]||0,measuredConfidence[bi]||0);ctx.strokeStyle=confidenceColor(trust,.25+.55*trust);ctx.beginPath();ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1]);ctx.stroke()}}
 if(state.points){for(let i=0;i<p.length;i++){const trust=measuredConfidence[i]||0,v=p[i],radius=Math.max((trust>=.72?1.3:.9)*d,1);ctx.fillStyle=confidenceColor(trust,.45+.5*trust);ctx.beginPath();ctx.arc(v[0],v[1],radius,0,Math.PI*2);ctx.fill()}}
 if(state.anchors){for(const c of data.controls||[]){const v=project(c.target,w,h),alpha=.35+.65*c.confidence;ctx.fillStyle=`rgba(126,240,189,${alpha})`;ctx.beginPath();ctx.arc(v[0],v[1],Math.max(1.7*d,2),0,Math.PI*2);ctx.fill()}}
}
function drawWarped(canvas){const ctx=canvas.getContext('2d'),[w,h,d]=sizeCanvas(canvas);ctx.clearRect(0,0,w,h);ctx.lineCap='round';ctx.lineJoin='round';const p=new Array(warped.length/3);for(let i=0,n=0;i<warped.length;i+=3,n++)p[n]=project([warped[i],warped[i+1],warped[i+2]],w,h);
 if(state.mesh){const count=warpedEdges.length/2,stride=Math.max(1,Math.ceil(count/42000));ctx.strokeStyle='rgba(72,225,203,.35)';ctx.lineWidth=Math.max(.48*d,.58);ctx.beginPath();for(let e=0;e<count;e+=stride){const a=p[warpedEdges[e*2]],b=p[warpedEdges[e*2+1]];if(!a||!b)continue;ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1])}ctx.stroke()}
 if(state.points){ctx.fillStyle='rgba(119,255,226,.52)';for(let i=0;i<p.length;i+=2){const v=p[i];ctx.fillRect(v[0],v[1],Math.max(.7*d,1),Math.max(.7*d,1))}}
 if(state.rays){ctx.strokeStyle='rgba(255,203,92,.55)';ctx.lineWidth=Math.max(.65*d,.8);ctx.beginPath();for(const c of data.controls||[]){const a=project(c.source,w,h),b=project(c.target,w,h);ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1])}ctx.stroke()}
 if(state.anchors){for(const c of data.controls||[]){const v=project(c.target,w,h),alpha=.35+.65*c.confidence;ctx.fillStyle=`rgba(126,240,189,${alpha})`;ctx.beginPath();ctx.arc(v[0],v[1],Math.max(1.7*d,2),0,Math.PI*2);ctx.fill()}}
}
function drawAll(){drawMeasured(canvases[0]);drawWarped(canvases[1])}
for(const canvas of canvases){let drag=false,lastX=0,lastY=0;canvas.addEventListener('pointerdown',e=>{drag=true;lastX=e.clientX;lastY=e.clientY;canvas.setPointerCapture(e.pointerId)});canvas.addEventListener('pointermove',e=>{if(!drag)return;state.yaw+=(e.clientX-lastX)*.009;state.pitch=Math.max(-1.45,Math.min(1.45,state.pitch+(e.clientY-lastY)*.009));lastX=e.clientX;lastY=e.clientY;drawAll()});canvas.addEventListener('pointerup',()=>drag=false);canvas.addEventListener('wheel',e=>{e.preventDefault();state.zoom=Math.max(.35,Math.min(5,state.zoom*(e.deltaY<0?1.1:.9)));drawAll()},{passive:false})}
window.addEventListener('resize',drawAll);drawAll();
</script>
</body>
</html>
""";
}
