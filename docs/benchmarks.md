# Splat2Play — 性能基准(benchmarks)

> 格式:`日期 | 场景 | splat 数 | 配置 | 指标 | 数值`。按里程碑汇总实测数据。

## M0 基线与渲染加载

| 日期 | 场景 | splat 数 | 配置 | 指标 | 数值 |
|---|---|---|---|---|---|
| 2026-11-11 | `scene_indoor.ply` | 559,263 | URP PC, SH2, 720p | 流式加载耗时 / 非黑像素 | 0.2s / 100.0% |
| 2026-11-11 | `scene_bonsai.ply` (INRIA iter30000) | 1,244,819 | URP PC, SH2, 720p | 流式加载耗时 / 非黑像素 | 0.2–0.3s / 99.1% |
| 2026-11-11 | 雕像样例 (SPZ 3.4MB) | — | URP PC, SPZ 渲染冒烟 | 非黑像素占比 | 100.0% |

## M1 碰撞代理烘焙与几何验证

| 日期 | 场景 | splat 数 | 体素配置 | 烘焙总时长 | 代理网格面数 | 碰撞验证 |
|---|---|---|---|---|---|---|
| 2026-11-11 | `scene_indoor.ply` | 559,263 | 256³ / 阈值 0.5 / 分位 1% | 1.3–2.1s | 90,195 三角形 | 封闭通过 |
| 2026-11-11 | `scene_bonsai.ply` | 1,244,819 | 256³ / 阈值 0.5 / 分位 1% | 1.8–1.9s | 61,035 三角形 (3 轮抽稀) | 房间中心 6 向 Raycast **6/6 命中** (地板 1.58m / 天花 4.76m / 四壁) |

## M2 NavMesh 寻路桥接

| 日期 | 场景 | splat 数 | 收集模式 | 代理面数 | NavMesh 烘焙结果 |
|---|---|---|---|---|---|
| 2026-11-11 | `scene_indoor.ply` | 559,263 | `PhysicsColliders` + `Children` | 90,195 | `ok=True` |
| 2026-11-11 | `scene_bonsai.ply` | 1,244,819 | `PhysicsColliders` + `Children` | 61,035 | `ok=True` |

## M3 近似动态光照调制

| 日期 | 场景 | splat 数 | 模式与阴影配置 | 实测结果 |
|---|---|---|---|---|
| 2026-11-11 | `scene_bonsai.ply` | 1,244,819 | `ShadowsOnly` 代理网格 + URP 聚光灯软阴影 + `GaussianComposite` Pass 0 合成调制 | 单次全屏合成采样开销 ≤ 0.3ms @ 1080p; 近景非黑像素对照 90.2% vs 93.8% |

## M4 资产级流送与运行时监控

| 日期 | 场景 | splat 数 | 配置 | 实测结果 |
|---|---|---|---|---|
| 2026-11-11 | `scene_bonsai.ply` | 1,244,819 | `FullDistance=20m` (SH3) / `CullDistance=80m` (SH1) / `Hysteresis=1.5s` | 状态机平滑切换 `Full` ↔ `Reduced` ↔ `Unloaded`，`S2PPerfPanel` 实时输出帧率、面数与内存 |

## M5 烘焙后端基准(2026-11-12 04:04)

资产:`scene_bonsai.ply`(294.4 MB,1,244,819 splats)

| 分辨率 | 后端 | 纯 GPU 派发 | 体素化阶段(含读回) | 烘焙总时长 | 三角形 |
|---|---|---|---|---|---|
| 256³ | CPU 确定性 | — | 1312 ms | 2.8 s | 76,143 |
| 256³ | GPUCompute | 36 ms | 139 ms | 1.8 s | 76,053 |
| 512³ | CPU 确定性 | — | 514 ms | 13.9 s | 47,224 |
| 512³ | GPUCompute | 67 ms | 758 ms | 14.6 s | 46,454 |

- 256³ 体素化+SDF 端到端 **139 ms(< 150ms 目标 ✓)**;纯 GPU 派发 36 ms
- 512³ 纯 GPU 派发 67 ms;端到端 758 ms 由 512MB 密度场 PCIe 读回主导(CPU 共享的 Fill/SurfaceNets 阶段不变)
- GPU 后端三角面与 CPU 略有差异(76,053 vs 76,143):GPU 路径 SDF 在填充前执行(§2.2 规格 GPU 流程),结构一致性由单元测试(UseSDF=false 下掩码/网格完全一致)保证

## M6/M7/M8 无头运行时验证(2026-11-12,scene_bonsai 1,244,819 splats,`SplatM678Verify.Run`)

| 模块 | 验证项 | 实测结果 |
|---|---|---|
| M6 | Morton 分块 | 304 chunks = ceil(1244819/4096) ✓ |
| M6 | GPU 剔除开启下远景/室内渲染 | 非黑像素均 **100.0%**(无破洞/误剔除)✓ |
| M6 | 可见计数读回 | 1,244,819/1,244,819(相机嵌入单体点云内,全部分块几何上保守可见;剔除收益需多区块/墙体遮挡场景,与 M4 9 点射线剔除互补)|
| M7 | 3 盏彩色点光源 + 手电筒(逐高斯路径) | 非黑像素 100.0%,画面完整 ✓ |
| M8 | 真实扫描动态物体分离 | 物体 2,826 splats / 背景 1,242,249 splats(含底面修补)✓ |
| M8 | 凸包碰撞装配 | 64³ 烘焙 2,177 面 + `MeshCollider(convex)` + `Rigidbody(mass=6)` + `NavMeshObstacle(carving)` ✓ |

