using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChessGame
{
    // waveOut with CALLBACK_EVENT and a feeder thread. Short fixed buffers are refilled from a queue; after an
    // underrun two frames are collected first so that a jittery network does not turn into crackling.
    // Like WaveInCapture: Stop() returns at once, the worker releases the device, and a worker that ends by itself
    // (the driver refused a buffer: headset unplugged) marks the playback Failed.
    internal sealed class WaveOutPlayback : IAudioPlayback
    {
        const int BufferCount = 8;
        const int MaxQueuedSamples = VoiceFormat.SampleRate * 3 / 2;
        // the mixer feeds 50 ms chunks, so two of them are a safe start without adding much delay
        const int PrebufferSamples = VoiceFormat.FrameSamples;
        // a lone short tail must not wait forever for its second chunk
        const long PrebufferTimeoutMs = 150;
        const int CloseWaitMs = 2000;

        internal static readonly DeviceCloseGate CloseGate = new DeviceCloseGate();

        readonly object sync = new object();   // lifetime state; never held across a slow driver call except the open itself
        readonly object qLock = new object();  // queue state
        readonly Queue<short[]> queue = new Queue<short[]>();
        int headOffset;
        int queued;
        long pendingSince;
        bool playing;

        IntPtr dev;
        WaveBufferSet bufs;
        volatile AutoResetEvent evt;
        Thread worker;
        bool started;
        bool closeCounted;
        volatile bool stopping;
        volatile bool failed;

        public bool Failed
        {
            get { return failed; }
        }

        internal bool WorkerAlive
        {
            get { Thread w = worker; return w != null && w.IsAlive; }
        }

        public bool Start(out string error)
        {
            // the previous output device of this process may still be closing in the background
            CloseGate.WaitIdle(CloseWaitMs);
            lock (sync)
            {
                if (stopping) { error = VoiceErrors.SpeakerClosed; return false; }
                if (failed) { error = VoiceErrors.SpeakerOpenFailed; return false; }
                if (started) { error = null; return true; }
                try
                {
                    if (WinMm.waveOutGetNumDevs() <= 0) { error = VoiceErrors.SpeakerNotFound; return false; }
                    evt = new AutoResetEvent(false);
                    WaveFormatEx fmt = WinMm.Format();
                    int rc = WinMm.waveOutOpen(out dev, WinMm.WAVE_MAPPER, ref fmt,
                        evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CALLBACK_EVENT);
                    if (rc != 0)
                    {
                        dev = IntPtr.Zero;
                        error = VoiceErrors.Describe(rc, false);
                        Release();
                        return false;
                    }

                    bufs = new WaveBufferSet(BufferCount, VoiceFormat.FrameSamples * 2);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveOutPrepareHeader(dev, bufs.Headers[i], WinMm.WaveHdrSize);
                        if (rc != 0) { error = VoiceErrors.Describe(rc, false); Release(); return false; }
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
                    error = VoiceErrors.SpeakerOpenFailed;
                    return false;
                }
            }
        }

        // The array now belongs to the playback (see IAudioPlayback.Enqueue).
        public void Enqueue(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            lock (qLock)
            {
                if (stopping || failed) return;
                if (queued == 0) pendingSince = VoiceClock.Ms;
                queue.Enqueue(pcm);
                queued += pcm.Length;

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
            WaveRelease.Signal(evt);
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
                        || VoiceClock.Ms - pendingSince >= PrebufferTimeoutMs;
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

        void ClearQueue()
        {
            lock (qLock)
            {
                queue.Clear();
                queued = 0;
                headOffset = 0;
            }
        }

        void Run()
        {
            // the resources are this thread's until it ends; Release() takes them over in the finally below
            IntPtr d = dev;
            WaveBufferSet b = bufs;
            AutoResetEvent e = evt;
            try
            {
                bool[] busy = new bool[BufferCount];
                int inFlight = 0;
                while (!stopping)
                {
                    e.WaitOne(50);
                    if (stopping) break;

                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (busy[i] && (b.Flags(i) & WinMm.WHDR_DONE) != 0) { busy[i] = false; inFlight--; }
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
                        Marshal.Copy(block, 0, b.Data[i], block.Length);
                        b.SetLength(i, block.Length * 2);
                        // if the driver refuses the buffer the device is gone: say so and end instead of spinning
                        if (WinMm.waveOutWrite(d, b.Headers[i], WinMm.WaveHdrSize) != 0) { failed = true; return; }
                        busy[i] = true;
                        inFlight++;
                    }
                }
            }
            catch (Exception)
            {
                // a worker thread must never take the process down
                failed = true;
            }
            finally
            {
                CountClosing();
                ClearQueue();
                try { Release(); }
                finally { CloseGate.End(); }
            }
        }

        // Takes the device, buffers and event away from the fields and gives them back to the system. The lock is
        // held only for the hand-over: a stalled driver in waveOutReset / waveOutClose must not block Stop().
        // Tolerates a half-initialised state (used on a failed Start too).
        void Release()
        {
            IntPtr d;
            WaveBufferSet b;
            AutoResetEvent e;
            lock (sync)
            {
                d = dev; b = bufs; e = evt;
                dev = IntPtr.Zero; bufs = null; evt = null;
            }
            WaveRelease.Run(WaveApi.Out, d, b, e);
        }

        // Registers this device with the close gate, once. Begin and End are paired: End comes from the worker.
        void CountClosing()
        {
            lock (sync)
            {
                if (closeCounted) return;
                closeCounted = true;
                CloseGate.Begin(); // inside the lock, so the worker's End can never overtake it
            }
        }

        public void Stop()
        {
            stopping = true;
            ClearQueue();
            Thread w;
            lock (sync)
            {
                w = worker;
                WaveRelease.Signal(evt);
            }
            // the worker releases the device when it ends; without a worker nothing else will
            if (w == null) Release();
            else CountClosing();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
