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
