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
