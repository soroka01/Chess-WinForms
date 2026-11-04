using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChessGame
{
    // waveIn with CALLBACK_EVENT: the driver only signals an event, all waveIn* calls happen on our own threads.
    // The worker thread owns the device and releases it when it ends, so Stop() returns at once and is safe even
    // from a handler. A worker that ends by itself (the driver refused a buffer: headset unplugged) marks the
    // capture Failed, so the owner can tell a dead microphone from an open one.
    internal sealed class WaveInCapture : IAudioCapture
    {
        const int BufferCount = 4;
        const int CloseWaitMs = 2000;

        internal static readonly DeviceCloseGate CloseGate = new DeviceCloseGate();

        readonly object sync = new object();   // lifetime state; never held across a slow driver call except the open itself
        IntPtr dev;
        WaveBufferSet bufs;
        AutoResetEvent evt;
        Thread worker;
        bool started;
        bool closeCounted;
        volatile bool stopping;
        volatile bool failed;

        public event Action<short[]> FrameCaptured;

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
            // the previous microphone of this process may still be closing in the background
            CloseGate.WaitIdle(CloseWaitMs);
            lock (sync)
            {
                if (stopping) { error = VoiceErrors.MicClosed; return false; }
                if (failed) { error = VoiceErrors.MicOpenFailed; return false; }
                if (started) { error = null; return true; }
                try
                {
                    if (WinMm.waveInGetNumDevs() <= 0) { error = VoiceErrors.MicNotFound; return false; }
                    evt = new AutoResetEvent(false);
                    WaveFormatEx fmt = WinMm.Format();
                    int rc = WinMm.waveInOpen(out dev, WinMm.WAVE_MAPPER, ref fmt,
                        evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CALLBACK_EVENT);
                    if (rc != 0)
                    {
                        dev = IntPtr.Zero;
                        error = VoiceErrors.Describe(rc, true);
                        Release();
                        return false;
                    }

                    bufs = new WaveBufferSet(BufferCount, VoiceFormat.FrameSamples * 2);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveInPrepareHeader(dev, bufs.Headers[i], WinMm.WaveHdrSize);
                        if (rc != 0) { error = VoiceErrors.Describe(rc, true); Release(); return false; }
                        bufs.Prepared[i] = true;
                    }
                    for (int i = 0; i < BufferCount; i++)
                    {
                        rc = WinMm.waveInAddBuffer(dev, bufs.Headers[i], WinMm.WaveHdrSize);
                        if (rc != 0) { error = VoiceErrors.Describe(rc, true); Release(); return false; }
                    }
                    rc = WinMm.waveInStart(dev);
                    if (rc != 0) { error = VoiceErrors.Describe(rc, true); Release(); return false; }

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
                    error = VoiceErrors.MicOpenFailed;
                    return false;
                }
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
                int next = 0;
                while (!stopping)
                {
                    e.WaitOne(100);
                    while (!stopping && (b.Flags(next) & WinMm.WHDR_DONE) != 0)
                    {
                        // a partially filled buffer only appears after a reset, i.e. during shutdown
                        if (b.BytesRecorded(next) >= VoiceFormat.FrameSamples * 2)
                        {
                            short[] frame = new short[VoiceFormat.FrameSamples];
                            Marshal.Copy(b.Data[next], frame, 0, frame.Length);
                            Raise(frame);
                        }
                        if (stopping) break;
                        // if the driver refuses the buffer the device is gone: say so and end instead of spinning
                        if (WinMm.waveInAddBuffer(d, b.Headers[next], WinMm.WaveHdrSize) != 0) { failed = true; return; }
                        next = (next + 1) % BufferCount;
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
                try { Release(); }
                finally { CloseGate.End(); }
            }
        }

        void Raise(short[] frame)
        {
            Action<short[]> h = FrameCaptured;
            if (h == null) return;
            try { h(frame); }
            catch (Exception) { }
        }

        // Takes the device, buffers and event away from the fields and gives them back to the system. The lock is
        // held only for the hand-over: a stalled driver in waveInReset / waveInClose must not block Stop().
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
            WaveRelease.Run(WaveApi.In, d, b, e);
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
            Thread w;
            lock (sync)
            {
                stopping = true;
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
