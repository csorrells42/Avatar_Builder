# Avatar Observation Storage

This module owns live Avatar Builder persistence. Camera rendering and 3DDFA inference hand accepted reconstruction candidates to a bounded background writer and never perform image encoding or disk I/O themselves.

## Layout

- `Storage/avatar-builder.sqlite3` is the transactional catalog for profiles, observation metadata, ranking scores, pose coverage, object paths, checksums, and revisions.
- `Storage/Objects/Scans` contains immutable content-addressed `.avscan` files. Dense frame geometry and canonical identity geometry use compact binary floating-point arrays instead of JSON.
- `Storage/Objects/Images` contains the exact high-quality camera image paired with each retained observation.
- `Storage/Objects/Topology` contains deduplicated topology objects shared by observations using the same 3DDFA mesh.

SQLite stores relationships and small searchable values. Large scans and images remain ordinary immutable files so viewers, recovery tools, and future HDF5 exporters can stream them directly.

## Retention

The catalog keeps at most 360 retained observations per profile. This is a storage safety ceiling, not a maturity claim. Selection favors reconstruction quality, capture quality, expression value, and underrepresented A/B/C/distance buckets. Near-duplicates and weak redundant samples are rejected or replaced.

Model maturity is measured separately through coefficient stability, pose coverage, confidence, and model movement.

## Verification

Run `dotnet run --project tools\AvatarStorageSmoke\AvatarStorageSmoke.csproj -- <scratch-folder>`. The console verifier writes, reopens, validates, replaces, rejects, and resets synthetic full-resolution observations through the same repository and codecs used by the app.
