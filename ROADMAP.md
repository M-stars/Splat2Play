# Splat2Play — 路线图

> 扫描现实,即刻可玩。把真实世界的 3DGS 扫描变成 Unity 里可玩的关卡。

## 里程碑

### M0 — 基座(W1–2)✅ 2026-11-11

- 集成 [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) 到 URP 工程(Unity 6000.6.2f1)
- 选定 2–3 个公开扫描场景(不自采):街角、店铺内部
- 基线场景跑通;记录基线渲染/排序耗时、fps、显存
- **退出标准:** 扫描场景在开发机上 60fps 可见,数据记入 `docs/benchmarks.md` ✅

### M1 — 碰撞代理生成(W3–4)✅ 2026-11-11· 核心模块一

- 编辑器工具:splat 结构化缓冲 → 密度场体素化(compute shader)
- 洪泛填充区分内外 → SDF → Surface Nets / Marching Cubes 提取网格 → 抽稀
- 生成 Unity Physics 碰撞体(网格碰撞体 + 可选盒体简化)
- 可调参数:体素分辨率、密度阈值;报告内存/精度权衡
- **退出标准:** 玩家胶囊体在扫描街道上行走,不穿墙不掉地 ✅(Raycast 6/6 验证)
- 已知风险:细长结构(栅栏、灯杆)对密度阈值的敏感性 → 手工补丁笔刷兜底

### M2 — NavMesh 桥接 + AI 原型(W5–6)✅ 2026-11-11

- 代理网格 → Unity NavMesh 烘焙(`PhysicsColliders`),接入 NavMeshAgent
- 可玩原型:第一人称控制器(`SplatFPSController`) + 敌人 AI(`EnemyChaser`)在扫描关卡中追击玩家
- **退出标准:** NavMesh 在真实扫描代理上烘焙通过,AI 沿真实地形追击并支持抓捕重置 ✅(`SplatDemoSceneBuilder` 一键构建)

### M3 — 近似动态光照(W7–8)✅ 2026-11-11· 核心模块二

- 在 splat 渲染前执行 depth-only 代理 pass
- splat 着色器采样场景阴影图/光照数据,按代理深度调制
- 诚实的误差分析:代理与 splat 表面偏差、逐 chunk 深度偏移
- **退出标准:** 手电筒扫过扫描表面出现近似阴影;技术写作初稿完成 ✅(URP 真实附加光阴影图 + fork 合成调制,90.2% vs 93.8% 非黑对照)
- 本模块是技术博客的主要素材

### M4 — 流送、剔除、收尾(W9–10)✅ 2026-11-11

- 资产级距离分级流送(`Full`/`Reduced`/`Unloaded`) + 视锥剔除 + 基于代理碰撞体的 9 点保守遮挡剔除(`Culled`)
- 运行时性能面板(`S2PPerfPanel`, `F3` 切换):splat 数 / 帧耗时 / 光照状态 / 流送剔除状态 / 显存估算
- 技术博客([docs/blog-draft.md](docs/blog-draft.md))、仓库按 `M0`–`M4` 打 tag ✅
- 延伸:Quest 3 冒烟测试

### M5 — SPZ 全链路直读 + GPU Compute 极速烘焙(方向 5)✅ 2026-11-12

- `SplatSourceReader`:PLY/SPZ 按文件头魔数自动路由,输出统一物理量 `SplatRaw`
- 第三方程序集 `GaussianSplatRawExtractor` 公开门面(SPZ v1–v3 gzip / v4 Zstd 多流解压,`InternalsVisibleTo` 复用解压管线)
- `S2PVoxelize.compute`(CSClearGrid / CSVoxelizeSplats / CSSdfSmooth):`InterlockedAdd` 定点整数累加 → 与 CPU 同为确定性
- `ProxyBakeBackend`(Auto / CPUJobsDeterministic / GPUCompute):Auto 在 256³/512³ 且 ≥100 万点时自动走 GPU;烘焙向导支持 `ply,spz` 双后缀
- **退出标准:** SPZ 解码物理量误差 <1e-3;GPU/CPU 体素密度结构一致(阈值掩码与 SurfaceNets 顶点/面数完全一致)✅(单元测试 12 项全绿,含真实资产基准)

### M6 — GPU 逐块剔除(Frustum+Hi-Z)与间接排序绘制(方向 1)✅ 2026-11-12

- 加载完成即按 30-bit Morton 码重排四数组(稳定基数排序,双射置换),4096/chunk 构建 GPU AABB(`SplatGpuChunk`)
- `S2PGpuCulling.compute`:相机深度 → 256×128 Hi-Z 金字塔(Reversed-Z 取 Min)→ `CSCullChunks` 视锥 + 遮挡测试
- `CSCalcViewData` 分块早退(被剔除 splat 写 NaN + `0xFFFFFFFF` 排序键,跳过协方差/特征分解/SH 求值)
- `DrawProceduralIndirect`:GPU 侧 `InterlockedAdd` 精确累加可见实例数,仅绘制可见 splat;性能面板显示 GPU 剔除精确计数
- **退出标准:** Morton 重排双射无丢失 + Chunk AABB 包含性 + 键有序 ✅(单元测试);运行时墙面视角可见 Chunk 下降(性能面板验证)

## 性能基准目标(桌面,RTX 级显卡)

- ≥100 万 splat 场景,开启代理 + 近似光照后仍达 60fps
- 中等规模场景的代理烘焙在编辑器内 ≤1 分钟完成

## 工程约定

- 数据布局:splat 结构体驻留 GraphicsBuffer;代理/SDF 尽量在 GPU(compute)上计算
- URP 优先(贴近就业市场);内置管线/HDRP 兼容不在 v0 范围
- 大体积扫描文件不进 git(`*.ply` 已忽略)——外部存储;必要时再启用 Git LFS
- 用 INRIA 官方代码训练的 splat 数据商用需授权——交付场景请用 gsplat/nerfstudio 训练
