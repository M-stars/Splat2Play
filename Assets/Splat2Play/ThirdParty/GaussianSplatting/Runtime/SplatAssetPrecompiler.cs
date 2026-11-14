// SPDX-License-Identifier: MIT
// Splat2Play v3 — M9(方向 6)T9.2:离线预编译器 + .s2pbin 读写器 + IEEE 802.3 CRC32
// 内部流程(BuildFromData):
//   1. 可选 Morton 重排(SplatMortonReorder.ReorderAndBuildChunks 就地重排并构建 Chunk AABB);
//   2. 四路 NativeArray<uint>(positions/geometry/colors/sh)计算 IEEE 802.3 CRC32;
//   3. 写 64 字节 S2PBinHeader,紧接四路原始字节(与 GPU GraphicsBuffer 布局逐位一致);
//   4. 返回填充好元数据的 GaussianSplatPrecompiledAsset(.asset 落盘由编辑器层负责)。
// 运行时(ReadPayload):校验魔数/版本/长度/CRC → 直读四路字节进 GaussianSplatData →
//   调用方 CreateResources(data, chunkBoundsOverride) 完成上传,零解码零排序。

using System;
using System.Diagnostics;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GaussianSplatting.Runtime
{
        /// <summary>IEEE 802.3 CRC32(反射多项式 0xEDB88320,初值/终值 0xFFFFFFFF)。</summary>
        public static class S2PCrc32
        {
            public const uint kInitial = 0xFFFFFFFFu;

            static readonly uint[] s_Table = BuildTable();

            static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; ++i)
                {
                    uint c = i;
                    for (int k = 0; k < 8; ++k)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                return table;
            }

            public static unsafe uint Update(uint crc, byte* data, long length)
            {
                for (long i = 0; i < length; ++i)
                    crc = s_Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
                return crc;
            }

            public static uint Finalize(uint crc) => crc ^ kInitial;
        }

        /// <summary>
        /// 四路载荷 CRC 的 Burst 作业(71MB 场景 Mono 下 ~200ms → Burst ~25ms;
        /// 供离线预编译与校验路径使用;运行时热路径按规格仅校验魔数/点数,不做全量 CRC)。
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct S2PCrc32Job : IJob
        {
            [ReadOnly] public NativeArray<uint> Positions;
            [ReadOnly] public NativeArray<uint> Geometry;
            [ReadOnly] public NativeArray<uint> Colors;
            [ReadOnly] public NativeArray<uint> Sh;
            public NativeArray<uint> Result; // 长度 1,Finalize 后的 CRC

            public unsafe void Execute()
            {
                uint crc = S2PCrc32.kInitial;
                crc = Update(crc, Positions);
                crc = Update(crc, Geometry);
                crc = Update(crc, Colors);
                crc = Update(crc, Sh);
                Result[0] = S2PCrc32.Finalize(crc);
            }

            static unsafe uint Update(uint crc, NativeArray<uint> arr)
            {
                var bytes = arr.Reinterpret<byte>(sizeof(uint));
                return S2PCrc32.Update(crc, (byte*)bytes.GetUnsafeReadOnlyPtr(), bytes.Length);
            }
        }

    public static class SplatAssetPrecompiler
    {
        /// <summary>预编译泵循环超时(离线编辑器操作,6 分钟上限防死等)。</summary>
        const int kPrecompileTimeoutMs = 600_000;

        /// <summary>四路数组 CRC 的 Burst 路径(写盘前/校验时调用;返回 Finalize 后的 CRC)。</summary>
        static uint ComputePayloadCrcBurst(GaussianSplatData data)
        {
            using var result = new NativeArray<uint>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new S2PCrc32Job
            {
                Positions = data.positions, Geometry = data.geometry,
                Colors = data.colors, Sh = data.sh, Result = result
            }.Schedule().Complete();
            return result[0];
        }

        /// <summary>
        /// 由已解码的 GaussianSplatData 构建预编译资产并写出 .s2pbin。
        /// 注意:mortonReorder=true 时会对 data 的四路数组就地重排(双射置换),调用方保留引用即可。
        /// </summary>
        public static GaussianSplatPrecompiledAsset BuildFromData(
            GaussianSplatData data,
            GaussianSplatCameras.CameraInfo[] cameras,
            string binOutputPath,
            bool mortonReorder = true)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(binOutputPath)) throw new ArgumentNullException(nameof(binOutputPath));

            // 1. Morton 重排 + Chunk AABB(或原始顺序下仅构建 AABB)
            Bounds[] chunkBounds = mortonReorder
                ? SplatMortonReorder.ReorderAndBuildChunks(data)
                : BuildChunkBoundsOnly(data);
            if (chunkBounds == null) chunkBounds = Array.Empty<Bounds>();

            // 2. 四路载荷 CRC32(Burst;先算后写,Header 中携带)
            uint crc = ComputePayloadCrcBurst(data);

            var header = new S2PBinHeader
            {
                Magic = S2PBinHeader.kMagic,
                Version = S2PBinHeader.kVersion,
                SplatCount = data.count,
                ShDegree = data.shDegree,
                ShCoefficients = data.shCoefficients,
                MortonReordered = mortonReorder ? 1 : 0,
                PosBytes = checked((uint)(data.positions.Length * 4)),
                OtherBytes = checked((uint)(data.geometry.Length * 4)),
                ColorBytes = checked((uint)(data.colors.Length * 4)),
                ShBytes = checked((uint)(data.sh.Length * 4)),
                PayloadCrc32 = crc
            };

            // 3. 写出 64B 头 + 四路原始字节
            string dir = Path.GetDirectoryName(Path.GetFullPath(binOutputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(binOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                bw.Write(header.Magic);
                bw.Write(header.Version);
                bw.Write(header.SplatCount);
                bw.Write(header.ShDegree);
                bw.Write(header.ShCoefficients);
                bw.Write(header.MortonReordered);
                bw.Write(header.PosBytes);
                bw.Write(header.OtherBytes);
                bw.Write(header.ColorBytes);
                bw.Write(header.ShBytes);
                bw.Write(header.PayloadCrc32);
                bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); // Reserved0..4 → 64B
                unsafe
                {
                    WritePayload(fs, data.positions);
                    WritePayload(fs, data.geometry);
                    WritePayload(fs, data.colors);
                    WritePayload(fs, data.sh);
                }
            }

            // 4. 元数据资产(轻量;二进制载荷外置)
            var asset = ScriptableObject.CreateInstance<GaussianSplatPrecompiledAsset>();
            asset.PayloadPath = MakePayloadPathRelative(binOutputPath);
            asset.SplatCount = data.count;
            asset.ShDegree = data.shDegree;
            asset.ShCoefficients = data.shCoefficients;
            asset.IsMortonReordered = mortonReorder;
            asset.LocalBounds = data.bounds;
            asset.PayloadCrc32 = crc;
            asset.ChunkBounds = chunkBounds;
            asset.ChunkSplatCounts = GaussianSplatPrecompiledAsset.BuildChunkSplatCounts(data.count, chunkBounds.Length);
            asset.Cameras = cameras ?? Array.Empty<GaussianSplatCameras.CameraInfo>();
            return asset;
        }

        /// <summary>
        /// 便捷入口:从 PLY/SPZ 源文件同步预编译(内部走 GaussianSplatLoadOperation 解码管线并泵完成)。
        /// progress(stage, 0..1) 可选;返回未落盘的 GaussianSplatPrecompiledAsset(编辑器层负责 .asset 存储)。
        /// </summary>
        public static GaussianSplatPrecompiledAsset PrecompileFromFile(string sourcePath, string binOutputPath,
            bool mortonReorder = true, Action<string, float> progress = null)
        {
            progress?.Invoke("解码扫描资产(PLY/SPZ)…", 0.05f);
            var op = new GaussianSplatLoadOperation(Application.streamingAssetsPath, sourcePath);
            GaussianSplatData data;
            GaussianSplatCameras.CameraInfo[] cameras;
            try
            {
                var sw = Stopwatch.StartNew();
                while (!op.isDone)
                {
                    op.Update();
                    if (sw.ElapsedMilliseconds > kPrecompileTimeoutMs)
                        throw new TimeoutException($"预编译解码超时({kPrecompileTimeoutMs / 1000}s): {sourcePath}");
                    System.Threading.Thread.Sleep(1);
                }
                cameras = op.cameras;
                if (op.cameraError != null)
                    Debug.LogWarning($"[S2P M9] 相机侧车文件加载失败(忽略): {op.cameraError}");
                data = op.TakeData();
                if (data == null)
                    throw new InvalidDataException("预编译失败:解码管线未产出 splat 数据");
            }
            finally
            {
                op.Dispose();
            }

            progress?.Invoke("Morton 重排 + 写出 .s2pbin…", 0.6f);
            try
            {
                return BuildFromData(data, cameras, binOutputPath, mortonReorder);
            }
            finally
            {
                data.Dispose();
            }
        }

        /// <summary>读取 .s2pbin 64B 头(校验魔数与版本)。</summary>
        public static S2PBinHeader ReadHeader(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            using var br = new System.IO.BinaryReader(fs);
            var h = new S2PBinHeader
            {
                Magic = br.ReadUInt32(),
                Version = br.ReadUInt32(),
                SplatCount = br.ReadInt32(),
                ShDegree = br.ReadInt32(),
                ShCoefficients = br.ReadInt32(),
                MortonReordered = br.ReadInt32(),
                PosBytes = br.ReadUInt32(),
                OtherBytes = br.ReadUInt32(),
                ColorBytes = br.ReadUInt32(),
                ShBytes = br.ReadUInt32(),
                PayloadCrc32 = br.ReadUInt32()
            };
            h.Reserved0 = br.ReadUInt32();
            h.Reserved1 = br.ReadUInt32();
            h.Reserved2 = br.ReadUInt32();
            h.Reserved3 = br.ReadUInt32();
            h.Reserved4 = br.ReadUInt32();
            if (h.Magic != S2PBinHeader.kMagic)
                throw new InvalidDataException($".s2pbin 魔数不符: 0x{h.Magic:X8}(期望 0x{S2PBinHeader.kMagic:X8})");
            if (h.Version != S2PBinHeader.kVersion)
                throw new InvalidDataException($".s2pbin 版本不符: {h.Version}(期望 {S2PBinHeader.kVersion})");
            if (fs.Length - 64 != h.PayloadBytes)
                throw new InvalidDataException($".s2pbin 长度不符: 文件 {fs.Length}B,Header 声明 {64 + h.PayloadBytes}B");
            return h;
        }

        /// <summary>
        /// 运行时直通读取:把四路字节零拷贝读进 GaussianSplatData。
        /// 校验策略:头部魔数/版本/长度恒校验(防截断);verifyCrc=true 时额外做全量 CRC32
        /// (Burst ~25ms,供单元测试与验证脚本);运行时热路径默认 false —— 规格 §2.2(3) 的
        /// 直通加载只要求魔数与 SplatCount 校验,以达成 &lt;35ms 目标。
        /// 载荷读取走 AsyncReadManager 原生并行 ReadCommand(4 路同时直读,绕过托管 Stream 层)。
        /// 返回的数据由调用方 Dispose(上传后即释放,与加载管线一致)。
        /// </summary>
        public static GaussianSplatData ReadPayload(string path, GaussianSplatPrecompiledAsset meta, bool verifyCrc = false)
        {
            S2PBinHeader h = ReadHeader(path);
            int n = h.SplatCount;
            if (n <= 0 || h.ShDegree < 0 || h.ShDegree > 3)
                throw new InvalidDataException($".s2pbin 数据无效: SplatCount={n}, ShDegree={h.ShDegree}");
            int shCoefficients = (h.ShDegree + 1) * (h.ShDegree + 1) - 1;
            if (h.ShCoefficients != shCoefficients)
                throw new InvalidDataException($".s2pbin SH 系数数不符: {h.ShCoefficients}(期望 {shCoefficients})");
            uint posBytes = checked((uint)(n * 12));
            uint otherBytes = checked((uint)(n * 12));
            uint colorBytes = checked((uint)(n * 4));
            uint shBytes = checked((uint)(n * Mathf.Max(1, GaussianUtils.GetSHDataSize(shCoefficients))));
            if (h.PosBytes != posBytes || h.OtherBytes != otherBytes || h.ColorBytes != colorBytes || h.ShBytes != shBytes)
                throw new InvalidDataException(
                    $".s2pbin 四路长度声明不符: pos {h.PosBytes}/{posBytes}, other {h.OtherBytes}/{otherBytes}, " +
                    $"color {h.ColorBytes}/{colorBytes}, sh {h.ShBytes}/{shBytes}");

            var data = new GaussianSplatData(n, h.ShDegree);
            try
            {
                ReadPayloadNative(path, data, h);
                if (verifyCrc)
                {
                    uint crc = ComputePayloadCrcBurst(data);
                    if (crc != h.PayloadCrc32)
                        throw new InvalidDataException($".s2pbin CRC32 校验失败: 0x{crc:X8}(Header 0x{h.PayloadCrc32:X8})");
                    if (meta != null && meta.PayloadCrc32 != 0 && meta.PayloadCrc32 != h.PayloadCrc32)
                        throw new InvalidDataException(
                            $".s2pbin CRC32 与资产元数据不符: 文件 0x{h.PayloadCrc32:X8}, 资产 0x{meta.PayloadCrc32:X8}");
                }

                Bounds lb = meta?.LocalBounds ?? default;
                if (meta == null || lb.size == Vector3.zero)
                {
                    // 无元数据时用分位包络兜底(与离线 bounds 语义近似;仅供诊断)
                    data.bounds = new Bounds(Vector3.zero, Vector3.one);
                }
                else
                {
                    data.bounds.SetMinMax(lb.min, lb.max);
                }
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }

        /// <summary>
        /// AsyncReadManager 原生并行读取四路载荷(单次调用 4 条 ReadCommand,绕过托管 Stream 层;
        /// 与 GaussianSplatLoadOperation 的 PLY 直读路径同一机制)。
        /// </summary>
        static unsafe void ReadPayloadNative(string path, GaussianSplatData data, S2PBinHeader h)
        {
            var arrs = new (NativeArray<uint> arr, uint bytes)[4]
            {
                (data.positions, h.PosBytes),
                (data.geometry, h.OtherBytes),
                (data.colors, h.ColorBytes),
                (data.sh, h.ShBytes)
            };
            var views = new NativeArray<byte>[4];
            var commands = new Unity.IO.LowLevel.Unsafe.ReadCommand[4];
            for (int i = 0; i < 4; ++i)
            {
                views[i] = arrs[i].arr.Reinterpret<byte>(sizeof(uint));
                if (views[i].Length != arrs[i].bytes)
                    throw new InvalidDataException($"载荷长度不符: 数组 {views[i].Length}B / Header {arrs[i].bytes}B");
                commands[i] = new Unity.IO.LowLevel.Unsafe.ReadCommand
                {
                    Buffer = views[i].GetUnsafePtr(),
                    Offset = 64, // 起始位置稍后修正(逐路累加)
                    Size = arrs[i].bytes
                };
            }
            // 四路在文件中连续排布:pos → other → color → sh
            long offset = 64;
            for (int i = 0; i < 4; ++i)
            {
                commands[i].Offset = offset;
                offset += arrs[i].bytes;
            }

            Unity.IO.LowLevel.Unsafe.ReadHandle handle;
            fixed (Unity.IO.LowLevel.Unsafe.ReadCommand* cmdPtr = commands)
            {
                handle = Unity.IO.LowLevel.Unsafe.AsyncReadManager.Read(path, cmdPtr, 4);
            }
            try
            {
                handle.JobHandle.Complete();
                if (handle.Status != Unity.IO.LowLevel.Unsafe.ReadStatus.Complete)
                    throw new EndOfStreamException(".s2pbin 载荷读取不完整");
                long got = handle.GetBytesRead();
                if (got != offset - 64)
                    throw new EndOfStreamException($".s2pbin 载荷被截断: 读取 {got}B / 期望 {offset - 64}B");
            }
            finally
            {
                handle.Dispose();
            }
        }

        // ---------- 内部实现 ----------

        /// <summary>
        /// M9 快速读取(内存映射 + 原生 memcpy):把 .s2pbin 以只读视图映射后,
        /// 直接 Buffer.MemoryCopy 进 GaussianSplatData 的 Persistent 数组 ——
        /// 绕过托管 Stream 层与 AsyncReadManager 调度,热缓存下 71MB ≈ 10-15ms
        /// (实测 FileStream 大缓冲 ≈ 30ms、AsyncReadManager ≈ 31-48ms)。
        /// 头部校验与 ReadPayload 一致;verifyCrc 语义与 ReadPayload 相同。
        /// </summary>
        public static GaussianSplatData ReadPayloadMapped(string path, GaussianSplatPrecompiledAsset meta)
        {
            S2PBinHeader h = ReadHeader(path);
            int n = h.SplatCount;
            if (n <= 0 || h.ShDegree < 0 || h.ShDegree > 3)
                throw new InvalidDataException($".s2pbin 数据无效: SplatCount={n}, ShDegree={h.ShDegree}");
            int shCoefficients = (h.ShDegree + 1) * (h.ShDegree + 1) - 1;
            if (h.ShCoefficients != shCoefficients)
                throw new InvalidDataException($".s2pbin SH 系数数不符: {h.ShCoefficients}(期望 {shCoefficients})");
            uint posBytes = checked((uint)(n * 12));
            uint otherBytes = checked((uint)(n * 12));
            uint colorBytes = checked((uint)(n * 4));
            uint shBytes = checked((uint)(n * Mathf.Max(1, GaussianUtils.GetSHDataSize(shCoefficients))));
            if (h.PosBytes != posBytes || h.OtherBytes != otherBytes || h.ColorBytes != colorBytes || h.ShBytes != shBytes)
                throw new InvalidDataException(".s2pbin 四路长度声明不符");

            var file = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            var data = new GaussianSplatData(n, h.ShDegree);
            try
            {
                var view = file.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                try
                {
                    unsafe
                    {
                        byte* basePtr = null;
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                        try
                        {
                            var views = new (NativeArray<uint> arr, uint bytes)[4]
                            {
                                (data.positions, h.PosBytes),
                                (data.geometry, h.OtherBytes),
                                (data.colors, h.ColorBytes),
                                (data.sh, h.ShBytes)
                            };
                            long offset = 64; // 64B 头部之后为四路连续载荷
                            for (int i = 0; i < 4; ++i)
                            {
                                var bytes = views[i].arr.Reinterpret<byte>(sizeof(uint));
                                if (bytes.Length != views[i].bytes)
                                    throw new InvalidDataException($"载荷长度不符: 数组 {bytes.Length}B / Header {views[i].bytes}B");
                                Buffer.MemoryCopy(basePtr + offset, (byte*)bytes.GetUnsafePtr(), views[i].bytes, views[i].bytes);
                                offset += views[i].bytes;
                            }
                            if (offset - 64 != h.PayloadBytes)
                                throw new InvalidDataException(".s2pbin 载荷映射长度不符");
                        }
                        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
                    }
                }
                finally { view.Dispose(); }

                Bounds lb = meta?.LocalBounds ?? default;
                if (meta == null || lb.size == Vector3.zero)
                    data.bounds = new Bounds(Vector3.zero, Vector3.one);
                else
                    data.bounds.SetMinMax(lb.min, lb.max);
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
            finally
            {
                file.Dispose();
            }
        }

        /// <summary>原生数组按 64MB 块直写(不经过托管数组)。</summary>
        static unsafe void WritePayload(Stream stream, NativeArray<uint> arr)
        {
            var bytes = arr.Reinterpret<byte>(sizeof(uint));
            long len = bytes.Length;
            byte* p = (byte*)bytes.GetUnsafeReadOnlyPtr();
            const long kChunk = 1 << 26; // 64MB
            long offset = 0;
            while (offset < len)
            {
                int n = (int)Math.Min(kChunk, len - offset);
                stream.Write(new ReadOnlySpan<byte>(p + offset, n));
                offset += n;
            }
        }

        /// <summary>不重排,仅构建 Chunk AABB(原始存储顺序;与 ChunkBoundsJob 逻辑一致)。</summary>
        static Bounds[] BuildChunkBoundsOnly(GaussianSplatData data)
        {
            int chunkCount = (data.count + SplatGpuChunking.kChunkSize - 1) / SplatGpuChunking.kChunkSize;
            var chunkMm = new NativeArray<float3>(chunkCount * 2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new ChunkBoundsJob
                {
                    Positions = data.positions, SplatCount = data.count,
                    ChunkSize = SplatGpuChunking.kChunkSize, MinMax = chunkMm
                }.Schedule(chunkCount, 1).Complete();

                var result = new Bounds[chunkCount];
                for (int c = 0; c < chunkCount; ++c)
                {
                    float3 mn = chunkMm[c * 2], mx = chunkMm[c * 2 + 1];
                    result[c] = new Bounds((Vector3)((mn + mx) * 0.5f), (Vector3)math.max(mx - mn, new float3(1e-4f)));
                }
                return result;
            }
            finally { chunkMm.Dispose(); }
        }

        /// <summary>.s2pbin 路径归一:优先相对 StreamingAssets,其次工程根,否则绝对路径。</summary>
        static string MakePayloadPathRelative(string binPath)
        {
            string full = Path.GetFullPath(binPath).Replace('\\', '/');
            string sa = Application.streamingAssetsPath.Replace('\\', '/').TrimEnd('/');
            if (full.StartsWith(sa, StringComparison.OrdinalIgnoreCase))
                return full.Substring(sa.Length).TrimStart('/');
            string root = (Path.GetDirectoryName(Application.dataPath) ?? "").Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length).TrimStart('/');
            return full;
        }
    }
}
