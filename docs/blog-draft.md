# 让 3D Gaussian Splatting 走进游戏关卡：碰撞代理与近似动态光照实践（Splat2Play 技术复盘）

> **摘要：** 3D Gaussian Splatting（3DGS）只需数小时采集即可重建照片级真实场景，但开箱即用的 splat 资产在游戏引擎中只是“一张能移动的立体照片”——没有拓扑网格、无法碰撞寻路、外观烘焙在球谐系数（SH）中不响应动态光照與阴影。本文复盘开源工具链 **Splat2Play（实境入戏）** 在 Unity 6（URP + D3D12）中的工程实践，重点剖析 **确定性碰撞代理烘焙（M1）** 与 **基于代理深度的近似动态光照调制（M3）** 的设计权衡、RenderGraph 管线踩坑与误差分析。

---

## 1. 核心矛盾：网格世界观 vs 高斯椭球云

现代游戏引擎（Unity / Unreal）的物理碰撞、导航寻路（NavMesh）、阴影投射（Shadow Map）与遮挡剔除，全部建立在**显式三角网格（Mesh）**之上。而一个典型的 3DGS 场景由 100 万–600 万个带三维尺度 $\sigma$、旋转四元数 $q$、不透明度 $\alpha$ 与球谐系数（SH0–SH3）的 3D 高斯椭球组成：

1. **无显式表面拓扑**：无法直接挂 `MeshCollider` 或烘焙 `NavMeshSurface`；
2. **外观静态烘焙**：静态环境光已被编码进 0–3 阶球谐系数，动态光源（如第一人称手电筒）扫过时既无明暗变化，也无法投射或接收阴影；
3. **半透明混合渲染**：传统的从后往前（或从前往后）Alpha 混合不写硬件深度缓冲（或仅在 Stochastic 模式下写出随机采样深度）。

**Splat2Play 的核心思路**是不改动 3DGS 训练端（不要求重训练 Relightable 3DGS），而是在编辑器离线阶段从高斯的物理分布中提取一层轻量、封闭的**几何代理网格（Proxy Mesh）**，并在运行时将其作为物理碰撞、AI 寻路与动态阴影的统一桥梁。

---

## 2. M1 碰撞代理管线：2 秒烘焙百万高斯且逐字节确定

从百万级离散椭球提取封闭可走的 `MeshCollider`，管线分为五步（详见 `SplatProxyBaker` 与 `ProxyBakeJobs`）：

```
PLY 流式分块解包 → 百分位包围盒 (剔除天空离群点) → 3σ 椭球密度体素化 (Q16.16)
    → 边界连通填充 + Seal/Exclusion 修正 → Surface Nets 等值面提取 → 桶聚类抽稀
```

### 2.1 为什么用 Q16.16 定点原子累加？
每个高斯 $i$ 对其 $3\sigma$ 世界包围盒内的体素中心 $\mathbf{x}$ 贡献马氏距离加权密度：

$$
\Delta \rho_i(\mathbf{x}) = \alpha_i \cdot \exp\left(-\frac{1}{2} \|\mathbf{\Sigma}_i^{-1/2}(\mathbf{x} - \boldsymbol{\mu}_i)\|^2\right)
$$

在 `IJobParallelFor` 多线程并行体素化时，若直接对 `float` 做原子累加，由于 IEEE 754 浮点加法**不满足结合律**，线程调度顺序的微小变化会导致体素密度在阈值边界（如 `0.5`）附近翻转，进而导致提取出的网格拓扑每次烘焙不一致。

我们将单次贡献量化为 **Q16.16 定点整数**（`1.0` 对应 `65536`），使用 `Interlocked.Add` 累加到 `NativeArray<int>`，并在 `SurfaceNetsJob` 读取时归一化回浮点密度 `(DensityQ16 & 0x7FFFFFFF) / 65536f`。由于整数加法严格满足交换律与结合律，配合无哈希表冲突的桶数组顶点聚类抽稀（`DecimateJob`），实现了**同参数重跑结果 100% 逐顶点一致**。

