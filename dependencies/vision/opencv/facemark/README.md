# OpenCV LBF Facemark Backend

This folder is reserved for the portable OpenCV LBF 68-point facial landmark model.

The current app looks for:

```text
dependencies/vision/opencv/facemark/lbfmodel.yaml
dependencies/vision/opencv/facemark/lbfmodel_manifest.json
```

When `lbfmodel.yaml` is present, Avatar Builder can run OpenCV's model-backed 68-point facemark backend before falling back to the aperture-only OpenCV tracker. The 68 landmarks are mapped into the shared face contour, eye contours, outer lip contour, inner lip contour, and jaw contour used by overlays, capture-quality scoring, and avatar inspection.

Keep model/runtime files under `dependencies` so they are copied beside the executable and the app remains portable.
