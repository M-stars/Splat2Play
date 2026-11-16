# Splat2Play

## 为什么做这个

游戏引擎默认关卡是网格:要有碰撞体、导航网格、光照、遮挡剔除。3DGS 用几小时的采集就能得到照片级的真实场景(传统手工制作需要数周到数月的美术工时)——但开箱即用的 splat 场景只是"一张能移动的照片":不能碰撞、不能寻路、不响应光照。

Splat2Play 是一套 Unity 工具链,为扫描场景补齐游戏引擎要求的全部"接入层",让一段真实扫描的街角可以作为真正的游戏关卡交付。

## 模块

| 模块 | 功能 |
|---|---|
| **碰撞代理生成 (M1/M5)** | Splat 密度场体素化(CPU Burst 确定性 / GPU Compute 139ms) → 洪泛填充 → SDF → Surface Nets 抽稀碰撞/寻路双网格 |
| **NavMesh 桥接与 AI (M2)** | 代理网格直出 Unity NavMesh,敌人 AI(`EnemyChaser`,含视线遮挡校验)沿真实扫描地形寻路追击 |
| **动态光照与法线重光照 (M3/M7)** | 代理深度阴影调制 + 椭球最短轴法线提取(`ExtractSplatNormalAndFlatness`) + 多光源 Blinn-Phong 动态重光照 |
| **流送、GPU 剔除与 3 档 HLOD (M4/M6/M10)** | Morton 30-bit 空间分块 + GPU Frustum/Hi-Z 剔除 + `DrawProceduralIndirect` + 零额外显存 3 档步进 HLOD(远景缩减 93.7%) |
| **预编译资产管线 (M9)** | `.s2pbin` 64B 对齐二进制流 + IEEE 802.3 CRC32 + `AsyncReadManager` 原生直通加载(124 万点 36.7ms,提速 16.9–26.3×) |
| **动态刚体剥离与凹体碰撞 (M8/M11)** | $96^3$ 3D CCL + XZ-PCA 自动物件检测(47ms) + OBB 旋转切割与底面修补 + 3D 贪心多盒组合碰撞(≤8 盒保留凹腔) |
| **物理材质场与程序化声学 (M12)** | 高斯色度/扁平度 5 类分类 + $32^3$ 材质场(32KB) $O(1)$ 查询 + `PhysicsMaterial` 绑定 + 零外部依赖 LCG 脚步/碰撞声合成 |
| **有效表面深度写回 (M13)** | `SortedBlended` 下 `RGBA32F` 透射率加权期望表面深度($\sum w_i z_i / \sum w_i$) + `SV_Depth` 写回(覆盖率 95.7%),打通 URP 后处理 |

## 目标 Demo

一段真实扫描街角/室内的第一人称可交互关卡:玩家与真实几何碰撞不穿墙、手电筒与多光源实时照亮扫描表面、敌人 AI 沿真实地形追击并绕行被推倒的 3DGS 动态刚体、脚步与射击按脚下真实材质发声、M9 直通加载 + M10 HLOD 保持高帧率。

## 路线图与规格文档

- **路线图 (`M0`–`M13` 全部完成)**:详见 [ROADMAP.md](ROADMAP.md)
- **实测性能基准**:详见 [docs/benchmarks.md](docs/benchmarks.md)
- **技术规格书**:
  - v1 基础管线 (`M0`–`M4`):[docs/项目书.md](docs/项目书.md)
  - v2 强化规格 (`M5`–`M8`):[docs/项目书-v2强化规格.md](docs/项目书-v2强化规格.md)
  - v3 商业化规格 (`M9`–`M13`):[docs/项目书-v3商业化规格.md](docs/项目书-v3商业化规格.md)

## 上游 Fork 基线与补丁清单 (`aras-p/UnityGaussianSplatting`)

`Assets/Splat2Play/ThirdParty/GaussianSplatting` 基于 [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) (Unity 6 RenderGraph + Stochastic/Temporal 分支基线)深度定制。向跟进上游修复时请保留以下 S2P 扩展锚点:

1. **M5 原生流解码门面**:`GaussianSplatRawExtractor.cs` + `AssemblyInfo.cs` (`InternalsVisibleTo("Splat2Play.Runtime")`)
2. **M6/M10 Morton 分块、GPU 剔除与 HLOD**:`SplatGpuChunking.cs`、`GaussianSplatRenderer.cs` (`DispatchGpuChunkCulling` / `m_IndirectArgs` 5-uint 索引间接绘制)、`GaussianSplatUtilities.compute` (`_S2PChunkVisibleFlags` 步进早退与 $\gamma_{\text{scale}}/\gamma_\alpha$ 补偿)
3. **M7 逐高斯法线重光照**:`GaussianSplatUtilities.compute` (`ExtractSplatNormalAndFlatness` + `_S2PGpuLight*`)
4. **M9 预编译直通加载**:`GaussianSplatPrecompiledAsset.cs`、`SplatAssetPrecompiler.cs`、`GaussianSplatRenderer.TryLoadPrecompiled`
5. **M3/M13 深度导出与合成调制**:`GaussianSplats.shader` (`SV_Target1` RGBA32F 预乘深度累积)、`GaussianComposite.shader` (`S2P_WRITE_COMPOSITE_DEPTH` 变体 `SV_Depth` 写回 + 手电筒阴影调制)、`GaussianSplatURPFeature.cs` (`m_SplatDepthRT` / `m_SceneDepthScratch`)

## 站在这些肩膀上

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)(MIT)——事实上的 Unity 3DGS 渲染器,本项目集成基座
- 相关研究:PhysGaussian、GaMeS、RigGS、Habitat-GS、Mip-Splatting

## 许可

本仓库代码:MIT。关于 splat **数据**的注意:使用 INRIA 官方实现训练的数据,商业使用需要 INRIA 授权——正式交付的场景请用 gsplat / nerfstudio 系训练。
