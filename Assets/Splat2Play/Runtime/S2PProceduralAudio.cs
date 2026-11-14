// Splat2Play v3 — M12(方向 8)T12.4:零外部音频资产的程序化音效合成器
// 运行时经 AudioClip.Create 合成 5 类短促 PCM(32kHz 单声道,LCG 种子按类型固定 → 确定性):
//   StoneOrConcrete: 2.3kHz 高阻尼正弦 + 3ms 击打瞬态(清脆敲击)
//   Wood:            190Hz 低频共鸣双分音(基频 + 1.9× 泛音)+ 轻噪声
//   Metal:           1.87/2.71/4.15kHz 非谐泛音簇(高频金属感,分层阻尼)
//   FoliageOrCarpet: 1.8kHz 单极低通白噪声 + 随机游走包络(草地沙沙)
//   Default:         280Hz 软质闷响
// 触发:SplatFPSController(脚步/射击命中)与 S2PDynamicProp(OnCollisionEnter 刚体碰撞)。
// SPDX-License-Identifier: MIT

using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    public static class S2PProceduralAudio
    {
        const int kSampleRate = 32000;
        const float kPeak = 0.7f;

        static readonly AudioClip[] s_Clips = new AudioClip[5];

        /// <summary>惰性合成并缓存对应材质类型的音效剪辑(确定性 PCM)。</summary>
        public static AudioClip GetClip(S2PSurfaceType type)
        {
            int i = (int)type;
            if (i < 0 || i >= s_Clips.Length) return null;
            if (s_Clips[i] != null) return s_Clips[i];
            var lcg = new S2PLcg(0x9E3779B9u ^ (uint)(int)type);
            float[] samples;
            switch (type)
            {
                case S2PSurfaceType.StoneOrConcrete: samples = SynthStone(lcg); break;
                case S2PSurfaceType.Wood: samples = SynthWood(lcg); break;
                case S2PSurfaceType.Metal: samples = SynthMetal(lcg); break;
                case S2PSurfaceType.FoliageOrCarpet: samples = SynthFoliage(lcg); break;
                default: samples = SynthDefault(lcg); break;
            }
            var clip = AudioClip.Create($"S2P_{type}", samples.Length, 1, kSampleRate, false);
            clip.SetData(samples, 0);
            s_Clips[i] = clip;
            return clip;
        }

        /// <summary>在世界坐标处播放材质音效(一次性临时播放源,自动销毁)。</summary>
        public static void PlayAt(S2PSurfaceType type, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            AudioClip clip = GetClip(type);
            if (clip == null || volume <= 0f) return;
            var go = new GameObject("S2P_AudioOneShot");
            go.transform.position = position;
            go.hideFlags = HideFlags.HideAndDontSave;
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 1f;   // 3D 定位
            src.minDistance = 1.5f;
            src.maxDistance = 25f;
            src.pitch = pitch;
            src.PlayOneShot(clip, volume);
            Object.Destroy(go, clip.length / Mathf.Max(pitch, 0.05f) + 0.1f);
        }

        // ---------- 合成器 ----------

        /// <summary>确定性 32-bit LCG(无 RNG 状态依赖,同一类型每次合成逐位一致)。</summary>
        struct S2PLcg
        {
            uint _s;
            public S2PLcg(uint seed) => _s = seed;
            public float Next() // [−1, 1)
            {
                _s = _s * 1664525u + 1013904223u;
                return _s * (1f / 2147483648f) - 1f;
            }
            public float Next01() => Next() * 0.5f + 0.5f;
        }

        static float[] SynthStone(S2PLcg rng)
        {
            const float dur = 0.14f;
            int n = (int)(dur * kSampleRate);
            var outp = new float[n];
            float f0 = 2250f + 260f * rng.Next01();
            float click = 0.5f;
            for (int i = 0; i < n; ++i)
            {
                float t = i / (float)kSampleRate;
                float ping = math.sin(2f * math.PI * f0 * t) * math.exp(-t * 30f) * 0.85f;
                float thud = math.sin(2f * math.PI * 340f * t) * math.exp(-t * 60f) * 0.35f;
                float transient = click * math.exp(-t * 500f) * rng.Next01();
                outp[i] = ping + thud + transient;
            }
            return Normalize(outp);
        }

        static float[] SynthWood(S2PLcg rng)
        {
            const float dur = 0.12f;
            int n = (int)(dur * kSampleRate);
            var outp = new float[n];
            float f0 = 180f + 70f * rng.Next01();
            for (int i = 0; i < n; ++i)
            {
                float t = i / (float)kSampleRate;
                float body = math.sin(2f * math.PI * f0 * t) * math.exp(-t * 32f) * 0.9f;
                float partial = math.sin(2f * math.PI * f0 * 1.9f * t) * math.exp(-t * 48f) * 0.35f;
                float knock = 0.25f * math.exp(-t * 300f) * rng.Next();
                outp[i] = body + partial + knock;
            }
            return Normalize(outp);
        }

        static float[] SynthMetal(S2PLcg rng)
        {
            const float dur = 0.18f;
            int n = (int)(dur * kSampleRate);
            var outp = new float[n];
            float detune = 0.92f + 0.16f * rng.Next01();
            float[] freqs = { 1870f * detune, 2710f * detune, 4150f * detune };
            float[] amps = { 1.0f, 0.6f, 0.35f };
            float[] decays = { 14f, 20f, 26f };
            for (int i = 0; i < n; ++i)
            {
                float t = i / (float)kSampleRate;
                float v = 0f;
                for (int k = 0; k < freqs.Length; ++k)
                    v += math.sin(2f * math.PI * freqs[k] * t) * math.exp(-t * decays[k]) * amps[k];
                outp[i] = v + 0.3f * math.exp(-t * 600f) * rng.Next();
            }
            return Normalize(outp);
        }

        static float[] SynthFoliage(S2PLcg rng)
        {
            const float dur = 0.22f;
            int n = (int)(dur * kSampleRate);
            var outp = new float[n];
            float lp = 0f;
            const float alpha = 0.36f;   // 一阶低通 ≈ 2.9kHz
            float env = 0f;
            for (int i = 0; i < n; ++i)
            {
                float t = i / (float)kSampleRate;
                lp += alpha * (rng.Next() - lp);
                // 攻击 15ms + 指数衰减(沙沙感的起伏包络)
                env = math.lerp(env, 0.9f * math.exp(-t * 16f) * (0.7f + 0.3f * math.sin(35f * t + 6f * rng.Next01())), 0.5f);
                float attack = math.saturate(t / 0.015f);
                outp[i] = lp * env * attack;
            }
            return Normalize(outp);
        }

        static float[] SynthDefault(S2PLcg rng)
        {
            const float dur = 0.10f;
            int n = (int)(dur * kSampleRate);
            var outp = new float[n];
            float f0 = 255f + 60f * rng.Next01();
            for (int i = 0; i < n; ++i)
            {
                float t = i / (float)kSampleRate;
                outp[i] = math.sin(2f * math.PI * f0 * t) * math.exp(-t * 26f) * 0.9f
                        + 0.15f * math.exp(-t * 250f) * rng.Next();
            }
            return Normalize(outp);
        }

        static float[] Normalize(float[] samples)
        {
            float peak = 0f;
            foreach (float s in samples) peak = math.max(peak, math.abs(s));
            if (peak < 1e-6f) return samples;
            float g = kPeak / peak;
            for (int i = 0; i < samples.Length; ++i) samples[i] *= g;
            return samples;
        }
    }
}