截图:`m6_cull_far.png` / `m6_cull_inside.png` / `m7_dynamic_lights.png`(smoke_out)。

### 验证期间发现并修复的问题

- `S2PGpuCulling.compute` 缺少 `_S2PTotalSplatCount` / `_S2PArgsUseVisibleCount` uniform 声明
  → CSCullChunks 编译失败 → 间接绘制 instanceCount=0(画面全黑);已修复
- `S2PVoxelize.compute` CSClearGrid 清零内核的循环覆盖范围错误(每线程自全局 id 扫至缓冲区末尾,
  写放大 4096 倍,512³ 达 4.3 万亿次存储 → D3D12 设备移除);修正为组内 4096 范围后
  256³ GPU 体素化+SDF 从 405ms 降至 **107ms**;密度场改用 RWByteAddressBuffer(Raw)承载 512MB

## M9–M13 v3 商业化里程碑无头验证(2026-11-15,scene_bonsai 1,244,819 splats,`SplatM9To13Verify.Run`)

| 里程碑 | 验证项 | 实测结果 |
|---|---|---|
| M9 | PLY → .s2pbin 预编译 | 1,244,819 splats / SH3 / 304 chunks,载荷 **71.2 MB**,预编译耗时 812-882 ms |
| M9 | CRC32 校验 | 文件头 0x84A668E3 与资产元数据 **100% 一致**;全量校验通过;单字节翻转损坏拦截 ✓ |
| M9 | 加载耗时对比 | 原始 PLY 路径 515-1594 ms(±3× 机器噪声);预编译直通稳态 **36.7-41.6 ms**(3 采样取最小),提速 **16.9-26.3×** |
| M10 | 三机位渲染完整性 | 远景非黑 99.5% / 中景 99.3% / 室内 93.0%(无孔洞/误剔除)✓ |
| M10 | LOD 分档分布 | 远景 L0 0 / L1 0 / L2 304;中景 1/1/302;室内 5/19/278(共 304 块,零额外显存)|
| M10 | 光栅化缩减 | 远景可见 77,802 / 1,244,819 = **93.7% 缩减**(室内 111,082)|
| M11 | 自动物件检测 | 96³ CCL + XZ-PCA **47-61 ms**;检出 1 个独立物件(1,584 splats,OBB 2.27×0.85×1.14 m,yaw 0.3°)|
| M11 | 组合碰撞 | 首个物件 → **8 个非重叠 BoxCollider**(凹体保留)✓ |
| M12 | 32³ 材质场 | 550-601 ms;实心 2,772 格 + 膨胀 2,018 格;Stone **8.9%** / Wood **2.4%** / Metal **1.1%** / Foliage **1.6%** / Default 86.1% |
| M12 | 程序化音效 | 5 类 AudioClip 确定性合成(Default 100 / Stone 140 / Wood 120 / Metal 180 / Foliage 220 ms @ 32kHz)✓ |
| M13 | 深度导出 | SortedBlended + m_WriteSplatDepth:_GaussianSplatDepthRT 非零有效像素 **95.7%**(目标 >50%)✓ |

截图:`m10_hlod_far.png` / `m10_hlod_mid.png` / `m10_hlod_inside.png` / `m13_depth_export.png`(smoke_out)。

### M9 <35ms 目标的硬件上下文(如实记录)

规格参考机按 ~850ms 原始基线 + NVMe 推算出 <35ms。本机实测:71.2MB 载荷 = 磁盘读(31-48ms,
AsyncReadManager 原生并行直读,VS FileStream 大缓冲 30ms / MMF memcpy 37-85ms±2× 噪声)+
PCIe 上传(20-39ms,4 缓冲 alloc≈0 / copy 主导)。解码/排序/转换工作已**结构性消除**(CRC 与
点数/分块校验全通过),稳态 36.7-41.6ms、提速 16.9-26.3×;在参考机 IO/上传带宽下可达 <35ms。

### 验证期间发现并修复的问题

- **M13 深度目标格式(R32 → RGBA32F)**:R32_SFloat 无 alpha 通道,D3D12 混合的 dst-α 因子恒为 1
  → `OneMinusDstAlpha` = 0,预乘深度永不累积(非零像素 0%)。修复:深度目标改 **RGBA32F**,
  SV_Target1 输出 float4(z·α, α, α, α)——α 通道以与颜色目标完全相同的递推累积 A,
  r 精确累积 D = Σ w_i·z_i;Reprojected 路径保持 R32 标量写入不变(One/Zero 混合不受 dst-α 影响)
- **M11 检测器中心 Y**:旋转系 OBB 中心回变换后漏加网格原点 lo.y(单元测试场景 lo.y=0 掩盖了 bug);
  真实扫描上物件整体错位 → Extract 命中 0 splat;已修复并经真实资产验证(检出盆景盆 1,584 splats)
- **M9 加载路径选型**:FileStream 大缓冲 ≈30ms / AsyncReadManager ≈31-48ms / MMF memcpy 37-85ms
  (±2× 缺页噪声);选定 AsyncReadManager 为主、映射 memcpy 为回退;CRC32 从热路径剥离
  (Burst CRC ≈ 71MB 25ms,保留给预编译写出与测试/验证路径)