---

## 3. M3 近似动态光照：三条技术路径对比与演进

要让玩家手中的聚光灯（手电筒）在扫描街角产生可信的光束照亮与遮挡阴影，我们在项目设计阶段评估了三条路径：

| 维度 | 路径 A：独立 RenderGraph Pass 调制 | 路径 B：splat 着色器内直采阴影 | 路径 C（最终落地）：ShadowsOnly 代理 + Composite 合成调制 |
|---|---|---|---|
| **遮挡几何源** | 自定义 Depth-Only Pass 渲染代理网格 | 场景碰撞体投射阴影 | 碰撞子节点 `ShadowCastingMode.ShadowsOnly` 直接写入 URP 附加光阴影图 |
| **调制阶段** | `AfterRenderingTransparents` 全屏后处理 | 每个高斯片元混合前逐次采样 | `GaussianComposite.shader` Pass 0（最终合成到 `cameraColor` 时）每像素采样一次 |
| **带宽与算力开销** | 需额外维护 Proxy Depth RT 与自定义 Pass | 极高（Overdraw 倍数 × 阴影采样） | **极低（≤ 0.3 ms @ 1080p）**，每个屏幕像素仅采样一次阴影图 |
| **时序滤波兼容性** | 易与 GS 合成 Pass 执行顺序冲突 | 增加片元寄存器压力 | **在 Temporal Accumulation 之后调制，不污染历史帧缓冲** |

### 3.1 踩坑实录：为什么放弃独立 RenderGraph Pass 与 Pass 1 累积内调制？

在早期原型中，我们曾尝试过两种写法并遇到了典型问题：

1. **URP RenderGraph 执行顺序陷阱**：
   `GaussianSplatURPFeature` 挂在 `RenderPassEvent.BeforeRenderingTransparents`，且使用 `AddUnsafePass` 将 splat 绘制并合成到 `activeColorTexture`。若外部再挂一个自定义 `ScriptableRendererFeature`，极易因 RenderGraph 资源读写依赖推导或事件排序导致调制发生在 splat 合成之前。
2. **Temporal Reprojection（Pass 1）历史帧污染与静止失效**：
   `UnityGaussianSplatting` 在开启 `TransparencyMode.Stochastic` + `StochasticTemporalFilter.Reprojected` 时，Pass 1（`fragAccumulate`）负责将当前帧与上一帧 `history.previous` 混合。
   - **陷阱一（静止帧失效）**：`GaussianSplatTemporalAccumulator` 仅当 `history.frames > 0 && history.viewProjection != viewProjection`（即非首帧且相机运动）时才开启 `SPLAT_REPROJECTED` 宏。若把光照写在 `#if defined(SPLAT_REPROJECTED)` 内，玩家一旦停下脚步或单帧截图时，手电筒光照会瞬间消失。
   - **陷阱二（运动帧指数过曝）**：若在 Pass 1 中直接将手电筒增量光加到 `history.next`，下一帧重投影读回 `history.previous` 时会带着上一帧的灯光再次叠加，导致相机转动时画面迅速过曝。

### 3.2 最终方案：ShadowsOnly 代理投射 + Pass 0 屏幕空间合成调制

最终落地的架构由三部分紧密咬合：

1. **隐形阴影投射体（Shadow Caster）**：
   `SplatLevel.BuildCollision()` 在生成 `S2P_Collision` 子节点时，除了挂载 `MeshCollider` 外，同时挂载指向 `Proxy.CollisionMesh` 的 `MeshFilter` 与 `MeshRenderer`（使用包含 `ShadowCaster` Pass 的 URP Lit 材质），并将 `shadowCastingMode` 设为 `ShadowCastingMode.ShadowsOnly`。这样代理网格在主相机中完全不可见，但会自动参与 URP 聚光灯 `_AdditionalLightsShadowmapTexture` 的渲染。
