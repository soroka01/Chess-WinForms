using System;
using System.Diagnostics;
using System.Threading;

namespace ChessGame
{
    // Calls a step every tickMs on its own thread. A step that throws only loses that tick: the mixer must keep
    // running, otherwise all incoming audio stays silent for the rest of the session.
    internal sealed class VoiceMixerThread
    {
        readonly Action step;
        readonly long tickMs;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly object gate = new object();
        readonly Thread thread;
        bool closed;

        VoiceMixerThread(Action step, long tickMs)
        {
            this.step = step;
            this.tickMs = tickMs;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "VoiceMixer";
        }

        public static VoiceMixerThread Start(Action step, long tickMs)
        {
            VoiceMixerThread m = new VoiceMixerThread(step, tickMs);
            m.thread.Start();
            return m;
        }

        public bool IsAlive
        {
            get { return thread.IsAlive; }
        }

        // Asks the thread to end and waits up to joinMs for it (0: do not wait). The thread closes the event itself
        // when it ends, so a stalled step can never leave the event half-used.
        public void Stop(int joinMs)
        {
            lock (gate)
            {
                if (!closed) stop.Set();
            }
            if (joinMs > 0 && thread != Thread.CurrentThread) thread.Join(joinMs);
        }

        void Loop()
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                long next = tickMs;
                while (true)
                {
                    long wait = next - sw.ElapsedMilliseconds;
                    if (stop.WaitOne(wait > 0 ? (int)wait : 0)) break;
                    try { step(); }
                    catch (Exception) { }
                    next += tickMs;
                    // after a long stall do not play catch-up bursts
                    if (sw.ElapsedMilliseconds - next > 200) next = sw.ElapsedMilliseconds + tickMs;
                }
            }
            catch (Exception)
            {
                // a worker thread must never take the process down
            }
            finally
            {
                lock (gate)
                {
                    closed = true;
                    try { stop.Close(); }
                    catch (Exception) { }
                }
            }
        }
    }
}
