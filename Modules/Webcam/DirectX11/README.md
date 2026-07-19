# Webcam.DirectX11 Module

Owns the D3D11 device/context and Media Foundation DXGI device-manager setup used by texture-native capture.

Current entry points:
- `Direct3D11DeviceManager.cs`

Keep this module narrow. Camera selection, frame ownership, and DX12 presentation belong in their respective webcam modules.
