// SPDX-License-Identifier: MIT
// M5(方向 5):向 Splat2Play 运行时程序集开放内部类型访问,
// 使 SplatSourceReader 能直接复用 SPZFileReader 的 v1–v4 解头与 Zstd 多流解压管线,
// 避免复制解码逻辑造成两份实现漂移。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Splat2Play.Runtime")]
