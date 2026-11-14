// SPDX-License-Identifier: MIT
// Splat2Play v3 — M9(方向 6)T9.1:预编译二进制资产管线
// 双文件架构:轻量 .asset(ScriptableObject 元数据)+ .s2pbin(64B 头 + 四路 GPU 原始字节流)。
// 为什么不把 uint[] 直接序列化进 ScriptableObject:上百兆 YAML/二进制数组在 Inspector 选中
// 或 SaveAssets 时会把主线程卡死数秒;外置二进制载荷以 FileStream 一次性读入 NativeArray<uint>
// 并 SetData 上传,全程 0 托管大数组、0 解码、0 运行时 Morton 排序。

using System;
using System.IO;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// .s2pbin 文件头(Little-Endian,固定 16 × uint = 64 字节)。
    /// 注意 OtherBytes:运行时 GPU 打包布局为 quat(1B packed)+ half2×2 → 每 splat 3 个 uint = 12 字节
    /// (与 GaussianSplatData.geometry 一致;规格书草案中 "N*8" 为笔误,以实际 GPU 布局为准)。
    /// </summary>
    public struct S2PBinHeader
    {
        public const uint kMagic = 0x42503253u; // "S2PB" (Little-Endian)
        public const uint kVersion = 1u;

        public uint Magic;              // 0x42503253 ("S2PB")
        public uint Version;            // 1
        public int SplatCount;          // 高斯点总数 N
        public int ShDegree;            // 0..3
        public int ShCoefficients;      // 0, 3, 8, 15
        public int MortonReordered;     // 1 = 已完成 30-bit Morton 空间重排, 0 = 原始顺序
        public uint PosBytes;           // N * 12 bytes (3 uints per splat)
        public uint OtherBytes;         // N * 12 bytes (3 uints per splat: quat + half2×2 scale/opacity)
        public uint ColorBytes;         // N * 4 bytes  (1 uint per splat, RGB10E2 biased)
        public uint ShBytes;            // N * GetSHDataSize(shCoefficients) bytes(或 shDegree=0 时 4 字节占位)
        public uint PayloadCrc32;       // 紧随 Header 之后的全部载荷字节的 IEEE 802.3 CRC32
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
        public uint Reserved4;          // 凑齐 16 * 4 = 64 字节头部

        /// <summary>四路载荷总字节数(校验文件完整性用)。</summary>
        public uint PayloadBytes => PosBytes + OtherBytes + ColorBytes + ShBytes;
    }

    /// <summary>
    /// 预编译 splat 商业资产:仅持有轻量元数据,二进制载荷外置于 .s2pbin。
    /// 由 SplatAssetPrecompiler 离线生成,GaussianSplatRenderer.m_PrecompiledAsset 直通加载。
    /// </summary>
    [CreateAssetMenu(menuName = "Splat2Play/Precompiled Splat Asset", fileName = "SplatPrecompiledAsset")]
    public sealed class GaussianSplatPrecompiledAsset : ScriptableObject
    {
        [Tooltip("相对于 StreamingAssets 的 .s2pbin 二进制载荷路径,或工程内相对路径,或绝对路径")]
        public string PayloadPath;
        public int SplatCount;
        public int ShDegree;
        public int ShCoefficients;
        public bool IsMortonReordered = true;
        public Bounds LocalBounds;
        public uint PayloadCrc32;
        public Bounds[] ChunkBounds = Array.Empty<Bounds>();
        public int[] ChunkSplatCounts = Array.Empty<int>();
        public GaussianSplatCameras.CameraInfo[] Cameras = Array.Empty<GaussianSplatCameras.CameraInfo>();

        /// <summary>
        /// 解析载荷全路径:绝对路径 → StreamingAssets 相对路径 → 工程根相对路径。
        /// </summary>
        public string ResolvePayloadFullPath()
        {
            if (string.IsNullOrWhiteSpace(PayloadPath)) return string.Empty;
            if (Path.IsPathRooted(PayloadPath))
                return File.Exists(PayloadPath) ? PayloadPath : Path.GetFullPath(PayloadPath);
            string p1 = Path.Combine(Application.streamingAssetsPath, PayloadPath);
            if (File.Exists(p1)) return p1;
            string root = Path.GetDirectoryName(Application.dataPath) ?? "";
            return Path.Combine(root, PayloadPath);
        }

        /// <summary>按 chunk 布局生成 ChunkSplatCounts(每 Chunk 4096,末块可能不满)。</summary>
        public static int[] BuildChunkSplatCounts(int splatCount, int chunkCount)
        {
            var counts = new int[Mathf.Max(chunkCount, 0)];
            for (int c = 0; c < counts.Length; ++c)
                counts[c] = Mathf.Min(SplatGpuChunking.kChunkSize, splatCount - c * SplatGpuChunking.kChunkSize);
            return counts;
        }
    }
}
