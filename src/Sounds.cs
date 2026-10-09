using System;
using System.IO;
using System.Media;

namespace ChessGame
{
    public enum Sfx { Move, Capture, Check, GameOver, Notify }

    /// <summary>
    /// Short sound effects, synthesised at start-up (no audio files). The "wood" click is filtered noise plus a low thump;
    /// the game-over chime is a soft major arpeggio.
    /// </summary>
    public static class Sounds
    {
        const int Rate = 22050;
        static readonly SoundPlayer[] players = new SoundPlayer[5];
        static readonly MemoryStream[] streams = new MemoryStream[5];
        static readonly object gate = new object();

        public static bool Muted;

        public static void Play(Sfx kind)
        {
            if (Muted) return;
            try
            {
                SoundPlayer p;
                lock (gate)
                {
                    p = players[(int)kind];
                    if (p == null)
                    {
                        streams[(int)kind] = new MemoryStream(Wav(Synth(kind)));
                        p = new SoundPlayer(streams[(int)kind]);
                        p.Load();
                        players[(int)kind] = p;
                    }
                    streams[(int)kind].Position = 0;
                }
                p.Play();   // asynchronous, replaces a sound that is still playing
            }
            catch (Exception) { /* no audio device: stay silent */ }
        }

        static float[] Synth(Sfx kind)
        {
            switch (kind)
            {
                case Sfx.Capture: return Click(0.16f, 0.35f, 105f, 1.0f);
                case Sfx.Check: return Mix(Click(0.10f, 0.25f, 150f, 0.8f), Tone(0.20f, 880f, 0.28f, 18f), 0.05f);
                case Sfx.GameOver: return Arpeggio();
                case Sfx.Notify: return Tone(0.14f, 740f, 0.3f, 22f);
                default: return Click(0.09f, 0.22f, 170f, 0.75f);
            }
        }

        /// <summary>A wooden "tock": low-passed noise burst + a decaying low sine.</summary>
        static float[] Click(float seconds, float brightness, float thumpHz, float gain)
        {
            int n = (int)(seconds * Rate);
            float[] s = new float[n];
            uint seed = 12345;
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 8) & 0xFFFF) / 32768f - 1f;
                lp += brightness * (noise - lp);
                float body = lp * (float)Math.Exp(-t * 55f);
                float thump = (float)(Math.Sin(2 * Math.PI * thumpHz * t) * Math.Exp(-t * 48f)) * 0.55f;
                float attack = Math.Min(1f, t * 900f);   // avoids a pop at the start
                s[i] = (body * 1.4f + thump) * attack * gain * 0.7f;
            }
            return s;
        }

        static float[] Tone(float seconds, float hz, float gain, float decay)
        {
            int n = (int)(seconds * Rate);
            float[] s = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float env = (float)Math.Exp(-t * decay) * Math.Min(1f, t * 400f);
                s[i] = (float)(Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t)) * env * gain;
            }
            return s;
        }

        static float[] Mix(float[] a, float[] b, float bDelaySeconds)
        {
            int delay = (int)(bDelaySeconds * Rate);
            float[] s = new float[Math.Max(a.Length, b.Length + delay)];
            for (int i = 0; i < a.Length; i++) s[i] += a[i];
            for (int i = 0; i < b.Length; i++) s[i + delay] += b[i];
            return s;
        }

        static float[] Arpeggio()
        {
            float[] notes = { 523.25f, 659.25f, 783.99f };   // C5 E5 G5
            float[] s = new float[(int)(0.75f * Rate)];
            for (int k = 0; k < notes.Length; k++)
            {
                float[] tone = Tone(k == notes.Length - 1 ? 0.45f : 0.22f, notes[k], 0.32f, k == notes.Length - 1 ? 6f : 9f);
                int start = (int)(k * 0.11f * Rate);
                for (int i = 0; i < tone.Length && start + i < s.Length; i++) s[start + i] += tone[i];
            }
            return s;
        }

        /// <summary>16-bit mono PCM WAV file in memory.</summary>
        static byte[] Wav(float[] samples)
        {
            int dataBytes = samples.Length * 2;
            using (MemoryStream ms = new MemoryStream(44 + dataBytes))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);          // PCM
                w.Write((short)1);          // mono
                w.Write(Rate);
                w.Write(Rate * 2);          // byte rate
                w.Write((short)2);          // block align
                w.Write((short)16);
                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (float f in samples)
                {
                    float v = Math.Max(-1f, Math.Min(1f, f));
                    w.Write((short)(v * 32000));
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
