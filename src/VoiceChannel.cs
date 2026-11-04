using System;
using System.Collections.Generic;
using System.Threading;

namespace ChessGame
{
    // The facade the UI talks to: capture front end and outgoing queue on one side, jitter buffers, mixer and
    // playback on the other. One lock (sync) guards all state; no device is opened, started or stopped while
    // holding it, so a slow driver never stalls the capture thread, the mixer or the UI timer.
    public sealed class VoiceChannel : IDisposable
    {
        const int MaxOutgoing = 25;
        const long SpeakingMs = 500;
        const long PlaybackRetryMs = 5000;
        // a playback that died sooner than this after it was opened is not replaced at once
        const long PlaybackMinLifeMs = 1000;
        const long MixerTickMs = 50;
        const long ForgetIdleMs = 30000;
        const int MixerStopWaitMs = 3000;
        const int MaxSenders = 32;
        const int MaxMutedSenders = 256;
        const int MaxSenderIdLength = 128;
        // a frame is 4 + 800 bytes = 1072 base64 chars; anything longer is not ours
        const int FrameBytes = AdpcmCodec.HeaderSize + VoiceFormat.FrameSamples / 2;
        const int MaxBase64Chars = (FrameBytes + 2) / 3 * 4;

        // One open microphone: the device, its own front end and the handler that ties its frames to this session.
        sealed class MicSession
        {
            public readonly IAudioCapture Device;
            public readonly VoiceFrontEnd FrontEnd = new VoiceFrontEnd();
            public Action<short[]> Handler;

            public MicSession(IAudioCapture device)
            {
                Device = device;
            }
        }

        readonly Func<IAudioCapture> captureFactory;
        readonly Func<IAudioPlayback> playbackFactory;
        readonly bool autoMixer;
        readonly object sync = new object();

        MicSession mic;
        IAudioPlayback playback;
        bool playbackOpening;
        bool disposed;
        bool speakerMuted;
        long playbackSince;
        long playbackRetryAt;
        long clockSkewMs;

        // outgoing side (capture thread writes, UI reads)
        bool gateOpen;
        readonly Queue<string> outgoing = new Queue<string>();

        // incoming side
        readonly Dictionary<string, VoiceSender> senders = new Dictionary<string, VoiceSender>(StringComparer.Ordinal);
        readonly HashSet<string> mutedSenders = new HashSet<string>(StringComparer.Ordinal);
        VoiceMixerThread mixer;

        public VoiceChannel()
            : this(delegate { return new WaveInCapture(); }, delegate { return new WaveOutPlayback(); })
        {
        }

        internal VoiceChannel(Func<IAudioCapture> captureFactory, Func<IAudioPlayback> playbackFactory)
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

        // Tests move the channel's clock instead of sleeping through the retry back-offs.
        internal void AdvanceClockForTest(long ms)
        {
            Interlocked.Add(ref clockSkewMs, ms);
        }

        long Now()
        {
            return VoiceClock.Ms + Interlocked.Read(ref clockSkewMs);
        }

        // A microphone that died by itself (unplugged) no longer counts as open.
        public bool MicOpen
        {
            get { lock (sync) { return mic != null && !mic.Device.Failed; } }
        }

        public bool Transmitting
        {
            get { lock (sync) { return mic != null && !mic.Device.Failed && gateOpen; } }
        }

        public bool IsSpeaking(string senderId)
        {
            if (string.IsNullOrEmpty(senderId)) return false;
            lock (sync)
            {
                VoiceSender s;
                return senders.TryGetValue(senderId, out s) && Now() - s.LastAccepted < SpeakingMs;
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
            get { lock (sync) { VoiceMixerThread m = mixer; return m != null && m.IsAlive; } }
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
                    // muted means the device is released, not just silent; neither step waits for a thread
                    StopMixer(0);
                    CloseQuietly(toStop);
                }
            }
        }

        // ---- microphone -------------------------------------------------------------------------------------

        public bool StartMic(out string error)
        {
            MicSession stale = null;
            lock (sync)
            {
                if (disposed) { error = VoiceErrors.ChannelClosed; return false; }
                if (mic != null)
                {
                    if (!mic.Device.Failed) { error = null; return true; }
                    stale = TakeMic(); // the microphone died (unplugged): start over with a new device
                }
            }
            CloseMic(stale);

            // The device is opened outside the lock; the result is published afterwards.
            MicSession session = null;
            string openError = null;
            bool started;
            try
            {
                session = new MicSession(captureFactory());
                MicSession own = session;
                session.Handler = delegate(short[] pcm) { OnFrame(own, pcm); };
                session.Device.FrameCaptured += session.Handler;
                started = session.Device.Start(out openError);
            }
            catch (Exception)
            {
                started = false;
                openError = null;
            }

            MicSession surplus = null;
            bool ok;
            lock (sync)
            {
                if (!started)
                {
                    ok = false;
                    surplus = session;
                }
                else if (disposed)
                {
                    ok = false;
                    openError = VoiceErrors.ChannelClosed;
                    surplus = session;
                }
                else if (mic != null && !mic.Device.Failed)
                {
                    // somebody else opened the microphone while we were busy: keep theirs
                    ok = true;
                    openError = null;
                    surplus = session;
                }
                else
                {
                    stale = mic;
                    mic = session;
                    gateOpen = false;
                    ok = true;
                    openError = null;
                }
            }
            CloseMic(stale);
            CloseMic(surplus);
            if (!ok && string.IsNullOrEmpty(openError)) openError = VoiceErrors.MicOpenFailed;
            error = openError;
            return ok;
        }

