using System;
using System.IO;
using System.Media;
using System.Text;

namespace PomoCC
{
    /// <summary>
    /// 提示音：**只有一种**，代码合成（内存里生成 PCM WAV，用 SoundPlayer 播放）。
    /// 不往仓库里放音频文件、不引第三方库，exe 依旧是单文件零依赖。
    ///
    /// 选的是"木琴轻敲"：一声木质敲击、0.34 秒，短、清、不刺耳 ——
    /// 比 Windows 自带那声星号音好认，也不会像系统提示音那样跟别的软件混在一起。
    /// </summary>
    public static class SoundBank
    {
        /// <summary>提示音名字（界面上显示）。</summary>
        public const string Name = "木琴轻敲";

        private const int Rate = 44100;

        /// <summary>最近一次播放的音（自检用：确认"专注走完"响的确实是这一声）。</summary>
        public static string LastPlayed = "";

        private static SoundPlayer player;
        private static MemoryStream playingStream;      // 播放期间必须持住，否则流被回收

        /// <summary>播放提示音。自检（App.Headless）不出声，调用方自己计数。</summary>
        public static void Play()
        {
            LastPlayed = Name;
            if (App.Headless) return;
            try
            {
                MemoryStream ms = new MemoryStream(Render(), false);
                SoundPlayer p = new SoundPlayer(ms);
                playingStream = ms;
                player = p;
                p.Play();                                // 异步播放，不阻塞界面
            }
            catch { }
        }

        /// <summary>合成这一声（PCM 16bit 单声道 44.1kHz 的 WAV 字节）。</summary>
        public static byte[] Render()
        {
            double[] buf = new double[Sec(0.34)];
            Tone(buf, 0.00, 0.32, 660, 0.70, 15.0, 0.50);      // 木体基音（快速衰减）
            Tone(buf, 0.00, 0.02, 2100, 0.16, 90.0, 0.00);     // 敲击瞬间那一下"哒"
            return ToWav(buf);
        }

        private static int Sec(double seconds) { return (int)(seconds * Rate); }

        /// <summary>
        /// 往缓冲区叠加一个音：正弦基音 + 可选二次谐波，指数衰减，
        /// 起音 2ms / 收尾 5ms 淡入淡出（不加会有"啪"的爆音）。
        /// </summary>
        private static void Tone(double[] buf, double at, double dur, double f0,
                                 double amp, double decay, double h2)
        {
            int i0 = (int)(at * Rate);
            int n = (int)(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = i0 + i;
                if (idx >= buf.Length) break;
                double t = (double)i / Rate;
                phase += 2 * Math.PI * f0 / Rate;

                double env = Math.Exp(-decay * t);
                if (t < 0.002) env *= t / 0.002;
                double left = dur - t;
                if (left < 0.005) env *= left / 0.005;

                double v = Math.Sin(phase);
                if (h2 > 0) v += h2 * Math.Sin(2 * phase);
                buf[idx] += amp * env * v / (1 + h2);
            }
        }

        private static byte[] ToWav(double[] samples)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms, Encoding.ASCII))
                {
                    int dataBytes = samples.Length * 2;
                    w.Write(Encoding.ASCII.GetBytes("RIFF"));
                    w.Write(36 + dataBytes);
                    w.Write(Encoding.ASCII.GetBytes("WAVE"));
                    w.Write(Encoding.ASCII.GetBytes("fmt "));
                    w.Write(16);
                    w.Write((short)1);              // PCM
                    w.Write((short)1);              // 单声道
                    w.Write(Rate);
                    w.Write(Rate * 2);              // 字节率
                    w.Write((short)2);              // 块对齐
                    w.Write((short)16);             // 位深
                    w.Write(Encoding.ASCII.GetBytes("data"));
                    w.Write(dataBytes);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        double v = samples[i];
                        if (v > 0.97) v = 0.97;      // 留余量，避免削顶
                        else if (v < -0.97) v = -0.97;
                        w.Write((short)Math.Round(v * 32767));
                    }
                }
                return ms.ToArray();
            }
        }
    }
}
