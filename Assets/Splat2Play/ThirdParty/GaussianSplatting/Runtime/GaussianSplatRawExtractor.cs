// SPDX-License-Identifier: MIT
// M5(方向 5)T5.1:SPZ 公开门面抽取器。
// 在不改动 internal SPZFileReader 语义的前提下,把 ".spz → 解压后属性字节流" 的能力
// 以 public API 暴露给 Splat2Play.Runtime(SplatSourceReader),供碰撞代理烘焙直读 SPZ。
//
// 解压后的字节布局与 GaussianSplatPrepareDataJob.DecodeSpz 完全一致(顺序流拼接):
//   [positions][alpha][colors][scale][rotation][SH]
//   positions: v1 = half3×N(6B/splat);v2–v4 = 24-bit 定点×N(9B/splat,fractionalBits 小数位)
//   alpha:     uint8×N —— 注意:已是激活后的 [0,1] alpha,解码时不得再做 Sigmoid
//   colors:    uint8×3×N(RGB,编码为 (c/255-0.5)/0.15 后过 SH0)
//   scale:     uint8×3×N —— log_scale = b/16 - 10,线性尺度 σ = exp(log_scale)
//   rotation:  v1–v2 = uint8×3(xyz ∈ [-1,1],w = sqrt(max(0,1-|xyz|²)));
//              v3–v4 = uint8×4(SPZ smallest-three 打包)
//   SH:        uint8×3×shCoefficients×N
// 版本差异:M0 基线已验证 —— v1–v3 整体为 gzip 流(头 + 未再压缩的属性流),
//           v4 为明文头 + TOC + 每属性独立 Zstd 流。

using System;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace GaussianSplatting.Runtime
{
    /// <summary>解压后的 SPZ 原始属性数据(调用方负责 Dispose)。</summary>
    public readonly struct SpzRawData : IDisposable
    {
        /// <summary>解压后的属性字节流(布局见类头注释)。</summary>
        public NativeArray<byte> Data { get; }
        public int Version { get; }
        public int SplatCount { get; }
        public int Degree { get; }
        public int FractionalBits { get; }

        public int ShCoefficients => (Degree + 1) * (Degree + 1) - 1;
        public bool IsCreated => Data.IsCreated;

        internal SpzRawData(NativeArray<byte> data, int version, int count, int degree, int fractionalBits)
        {
            Data = data; Version = version; SplatCount = count; Degree = degree; FractionalBits = fractionalBits;
        }

        public void Dispose()
        {
            if (Data.IsCreated) Data.Dispose();
        }
    }

    /// <summary>
    /// SPZ → 解压属性流的同步读取门面(编辑器烘焙路径,一次性读入)。
    /// 自动路由 v1–v3(gzip)与 v4(Zstd 多流),失败抛 InvalidDataException。
    /// </summary>
    public static class GaussianSplatRawExtractor
    {
        /// <summary>嗅探流头部是否为 SPZ(明文 "NGSP" 头或 gzip 魔数,v1–v3 均为 gzip 包裹)。</summary>
        public static bool IsSpzStream(Stream stream)
        {
            long start = stream.Position;
            try
            {
                int a = stream.ReadByte(), b = stream.ReadByte();
                return (a == 'N' && b == 'G') || (a == 0x1f && b == 0x8b);
            }
            finally { stream.Position = start; }
        }

        /// <summary>读取并解压整个 SPZ。流可为 FileStream / MemoryStream(只向前读,结束位置不保证)。</summary>
        public static SpzRawData Read(Stream stream)
        {
            long start = stream.Position;
            int a = stream.ReadByte(), b = stream.ReadByte();
            stream.Position = start;
            if (a == 0x1f && b == 0x8b)
                return ReadGzipV1ToV3(stream);
            return ReadZstdV4(stream);
        }

        static SpzRawData ReadGzipV1ToV3(Stream stream)
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, true);
            // 头部从 gzip 流内消费(16 字节),剩余即原始属性数据
            SPZFileReader.Header header = SPZFileReader.ReadHeader(gzip);
            var data = new NativeArray<byte>(header.dataSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                ReadExactly(gzip, data);
                if (gzip.ReadByte() != -1)
                    throw new InvalidDataException("SPZ gzip 载荷存在尾随数据");
                return new SpzRawData(data, header.version, header.count, header.degree, header.fractionalBits);
            }
            catch { data.Dispose(); throw; }
        }

        static unsafe SpzRawData ReadZstdV4(Stream stream)
        {
            SPZFileReader.Header header = SPZFileReader.ReadHeader(stream);
            NativeArray<SPZFileReader.StreamInfo> streams = SPZFileReader.ReadStreams(stream, header);
            NativeArray<byte> compressed = default, scratch = default, output = default;
            try
            {
                int remaining = checked((int)(stream.Length - stream.Position));
                compressed = new NativeArray<byte>(remaining, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                int offset = 0;
                while (offset < remaining)
                {
                    int read = stream.Read(new Span<byte>((byte*)compressed.GetUnsafePtr() + offset, remaining - offset));
                    if (read == 0) throw new EndOfStreamException("SPZ v4 压缩数据被截断");
                    offset += read;
                }
                scratch = new NativeArray<byte>(streams.Length * ZstdDecoder.ScratchSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                output = new NativeArray<byte>(header.dataSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var job = new SPZFileReader.DecompressJob
                {
                    input = compressed, output = output, scratch = scratch, streams = streams
                };
                job.Schedule(streams.Length, 1).Complete();
                for (int i = 0; i < streams.Length; ++i)
                    if (streams[i].error != ZstdDecoder.Error.None)
                        throw new InvalidDataException($"SPZ v4 属性流 {i} 解压失败:{streams[i].error}");
                return new SpzRawData(output, header.version, header.count, header.degree, header.fractionalBits);
            }
            catch
            {
                if (output.IsCreated) output.Dispose();
                throw;
            }
            finally
            {
                if (streams.IsCreated) streams.Dispose();
                if (compressed.IsCreated) compressed.Dispose();
                if (scratch.IsCreated) scratch.Dispose();
            }
        }

        static unsafe void ReadExactly(Stream stream, NativeArray<byte> target)
        {
            int offset = 0;
            while (offset < target.Length)
            {
                int read = stream.Read(new Span<byte>((byte*)target.GetUnsafePtr() + offset, target.Length - offset));
                if (read == 0) throw new EndOfStreamException("SPZ 属性数据被截断");
                offset += read;
            }
        }
    }
}
