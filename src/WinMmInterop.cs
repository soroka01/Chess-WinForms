// winmm interop: structures, entry points, the unmanaged buffers the driver reads and writes, and the shared
// release / close-ordering helpers of the two device wrappers.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChessGame
{
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

        public static readonly int WaveHdrSize = Marshal.SizeOf(typeof(WaveHdr));

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
                    Headers[i] = Marshal.AllocHGlobal(WinMm.WaveHdrSize);
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

    // The three driver calls that differ between waveIn and waveOut when a device is released.
    sealed class WaveApi
    {
        public static readonly WaveApi In = new WaveApi(WinMm.waveInReset, WinMm.waveInUnprepareHeader, WinMm.waveInClose);
        public static readonly WaveApi Out = new WaveApi(WinMm.waveOutReset, WinMm.waveOutUnprepareHeader, WinMm.waveOutClose);

        public readonly Func<IntPtr, int> Reset;
        public readonly Func<IntPtr, IntPtr, int, int> Unprepare;
        public readonly Func<IntPtr, int> Close;

        public WaveApi(Func<IntPtr, int> reset, Func<IntPtr, IntPtr, int, int> unprepare, Func<IntPtr, int> close)
        {
            Reset = reset;
            Unprepare = unprepare;
            Close = close;
        }
    }

    static class WaveRelease
    {
        // Gives a device back to the system. It runs in the finally of worker threads, where an exception would end
        // the process, so every step is guarded on its own and a failing step never skips the ones after it.
        public static void Run(WaveApi api, IntPtr dev, WaveBufferSet bufs, AutoResetEvent evt)
        {
            if (dev != IntPtr.Zero)
            {
                Try(delegate { api.Reset(dev); }); // hands all queued buffers back
                if (bufs != null)
                {
                    for (int i = 0; i < bufs.Count; i++)
                    {
                        if (!bufs.Prepared[i]) continue;
                        int k = i;
                        Try(delegate { api.Unprepare(dev, bufs.Headers[k], WinMm.WaveHdrSize); });
                        bufs.Prepared[i] = false;
                    }
                }
                Try(delegate { api.Close(dev); });
            }
            if (bufs != null) Try(bufs.Free);
            if (evt != null) Try(evt.Dispose);
        }

        // Wakes a worker; the event may already be gone when the worker released the device.
        public static void Signal(AutoResetEvent evt)
        {
            if (evt == null) return;
            try { evt.Set(); }
            catch (ObjectDisposedException) { }
        }

        static void Try(Action step)
        {
            try { step(); }
            catch (Exception) { }
        }
    }

    // Counts devices that are being closed in the background. Stop() of the wrappers returns at once, so a device
    // opened right afterwards (a new voice channel after the room was left) waits here instead of meeting the
    // old one still holding the hardware (MMSYSERR_ALLOCATED).
    sealed class DeviceCloseGate
    {
        readonly object gate = new object();
        int closing;

        public int Closing
        {
            get { lock (gate) { return closing; } }
        }

        public void Begin()
        {
            lock (gate) { closing++; }
        }

        public void End()
        {
            lock (gate)
            {
                if (closing > 0) closing--;
                Monitor.PulseAll(gate);
            }
        }

        // True when nothing is closing any more; false after the timeout (a stalled driver must not block forever).
        public bool WaitIdle(int timeoutMs)
        {
            long deadline = VoiceClock.Ms + timeoutMs;
            lock (gate)
            {
                while (closing > 0)
                {
                    long left = deadline - VoiceClock.Ms;
                    if (left <= 0) return false;
                    Monitor.Wait(gate, (int)left);
                }
                return true;
            }
        }
    }
}