        public void StopMic()
        {
            MicSession m;
            lock (sync) { m = TakeMic(); }
            // outside the lock: the capture thread may be waiting for it
            CloseMic(m);
        }

        // Caller holds sync.
        MicSession TakeMic()
        {
            MicSession m = mic;
            mic = null;
            gateOpen = false;
            return m;
        }

        static void CloseMic(MicSession m)
        {
            if (m == null) return;
            try { m.Device.FrameCaptured -= m.Handler; }
            catch (Exception) { }
            CloseQuietly(m.Device);
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

        void OnFrame(MicSession session, short[] pcm)
        {
            try
            {
                if (pcm == null || pcm.Length != VoiceFormat.FrameSamples) return;
                lock (sync)
                {
                    if (mic != session || disposed) return;
                }

                // only this session's capture thread runs its front end, so the front end needs no lock
                List<short[]> frames = session.FrontEnd.Process(pcm);
                bool open = session.FrontEnd.GateOpen;

                // encoding is the expensive part, keep it outside the shared lock
                string[] payloads = new string[frames.Count];
                for (int i = 0; i < payloads.Length; i++)
                    payloads[i] = Convert.ToBase64String(AdpcmCodec.Encode(frames[i]));

                lock (sync)
                {
                    if (mic != session) return;
                    gateOpen = open;
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

                IAudioPlayback dead = null;
                bool openDevice = false;
                lock (sync)
                {
                    if (disposed || speakerMuted || mutedSenders.Contains(senderId)) return;
                    long now = Now();
                    VoiceSender s;
                    if (!senders.TryGetValue(senderId, out s))
                    {
                        if (senders.Count >= MaxSenders) return;
                        s = new VoiceSender();
                        senders[senderId] = s;
                    }
                    if (!s.AcceptRate(now)) return;
                    s.LastAccepted = now;

                    if (playback != null && playback.Failed)
                    {
                        // the device died (headset unplugged): drop it and open a new one; one that died right
                        // after it was opened waits for the retry back-off like any other failing device
                        dead = playback;
                        playback = null;
                        if (now - playbackSince < PlaybackMinLifeMs) playbackRetryAt = now + PlaybackRetryMs;
                    }
                    if (playback != null) s.Push(pcm);
                    else if (!playbackOpening && now >= playbackRetryAt)
                    {
                        playbackOpening = true;
                        openDevice = true;
                    }
                    // else: no device, no point in buffering
                }
                CloseQuietly(dead);
                if (openDevice) OpenPlayback(senderId, pcm);
            }
            catch (Exception)
            {
                // network garbage or a device hiccup must never reach the caller
            }
        }

        // Creates and starts the playback outside the lock, then publishes it unless the situation changed meanwhile.
        void OpenPlayback(string senderId, short[] pcm)
        {
            IAudioPlayback pb = null;
            bool started;
            try
            {
                pb = playbackFactory();
                string err;
                started = pb.Start(out err);
            }
            catch (Exception)
            {
                started = false;
            }

            IAudioPlayback surplus = null;
            lock (sync)
            {
                playbackOpening = false;
                long now = Now();
                if (!started)
                {
                    // do not hammer a missing device on every frame
                    playbackRetryAt = now + PlaybackRetryMs;
                    surplus = pb;
                }
                else if (disposed || speakerMuted || playback != null)
                {
                    surplus = pb;
                }
                else
                {
                    playback = pb;
                    playbackSince = now;
                    if (autoMixer) StartMixer();
                    VoiceSender s;
                    if (senders.TryGetValue(senderId, out s)) s.Push(pcm);
                }
            }
            CloseQuietly(surplus);
        }

        // Caller holds sync.
        void StartMixer()
        {
            if (mixer != null && mixer.IsAlive) return;
            mixer = VoiceMixerThread.Start(MixStep, MixerTickMs);
        }

        void StopMixer(int joinMs)
        {
            VoiceMixerThread m;
            lock (sync)
            {
                m = mixer;
                mixer = null;
            }
            if (m != null) m.Stop(joinMs);
        }

        // Mixes 50 ms of every active sender into one chunk (sum, soft limiter) and hands it to the playback,
        // which owns the chunk from then on.
        internal void MixStep()
        {
            IAudioPlayback pb;
            short[] chunk = null;
            lock (sync)
            {
                if (disposed || playback == null || playback.Failed) return;
                pb = playback;
                float[] mix = null;
                bool any = false;
                List<string> stale = null;
                long now = Now();
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
            StopMixer(MixerStopWaitMs);
            StopMic();
            CloseQuietly(pb);
        }
    }
}
