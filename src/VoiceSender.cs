using System;
using System.Collections.Generic;

namespace ChessGame
{
    // Jitter buffer + concealment for one sender. It produces a continuous stream: Pull(n) always returns
    // samples that start and end smoothly (fade-in at a talk spurt, fade-out on underruns, cross-fades on repairs).
    // Not thread safe: the channel locks around it.
    internal sealed class VoiceSender
    {
        public const int TickSamples = VoiceFormat.FrameSamples / 2;                      // 50 ms
        public const int MinDepth = 2;
        public const int MaxDepth = 4;
        public const int MaxFramesPerSecond = 16;
        const int HardQueueLimit = 10;                                                    // ~1 s
        const int MaxWaitTicks = 4;
        const int FadeInSamples = VoiceFormat.SampleRate / 125;                           // 8 ms
        const int CrossfadeSamples = VoiceFormat.SampleRate / 100;                        // 10 ms
        const int HistSamples = VoiceFormat.SampleRate / 40;                              // 25 ms
        const int MinLoop = VoiceFormat.SampleRate / 250;                                 // 4 ms
        const int ShrinkAfterTicks = 200;                                                 // 10 s without trouble

        readonly Queue<short[]> queue = new Queue<short[]>();
        readonly long[] acceptTimes = new long[MaxFramesPerSecond];
        int acceptCount, acceptNext;

        public long LastAccepted = long.MinValue / 4;
        public int Depth = MinDepth;

        bool playing;
        float[] cur;
        int pos;
        int waitTicks, smoothTicks;
        int plcChunks;
        float plcEnv;
        bool needCrossfade;
        readonly float[] hist = new float[HistSamples];
        float[] seg;
        int segPos;

        public bool Active { get { return playing || queue.Count > 0; } }

        // Sliding window: at most MaxFramesPerSecond accepted frames in any second.
        public bool AcceptRate(long now)
        {
            if (acceptCount == MaxFramesPerSecond)
            {
                if (now - acceptTimes[acceptNext] < 1000) return false;
            }
            else acceptCount++;
            acceptTimes[acceptNext] = now;
            acceptNext = (acceptNext + 1) % MaxFramesPerSecond;
            return true;
        }

        public void Push(short[] frame)
        {
            queue.Enqueue(frame);
            while (queue.Count > HardQueueLimit) { queue.Dequeue(); needCrossfade = true; }
        }

        // Adds n samples of this sender to mix. Returns false when the sender is silent for the whole tick.
        public bool Pull(float[] mix, int n)
        {
            if (playing && queue.Count > Depth + 4) { queue.Dequeue(); needCrossfade = true; } // drift: stay near real time
            if (!playing && queue.Count > 0) waitTicks++;
            if (playing && plcChunks == 0 && ++smoothTicks >= ShrinkAfterTicks)
            {
                smoothTicks = 0;
                if (Depth > MinDepth) Depth--;
            }

            int filled = 0;
            bool any = false;
            while (filled < n)
            {
                if (cur == null || pos >= cur.Length)
                {
                    cur = null;
                    if (!Fetch()) break;
                }
                int take = Math.Min(n - filled, cur.Length - pos);
                for (int i = 0; i < take; i++) mix[filled + i] += cur[pos + i];
                AppendHistory(cur, pos, take);
                pos += take;
                filled += take;
                any = true;
            }
            return any;
        }

        void AppendHistory(float[] src, int from, int count)
        {
            if (count >= HistSamples)
            {
                Array.Copy(src, from + count - HistSamples, hist, 0, HistSamples);
                return;
            }
            Array.Copy(hist, count, hist, 0, HistSamples - count);
            Array.Copy(src, from, hist, HistSamples - count, count);
        }

        static float[] ToFloat(short[] f)
        {
            float[] c = new float[f.Length];
            for (int i = 0; i < c.Length; i++) c[i] = f[i];
            return c;
        }

        // Prepares the next piece of audio in cur. False means: nothing to play right now.
        bool Fetch()
        {
            if (!playing)
            {
                if (queue.Count == 0) return false;
                // collect Depth frames first so small delays do not become holes
                if (queue.Count < Depth && waitTicks < MaxWaitTicks) return false;
                float[] first = ToFloat(queue.Dequeue());
                for (int i = 0; i < FadeInSamples && i < first.Length; i++) first[i] *= (float)i / FadeInSamples;
                Array.Clear(hist, 0, hist.Length);
                playing = true;
                plcChunks = 0;
                needCrossfade = false;
                waitTicks = 0;
                smoothTicks = 0;
                seg = null;
                cur = first;
                pos = 0;
                return true;
            }

            if (queue.Count > 0)
            {
                float[] next = ToFloat(queue.Dequeue());
                if (plcChunks > 0 || needCrossfade)
                {
                    float env = 1f;
                    if (plcChunks > 0)
                    {
                        // the frame came, but late: be more patient from now on
                        if (Depth < MaxDepth) Depth++;
                        smoothTicks = 0;
                        env = plcEnv;
                    }
                    else BuildLoop();
                    // blend the repaired stream into the real audio instead of jumping
                    for (int i = 0; i < CrossfadeSamples && i < next.Length; i++)
                    {
                        float w = (float)i / CrossfadeSamples;
                        next[i] = next[i] * w + NextLoopSample() * env * (1f - w);
                    }
                }
                plcChunks = 0;
                needCrossfade = false;
                seg = null;
                cur = next;
                pos = 0;
                return true;
            }

            // underrun: continue the waveform for a moment, fading out
            if (plcChunks >= 2)
            {
                playing = false;
                plcChunks = 0;
                seg = null;
                cur = null;
                return false;
            }
            if (plcChunks == 0) BuildLoop();
            float e0 = plcChunks == 0 ? 1f : 0.5f;
            float e1 = plcChunks == 0 ? 0.5f : 0f;
            float[] chunk = new float[TickSamples];
            for (int i = 0; i < chunk.Length; i++)
            {
                float env = e0 + (e1 - e0) * (i + 1) / chunk.Length;
                chunk[i] = NextLoopSample() * env;
            }
            plcEnv = e1;
            plcChunks++;
            cur = chunk;
            pos = 0;
            return true;
        }

        float NextLoopSample()
        {
            float v = seg[segPos];
            segPos = (segPos + 1) % seg.Length;
            return v;
        }

        // Picks the part of the recent output that continues the waveform best (same value and slope as the very
        // last sample) and repeats it. The loop wraps exactly like the splice, so it is click-free by construction.
        void BuildLoop()
        {
            int h = hist.Length;
            float last = hist[h - 1], prevLast = hist[h - 2];
            float peak = 1f;
            for (int i = 0; i < h; i++) peak = Math.Max(peak, Math.Abs(hist[i]));
            int best = h - MinLoop;
            float bestCost = float.MaxValue;
            for (int k = 1; k <= h - MinLoop; k++)
            {
                float cost = (Math.Abs(hist[k - 1] - last) + Math.Abs((hist[k] - hist[k - 1]) - (last - prevLast))) / peak
                    + 0.0005f * k; // slightly prefer longer loops
                if (cost < bestCost) { bestCost = cost; best = k; }
            }
            seg = new float[h - best];
            Array.Copy(hist, best, seg, 0, seg.Length);
            segPos = 0;
        }
    }
}
