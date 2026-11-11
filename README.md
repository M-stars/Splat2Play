# Splat2Play(实境入戏)

> **扫描现实,即刻可玩。** —— 把真实世界的 3D 高斯泼溅(3DGS)扫描,变成 Unity 里可玩的关卡。

**状态:** ✅ M0–M4 核心工具链已落地并通过实测验证(见 [ROADMAP.md](ROADMAP.md)、[docs/benchmarks.md](docs/benchmarks.md)、[docs/blog-draft.md](docs/blog-draft.md) 与 [docs/项目书.md](docs/项目书.md))

## 为什么做这个

游戏引擎默认关卡是网格:要有碰撞体、导航网格、光照、遮挡剔除。3DGS 用几小时的采集就能得到照片级的真实场景(传统手工制作需要数周到数月的美术工时)——但开箱即用的 splat 场景只是"一张能移动的照片":不能碰撞、不能寻路、不响应光照。

Splat2Play 是一套 Unity 工具链,为扫描场景补齐游戏引擎要求的全部"接入层",让一段真实扫描的街角可以作为真正的游戏关卡交付。

## 模块

| 模块 | 功能 |
|---|---|
| **碰撞代理生成** | splat 密度场体素化 → 洪泛填充 → SDF → 抽稀碰撞网格 → Unity Collider |
| **NavMesh 桥接** | 代理网格直出 Unity NavMesh,敌人 AI 可在扫描街道上寻路 |
| **近似动态光照** | depth-only 代理 pass 让 splat 近似接收场景阴影与动态光源,无需重训练 |
| **分块流送与剔除** | chunk 渲染 + 基于代理的遮挡剔除 + 运行时内存面板 |

## 目标 Demo

一段真实扫描街角的第一人称微型关卡:玩家与真实几何碰撞不穿墙、手电筒扫过出现近似影子、敌人 AI 沿真实地形追击、雾效/景深与 splat 融合、分块流送保持 60fps。

## 路线图

| 里程碑 | 周期 | 交付物 |
|---|---|---|
| M0 — 基座 | W1–2 | 集成 UnityGaussianSplatting、基线场景、性能数据 |
| M1 — 碰撞代理 | W3–4 | 体素化/洪泛填充/SDF/网格提取管线(核心模块一) |
| M2 — NavMesh + AI | W5–6 | 可玩的 AI 追击原型 |
| M3 — 近似动态光照 | W7–8 | 基于代理深度的阴影调制(核心模块二) |
| M4 — 流送 + 收尾 | W9–10 | 遮挡剔除、性能面板、Demo 视频、技术博客 |

详见 [ROADMAP.md](ROADMAP.md);执行级技术规格见 [docs/项目书.md](docs/项目书.md)。

## 站在这些肩膀上

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)(MIT)——事实上的 Unity 3DGS 渲染器,本项目集成基座
- 相关研究:PhysGaussian、GaMeS、RigGS、Habitat-GS、Mip-Splatting

## 许可

本仓库代码:MIT。关于 splat **数据**的注意:使用 INRIA 官方实现训练的数据,商业使用需要 INRIA 授权——正式交付的场景请用 gsplat / nerfstudio 系训练。
