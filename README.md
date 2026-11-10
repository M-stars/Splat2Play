# Splat2Play

> **Scan reality. Play it.** — turn real-world 3D Gaussian Splatting captures into playable levels in Unity.
>
> 中文:**实境入戏** —— 把真实世界的 3DGS 扫描变成 Unity 里可玩的关卡。

**Status:** 🚧 design phase — code lands starting M0 (see [ROADMAP.md](ROADMAP.md))

## Why

Game engines expect levels to be meshes: colliders, navmesh, lights, occlusion culling. 3D Gaussian Splatting (3DGS) delivers photoreal captures of real places in hours instead of weeks of art time — but out of the box, a splat scene is "a photo you can move around": nothing collides, nothing navigates, nothing responds to light.

Splat2Play is a Unity toolchain that generates the game-facing layers a scanned scene is missing, so a captured street corner can ship as an actual game level.

## Modules

| Module | What it does |
|---|---|
| **Proxy Collider Generation** | Voxelize splat density → flood fill → SDF → decimated collision mesh → Unity colliders |
| **NavMesh Bridge** | Proxy mesh drives Unity NavMesh, so AI agents navigate scanned environments |
| **Approximate Dynamic Lighting** | A depth-only proxy pass lets splats approximately receive scene shadows and dynamic lights — no re-training required |
| **Chunk Streaming & Culling** | Chunked rendering with proxy-driven occlusion culling and a runtime memory panel |

## Target demo

A first-person micro-level inside a real scanned street corner: the player collides with real geometry, a flashlight casts approximate shadows, an enemy AI chases you along the actual terrain, fog/DOF integrate with splats — chunk streaming holds 60 fps.

## Roadmap

| Milestone | Weeks | Deliverable |
|---|---|---|
| M0 — Base | W1–2 | UnityGaussianSplatting integration, baseline scene, metrics |
| M1 — Proxy colliders | W3–4 | Voxel / flood-fill / SDF / mesh extraction pipeline (core module #1) |
| M2 — NavMesh + AI | W5–6 | Playable AI-chase prototype |
| M3 — Approximate lighting | W7–8 | Proxy-depth shadow modulation (core module #2) |
| M4 — Streaming + ship | W9–10 | Occlusion culling, perf panel, demo video, tech blog |

Details in [ROADMAP.md](ROADMAP.md).

## Built on

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) (MIT) — the de facto Unity 3DGS renderer; planned integration base.
- Related research: PhysGaussian, GaMeS, RigGS, Habitat-GS, Mip-Splatting.

## License

Code in this repo: MIT. Note on splat **data**: training with the original INRIA implementation requires an INRIA license for commercial use — prefer gsplat / nerfstudio-based training for shipped scenes.

---

## 中文说明

**Splat2Play(实境入戏)** 解决一个问题:3DGS 扫描能以小时级成本得到照片级真实场景,但放进引擎后它只是"一张能移动的照片"——没有碰撞、不能寻路、不响应光照。

本项目构建一套 Unity 工具链,为扫描场景补齐游戏引擎要求的接入层:

- **碰撞代理生成**:splat 密度场体素化 → 洪泛填充 → SDF → 抽稀碰撞网格 → Unity Collider
- **NavMesh 桥接**:代理网格直出 NavMesh,敌人 AI 可在扫描街道上寻路
- **近似动态光照**:depth-only 代理 pass 让 splat 近似接收场景阴影与动态光源,无需重训练
- **分块流送与剔除**:chunk 渲染 + 基于代理的遮挡剔除 + 运行时内存面板

目标 demo:一段真实扫描街角的第一人称小关卡——玩家不穿墙、手电筒有近似影子、AI 沿真实地形追人、雾效融合、流送保 60fps。

路线图与里程碑详情见 [ROADMAP.md](ROADMAP.md)。
