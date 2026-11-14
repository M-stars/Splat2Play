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

### M7 — 椭球最短轴法线 + 多光源 SH 方向性动态重光照(方向 2)✅ 2026-11-12

- `ExtractSplatNormalAndFlatness`(HLSL + C# 镜像):最短尺度轴即表面法线,flatness∈[0,1] 置信度,视线侧双面翻转
- 逐高斯多光源 Lambert/Blinn-Phong:`S2PGpuLight`(≤8 盏)经 `Shader.SetGlobalVectorArray` 注入,shininess 随扁平度自适应
- 与 M3 解耦:主手电筒保留屏幕空间真实阴影;附加灯提供"逐高斯"与"逐像素回退"互斥双路径(避免双重光照)
- **退出标准:** 扁平圆盘→(0,1,0)/flatness>0.95、球状→flatness=0、旋转对齐、双面翻转 ✅(单元测试 4 项全绿)

### M8 — 可交互/可破坏 3DGS 动态刚体剥离(方向 3)✅ 2026-11-12

- SceneView 黄色手柄框选 `DynamicPropVolume`(名称/质量/可破坏);向导一键"分离动态物体 (Extract Dynamic Props)"
- `SplatPropExtractor`:盒内 splat 剥离为独立 PLY(质心居中)+ 64³ 凸包 `MeshCollider` + `Rigidbody` + `NavMeshObstacle(carving)`
- 底面邻域高斯修补(Floor Inpainting):环形采样框统计(±0.08m 切片 / 0.20m 环带)→ 盒底铺设扁平修补高斯(α=0.95,±3% 确定性扰动)
- `SplatFPSController` 交互:左键射击冲量(`AddForceAtPosition`)、E 键抓取/投掷;被推倒物件经 NavMeshObstacle 令 `EnemyChaser` 自动绕行
- **退出标准:** 分离/修补/PLY 往返单元测试 ✅;运行时射击-翻滚-阴影-绕行(无头验证,见 benchmarks)

### M9 — 预编译二进制资产管线(方向 6)✅ 2026-11-15

- `GaussianSplatPrecompiledAsset`(.asset 轻量元数据)+ `.s2pbin`(64B 头 + 四路 GPU 原始字节流,IEEE 802.3 CRC32)
- `SplatAssetPrecompiler`:离线解码 → Morton 重排 → Chunk AABB → 写出(Burst CRC;PrecompileFromFile 一键入口)
- `GaussianSplatRenderer.m_PrecompiledAsset` 直通加载:AsyncReadManager 原生并行直读 + 映射 memcpy 回退,零解码/零运行时排序;m_SourcePath 100% 保留回退
- 烘焙向导"预编译为商业资产包"按钮 + 一键绑定;Renderer Inspector 显示加载耗时
- **退出标准:** 往返逐位相等 + CRC/魔数/损坏拦截 ✅(单元测试);加载稳态 36.7-41.6ms、提速 16.9-26.3×(参考机 <35ms 目标,硬件上下文见 benchmarks)✅

### M10 — Chunk 级 3 档 HLOD(方向 4)✅ 2026-11-15

- Morton 局部性步进抽样(LOD0 1× / LOD1 1/4 / LOD2 1/16)——零额外显存,kChunkSize=4096 对齐保证累加数与早退数逐位恒等
- `CSCullChunks` 距离分档 + 激活数精确累加;`CSCalcViewData` 步进早退 + γscale(1.65/2.60)/γα(1.35/1.75) 能量守恒补偿
- `S2PLodBudgetController` 帧预算自适应距离乘数;性能面板 LOD0/1/2 分布与缩减比
- **退出标准:** 远景光栅化缩减 **93.7%**(目标 40-75%)+ 三机位非黑 ≥93% ✅(真实扫描无头验证)

### M11 — OBB 有向包围盒 + 连通域自动分割 + 凹体组合碰撞(方向 7)✅ 2026-11-15

- `ObbRange`(Burst 兼容)+ `DynamicPropVolume.RotationEuler/ColliderMode`;`SplatPropExtractor` OBB 局部空间切割与底面修补(零旋转退化与 M8 逐位一致)
- `SplatAutoPropDetector`:分位包络 → 96³ 各向同性体素占用 → 地面检测 → 6-邻域 CCL → XZ 2D PCA ±θ 最小面积 OBB(真实扫描 47-61ms)
- `SplatCompoundColliderBuilder`:Greedy 3D Box Merging(≤8 盒,凹腔保留);SceneView `BoxBoundsHandle` + 旋转手柄 + 一键自动检测
- **退出标准:** 45° 物件 100% 剥离/背景零误伤/修补点落在旋转足迹 ✅(单元测试);真实扫描 1 物件 → 8 盒组合碰撞 ✅

### M12 — 物理材质自适应 + 程序化声学(方向 8)✅ 2026-11-15

- `S2PSurfaceClassifier` 五类分类(高斯色度 + 最短轴扁平度)→ 32³ 空间材质场(32KB)+ 空体素 6-邻域膨胀
- `S2PSurfaceMaterialMap` O(1) 无 GC 查询 + Unity 6 `PhysicsMaterial` 惰性创建;`SplatLevel` 自动挂载
- `S2PProceduralAudio` 零外部依赖 5 类确定性合成(LCG + AudioClip.Create);FPSController 脚步/命中、S2PDynamicProp 碰撞音效
- **退出标准:** 4 材质 100% 识别 + PhysicsMaterial 属性表 ✅(单元测试);真实扫描材质场 Stone 8.9%/Wood 2.4%/Foliage 1.6% 合理 ✅

### M13 — SortedBlended 有效表面深度写回(方向 9)✅ 2026-11-15

- SV_Target1 输出 float4(z·α, α, α, α)→ **RGBA32F** 深度目标累积 D/A(R32 的 dst-α 混合因子恒为 1,无法累积——关键踩坑)
- `GaussianComposite`:zEff = D/max(A,1e-4) 透射率加权期望表面深度,经 SV_Depth 写回相机深度并与场景深度 Reversed-Z max 融合;场景深度先复制 R32 副本规避 DSV+SRV 冲突
- `SampleS2PDepth` 预乘归一化 → M3 手电筒/M7 屏幕空间法线在 SortedBlended 下获得精确 splat 深度(无需代理网格)
- **退出标准:** 深度目标非零有效像素 **95.7%**(目标 >50%)✅(真实扫描无头验证)

## 性能基准目标(桌面,RTX 级显卡)

- ≥100 万 splat 场景,开启代理 + 近似光照后仍达 60fps
- 中等规模场景的代理烘焙在编辑器内 ≤1 分钟完成

## 工程约定

- 数据布局:splat 结构体驻留 GraphicsBuffer;代理/SDF 尽量在 GPU(compute)上计算
- URP 优先(贴近就业市场);内置管线/HDRP 兼容不在 v0 范围
- 大体积扫描文件不进 git(`*.ply` 已忽略)——外部存储;必要时再启用 Git LFS
- 用 INRIA 官方代码训练的 splat 数据商用需授权——交付场景请用 gsplat/nerfstudio 训练
