# Splat2Play — 路线图

> 扫描现实,即刻可玩。把真实世界的 3DGS 扫描变成 Unity 里可玩的关卡。

## 里程碑

### M0 — 基座(W1–2)

- 集成 [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) 到 URP 工程(Unity 6000.6.2f1)
- 选定 2–3 个公开扫描场景(不自采):街角、店铺内部
- 基线场景跑通;记录基线渲染/排序耗时、fps、显存
- **退出标准:** 扫描场景在开发机上 60fps 可见,数据记入 `docs/benchmarks.md`

### M1 — 碰撞代理生成(W3–4)· 核心模块一

- 编辑器工具:splat 结构化缓冲 → 密度场体素化(compute shader)
- 洪泛填充区分内外 → SDF → Surface Nets / Marching Cubes 提取网格 → 抽稀
- 生成 Unity Physics 碰撞体(网格碰撞体 + 可选盒体简化)
- 可调参数:体素分辨率、密度阈值;报告内存/精度权衡
- **退出标准:** 玩家胶囊体在扫描街道上行走,不穿墙不掉地
- 已知风险:细长结构(栅栏、灯杆)对密度阈值的敏感性 → 手工补丁笔刷兜底

### M2 — NavMesh 桥接 + AI 原型(W5–6)

- 代理网格 → Unity NavMesh 烘焙,接入 NavMeshAgent
- 可玩原型:敌人 AI 在扫描关卡中追击玩家
- **退出标准:** AI 沿真实地形追到玩家;录制演示片段

### M3 — 近似动态光照(W7–8)· 核心模块二

- 在 splat 渲染前执行 depth-only 代理 pass
- splat 着色器采样场景阴影图/光照数据,按代理深度调制
- 诚实的误差分析:代理与 splat 表面偏差、逐 chunk 深度偏移
- **退出标准:** 手电筒扫过扫描表面出现近似阴影;技术写作初稿完成
- 本模块是技术博客的主要素材

### M4 — 流送、剔除、收尾(W9–10)

- 分块流送 + 基于代理的遮挡剔除
- 运行时性能面板:splat 数 / 排序耗时 / 光照耗时 / 显存
- Demo 视频(≤60 秒)、技术博客(知乎/个人博客)、仓库按 `M0`–`M4` 打 tag
- 延伸:Quest 3 冒烟测试

## 性能基准目标(桌面,RTX 级显卡)

- ≥100 万 splat 场景,开启代理 + 近似光照后仍达 60fps
- 中等规模场景的代理烘焙在编辑器内 ≤1 分钟完成

## 工程约定

- 数据布局:splat 结构体驻留 GraphicsBuffer;代理/SDF 尽量在 GPU(compute)上计算
- URP 优先(贴近就业市场);内置管线/HDRP 兼容不在 v0 范围
- 大体积扫描文件不进 git(`*.ply` 已忽略)——外部存储;必要时再启用 Git LFS
- 用 INRIA 官方代码训练的 splat 数据商用需授权——交付场景请用 gsplat/nerfstudio 训练