2. **双源深度回退重建世界坐标**：
   在 `GaussianSplatURPFeature` 执行 Pass 0 合成前注入逆视图投影矩阵 `_SplatInvViewProjection` 与场景深度 `_S2PSceneDepthTexture`。在 `GaussianComposite.shader` 的 Pass 0 (`frag`) 中，优先读取 `_GaussianSplatDepthRT`（Stochastic Reprojected 模式下由 splat 直接写入），若为 `0`（如普通 Alpha 混合模式）则自动回退读取 `_S2PSceneDepthTexture`，结合 D3D12 投影翻转修正重建像素世界坐标 $\mathbf{p}_{\text{world}}$。
3. **D3D12 Reversed-Z 阴影比较与明暗调制**：
   将 $\mathbf{p}_{\text{world}}$ 经 `_AdditionalLightsWorldToShadow[slice]` 变换至附加光阴影图 UV 空间 $\mathbf{s} = (u, v, z_{\text{point}})$。在 D3D12 Reversed-Z 下（近平面为 $1$，远平面为 $0$），若阴影图记录的遮挡物深度 $z_{\text{map}} > z_{\text{point}} + b$，则说明该点被前方代理几何遮挡：

$$
\text{lit} = \text{saturate}\left(\frac{z_{\text{point}} - z_{\text{map}} + b}{\max(b, 10^{-4})}\right)
$$

   最终合成颜色同时调制环境暗部保留率 `AmbientFloor` 与聚光灯锥角/距离衰减高光，使光锥内外与阴影边界层次分明。

---

## 4. 诚实的误差分析：近似光照的边界在哪里？

必须明确指出：**基于碰撞代理的动态光照是一种工程近似，而非物理真实的 3DGS 重光照（Relighting）**。其误差主要来自三个方面：

1. **代理网格与真实 splat 表面的几何偏差 $\varepsilon$**：
   体素化步长 $h = L_{\max} / R$（256 分辨率下室内场景约 $4\text{–}6\text{ cm}$），经过顶点聚类抽稀后，代理网格表面与高斯椭球视觉表面之间存在 $\varepsilon \approx 0.5h \sim 1.5h$ 的法向偏移。
   - **表现**：若阴影偏移 `Bias` 过小，代理网格置于 splat 表面外侧时会产生自阴影条纹（Shadow Acne）；若 `Bias` 过大，墙角阴影会出现轻微脱离（Peter Panning）。
   - **缓解**：通过 `S2PLightModulationDriver.Bias`（默认 `0.002`）与软阴影过滤带宽平滑过渡区。
2. **高频细长结构的阴影缺失**：
   直径小于体素步长 $h$ 的细杆、电线或镂空栅栏在体素化中密度不足被过滤，除非手动添加 `PatchBoxes`，否则不会投射独立阴影。
3. **缺乏真实表面法线与材质 BRDF 解耦**：
   由于原场景烘焙光已固化在 SH 系数中，合成阶段的乘法/加法调制无法消除原场景自带的强高光或固有阴影，最适合“昏暗室内/夜间街角 + 强聚光手电筒”的玩法氛围。

---

## 5. 实测性能汇总

在开发机（Windows D3D12, Unity 6000.6.2f1, URP PC 档）上对公开数据集的实测结果（详见 [benchmarks.md](benchmarks.md)）：

- **`bonsai`（1,244,819 splats, SH2）**：
  - 流式加载耗时：**0.2–0.3 s**
  - 256³ 体素化 + 填充 + Surface Nets + 3 轮抽稀烘焙总耗时：**1.8–1.9 s**（生成 61,035 个三角形，≤ 15 万面预算）
  - 房间中心 6 向物理射线检测：**6/6 全部命中**（地板 1.58m、天花板 4.76m、四壁闭合）
  - NavMesh 烘焙（`PhysicsColliders`）：**一次通过**
- **`scene_indoor`（559,263 splats）**：
  - 加载 **0.2 s**，代理烘焙 **1.3–2.1 s**（90,195 个三角形），NavMesh 烘焙通过。
