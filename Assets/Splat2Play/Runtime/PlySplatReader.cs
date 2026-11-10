// Splat2Play — 自研二进制 PLY(3DGS)解析器
// 为什么不用包内 PLYFileReader:它是 internal 且直接输出打包后的 GPU 格式;
// M1 碰撞代理需要逐 splat 的原始 float(position/scale/rotation/opacity),
// 因此按 §5.1 规格自行解析,同时支持"仅位置"的快速路径。
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splat2Play.Runtime
{
    /// <summary>烘焙输入所需的单 splat 原始数据(已从 PLY 原始量转换为物理量)。</summary>
    public struct SplatRaw
    {
        public float3 Position;   // 资产局部坐标
        public float3 Scale;      // σ = exp(log_scale)
        public float4 Rotation;   // xyzw,已归一化
        public float Opacity;     // sigmoid(raw)
    }

    /// <summary>
    /// 解析 binary_little_endian 3DGS PLY。
    /// 与 INRIA/nerfstudio 输出兼容:必需属性 x/y/z, scale_0..2, rot_0..3, opacity, f_dc_0..2;
    /// 忽略其余属性(f_rest_*、法线等)。
    /// </summary>
    public static class PlySplatReader
    {
        public struct PlyHeader
        {
            public int SplatCount;   // vertex 数
            public int Stride;       // 每 splat 字节数
            public int OffsetX, OffsetY, OffsetZ;
            public int OffsetScale0, OffsetScale1, OffsetScale2;
            public int OffsetRot0, OffsetRot1, OffsetRot2, OffsetRot3;
            public int OffsetOpacity;
        }

        /// <summary>解析头部并校验格式。失败抛异常,消息中文以便定位。</summary>
        public static PlyHeader ReadHeader(Stream stream)
        {
            if (ReadLine(stream) != "ply")
                throw new InvalidDataException("不是有效的 PLY 文件(缺少 magic)");
            bool binary = false, inVertex = false, vertexFound = false, ended = false;
            int count = 0;
            var names = new System.Collections.Generic.List<string>(64);
            var types = new System.Collections.Generic.List<string>(64);
            for (int line = 0; line < 4096; ++line)
            {
                string text = ReadLine(stream);
                string[] t = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0 || t[0] == "comment" || t[0] == "obj_info") continue;
                if (t[0] == "end_header") { ended = true; break; }
                if (t[0] == "format")
                    binary = text == "format binary_little_endian 1.0";
                if (t[0] == "element")
                {
                    if (t.Length != 3) throw new InvalidDataException("PLY element 行无效");
                    inVertex = t[1] == "vertex";
                    int n = int.Parse(t[2], CultureInfo.InvariantCulture);
                    if (n < 0) throw new InvalidDataException("PLY 元素数量为负");
                    if (inVertex) { if (vertexFound) throw new InvalidDataException("重复的 vertex element"); vertexFound = true; count = n; }
                    else if (!vertexFound && n != 0) throw new InvalidDataException("vertex 必须是第一个非空 element");
                }
                if (t[0] == "property" && inVertex)
                {
                    if (t.Length != 3 || ScalarSize(t[1]) == 0) throw new InvalidDataException("不支持的 PLY property 类型: " + text);
                    if (names.Contains(t[2])) throw new InvalidDataException("重复的 PLY property: " + t[2]);
                    names.Add(t[2]); types.Add(t[1]);
                }
            }
            if (!binary || !ended || !vertexFound)
                throw new InvalidDataException("仅支持 binary_little_endian 1.0 的 3DGS PLY");
            int Require(string name)
            {
                int i = names.IndexOf(name);
                if (i < 0) throw new InvalidDataException("缺少必需的 PLY 属性: " + name);
                if (types[i] != "float") throw new InvalidDataException("属性必须为 float 类型: " + name);
                return i;
            }
            int rest = names.FindAll(n => n.StartsWith("f_rest_", StringComparison.Ordinal)).Count;
            if (rest != 0 && rest != 9 && rest != 24 && rest != 45)
                throw new InvalidDataException("f_rest_* 属性数量必须为 0/9/24/45,实际 " + rest);
            for (int i = 0; i < types.Count; ++i)
                if (types[i] != "float")
                    throw new InvalidDataException($"属性 {names[i]} 类型为 {types[i]},本解析器仅支持全 float 属性的 PLY");

            int stride = 0;
            var offsets = new int[names.Count];
            for (int i = 0; i < names.Count; ++i) { offsets[i] = stride; stride += ScalarSize(types[i]); }

            var h = new PlyHeader { SplatCount = count, Stride = stride };
            h.OffsetX = offsets[Require("x")]; h.OffsetY = offsets[Require("y")]; h.OffsetZ = offsets[Require("z")];
            h.OffsetScale0 = offsets[Require("scale_0")]; h.OffsetScale1 = offsets[Require("scale_1")]; h.OffsetScale2 = offsets[Require("scale_2")];
            h.OffsetRot0 = offsets[Require("rot_0")]; h.OffsetRot1 = offsets[Require("rot_1")];
            h.OffsetRot2 = offsets[Require("rot_2")]; h.OffsetRot3 = offsets[Require("rot_3")];
            h.OffsetOpacity = offsets[Require("opacity")];
            if (names.IndexOf("f_dc_0") < 0) throw new InvalidDataException("缺少必需的 PLY 属性: f_dc_0");
            return h;
        }

        /// <summary>
        /// 读取全部 splat。顶点数据整块读入后由 Burst 并行解包(确定性)。
        /// 返回的 NativeArray 由调用方负责 Dispose。
        /// </summary>
        public static NativeArray<SplatRaw> ReadSplats(Stream stream, in PlyHeader header, out long vertexBytes)
        {
            NativeArray<byte> bytes = default;
            NativeArray<SplatRaw> result = default;
            bool ok = false;
            try
            {
                long total = (long)header.SplatCount * header.Stride;
                if (stream.Length - stream.Position < total)
                    throw new EndOfStreamException("PLY 顶点数据不完整");

                bytes = new NativeArray<byte>((int)total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                ReadVertexBlock(stream, bytes, total);

                result = new NativeArray<SplatRaw>(header.SplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                new UnpackJob
                {
                    Bytes = bytes,
                    Stride = header.Stride,
                    OffX = header.OffsetX, OffY = header.OffsetY, OffZ = header.OffsetZ,
                    OffS0 = header.OffsetScale0, OffS1 = header.OffsetScale1, OffS2 = header.OffsetScale2,
                    OffR0 = header.OffsetRot0, OffR1 = header.OffsetRot1, OffR2 = header.OffsetRot2, OffR3 = header.OffsetRot3,
                    OffO = header.OffsetOpacity,
                    Out = result
                }.Schedule(header.SplatCount, 4096).Complete();

                vertexBytes = total;
                ok = true;
                return result;
            }
            finally
            {
                if (bytes.IsCreated) bytes.Dispose();
                if (!ok && result.IsCreated) result.Dispose();
            }
        }

        /// <summary>快速路径:只提取位置(边界估算等场景)。返回数组由调用方 Dispose。</summary>
        public static NativeArray<float3> ReadPositions(Stream stream, in PlyHeader header)
        {
            NativeArray<byte> bytes = default;
            NativeArray<float3> result = default;
            bool ok = false;
            try
            {
                long total = (long)header.SplatCount * header.Stride;
                if (stream.Length - stream.Position < total)
                    throw new EndOfStreamException("PLY 顶点数据不完整");

                bytes = new NativeArray<byte>((int)total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                ReadVertexBlock(stream, bytes, total);

                result = new NativeArray<float3>(header.SplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                new UnpackPositionsJob
                {
                    Bytes = bytes, Stride = header.Stride,
                    OffX = header.OffsetX, OffY = header.OffsetY, OffZ = header.OffsetZ,
                    Out = result
                }.Schedule(header.SplatCount, 8192).Complete();

                ok = true;
                return result;
            }
            finally
            {
                if (bytes.IsCreated) bytes.Dispose();
                if (!ok && result.IsCreated) result.Dispose();
            }
        }

        static unsafe void ReadVertexBlock(Stream stream, NativeArray<byte> target, long total)
        {
            const int kChunk = 64 * 1024 * 1024;
            byte* ptr = (byte*)target.GetUnsafePtr();
            long remaining = total;
            while (remaining > 0)
            {
                int chunk = (int)math.min(remaining, kChunk);
                int read = 0;
                while (read < chunk)
                {
                    int r = stream.Read(new Span<byte>(ptr + (total - remaining) + read, chunk - read));
                    if (r == 0) throw new EndOfStreamException("PLY 顶点数据被截断");
                    read += r;
                }
                remaining -= read;
            }
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct UnpackJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Bytes;
            public int Stride;
            public int OffX, OffY, OffZ;
            public int OffS0, OffS1, OffS2;
            public int OffR0, OffR1, OffR2, OffR3;
            public int OffO;
            [NativeDisableParallelForRestriction] public NativeArray<SplatRaw> Out;

            public unsafe void Execute(int i)
            {
                float* row = (float*)((byte*)Bytes.GetUnsafeReadOnlyPtr() + (long)i * Stride);
                float3 scale = new(math.exp(row[OffS0 >> 2]), math.exp(row[OffS1 >> 2]), math.exp(row[OffS2 >> 2]));
                float4 rot = new(row[OffR1 >> 2], row[OffR2 >> 2], row[OffR3 >> 2], row[OffR0 >> 2]); // xyzw
                float len = math.length(rot);
                rot = len > 1e-8f ? rot / len : new float4(0f, 0f, 0f, 1f);
                Out[i] = new SplatRaw
                {
                    Position = new float3(row[OffX >> 2], row[OffY >> 2], row[OffZ >> 2]),
                    Scale = scale,
                    Rotation = rot,
                    Opacity = math.rcp(1f + math.exp(-row[OffO >> 2]))
                };
            }
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct UnpackPositionsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Bytes;
            public int Stride;
            public int OffX, OffY, OffZ;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Out;

            public unsafe void Execute(int i)
            {
                float* row = (float*)((byte*)Bytes.GetUnsafeReadOnlyPtr() + (long)i * Stride);
                Out[i] = new float3(row[OffX >> 2], row[OffY >> 2], row[OffZ >> 2]);
            }
        }

        static int ScalarSize(string type) => type switch
        {
            "float" => 4,
            "double" => 8,
            "uchar" => 1,
            _ => 0
        };

        static string ReadLine(Stream stream)
        {
            var line = new System.Text.StringBuilder(128);
            for (int i = 0; i < 8192; ++i)
            {
                int c = stream.ReadByte();
                if (c < 0) throw new EndOfStreamException("PLY 头部不完整");
                if (c == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)c);
            }
            throw new InvalidDataException("PLY 头部行超长");
        }
    }
}
