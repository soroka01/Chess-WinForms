// Voice chat: IMA ADPCM codec, winmm capture/playback and a voice-activity gate.
// Self-contained on purpose (no other src file is needed); the network layer only moves base64 strings around.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChessGame
{
    public static class VoiceFormat
    {
        // 16 kHz keeps the 4-8 kHz band that makes speech sound natural; ADPCM at 4 bits costs 64 kbps
        public const int SampleRate = 16000;
        // 100 ms of mono 16-bit audio: 804 bytes of ADPCM, 1072 base64 characters
        public const int FrameSamples = 1600;
    }

    // IMA ADPCM with a self-contained header per frame, so a lost frame never breaks the next one.
    public static class AdpcmCodec
    {
        public const int MaxSamples = 4000;
        const int HeaderSize = 4;

        static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88,
            97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660,
            4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818,
            18500, 20350, 22385, 24623, 27086, 29794, 32767
        };
        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static byte[] Encode(short[] pcm)
        {
            if (pcm == null) throw new ArgumentNullException("pcm");
            if (pcm.Length > MaxSamples || (pcm.Length & 1) != 0)
                throw new ArgumentException("PCM length must be even and at most " + MaxSamples);

            byte[] data = new byte[HeaderSize + pcm.Length / 2];
            int pred = pcm.Length > 0 ? pcm[0] : 0;
            // start with a step that roughly matches the first slope, so the first samples are not coarse
            int index = 0;
            if (pcm.Length > 1)
            {
                int d = Math.Abs(pcm[1] - pcm[0]);
                while (index < 88 && StepTable[index] * 2 < d) index++;
            }

            data[0] = (byte)(pred & 0xFF);
            data[1] = (byte)((pred >> 8) & 0xFF);
            data[2] = (byte)index;
            data[3] = 0;

            for (int i = 0; i < pcm.Length; i++)
            {
                int step = StepTable[index];
                int diff = pcm[i] - pred;
                int code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int vpdiff = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; vpdiff += step; }
                step >>= 1;
                if (diff >= step) { code |= 2; diff -= step; vpdiff += step; }
                step >>= 1;
                if (diff >= step) { code |= 1; vpdiff += step; }

                pred = Clamp(pred + ((code & 8) != 0 ? -vpdiff : vpdiff), short.MinValue, short.MaxValue);
                index = Clamp(index + IndexTable[code & 7], 0, 88);

                int pos = HeaderSize + (i >> 1);
                if ((i & 1) == 0) data[pos] = (byte)code;
                else data[pos] |= (byte)(code << 4);
            }
            return data;
        }

        // Input comes from the network, so anything odd yields null instead of an exception.
        public static short[] Decode(byte[] data)
        {
            try
            {
                if (data == null || data.Length <= HeaderSize) return null;
                int samples = (data.Length - HeaderSize) * 2;
                if (samples > MaxSamples) return null;
                int index = data[2];
                if (index > 88 || data[3] != 0) return null;

                int pred = (short)(data[0] | (data[1] << 8));
                short[] pcm = new short[samples];
                for (int i = 0; i < samples; i++)
                {
                    int b = data[HeaderSize + (i >> 1)];
                    int code = (i & 1) == 0 ? (b & 0x0F) : (b >> 4);
                    int step = StepTable[index];
                    int vpdiff = step >> 3;
                    if ((code & 4) != 0) vpdiff += step;
                    if ((code & 2) != 0) vpdiff += step >> 1;
                    if ((code & 1) != 0) vpdiff += step >> 2;
                    pred = Clamp(pred + ((code & 8) != 0 ? -vpdiff : vpdiff), short.MinValue, short.MaxValue);
                    index = Clamp(index + IndexTable[code & 7], 0, 88);
                    pcm[i] = (short)pred;
                }
                return pcm;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public interface IAudioCapture : IDisposable
    {
        // Raised on a worker thread, always with exactly FrameSamples samples.
        event Action<short[]> FrameCaptured;
        bool Start(out string error);
        void Stop();
    }

    public interface IAudioPlayback : IDisposable
    {
        bool Start(out string error);
        void Enqueue(short[] pcm);
        void Stop();
    }

    // ---- winmm interop ----------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    static class WinMm
    {
        public const int CALLBACK_EVENT = 0x00050000;
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const int WHDR_DONE = 0x1;

        public static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHdr));

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr hwi, uint deviceId, ref WaveFormatEx fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr hwo, uint deviceId, ref WaveFormatEx fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);

        public static WaveFormatEx Format()
        {
            WaveFormatEx f = new WaveFormatEx();
            f.wFormatTag = 1; // WAVE_FORMAT_PCM
            f.nChannels = 1;
            f.nSamplesPerSec = VoiceFormat.SampleRate;
            f.wBitsPerSample = 16;
            f.nBlockAlign = 2;
            f.nAvgBytesPerSec = VoiceFormat.SampleRate * 2;
            f.cbSize = 0;
            return f;
        }

        // Short Russian texts for the most common open failures.
        public static string DescribeError(int code, bool capture)
        {
            switch (code)
            {
                case 2: // MMSYSERR_BADDEVICEID
                case 6: // MMSYSERR_NODRIVER
                    return capture ? "Микрофон не найден." : "Устройство вывода звука не найдено.";
                case 4: // MMSYSERR_ALLOCATED
                    return capture ? "Микрофон занят другой программой." : "Устройство вывода звука занято другой программой.";
                case 32: // WAVERR_BADFORMAT
                    return "Звуковое устройство не поддерживает нужный формат.";
                default:
                    return (capture ? "Не удалось открыть микрофон" : "Не удалось открыть устройство вывода звука")
                        + " (код " + code + ").";
            }
        }
    }

    // Unmanaged WAVEHDR + data buffers; the driver owns them while a buffer is queued, so they are never movable.
    sealed class WaveBufferSet
    {
        static readonly int OffFlags = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwFlags");
        static readonly int OffLength = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwBufferLength");
        static readonly int OffRecorded = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwBytesRecorded");

        public readonly int Count;
        public readonly IntPtr[] Headers;
        public readonly IntPtr[] Data;
        public readonly bool[] Prepared;

        public WaveBufferSet(int count, int bytes)
        {
            Count = count;
            Headers = new IntPtr[count];
            Data = new IntPtr[count];
            Prepared = new bool[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Data[i] = Marshal.AllocHGlobal(bytes);
                    WaveHdr h = new WaveHdr();
                    h.lpData = Data[i];
                    h.dwBufferLength = (uint)bytes;
                    Headers[i] = Marshal.AllocHGlobal(WinMm.HeaderSize);
                    Marshal.StructureToPtr(h, Headers[i], false);
                }
            }
            catch (Exception)
            {
                Free();
                throw;
            }
        }

        public int Flags(int i) { return Marshal.ReadInt32(Headers[i], OffFlags); }
        public int BytesRecorded(int i) { return Marshal.ReadInt32(Headers[i], OffRecorded); }
        public void SetLength(int i, int bytes) { Marshal.WriteInt32(Headers[i], OffLength, bytes); }

        // Call only after the buffers were unprepared / the device was reset.
        public void Free()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(Headers[i]); Headers[i] = IntPtr.Zero; }
                if (Data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(Data[i]); Data[i] = IntPtr.Zero; }
            }
        }
    }

    // ---- capture ---------------------------------------------------------------------------------------------

    // waveIn with CALLBACK_EVENT: the driver only signals an event, all waveIn* calls happen on our own threads.
    // The worker thread owns the device and releases it when it ends, so Stop() is safe even from a handler.
    public sealed class WaveInCapture : IAudioCapture
    {
        const int BufferCount = 4;

        readonly object sync = new object();
        IntPtr dev;
        WaveBufferSet bufs;
        AutoResetEvent evt;
        Thread worker;
        bool started;
        volatile bool stopping;

        public event Action<short[]> FrameCaptured;

        internal bool WorkerAlive
        {
            get { Thread w = worker; return w != null && w.IsAlive; }
        }

        public bool Start(out string error)
        {
            lock (sync)
            {
                if (stopping) { error = "Микрофон уже закрыт."; return false; }
                if (started) { error = null; return true; }
                try
                {
                    if (WinMm.waveInGetNumDevs() <= 0) { error = "Микрофон не найден."; return false; }
                    evt = new AutoResetEvent(false);
                    WaveFormatEx fmt = WinMm.Format();
                    int rc = WinMm.waveInOpen(out dev, WinMm.WAVE_MAPPER, ref fmt,
                        evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CALLBACK_EVENT);
                    if (rc != 0)
                    {
                        dev = IntPtr.Zero;
                        error = WinMm.DescribeError(rc, true);
                        Release();
                        return false;
                    }

                    bufs = new WaveBufferSet(BufferCount, VoiceFormat.FrameSamples * 2);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveInPrepareHeader(dev, bufs.Headers[i], WinMm.HeaderSize);
                        if (rc != 0) { error = WinMm.DescribeError(rc, true); Release(); return false; }
                        bufs.Prepared[i] = true;
                    }
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveInAddBuffer(dev, bufs.Headers[i], WinMm.HeaderSize);
                        if (rc != 0) { error = WinMm.DescribeError(rc, true); Release(); return false; }
                    }
                    rc = WinMm.waveInStart(dev);
                    if (rc != 0) { error = WinMm.DescribeError(rc, true); Release(); return false; }

                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Name = "VoiceCapture";
                    worker.Start();
                    started = true;
                    error = null;
                    return true;
                }
                catch (Exception)
                {
                    Release();
                    error = "Не удалось открыть микрофон.";
                    return false;
                }
            }
        }

        void Run()
        {
            try
            {
                int next = 0;
                while (!stopping)
                {
                    evt.WaitOne(100);
                    while (!stopping && (bufs.Flags(next) & WinMm.WHDR_DONE) != 0)
                    {
                        // a partially filled buffer only appears after a reset, i.e. during shutdown
                        if (bufs.BytesRecorded(next) >= VoiceFormat.FrameSamples * 2)
                        {
                            short[] frame = new short[VoiceFormat.FrameSamples];
                            Marshal.Copy(bufs.Data[next], frame, 0, frame.Length);
                            Raise(frame);
                        }
                        if (stopping) break;
                        // if the driver refuses the buffer the device is broken: end instead of spinning
                        if (WinMm.waveInAddBuffer(dev, bufs.Headers[next], WinMm.HeaderSize) != 0) return;
                        next = (next + 1) % BufferCount;
                    }
                }
            }
            catch (Exception)
            {
                // a worker thread must never take the process down
            }
            finally
            {
                lock (sync) { Release(); }
            }
        }

        void Raise(short[] frame)
        {
            Action<short[]> h = FrameCaptured;
            if (h == null) return;
            try { h(frame); }
            catch (Exception) { }
        }

        // Caller holds sync. Tolerates a half-initialised state (used on failed Start too).
        void Release()
        {
            if (dev != IntPtr.Zero)
            {
                WinMm.waveInReset(dev); // hands all queued buffers back
                if (bufs != null)
                {
                    for (int i = 0; i < bufs.Count; i++)
                    {
                        if (bufs.Prepared[i])
                        {
                            WinMm.waveInUnprepareHeader(dev, bufs.Headers[i], WinMm.HeaderSize);
                            bufs.Prepared[i] = false;
                        }
                    }
                }
                WinMm.waveInClose(dev);
                dev = IntPtr.Zero;
            }
            if (bufs != null) { bufs.Free(); bufs = null; }
            if (evt != null) { evt.Dispose(); evt = null; }
        }

        public void Stop()
        {
            Thread w;
            lock (sync)
            {
                stopping = true;
                w = worker;
                if (evt != null) evt.Set();
                if (w == null) Release();
            }
            if (w != null && w != Thread.CurrentThread) w.Join(5000);
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // ---- playback --------------------------------------------------------------------------------------------

    // waveOut with CALLBACK_EVENT and a feeder thread. Short fixed buffers are refilled from a queue; after an
    // underrun two frames are collected first so that a jittery network does not turn into crackling.
    public sealed class WaveOutPlayback : IAudioPlayback
    {
        const int BufferCount = 8;
        const int MaxQueuedSamples = VoiceFormat.SampleRate * 3 / 2;
        // the mixer feeds 50 ms chunks, so two of them are a safe start without adding much delay
        const int PrebufferSamples = VoiceFormat.FrameSamples;
        // a lone short tail must not wait forever for its second chunk
        const long PrebufferTimeoutMs = 150;

        static readonly Stopwatch Clock = Stopwatch.StartNew();

        readonly object sync = new object();   // device lifetime
        readonly object qLock = new object();  // queue state
        readonly Queue<short[]> queue = new Queue<short[]>();
        int headOffset;
        int queued;
        long pendingSince;
        bool playing;

        IntPtr dev;
        WaveBufferSet bufs;
        AutoResetEvent evt;
        Thread worker;
        bool started;
        volatile bool stopping;

        internal bool WorkerAlive
        {
            get { Thread w = worker; return w != null && w.IsAlive; }
        }

        public bool Start(out string error)
        {
            lock (sync)
            {
                if (stopping) { error = "Вывод звука уже закрыт."; return false; }
                if (started) { error = null; return true; }
                try
                {
                    if (WinMm.waveOutGetNumDevs() <= 0) { error = "Устройство вывода звука не найдено."; return false; }
                    evt = new AutoResetEvent(false);
                    WaveFormatEx fmt = WinMm.Format();
                    int rc = WinMm.waveOutOpen(out dev, WinMm.WAVE_MAPPER, ref fmt,
                        evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CALLBACK_EVENT);
                    if (rc != 0)
                    {
                        dev = IntPtr.Zero;
                        error = WinMm.DescribeError(rc, false);
                        Release();
                        return false;
                    }

                    bufs = new WaveBufferSet(BufferCount, VoiceFormat.FrameSamples * 2);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveOutPrepareHeader(dev, bufs.Headers[i], WinMm.HeaderSize);
                        if (rc != 0) { error = WinMm.DescribeError(rc, false); Release(); return false; }
                        bufs.Prepared[i] = true;
                    }

                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Name = "VoicePlayback";
                    worker.Start();
                    started = true;
                    error = null;
                    return true;
                }
                catch (Exception)
                {
                    Release();
                    error = "Не удалось открыть устройство вывода звука.";
                    return false;
                }
            }
        }

        public void Enqueue(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            short[] copy = (short[])pcm.Clone();
            lock (qLock)
            {
                if (stopping) return;
                if (queued == 0) pendingSince = Clock.ElapsedMilliseconds;
                queue.Enqueue(copy);
                queued += copy.Length;

                // too much backlog means we fell behind: dropping the oldest audio keeps the delay bounded
                while (queued > MaxQueuedSamples)
                {
                    int excess = queued - MaxQueuedSamples;
                    int avail = queue.Peek().Length - headOffset;
                    if (excess >= avail)
                    {
                        queue.Dequeue();
                        queued -= avail;
                        headOffset = 0;
                    }
                    else
                    {
                        headOffset += excess;
                        queued = MaxQueuedSamples;
                    }
                }
            }
            WakeWorker();
        }

        void WakeWorker()
        {
            lock (sync)
            {
                if (evt != null) evt.Set();
            }
        }

        // Returns the next block to play or null when we should wait (nothing queued / still pre-buffering).
        short[] TakeBlock()
        {
            lock (qLock)
            {
                if (queued == 0) return null;
                if (!playing)
                {
                    bool enough = queued >= PrebufferSamples
                        || Clock.ElapsedMilliseconds - pendingSince >= PrebufferTimeoutMs;
                    if (!enough) return null;
                    playing = true;
                }

                int n = Math.Min(queued, VoiceFormat.FrameSamples);
                short[] block = new short[n];
                int filled = 0;
                while (filled < n)
                {
                    short[] head = queue.Peek();
                    int take = Math.Min(n - filled, head.Length - headOffset);
                    Array.Copy(head, headOffset, block, filled, take);
                    filled += take;
                    headOffset += take;
                    if (headOffset >= head.Length) { queue.Dequeue(); headOffset = 0; }
                }
                queued -= n;
                return block;
            }
        }

        void Run()
        {
            try
            {
                bool[] busy = new bool[BufferCount];
                int inFlight = 0;
                while (!stopping)
                {
                    evt.WaitOne(50);
                    if (stopping) break;

                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (busy[i] && (bufs.Flags(i) & WinMm.WHDR_DONE) != 0) { busy[i] = false; inFlight--; }
                    }

                    lock (qLock)
                    {
                        // everything played and nothing left: the next audio has to be pre-buffered again
                        if (playing && inFlight == 0 && queued == 0) playing = false;
                    }

                    for (int i = 0; i < BufferCount && !stopping; i++)
                    {
                        if (busy[i]) continue;
                        short[] block = TakeBlock();
                        if (block == null) break;
                        Marshal.Copy(block, 0, bufs.Data[i], block.Length);
                        bufs.SetLength(i, block.Length * 2);
                        if (WinMm.waveOutWrite(dev, bufs.Headers[i], WinMm.HeaderSize) != 0) return;
                        busy[i] = true;
                        inFlight++;
                    }
                }
            }
            catch (Exception)
            {
                // a worker thread must never take the process down
            }
            finally
            {
                lock (sync) { Release(); }
            }
        }

        // Caller holds sync. Tolerates a half-initialised state (used on failed Start too).
        void Release()
        {
            if (dev != IntPtr.Zero)
            {
                WinMm.waveOutReset(dev); // stops playback and returns every buffer
                if (bufs != null)
                {
                    for (int i = 0; i < bufs.Count; i++)
                    {
                        if (bufs.Prepared[i])
                        {
                            WinMm.waveOutUnprepareHeader(dev, bufs.Headers[i], WinMm.HeaderSize);
                            bufs.Prepared[i] = false;
                        }
                    }
                }
                WinMm.waveOutClose(dev);
                dev = IntPtr.Zero;
            }
            if (bufs != null) { bufs.Free(); bufs = null; }
            if (evt != null) { evt.Dispose(); evt = null; }
        }

        public void Stop()
        {
            Thread w;
            lock (qLock)
            {
                stopping = true;
                queue.Clear();
                queued = 0;
                headOffset = 0;
            }
            lock (sync)
            {
                w = worker;
                if (evt != null) evt.Set();
                if (w == null) Release();
            }
            if (w != null && w != Thread.CurrentThread) w.Join(5000);
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // ---- signal helpers ---------------------------------------------------------------------------------------

    static class VoiceMath
    {
        const float LimiterStart = 24000f;
        const float LimiterCeiling = 32700f;

        // Linear up to 24000, then a smooth tanh knee towards the ceiling: loud peaks get rounded, never clipped.
        public static float SoftLimit(float x)
        {
            float a = Math.Abs(x);
            if (a <= LimiterStart) return x;
            float range = LimiterCeiling - LimiterStart;
            float y = LimiterStart + range * (float)Math.Tanh((a - LimiterStart) / range);
            return x < 0 ? -y : y;
        }

        public static short ToShort(float x)
        {
            x = SoftLimit(x);
            return (short)(x >= 0 ? x + 0.5f : x - 0.5f);
        }

        // -70 dBFS (a very quiet room) .. -20 dBFS (loud) -> 0..1, so even a weak microphone moves the meter.
        public static float LevelFromRms(double rms)
        {
            if (rms < 0.5) return 0f;
            double db = 20.0 * Math.Log10(rms / 32768.0);
            double v = (db + 70.0) / 50.0;
            return (float)(v < 0 ? 0 : (v > 1 ? 1 : v));
        }
    }

    // ---- capture front end ------------------------------------------------------------------------------------

    // DC blocker -> voice activity detection -> AGC -> pre-roll. The detector looks at the signal BEFORE the AGC,
    // otherwise the gain would amplify the room noise until it looks like speech. Not thread safe: callers lock it.
    internal sealed class VoiceFrontEnd
    {
        public const int BlockSamples = VoiceFormat.SampleRate / 50;                       // 20 ms
        const int BlocksPerFrame = VoiceFormat.FrameSamples / BlockSamples;

        // DC blocker: y = x - x' + R*y', corner ~90 Hz
        static readonly float HighPassR = 1f - (float)(2 * Math.PI * 90 / VoiceFormat.SampleRate);

        public const float TargetRms = 3000f;
        const float PeakRms = 9000f;              // block loudness the output may never exceed
        public const float DefaultGain = 8f;      // ~18 dB: a sensible start for the weak microphones we have seen
        const float MaxGain = 100f;               // +40 dB
        const float MinGain = 0.05f;
        const float RiseStep = 1.035f;            // per 20 ms block: ~15 dB per second
        const int GainRampSamples = 32;           // 2 ms, so gain changes do not click

        const float OpenFactor = 3f;              // ~ +10 dB over the noise floor
        const float AbsoluteMinRms = 20f;
        const float CloseRatio = 0.7f;            // hysteresis
        const int OpenBlocks = 2;
        const int HangoverBlocks = 25;            // 500 ms
        const int RelaxAfterBlocks = 100;         // 2 s without speech before the gain drifts back

        float hpX, hpY;
        float smoothE;
        float noiseFloor = -1f;
        int above, hang;
        bool open;
        float level;
        float gain = DefaultGain, lastGain = DefaultGain;
        float speechPower;
        int silentBlocks;
        short[] prev;
        bool prevSent;

        public bool GateOpen { get { return open; } }
        public float Level { get { return level; } }
        public float Gain { get { return gain; } }
        public float NoiseFloor { get { return noiseFloor; } }

        // Returns the frames that must be sent for this input frame: none, the frame, or pre-roll + the frame.
        public List<short[]> Process(short[] frame)
        {
            List<short[]> result = new List<short[]>(2);
            short[] output = new short[VoiceFormat.FrameSamples];
            float[] hp = new float[BlockSamples];
            bool anyOpen = false;

            for (int b = 0; b < BlocksPerFrame; b++)
            {
                int off = b * BlockSamples;
                double sum = 0;
                for (int i = 0; i < BlockSamples; i++)
                {
                    float x = frame[off + i];
                    float y = x - hpX + HighPassR * hpY;
                    hpX = x;
                    hpY = y;
                    hp[i] = y;
                    sum += (double)y * y;
                }
                float e = (float)Math.Sqrt(sum / BlockSamples);

                float lt = VoiceMath.LevelFromRms(e);
                level += (lt - level) * (lt > level ? 0.6f : 0.12f);

                // --- voice activity: compare with the noise floor as it was BEFORE this block
                if (noiseFloor < 0f) { noiseFloor = Math.Max(e, 1f); smoothE = e; }
                float openThr = Math.Max(noiseFloor * OpenFactor, AbsoluteMinRms);
                float closeThr = openThr * CloseRatio;
                bool candidate = e > openThr;
                bool wasOpen = open;
                if (!open)
                {
                    if (candidate)
                    {
                        above++;
                        if (above >= OpenBlocks) { open = true; hang = HangoverBlocks; }
                    }
                    else above = 0;
                }
                else
                {
                    if (e > closeThr) hang = HangoverBlocks;
                    else if (--hang <= 0) { open = false; above = 0; }
                }

                // noise floor: falls quickly, rises slowly (and almost not at all while somebody speaks)
                smoothE += (e - smoothE) * 0.5f;
                if (smoothE < noiseFloor) noiseFloor += (smoothE - noiseFloor) * 0.3f;
                else noiseFloor = Math.Min(smoothE, noiseFloor * (open ? 1.0005f : 1.005f));
                if (noiseFloor < 0.5f) noiseFloor = 0.5f;

                // --- AGC: only real speech may raise the gain; loudness may lower it at any time
                if (open && !wasOpen) speechPower = 0f;
                bool speech = open && e > closeThr;
                float g = gain;
                if (speech)
                {
                    silentBlocks = 0;
                    // running mean POWER of the speech blocks: syllable pauses count, so the average RMS hits the target
                    float p2 = e * e;
                    if (speechPower <= 0f) speechPower = p2;
                    else speechPower += (p2 - speechPower) * (p2 > speechPower ? 0.08f : 0.05f);
                    float desired = Clamp(TargetRms / Math.Max((float)Math.Sqrt(speechPower), 1f), MinGain, MaxGain);
                    g = desired < g ? desired : Math.Min(desired, g * RiseStep);
                    // a sudden shout must not wait for the average to catch up
                    g = Math.Min(g, Clamp(PeakRms / Math.Max(e, 1f), MinGain, MaxGain));
                }
                else if (candidate)
                {
                    // speech is starting but the gate is still counting: just protect the ears
                    silentBlocks = 0;
                    float desired = Clamp(TargetRms / e, MinGain, MaxGain);
                    if (desired < g) g = desired;
                }
                else
                {
                    silentBlocks++;
                    // after a long pause the gain slowly goes back to a moderate value (never while the gate is open)
                    if (!open && silentBlocks > RelaxAfterBlocks)
                        g = Clamp(g * (float)Math.Pow(DefaultGain / g, 0.005), MinGain, MaxGain);
                }
                gain = g;

                for (int i = 0; i < BlockSamples; i++)
                {
                    // only rising gain is ramped; lowering it at once is what keeps a loud onset from clipping
                    float gi = (i < GainRampSamples && g > lastGain) ? lastGain + (g - lastGain) * (i + 1) / GainRampSamples : g;
                    output[off + i] = VoiceMath.ToShort(hp[i] * gi);
                }
                lastGain = g;

                if (open) anyOpen = true;
            }

            if (anyOpen)
            {
                // the word onset is usually inside the frame before the gate decided to open
                if (!prevSent && prev != null) result.Add(prev);
                result.Add(output);
            }
            prevSent = anyOpen;
            prev = output;
            return result;
        }

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    // ---- one remote speaker -----------------------------------------------------------------------------------

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
        public int Queued { get { return queue.Count; } }

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

    // ---- channel: front end + queues between the devices and the network -------------------------------------

    public sealed class VoiceChannel : IDisposable
    {
        const int MaxOutgoing = 25;
        const long SpeakingMs = 500;
        const long PlaybackRetryMs = 5000;
        const long MixerTickMs = 50;
        const long ForgetIdleMs = 30000;
        const int MaxSenders = 32;
        const int MaxMutedSenders = 256;
        const int MaxSenderIdLength = 128;
        // a frame is 4 + 800 bytes = 1072 base64 chars; anything much longer is not ours
        const int MaxBase64Chars = 1100;
        const int FrameBytes = 4 + VoiceFormat.FrameSamples / 2;

        static readonly Stopwatch Clock = Stopwatch.StartNew();

        readonly Func<IAudioCapture> captureFactory;
        readonly Func<IAudioPlayback> playbackFactory;
        readonly bool autoMixer;
        readonly object sync = new object();

        IAudioCapture capture;
        IAudioPlayback playback;
        bool disposed;
        bool speakerMuted;
        long playbackRetryAt;

        // outgoing side (capture thread writes, UI reads)
        VoiceFrontEnd frontEnd;
        bool gateOpen;
        float level;
        readonly Queue<string> outgoing = new Queue<string>();

        // incoming side
        readonly Dictionary<string, VoiceSender> senders = new Dictionary<string, VoiceSender>(StringComparer.Ordinal);
        readonly HashSet<string> mutedSenders = new HashSet<string>(StringComparer.Ordinal);
        Thread mixer;
        ManualResetEvent mixerStop;

        public VoiceChannel()
            : this(delegate { return new WaveInCapture(); }, delegate { return new WaveOutPlayback(); })
        {
        }

        public VoiceChannel(Func<IAudioCapture> captureFactory, Func<IAudioPlayback> playbackFactory)
            : this(captureFactory, playbackFactory, true)
        {
        }

        // Tests pass false and drive the mixer by hand with MixStep() to stay independent of timers.
        internal VoiceChannel(Func<IAudioCapture> captureFactory, Func<IAudioPlayback> playbackFactory, bool autoMixer)
        {
            if (captureFactory == null) throw new ArgumentNullException("captureFactory");
            if (playbackFactory == null) throw new ArgumentNullException("playbackFactory");
            this.captureFactory = captureFactory;
            this.playbackFactory = playbackFactory;
            this.autoMixer = autoMixer;
        }

        public static bool MicrophoneAvailable
        {
            get
            {
                try { return WinMm.waveInGetNumDevs() > 0; }
                catch (Exception) { return false; }
            }
        }

        public static bool SpeakerAvailable
        {
            get
            {
                try { return WinMm.waveOutGetNumDevs() > 0; }
                catch (Exception) { return false; }
            }
        }

        public bool MicOpen
        {
            get { lock (sync) { return capture != null; } }
        }

        public bool Transmitting
        {
            get { lock (sync) { return capture != null && gateOpen; } }
        }

        public float MicLevel
        {
            get { lock (sync) { return capture != null ? level : 0f; } }
        }

        public bool AnyoneSpeaking
        {
            get
            {
                lock (sync)
                {
                    long now = Clock.ElapsedMilliseconds;
                    foreach (VoiceSender s in senders.Values)
                        if (now - s.LastAccepted < SpeakingMs) return true;
                    return false;
                }
            }
        }

        public bool IsSpeaking(string senderId)
        {
            if (string.IsNullOrEmpty(senderId)) return false;
            lock (sync)
            {
                VoiceSender s;
                return senders.TryGetValue(senderId, out s) && Clock.ElapsedMilliseconds - s.LastAccepted < SpeakingMs;
            }
        }

        public string[] SpeakingIds()
        {
            lock (sync)
            {
                long now = Clock.ElapsedMilliseconds;
                List<string> ids = new List<string>();
                foreach (KeyValuePair<string, VoiceSender> kv in senders)
                    if (now - kv.Value.LastAccepted < SpeakingMs) ids.Add(kv.Key);
                return ids.ToArray();
            }
        }

        // The player left: drop the buffered audio. A mute choice is kept, it belongs to the user, not to the stream.
        public void ForgetSender(string senderId)
        {
            if (string.IsNullOrEmpty(senderId)) return;
            lock (sync) { senders.Remove(senderId); }
        }

        public void SetSenderMuted(string senderId, bool muted)
        {
            if (string.IsNullOrEmpty(senderId) || senderId.Length > MaxSenderIdLength) return;
            lock (sync)
            {
                if (muted)
                {
                    if (mutedSenders.Count < MaxMutedSenders) mutedSenders.Add(senderId);
                    senders.Remove(senderId);
                }
                else mutedSenders.Remove(senderId);
            }
        }

        internal int SenderCount
        {
            get { lock (sync) { return senders.Count; } }
        }

        internal bool MixerAlive
        {
            get { lock (sync) { Thread t = mixer; return t != null && t.IsAlive; } }
        }

        public bool SpeakerMuted
        {
            get { lock (sync) { return speakerMuted; } }
            set
            {
                IAudioPlayback toStop = null;
                lock (sync)
                {
                    speakerMuted = value;
                    if (value)
                    {
                        senders.Clear();
                        toStop = playback;
                        playback = null;
                    }
                }
                if (value)
                {
                    // muted means the device is released, not just silent
                    StopMixer();
                    CloseQuietly(toStop);
                }
            }
        }

        // ---- microphone -------------------------------------------------------------------------------------

        public bool StartMic(out string error)
        {
            IAudioCapture cap = null;
            lock (sync)
            {
                if (disposed) { error = "Голосовой чат закрыт."; return false; }
                if (capture != null) { error = null; return true; }
                try
                {
                    cap = captureFactory();
                    cap.FrameCaptured += OnFrame;
                    // frames may arrive right after Start, so the channel must already count as open
                    frontEnd = new VoiceFrontEnd();
                    capture = cap;
                    gateOpen = false;
                    level = 0f;
                    if (cap.Start(out error)) return true;
                }
                catch (Exception)
                {
                    error = "Не удалось открыть микрофон.";
                }
                capture = null;
                frontEnd = null;
            }
            if (string.IsNullOrEmpty(error)) error = "Не удалось открыть микрофон.";
            if (cap != null)
            {
                try { cap.FrameCaptured -= OnFrame; } catch (Exception) { }
                CloseQuietly(cap);
            }
            return false;
        }

        public void StopMic()
        {
            IAudioCapture cap;
            lock (sync)
            {
                cap = capture;
                capture = null;
                frontEnd = null;
                gateOpen = false;
                level = 0f;
            }
            if (cap == null) return;
            // outside the lock: Stop() waits for the capture thread, which may be waiting for the lock
            try { cap.FrameCaptured -= OnFrame; } catch (Exception) { }
            CloseQuietly(cap);
        }

        public string[] TakeOutgoing()
        {
            lock (sync)
            {
                if (outgoing.Count == 0) return new string[0];
                string[] result = outgoing.ToArray();
                outgoing.Clear();
                return result;
            }
        }

        void OnFrame(short[] pcm)
        {
            try
            {
                if (pcm == null || pcm.Length != VoiceFormat.FrameSamples) return;
                VoiceFrontEnd fe;
                lock (sync)
                {
                    if (capture == null || disposed) return;
                    fe = frontEnd;
                }
                if (fe == null) return;

                List<short[]> frames;
                bool open;
                float lvl;
                lock (fe)
                {
                    frames = fe.Process(pcm);
                    open = fe.GateOpen;
                    lvl = fe.Level;
                }

                // encoding is the expensive part, keep it outside the shared lock
                string[] payloads = new string[frames.Count];
                for (int i = 0; i < payloads.Length; i++)
                    payloads[i] = Convert.ToBase64String(AdpcmCodec.Encode(frames[i]));

                lock (sync)
                {
                    if (capture == null || frontEnd != fe) return;
                    gateOpen = open;
                    level = lvl;
                    for (int i = 0; i < payloads.Length; i++) outgoing.Enqueue(payloads[i]);
                    while (outgoing.Count > MaxOutgoing) outgoing.Dequeue();
                }
            }
            catch (Exception)
            {
                // runs on the capture thread: a bad frame must not kill the capture
            }
        }

        // ---- incoming audio ---------------------------------------------------------------------------------

        public void Receive(string senderId, string base64)
        {
            try
            {
                if (string.IsNullOrEmpty(senderId) || senderId.Length > MaxSenderIdLength) return;
                if (string.IsNullOrEmpty(base64) || base64.Length > MaxBase64Chars) return;
                lock (sync)
                {
                    if (disposed || speakerMuted || mutedSenders.Contains(senderId)) return;
                }

                byte[] data;
                try { data = Convert.FromBase64String(base64); }
                catch (FormatException) { return; }
                // only whole frames: the sender always produces them, so any other size is noise
                if (data.Length != FrameBytes) return;
                short[] pcm = AdpcmCodec.Decode(data);
                if (pcm == null) return;

                lock (sync)
                {
                    if (disposed || speakerMuted || mutedSenders.Contains(senderId)) return;
                    long now = Clock.ElapsedMilliseconds;
                    VoiceSender s;
                    if (!senders.TryGetValue(senderId, out s))
                    {
                        if (senders.Count >= MaxSenders) return;
                        s = new VoiceSender();
                        senders[senderId] = s;
                    }
                    if (!s.AcceptRate(now)) return;
                    s.LastAccepted = now;
                    // no device, no point in buffering
                    if (EnsurePlayback(now) != null) s.Push(pcm);
                }
            }
            catch (Exception)
            {
                // network garbage or a device hiccup must never reach the caller
            }
        }

        // Caller holds sync.
        IAudioPlayback EnsurePlayback(long now)
        {
            if (playback != null) return playback;
            if (now < playbackRetryAt) return null;

            IAudioPlayback pb = null;
            try
            {
                pb = playbackFactory();
                string err;
                if (pb.Start(out err))
                {
                    playback = pb;
                    if (autoMixer) StartMixer();
                    return pb;
                }
            }
            catch (Exception) { }
            // do not hammer a missing device on every frame
            playbackRetryAt = now + PlaybackRetryMs;
            CloseQuietly(pb);
            return null;
        }

        // Caller holds sync.
        void StartMixer()
        {
            if (mixer != null) return;
            ManualResetEvent stop = new ManualResetEvent(false);
            Thread t = new Thread(delegate() { MixerLoop(stop); });
            t.IsBackground = true;
            t.Name = "VoiceMixer";
            mixerStop = stop;
            mixer = t;
            t.Start();
        }

        void StopMixer()
        {
            Thread t;
            ManualResetEvent stop;
            lock (sync)
            {
                t = mixer;
                stop = mixerStop;
                mixer = null;
                mixerStop = null;
            }
            if (t == null) return;
            try { stop.Set(); } catch (Exception) { }
            if (t != Thread.CurrentThread && !t.Join(3000)) return; // leave the event alone if it is still in use
            try { stop.Close(); } catch (Exception) { }
        }

        void MixerLoop(ManualResetEvent stop)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                long next = MixerTickMs;
                while (true)
                {
                    long wait = next - sw.ElapsedMilliseconds;
                    if (stop.WaitOne(wait > 0 ? (int)wait : 0)) break;
                    MixStep();
                    next += MixerTickMs;
                    // after a long stall do not play catch-up bursts
                    if (sw.ElapsedMilliseconds - next > 200) next = sw.ElapsedMilliseconds + MixerTickMs;
                }
            }
            catch (Exception)
            {
                // a worker thread must never take the process down
            }
        }

        // Mixes 50 ms of every active sender into one chunk (sum, soft limiter) and hands it to the playback.
        internal void MixStep()
        {
            IAudioPlayback pb;
            short[] chunk = null;
            lock (sync)
            {
                if (disposed || playback == null) return;
                pb = playback;
                float[] mix = null;
                bool any = false;
                List<string> stale = null;
                long now = Clock.ElapsedMilliseconds;
                foreach (KeyValuePair<string, VoiceSender> kv in senders)
                {
                    VoiceSender s = kv.Value;
                    if (!s.Active)
                    {
                        if (now - s.LastAccepted > ForgetIdleMs)
                        {
                            if (stale == null) stale = new List<string>();
                            stale.Add(kv.Key);
                        }
                        continue;
                    }
                    if (mix == null) mix = new float[VoiceSender.TickSamples];
                    if (s.Pull(mix, VoiceSender.TickSamples)) any = true;
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++) senders.Remove(stale[i]);

                if (any)
                {
                    chunk = new short[mix.Length];
                    for (int i = 0; i < chunk.Length; i++) chunk[i] = VoiceMath.ToShort(mix[i]);
                }
            }
            if (chunk != null) pb.Enqueue(chunk);
        }

        static void CloseQuietly(IDisposable d)
        {
            if (d == null) return;
            try
            {
                IAudioCapture c = d as IAudioCapture;
                IAudioPlayback p = d as IAudioPlayback;
                if (c != null) c.Stop();
                if (p != null) p.Stop();
            }
            catch (Exception) { }
            try { d.Dispose(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            IAudioPlayback pb;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                pb = playback;
                playback = null;
                outgoing.Clear();
                senders.Clear();
            }
            StopMixer();
            StopMic();
            CloseQuietly(pb);
        }
    }
}
