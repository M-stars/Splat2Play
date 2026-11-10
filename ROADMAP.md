# Splat2Play — Roadmap

> Scan reality. Play it. — 把真实世界的 3DGS 扫描变成 Unity 里可玩的关卡。

## Milestones

### M0 — Foundation (W1–2)

- Integrate [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) into a URP project
- Pick 2–3 public scan scenes (no self-capture): street corner, shop interior
- Baseline scene running; record baseline render/sort ms, fps, VRAM
- **Exit criteria:** scanned scene visible at 60 fps on dev machine, metrics noted in `docs/benchmarks.md`

### M1 — Proxy Collider Generation (W3–4) · core module #1

- Editor tool: splat structured buffers → density field voxelization (compute shader)
- Flood fill for interior/exterior separation → SDF → marching cubes → decimated mesh
- Generate Unity Physics colliders (mesh collider + optional box simplification)
- Tunable parameters: voxel resolution, density threshold; report memory/accuracy tradeoff
- **Exit criteria:** player capsule walks a scanned street without falling through floors or walls
- Known risks: thin structures (fences, poles) vs density threshold → manual patch-brush as fallback

### M2 — NavMesh Bridge + AI Prototype (W5–6)

- Proxy mesh → Unity NavMesh bake, connect NavMeshAgent
- Playable prototype: enemy AI chases the player through the scanned level
- **Exit criteria:** AI reaches the player along real terrain; capture demo footage

### M3 — Approximate Dynamic Lighting (W7–8) · core module #2

- Render a depth-only proxy pass before splats
- Splat shader samples scene shadow map / light data, modulated by proxy depth
- Honest error analysis: proxy-vs-splat surface offset, per-chunk depth bias
- **Exit criteria:** flashlight sweep shows approximate shadows on scanned surfaces; tech write-up drafted
- This module is the main subject of the technical blog post

### M4 — Streaming, Culling, Ship (W9–10)

- Chunk-based streaming + proxy-driven occlusion culling
- Runtime perf panel: splat count / sort ms / lighting ms / VRAM
- Demo video (≤60s), tech blog (知乎/个人博客), repo polish with tags `M0`–`M4`
- Stretch: Quest 3 smoke test

## Benchmark targets (desktop, RTX-class)

- ≥1M splats scene @ 60 fps with proxy + approximate lighting enabled
- Proxy generation for a mid-size scene completes in-editor in ≤1 minute

## Engineering notes

- Data layout: splat structs in GraphicsBuffers; proxy/SDF computed on GPU where possible
- URP first (job-market relevance); Built-in/HDRP compatibility out of scope for v0
- Large scan files stay out of git (`*.ply` ignored) — external storage; consider Git LFS if needed
- Original INRIA-trained splat data requires an INRIA license for commercial use — use gsplat/nerfstudio training for shipped scenes
