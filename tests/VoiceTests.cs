// Console test harness for the voice chat module (codec, capture front end, mixer, silent playback smoke test).
// The microphone is private: nothing here ever opens a real capture device, and real playback gets only silence.
// Build & run:  tests\run-voice-tests.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ChessGame;

static class VoiceTests
{
    static int failures;
    const int N = VoiceFormat.FrameSamples;
    const int Rate = VoiceFormat.SampleRate;

    static void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
    }

    // ---- fakes ----

    sealed class FakeCapture : IAudioCapture
    {
        public event Action<short[]> FrameCaptured;
        public bool StartResult = true;
        public string StartError = "";
        public int Starts, Stops, Disposes;

        public bool Start(out string error)
        {
            Starts++;
            error = StartResult ? null : StartError;
            return StartResult;
        }
        public void Stop() { Stops++; }
        public void Dispose() { Disposes++; }
        public void Feed(short[] frame)
        {
            Action<short[]> h = FrameCaptured;
            if (h != null) h(frame);
        }
    }

    sealed class FakePlayback : IAudioPlayback
    {
        readonly object gate = new object();
        readonly List<short[]> chunks = new List<short[]>();
        public int Starts, Stops, Disposes;

        public bool Start(out string error) { Starts++; error = null; return true; }
        public void Enqueue(short[] pcm) { lock (gate) { chunks.Add(pcm); } }
        public void Stop() { Stops++; }
        public void Dispose() { Disposes++; }
        public int Count { get { lock (gate) { return chunks.Count; } } }
        public short[] At(int i) { lock (gate) { return chunks[i]; } }
        public int TotalSamples
        {
            get { lock (gate) { int n = 0; foreach (short[] c in chunks) n += c.Length; return n; } }
        }
        public short[] All()
        {
            lock (gate)
            {
                int n = 0;
                foreach (short[] c in chunks) n += c.Length;
                short[] all = new short[n];
                int p = 0;
                foreach (short[] c in chunks) { Array.Copy(c, 0, all, p, c.Length); p += c.Length; }
                return all;
            }
        }
    }

    // A channel whose mixer is driven by hand, with one shared fake playback.
    sealed class Rig
    {
        public readonly FakePlayback Playback = new FakePlayback();
        public readonly VoiceChannel Channel;
        public Rig()
        {
            Channel = new VoiceChannel(delegate { return new FakeCapture(); }, delegate { return Playback; }, false);
        }
        public void Steps(int n) { for (int i = 0; i < n; i++) Channel.MixStep(); }
    }

    // ---- signal helpers ----

    static short[] Tone(double freq, double amp, long start, int n)
    {
        short[] s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * freq * (start + i) / Rate));
        return s;
    }

    static double Rms(short[] s)
    {
        double sum = 0;
        for (int i = 0; i < s.Length; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / s.Length);
    }

    static double Rms(short[] s, int from, int count)
    {
        double sum = 0;
        for (int i = from; i < from + count; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / count);
    }

    static int MaxAbs(short[] s)
    {
        int m = 0;
        for (int i = 0; i < s.Length; i++) m = Math.Max(m, Math.Abs((int)s[i]));
        return m;
    }

    static int MaxStep(short[] s)
    {
        int m = 0;
        for (int i = 1; i < s.Length; i++) m = Math.Max(m, Math.Abs(s[i] - s[i - 1]));
        return m;
    }

    static double SnrDb(short[] orig, short[] dec)
    {
        double sig = 0, err = 0;
        for (int i = 0; i < orig.Length; i++)
        {
            double d = orig[i] - dec[i];
            sig += (double)orig[i] * orig[i];
            err += d * d;
        }
        if (err == 0) return 200;
        return 10 * Math.Log10(sig / err);
    }

    // power at one exact DFT bin (freq must fit the frame length)
    static double BinPower(short[] s, double freq)
    {
        double re = 0, im = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double a = 2 * Math.PI * freq * i / Rate;
            re += s[i] * Math.Cos(a);
            im -= s[i] * Math.Sin(a);
        }
        return (re * re + im * im) / ((double)s.Length * s.Length);
    }

    static string Payload(short[] frame)
    {
        return Convert.ToBase64String(AdpcmCodec.Encode(frame));
    }

    static short[] Roundtrip(short[] frame)
    {
        return AdpcmCodec.Decode(AdpcmCodec.Encode(frame));
    }

    // Deterministic speech-like signal: harmonics of 120 Hz shaped by three formants, 4 Hz syllable modulation.
    sealed class SpeechGen
    {
        readonly double[] amps;
        readonly double f0 = 120;
        readonly double scale;

        public SpeechGen(double rms)
        {
            int count = (int)(7000 / f0);
            amps = new double[count];
            for (int k = 1; k <= count; k++)
            {
                double f = k * f0;
                amps[k - 1] = Math.Exp(-Sq((f - 500) / 300)) + 0.6 * Math.Exp(-Sq((f - 1500) / 400))
                    + 0.3 * Math.Exp(-Sq((f - 2500) / 500)) + 0.04;
            }
            double sum = 0;
            for (int i = 0; i < 2 * Rate; i++) sum += Math.Pow(Raw(i), 2);
            scale = rms / Math.Sqrt(sum / (2 * Rate));
        }

        static double Sq(double x) { return x * x; }

        double Raw(long i)
        {
            double t = (double)i / Rate;
            double env = 0.1 + 0.9 * 0.5 * (1 + Math.Sin(2 * Math.PI * 4 * t));
            double v = 0;
            for (int k = 1; k <= amps.Length; k++) v += amps[k - 1] * Math.Sin(2 * Math.PI * k * f0 * t);
            return v * env;
        }

        public double At(long i) { return Raw(i) * scale; }
    }

    sealed class Noise
    {
        readonly Random rnd;
        readonly double rms;
        public Noise(int seed, double rms) { rnd = new Random(seed); this.rms = rms; }
        public double Next()
        {
            double s = 0;
            for (int i = 0; i < 12; i++) s += rnd.NextDouble();
            return (s - 6.0) * rms; // sum of 12 uniforms has unit variance
        }
    }

    static short Clip(double v)
    {
        return (short)Math.Max(-32768, Math.Min(32767, Math.Round(v)));
    }

    // ---- codec ----

    static void CodecTests()
    {
        Console.WriteLine("codec");
        short[] sine = Tone(440, 12000, 0, N);
        byte[] enc = AdpcmCodec.Encode(sine);
        Check(enc.Length == 4 + N / 2, "encoded frame length (" + enc.Length + ")");
        Check(Convert.ToBase64String(enc).Length == 1072, "frame is 1072 base64 chars");
        Check(("VOICE " + Convert.ToBase64String(enc)).Length < 1100, "whole message is far below the 2048 line limit");
        Check(enc[3] == 0 && enc[2] <= 88, "header: reserved byte 0, step index in range");
        Check((short)(enc[0] | (enc[1] << 8)) == sine[0], "header predictor is little-endian int16 of the first sample");
        short[] dec = AdpcmCodec.Decode(enc);
        Check(dec != null && dec.Length == sine.Length, "decoded length");
        if (dec != null)
        {
            double snr = SnrDb(sine, dec);
            Console.WriteLine("  440 Hz tone SNR " + snr.ToString("F1") + " dB");
            Check(snr > 25, "440 Hz SNR > 25 dB (" + snr.ToString("F1") + ")");
        }

        // speech-like at two levels
        SpeechGen speech = new SpeechGen(3000);
        short[] sp = new short[N];
        double worst = 999, best = 0;
        for (int f = 0; f < 10; f++)
        {
            for (int i = 0; i < N; i++) sp[i] = Clip(speech.At((long)f * N + i));
            short[] d = Roundtrip(sp);
            double snr = SnrDb(sp, d);
            worst = Math.Min(worst, snr);
            best = Math.Max(best, snr);
        }
        Console.WriteLine("  speech-like (RMS 3000) SNR " + worst.ToString("F1") + " .. " + best.ToString("F1") + " dB over 10 frames");
        Check(worst > 14, "speech-like SNR > 14 dB in every frame (" + worst.ToString("F1") + ")");

        SpeechGen quiet = new SpeechGen(150);
        for (int i = 0; i < N; i++) sp[i] = Clip(quiet.At(i + 4000));
        double quietSnr = SnrDb(sp, Roundtrip(sp));
        Console.WriteLine("  quiet speech-like (RMS 150) SNR " + quietSnr.ToString("F1") + " dB");

        // the band above 4 kHz must survive (this is what 16 kHz is for)
        double[] freqs = { 1000, 3000, 4500, 6000, 7000 };
        for (int k = 0; k < freqs.Length; k++)
        {
            short[] tone = Tone(freqs[k], 8000, 0, N);
            short[] td = Roundtrip(tone);
            double snr = SnrDb(tone, td);
            double kept = BinPower(td, freqs[k]) / BinPower(tone, freqs[k]);
            Console.WriteLine("  " + freqs[k] + " Hz tone: SNR " + snr.ToString("F1") + " dB, level kept " + (10 * Math.Log10(kept)).ToString("F2") + " dB");
            if (freqs[k] >= 4500)
            {
                Check(kept > 0.7 && kept < 1.3, freqs[k] + " Hz tone keeps its energy (" + kept.ToString("F2") + ")");
                Check(snr > 12, freqs[k] + " Hz tone SNR > 12 dB (" + snr.ToString("F1") + ")");
            }
        }

        // extremes must not overflow
        short[] sq = new short[400];
        for (int i = 0; i < sq.Length; i++) sq[i] = (i / 10 % 2 == 0) ? short.MaxValue : short.MinValue;
        short[] sqd = Roundtrip(sq);
        Check(sqd != null && sqd.Length == sq.Length, "full-scale square wave round trip");

        // frames are independent
        List<byte[]> encoded = new List<byte[]>();
        List<short[]> sequence = new List<short[]>();
        for (int f = 0; f < 3; f++)
        {
            byte[] e = AdpcmCodec.Encode(Tone(440, 9000, (long)f * N, N));
            encoded.Add(e);
            sequence.Add(AdpcmCodec.Decode(e));
        }
        short[] alone = AdpcmCodec.Decode((byte[])encoded[1].Clone());
        bool same = alone != null && alone.Length == sequence[1].Length;
        for (int i = 0; same && i < alone.Length; i++) if (alone[i] != sequence[1][i]) same = false;
        Check(same, "frame #2 decoded alone equals decoded in sequence");

        // Decode rejects malformed input
        Check(AdpcmCodec.Decode(null) == null, "null -> null");
        Check(AdpcmCodec.Decode(new byte[0]) == null, "empty -> null");
        Check(AdpcmCodec.Decode(new byte[3]) == null, "3 bytes -> null");
        Check(AdpcmCodec.Decode(new byte[4]) == null, "header only -> null");
        byte[] bad = (byte[])enc.Clone();
        bad[2] = 89;
        Check(AdpcmCodec.Decode(bad) == null, "step index 89 -> null");
        bad[2] = 88;
        Check(AdpcmCodec.Decode(bad) != null, "step index 88 is valid");
        bad[3] = 1;
        Check(AdpcmCodec.Decode(bad) == null, "non-zero reserved byte -> null");
        Check(AdpcmCodec.Decode(new byte[4 + 2001]) == null, "more than 4000 samples -> null");
        Check(AdpcmCodec.Decode(new byte[4 + 2000]) != null, "exactly 4000 samples is valid");

        bool threw = false;
        try { AdpcmCodec.Encode(new short[3]); } catch (ArgumentException) { threw = true; }
        Check(threw, "odd PCM length throws");
        threw = false;
        try { AdpcmCodec.Encode(new short[4002]); } catch (ArgumentException) { threw = true; }
        Check(threw, "too long PCM throws");

        Random fz = new Random(7);
        bool fuzzOk = true;
        for (int n = 0; n < 10000; n++)
        {
            byte[] b = new byte[fz.Next(0, 2100)];
            fz.NextBytes(b);
            if (b.Length > 4 && (n & 1) == 0) { b[2] = (byte)fz.Next(0, 89); b[3] = 0; }
            try
            {
                short[] r = AdpcmCodec.Decode(b);
                if (r != null && r.Length != (b.Length - 4) * 2) fuzzOk = false;
            }
            catch (Exception) { fuzzOk = false; }
        }
        Check(fuzzOk, "10000 random arrays: no exception, consistent length");
    }

    // ---- capture front end through the channel ----

    sealed class Scenario
    {
        public readonly FakeCapture Cap = new FakeCapture();
        public readonly VoiceChannel Ch;
        readonly SpeechGen speech;
        readonly Noise noise;
        readonly double dc;
        long index;

        public Scenario(double noiseRms, double speechRms, double dc)
        {
            Ch = new VoiceChannel(delegate { return Cap; }, delegate { return new FakePlayback(); }, false);
            speech = new SpeechGen(speechRms);
            noise = new Noise(5, noiseRms);
            this.dc = dc;
            string err;
            Ch.StartMic(out err);
        }

        // feeds n frames, with or without speech on top of the noise
        public void Feed(int n, bool talking)
        {
            for (int f = 0; f < n; f++)
            {
                short[] frame = new short[N];
                for (int i = 0; i < N; i++)
                {
                    double v = dc + noise.Next();
                    if (talking) v += speech.At(index);
                    frame[i] = Clip(v);
                    index++;
                }
                Cap.Feed(frame);
            }
        }
    }

    static List<short[]> DecodeAll(string[] payloads)
    {
        List<short[]> list = new List<short[]>();
        for (int i = 0; i < payloads.Length; i++) list.Add(AdpcmCodec.Decode(Convert.FromBase64String(payloads[i])));
        return list;
    }

    static void QuietMicTests()
    {
        Console.WriteLine("quiet microphone (noise RMS 10, speech RMS 120, small DC offset)");
        Scenario sc = new Scenario(10, 120, 100);
        sc.Feed(10, false);
        Check(sc.Ch.TakeOutgoing().Length == 0, "10 noise-only frames: nothing is sent (DC offset is blocked)");
        Check(!sc.Ch.Transmitting, "not transmitting on noise");
        Check(sc.Ch.MicLevel < 0.1f, "meter is low on room noise (" + sc.Ch.MicLevel.ToString("F2") + ")");

        float maxLevel = 0;
        bool sawTransmit = false;
        List<string> sent = new List<string>();
        for (int f = 0; f < 20; f++)
        {
            sc.Feed(1, true);
            maxLevel = Math.Max(maxLevel, sc.Ch.MicLevel);
            sawTransmit |= sc.Ch.Transmitting;
            sent.AddRange(sc.Ch.TakeOutgoing());
        }
        Check(sawTransmit && sc.Ch.Transmitting, "the gate opens on weak speech and stays open");
        Check(maxLevel > 0.25f, "meter moves on quiet speech (" + maxLevel.ToString("F2") + ")");

        int speechSent = sent.Count;
        // hangover: the gate stays open ~500 ms after the last speech
        List<string> tail = new List<string>();
        sc.Feed(1, false);
        Check(sc.Ch.Transmitting, "still open right after the speech");
        tail.AddRange(sc.Ch.TakeOutgoing());
        for (int f = 0; f < 5; f++)
        {
            sc.Feed(1, false);
            tail.AddRange(sc.Ch.TakeOutgoing());
        }
        Check(!sc.Ch.Transmitting, "gate closed after ~600 ms of noise");
        sent.AddRange(tail);
        sc.Feed(14, false);
        Check(sc.Ch.TakeOutgoing().Length == 0, "noise after the hangover produces no frames");

        Console.WriteLine("  frames sent: " + sent.Count + " (20 speech frames, " + speechSent + " sent while talking, " + tail.Count + " in the tail)");
        Check(sent.Count >= 23 && sent.Count <= 28, "plausible frame count: 1 pre-roll + 20 speech + ~5 hangover (" + sent.Count + ")");
        Check(tail.Count >= 3 && tail.Count <= 6, "hangover is ~5 frames (" + tail.Count + ")");

        List<short[]> frames = DecodeAll(sent.ToArray());
        bool allDecode = frames.Count == sent.Count;
        for (int i = 0; i < frames.Count; i++) if (frames[i] == null || frames[i].Length != N) allDecode = false;
        Check(allDecode, "all sent frames decode to 100 ms");
        if (allDecode && frames.Count > 12)
        {
            double r0 = Rms(frames[0]), r1 = Rms(frames[1]);
            Console.WriteLine("  first frame RMS " + r0.ToString("F0") + " (pre-roll, noise), second " + r1.ToString("F0") + " (speech)");
            Check(r0 < r1 * 0.4, "the first sent frame is the pre-roll before the speech");
            double sum = 0;
            int cnt = 0;
            for (int i = 14; i <= 19; i++) { sum += Math.Pow(Rms(frames[i]), 2); cnt++; }
            double steady = Math.Sqrt(sum / cnt);
            Console.WriteLine("  AGC output RMS after 1.4 s of speech: " + steady.ToString("F0") + " (target " + VoiceFrontEnd.TargetRms + ")");
            Check(steady > 2300 && steady < 3800, "AGC brings weak speech to roughly the target level (" + steady.ToString("F0") + ")");
            int peak = 0;
            for (int i = 0; i < frames.Count; i++) peak = Math.Max(peak, MaxAbs(frames[i]));
            Check(peak < 32000, "no clipping on the way (" + peak + ")");
        }
        sc.Ch.Dispose();

        // the measured real microphone: ambient RMS ~11, speech ~100
        Console.WriteLine("real-mic-like levels (noise RMS 11, speech RMS 100)");
        Scenario sc2 = new Scenario(11, 100, 0);
        sc2.Feed(10, false);
        Check(sc2.Ch.TakeOutgoing().Length == 0, "ambient only: nothing is sent");
        int got = 0;
        for (int f = 0; f < 20; f++) { sc2.Feed(1, true); got += sc2.Ch.TakeOutgoing().Length; }
        Check(got >= 19, "RMS 100 speech over RMS 11 noise is transmitted (" + got + " frames)");
        sc2.Feed(20, false);
        sc2.Ch.TakeOutgoing();
        sc2.Feed(10, false);
        Check(sc2.Ch.TakeOutgoing().Length == 0, "and the room is quiet again afterwards");
        sc2.Ch.Dispose();
    }

    static void LoudMicTests()
    {
        Console.WriteLine("loud speech (RMS 4000)");
        Scenario sc = new Scenario(10, 4000, 0);
        sc.Feed(5, false);
        List<string> sent = new List<string>();
        for (int f = 0; f < 15; f++) { sc.Feed(1, true); sent.AddRange(sc.Ch.TakeOutgoing()); }
        List<short[]> frames = DecodeAll(sent.ToArray());
        Check(frames.Count >= 15, "loud speech is transmitted (" + frames.Count + " frames)");
        int peak = 0, over = 0;
        double sum = 0;
        int cnt = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            peak = Math.Max(peak, MaxAbs(frames[i]));
            for (int j = 0; j < frames[i].Length; j++) if (Math.Abs((int)frames[i][j]) > 30000) over++;
            if (i >= 4) { sum += Math.Pow(Rms(frames[i]), 2); cnt++; }
        }
        double steady = cnt > 0 ? Math.Sqrt(sum / cnt) : 0;
        Console.WriteLine("  peak " + peak + ", samples above 30000: " + over + ", steady RMS " + steady.ToString("F0"));
        Check(peak < 32700, "never clipped (peak " + peak + ")");
        Check(over == 0, "no sample close to full scale (" + over + ")");
        Check(steady > 2000 && steady < 3800, "loud speech is not amplified beyond the target (" + steady.ToString("F0") + ")");
        Check(sc.Ch.MicLevel > 0.9f, "meter is almost full on loud speech");
        sc.Ch.Dispose();

        // pure front end: AGC must not pump up the background noise and must go back to a moderate gain
        Console.WriteLine("front end details");
        VoiceFrontEnd fe = new VoiceFrontEnd();
        Noise nz = new Noise(9, 11);
        SpeechGen sp = new SpeechGen(120);
        long idx = 0;
        short[] frame = new short[N];
        for (int f = 0; f < 100; f++)
        {
            for (int i = 0; i < N; i++) frame[i] = Clip(nz.Next());
            fe.Process(frame);
        }
        Check(fe.Gain <= VoiceFrontEnd.DefaultGain * 1.01f, "gain does not rise on background noise (" + fe.Gain.ToString("F1") + ")");
        Check(fe.NoiseFloor > 7 && fe.NoiseFloor < 15, "noise floor is tracked (" + fe.NoiseFloor.ToString("F1") + ")");
        for (int f = 0; f < 30; f++)
        {
            for (int i = 0; i < N; i++) frame[i] = Clip(nz.Next() + sp.At(idx++));
            fe.Process(frame);
        }
        float adapted = fe.Gain;
        Check(adapted > 15 && adapted <= 100, "gain rose for weak speech (" + adapted.ToString("F1") + ")");
        for (int f = 0; f < 400; f++)
        {
            for (int i = 0; i < N; i++) frame[i] = Clip(nz.Next());
            fe.Process(frame);
        }
        Check(fe.Gain < adapted * 0.5f && fe.Gain >= VoiceFrontEnd.DefaultGain * 0.9f,
            "after a long silence the gain returns towards the default (" + fe.Gain.ToString("F1") + ")");
    }

    // ---- channel basics with fakes ----

    static void ChannelTests()
    {
        Console.WriteLine("channel");
        FakeCapture cap = new FakeCapture();
        VoiceChannel ch = new VoiceChannel(delegate { return cap; }, delegate { return new FakePlayback(); }, false);
        Check(!ch.MicOpen && !ch.Transmitting && ch.MicLevel == 0f, "closed channel is idle");
        string err;
        Check(ch.StartMic(out err), "StartMic ok");
        Check(ch.MicOpen && cap.Starts == 1, "mic open, Start called once");
        Check(ch.StartMic(out err) && cap.Starts == 1, "second StartMic is a no-op");

        for (int i = 0; i < 10; i++) cap.Feed(new short[N]);
        Check(ch.TakeOutgoing().Length == 0, "digital silence is never sent");
        cap.Feed(new short[100]);
        cap.Feed(null);
        Check(ch.TakeOutgoing().Length == 0, "wrong-sized / null frames are ignored");

        // outgoing queue cap
        for (int i = 0; i < 60; i++) cap.Feed(Tone(300, 3000 + i * 50, (long)i * N, N));
        string[] capped = ch.TakeOutgoing();
        Check(capped.Length == 25, "outgoing queue capped at 25 (" + capped.Length + ")");
        Check(ch.TakeOutgoing().Length == 0, "queue empty after drain");
        bool sizeOk = capped.Length > 0;
        for (int i = 0; i < capped.Length; i++) if (capped[i].Length != 1072) sizeOk = false;
        Check(sizeOk, "every frame is 1072 base64 chars");

        cap.Feed(Tone(300, 8000, 0, N));
        Check(ch.Transmitting && ch.MicLevel > 0f, "transmitting before StopMic");
        ch.StopMic();
        Check(cap.Stops >= 1 && cap.Disposes == 1, "StopMic stops and disposes the capture");
        Check(!ch.MicOpen && !ch.Transmitting && ch.MicLevel == 0f, "after StopMic: closed, silent meter");
        cap.Feed(Tone(300, 8000, 0, N));
        Check(!ch.Transmitting, "frames after StopMic are ignored");
        ch.StopMic();
        Check(cap.Disposes == 1, "second StopMic does nothing");
        ch.Dispose();

        // start failures
        FakeCapture bad = new FakeCapture();
        bad.StartResult = false;
        bad.StartError = "Микрофон занят.";
        VoiceChannel chBad = new VoiceChannel(delegate { return bad; }, delegate { return new FakePlayback(); }, false);
        Check(!chBad.StartMic(out err), "StartMic reports failure");
        Check(err == "Микрофон занят.", "error text is passed through (" + err + ")");
        Check(!chBad.MicOpen && bad.Disposes == 1, "mic not open, failed capture disposed");
        bad.StartError = null;
        Check(!chBad.StartMic(out err) && !string.IsNullOrEmpty(err), "failure without text still yields an error text");

        bool failFirst = true;
        FakeCapture good = new FakeCapture();
        VoiceChannel ch2 = new VoiceChannel(
            delegate { if (failFirst) { failFirst = false; throw new InvalidOperationException("boom"); } return good; },
            delegate { return new FakePlayback(); }, false);
        Check(!ch2.StartMic(out err) && !string.IsNullOrEmpty(err), "throwing factory -> false + text");
        Check(ch2.StartMic(out err) && ch2.MicOpen, "retry after a failure works");
        ch2.Dispose();
        chBad.Dispose();
        Check(!ch2.StartMic(out err) && !string.IsNullOrEmpty(err), "StartMic after Dispose fails");
    }

    // ---- sender-level behaviour ----

    static void RateLimitTests()
    {
        Console.WriteLine("rate limit");
        VoiceSender s = new VoiceSender();
        int accepted = 0;
        for (int i = 0; i < 40; i++) if (s.AcceptRate(5000 + i)) accepted++;
        Check(accepted == 16, "16 frames per second accepted from a burst of 40 (" + accepted + ")");
        Check(!s.AcceptRate(5900), "still limited inside the window");
        Check(s.AcceptRate(6100), "accepting again after the window passed");

        Rig rig = new Rig();
        short[] frame = Tone(440, 5000, 0, N);
        string p = Payload(frame);
        for (int i = 0; i < 40; i++) rig.Channel.Receive("A", p);
        rig.Steps(50);
        int total = rig.Playback.TotalSamples;
        // 10 frames survive the 1 s queue limit; the rest is dropped as the oldest audio
        Console.WriteLine("  played samples from a burst of 40: " + total);
        Check(total >= 3200 && total <= 10 * N + N + 400, "burst is bounded by the queue limit (" + total + ")");

        Rig rig2 = new Rig();
        for (int i = 0; i < 40; i++) { rig2.Channel.Receive("A", p); rig2.Steps(2); }
        rig2.Steps(8);
        int played = rig2.Playback.TotalSamples;
        int frames = (played - N) / N; // two concealment chunks of 800 samples follow the last frame
        Check(frames == 16, "channel accepted 16 of 40 frames in a burst (" + frames + ")");

        // limits are per sender
        Rig rig3 = new Rig();
        for (int i = 0; i < 20; i++) { rig3.Channel.Receive("A", p); rig3.Channel.Receive("B", p); }
        Check(rig3.Channel.SenderCount == 2, "two independent senders");
        rig.Channel.Dispose(); rig2.Channel.Dispose(); rig3.Channel.Dispose();
    }

    static void GarbageTests()
    {
        Console.WriteLine("garbage input");
        List<FakePlayback> made = new List<FakePlayback>();
        VoiceChannel ch = new VoiceChannel(delegate { return new FakeCapture(); },
            delegate { FakePlayback p = new FakePlayback(); made.Add(p); return p; }, false);
        string valid = Payload(Tone(440, 5000, 0, N));
        string[] garbage = { null, "", "not base64!!!", "AAAA", "AAAAAAAA", new string('A', 5000), "====", "QUJD",
            Payload(Tone(440, 5000, 0, 1000)), valid.Substring(4), valid + "AAAA" };
        for (int i = 0; i < garbage.Length; i++) ch.Receive("A", garbage[i]);
        ch.Receive(null, valid);
        ch.Receive("", valid);
        ch.Receive(new string('x', 500), valid);
        Check(made.Count == 0, "garbage never creates the playback");
        Check(ch.SenderCount == 0 && !ch.AnyoneSpeaking, "garbage creates no sender and nobody 'speaks'");
        ch.SetSenderMuted(null, true);
        ch.SetSenderMuted("", true);
        ch.ForgetSender(null);
        ch.ForgetSender("nobody");
        Check(!ch.IsSpeaking(null) && !ch.IsSpeaking(""), "null/empty ids are never speaking");
        ch.Receive("A", valid);
        Check(made.Count == 1 && made[0].Starts == 1, "a valid frame starts the playback lazily");
        ch.Dispose();
    }

    static void SpeakingTests()
    {
        Console.WriteLine("speaking indicators");
        Rig rig = new Rig();
        string p = Payload(Tone(440, 5000, 0, N));
        Check(!rig.Channel.AnyoneSpeaking && rig.Channel.SpeakingIds().Length == 0, "silent at the start");
        rig.Channel.Receive("alice", p);
        Check(rig.Channel.IsSpeaking("alice") && !rig.Channel.IsSpeaking("bob"), "alice speaks right after her frame");
        Check(rig.Channel.AnyoneSpeaking, "AnyoneSpeaking");
        rig.Channel.Receive("bob", p);
        string[] ids = rig.Channel.SpeakingIds();
        Array.Sort(ids);
        Check(ids.Length == 2 && ids[0] == "alice" && ids[1] == "bob", "SpeakingIds lists both");
        Thread.Sleep(300);
        Check(rig.Channel.IsSpeaking("alice"), "still speaking after 300 ms");
        Thread.Sleep(350);
        Check(!rig.Channel.IsSpeaking("alice") && !rig.Channel.AnyoneSpeaking && rig.Channel.SpeakingIds().Length == 0,
            "not speaking ~650 ms after the last frame");

        // forget / mute
        rig.Channel.Receive("alice", p);
        rig.Channel.Receive("bob", p);
        Check(rig.Channel.SenderCount == 2, "two senders known");
        rig.Channel.ForgetSender("alice");
        Check(rig.Channel.SenderCount == 1 && !rig.Channel.IsSpeaking("alice") && rig.Channel.IsSpeaking("bob"),
            "ForgetSender frees one sender only");
        rig.Channel.SetSenderMuted("bob", true);
        Check(rig.Channel.SenderCount == 0 && !rig.Channel.IsSpeaking("bob"), "muting a sender drops its state");
        rig.Channel.Receive("bob", p);
        Check(rig.Channel.SenderCount == 0 && !rig.Channel.IsSpeaking("bob"), "muted sender is ignored");
        rig.Channel.ForgetSender("bob");
        rig.Channel.Receive("bob", p);
        Check(rig.Channel.SenderCount == 0, "a forgotten sender stays muted");
        rig.Channel.SetSenderMuted("bob", false);
        rig.Channel.Receive("bob", p);
        Check(rig.Channel.IsSpeaking("bob"), "unmuted sender is heard again");
        rig.Channel.Dispose();

        // senders are capped
        Rig rig2 = new Rig();
        for (int i = 0; i < 100; i++) rig2.Channel.Receive("s" + i, p);
        Check(rig2.Channel.SenderCount <= 32, "number of senders is bounded (" + rig2.Channel.SenderCount + ")");
        rig2.Channel.Dispose();
    }

    // ---- mixing ----

    static short[][] ToneFrames(double freq, double amp, int count, bool decoded)
    {
        short[][] frames = new short[count][];
        for (int f = 0; f < count; f++)
        {
            short[] t = Tone(freq, amp, (long)f * N, N);
            frames[f] = decoded ? Roundtrip(t) : t;
        }
        return frames;
    }

    static void MixTests()
    {
        Console.WriteLine("mixing");
        const int Frames = 6;
        short[][] a = ToneFrames(440, 4000, Frames, false);
        short[][] b = ToneFrames(700, 3000, Frames, false);
        short[][] da = ToneFrames(440, 4000, Frames, true);
        short[][] db = ToneFrames(700, 3000, Frames, true);

        Rig rig = new Rig();
        for (int f = 0; f < Frames; f++)
        {
            rig.Channel.Receive("A", Payload(a[f]));
            rig.Channel.Receive("B", Payload(b[f]));
        }
        rig.Steps(2 * Frames + 4);
        short[] outp = rig.Playback.All();
        Check(outp.Length == (2 * Frames + 2) * 800, "frames + two concealment chunks were played (" + outp.Length + " samples)");
        double worst = 0;
        for (int f = 0; f < Frames; f++)
            for (int i = 0; i < N; i++)
            {
                int pos = f * N + i;
                if (pos < 200) continue; // fade-in region
                worst = Math.Max(worst, Math.Abs(outp[pos] - (da[f][i] + db[f][i])));
            }
        Check(worst <= 1.5, "two senders: output equals the sum of both (max deviation " + worst.ToString("F1") + ")");
        Check(Math.Abs(outp[0]) < 5, "stream starts from silence (fade-in)");
        Check(MaxStep(outp) < 2600, "mixed stream has no jumps (max step " + MaxStep(outp) + ")");
        Check(Math.Abs((int)outp[outp.Length - 1]) < 40, "stream ends at silence");
        rig.Channel.Dispose();

        // a muted sender contributes nothing
        Rig rigM = new Rig();
        rigM.Channel.SetSenderMuted("B", true);
        for (int f = 0; f < Frames; f++)
        {
            rigM.Channel.Receive("A", Payload(a[f]));
            rigM.Channel.Receive("B", Payload(b[f]));
        }
        rigM.Steps(2 * Frames);
        short[] om = rigM.Playback.All();
        worst = 0;
        for (int f = 0; f < Frames; f++)
            for (int i = 0; i < N; i++)
            {
                int pos = f * N + i;
                if (pos < 200 || pos >= om.Length) continue;
                worst = Math.Max(worst, Math.Abs(om[pos] - da[f][i]));
            }
        Check(om.Length >= Frames * N && worst <= 1.5, "muted sender: only the other one is heard (" + worst.ToString("F1") + ")");
        rigM.Channel.Dispose();

        // soft clipping
        Rig rigC = new Rig();
        short[][] loud = ToneFrames(440, 20000, Frames, false);
        for (int f = 0; f < Frames; f++)
        {
            rigC.Channel.Receive("A", Payload(loud[f]));
            rigC.Channel.Receive("B", Payload(loud[f]));
        }
        rigC.Steps(2 * Frames);
        short[] oc = rigC.Playback.All();
        int peak = MaxAbs(oc);
        int railed = 0;
        for (int i = 0; i < oc.Length; i++) if (Math.Abs((int)oc[i]) >= 32700) railed++;
        Console.WriteLine("  two 20000-peak senders: output peak " + peak);
        Check(peak > 26000 && peak < 32700, "sum of 40000 is compressed softly, not clipped (peak " + peak + ")");
        Check(railed == 0, "no flat-topped samples");
        int rawStep = 0;
        for (int f = 0; f < Frames; f++)
        {
            short[] dl = Roundtrip(loud[f]);
            for (int i = 1; i < dl.Length; i++) rawStep = Math.Max(rawStep, Math.Abs(2 * dl[i] - 2 * dl[i - 1]));
        }
        Check(MaxStep(oc) <= rawStep, "limiting never makes a step bigger than the plain sum (" + MaxStep(oc) + " <= " + rawStep + ")");
        rigC.Channel.Dispose();
    }

    static void ContinuityTests()
    {
        Console.WriteLine("fades and concealment");
        const double Freq = 450, Amp = 8000;
        int natural = (int)(2 * Math.PI * Freq / Rate * Amp);
        short[] frame = Tone(Freq, Amp, 0, N);
        string p = Payload(frame);
        double toneRms = Rms(Roundtrip(frame));

        // a plain talk spurt: fade in, play, fade out
        Rig rig = new Rig();
        for (int f = 0; f < 5; f++) rig.Channel.Receive("A", p);
        rig.Steps(10 + 2);
        int chunksAfterTail = rig.Playback.Count;
        rig.Steps(4);
        Check(rig.Playback.Count == chunksAfterTail, "nothing is played once the fade-out is over");
        short[] o = rig.Playback.All();
        Check(o.Length == 12 * 800, "5 frames + 2 fade-out chunks (" + o.Length + ")");
        Check(Math.Abs((int)o[0]) < 5, "spurt starts at zero (no click)");
        Check(Math.Abs((int)o[o.Length - 1]) < 40, "spurt ends near zero (no click)");
        Console.WriteLine("  tone max step " + MaxStep(o) + " (natural " + natural + ")");
        Check(MaxStep(o) <= natural + 700, "no jump larger than the tone's own slope (" + MaxStep(o) + ")");
        // the last 100 ms of real audio is followed by a fade of the repeated waveform
        double r1 = Rms(o, 5 * N, 800), r2 = Rms(o, 5 * N + 800, 800);
        Check(r1 > toneRms * 0.45 && r2 < r1 * 0.7, "fade-out continues the waveform and dies away (" + r1.ToString("F0") + " -> " + r2.ToString("F0") + ")");
        rig.Channel.Dispose();

        // a missing middle frame
        Rig gap = new Rig();
        gap.Channel.Receive("A", p);
        gap.Channel.Receive("A", p);
        gap.Steps(4);                       // both frames played
        gap.Steps(1);                       // frame 3 is missing: concealment
        gap.Channel.Receive("A", p);        // frame 4 arrives late, followed by frame 5
        gap.Channel.Receive("A", p);
        gap.Steps(4);
        gap.Steps(3);                       // fade-out
        short[] g = gap.Playback.All();
        short[] concealed = new short[800];
        Array.Copy(g, 4 * 800, concealed, 0, 800);
        double concRms = Rms(concealed);
        Console.WriteLine("  concealed chunk RMS " + concRms.ToString("F0") + " (tone " + toneRms.ToString("F0") + "), max step " + MaxStep(g));
        Check(concRms > toneRms * 0.45, "the missing frame is concealed, not silent");
        Check(MaxStep(g) <= natural + 700, "no click at the concealment seams (" + MaxStep(g) + ")");
        Check(g.Length == 12 * 800 || g.Length == 11 * 800 || g.Length == 10 * 800 + 800, "stream length is plausible (" + g.Length + ")");
        gap.Channel.Dispose();

        // the same with speech-like material (no clean periodicity to hide behind)
        SpeechGen speech = new SpeechGen(3000);
        short[][] sf = new short[6][];
        string[] sp = new string[6];
        int srcStep = 0;
        for (int f = 0; f < 6; f++)
        {
            sf[f] = new short[N];
            for (int i = 0; i < N; i++) sf[f][i] = Clip(speech.At((long)f * N + i));
            sp[f] = Payload(sf[f]);
            srcStep = Math.Max(srcStep, MaxStep(Roundtrip(sf[f])));
        }
        Rig sg = new Rig();
        sg.Channel.Receive("A", sp[0]);
        sg.Channel.Receive("A", sp[1]);
        sg.Steps(5);
        sg.Channel.Receive("A", sp[3]);
        sg.Channel.Receive("A", sp[4]);
        sg.Steps(8);
        short[] so = sg.Playback.All();
        Console.WriteLine("  speech-like: stream max step " + MaxStep(so) + ", source max step " + srcStep);
        Check(MaxStep(so) <= srcStep * 1.3 + 200, "speech-like stream with a hole: no click (" + MaxStep(so) + " vs " + srcStep + ")");
        sg.Channel.Dispose();
    }

    static void MuteAndDisposeTests()
    {
        Console.WriteLine("speaker mute, dispose");
        List<FakePlayback> made = new List<FakePlayback>();
        VoiceChannel ch = new VoiceChannel(delegate { return new FakeCapture(); },
            delegate { FakePlayback p = new FakePlayback(); made.Add(p); return p; }, false);
        string payload = Payload(Tone(440, 5000, 0, N));

        ch.SpeakerMuted = true;
        Check(ch.SpeakerMuted, "SpeakerMuted getter");
        for (int i = 0; i < 5; i++) ch.Receive("A", payload);
        Check(made.Count == 0, "muted from the start: playback never created");
        Check(!ch.AnyoneSpeaking && ch.SenderCount == 0, "muted: nobody counts as speaking");
        ch.SpeakerMuted = false;
        ch.Receive("A", payload);
        Check(made.Count == 1 && made[0].Starts == 1, "unmuted: playback is created");
        ch.SpeakerMuted = true;
        Check(made[0].Stops >= 1 && made[0].Disposes == 1, "muting releases the playback device");
        Check(ch.SenderCount == 0, "muting drops buffered audio");
        ch.MixStep();
        Check(made[0].Count == 0, "nothing reaches the released device");
        ch.SpeakerMuted = false;
        ch.Receive("A", payload);
        Check(made.Count == 2, "a fresh playback after unmuting");
        ch.Dispose();
        Check(made[1].Disposes == 1, "Dispose disposes the playback");
        ch.Receive("A", payload);
        Check(made.Count == 2, "Receive after Dispose does nothing");

        // a playback that cannot start is not retried on every frame
        int factoryCalls = 0;
        VoiceChannel ch3 = new VoiceChannel(delegate { return new FakeCapture(); },
            delegate { factoryCalls++; throw new InvalidOperationException("no device"); }, false);
        for (int i = 0; i < 5; i++) ch3.Receive("A", payload);
        Check(factoryCalls == 1, "failing playback factory is retried only after a pause (" + factoryCalls + ")");
        Check(ch3.IsSpeaking("A"), "frames still count as received when the device fails");
        ch3.MixStep();
        ch3.Dispose();

        // dispose: prompt, idempotent, everything released once
        FakeCapture cap = new FakeCapture();
        FakePlayback pb = new FakePlayback();
        VoiceChannel d = new VoiceChannel(delegate { return cap; }, delegate { return pb; });
        string err;
        d.StartMic(out err);
        d.Receive("A", payload);
        d.Receive("A", payload);
        Thread.Sleep(250);
        Check(pb.Count > 0, "the background mixer feeds the playback (" + pb.Count + " chunks)");
        Check(d.MixerAlive, "mixer thread is running");
        Stopwatch sw = Stopwatch.StartNew();
        d.Dispose();
        d.Dispose();
        d.Dispose();
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 1000, "Dispose returns quickly (" + sw.ElapsedMilliseconds + " ms)");
        Check(!d.MixerAlive, "mixer thread is gone after Dispose");
        Check(cap.Disposes == 1 && pb.Disposes == 1, "both devices disposed exactly once");
        int before = pb.Count;
        Thread.Sleep(150);
        Check(pb.Count == before, "nothing is enqueued after Dispose");
        Check(!d.MicOpen && !d.Transmitting && d.MicLevel == 0f && !d.AnyoneSpeaking, "idle after Dispose");
        Check(d.TakeOutgoing().Length == 0, "TakeOutgoing after Dispose is empty");

        // the auto mixer plays a spurt to its end
        FakePlayback pb2 = new FakePlayback();
        VoiceChannel auto = new VoiceChannel(delegate { return new FakeCapture(); }, delegate { return pb2; });
        for (int i = 0; i < 4; i++) auto.Receive("A", payload);
        for (int i = 0; i < 60 && pb2.TotalSamples < 4 * N + N; i++) Thread.Sleep(50);
        Check(pb2.TotalSamples == 4 * N + N, "timer-driven mixer plays 4 frames and a fade-out (" + pb2.TotalSamples + " samples)");
        auto.Dispose();

        // never started anything / real classes that were never started
        new VoiceChannel(delegate { return new FakeCapture(); }, delegate { return new FakePlayback(); }).Dispose();
        VoiceChannel real = new VoiceChannel();
        real.Dispose();
        WaveInCapture wi = new WaveInCapture();
        wi.Stop();
        wi.Dispose();
        string e2;
        Check(!wi.Start(out e2) && !string.IsNullOrEmpty(e2), "WaveInCapture refuses to start after Stop (no device is touched)");
        Check(!wi.WorkerAlive, "no capture worker thread exists");
    }

    static void ConcurrencyTests()
    {
        Console.WriteLine("threads");
        FakeCapture cap = new FakeCapture();
        FakePlayback pb = new FakePlayback();
        VoiceChannel ch = new VoiceChannel(delegate { return cap; }, delegate { return pb; });
        string err;
        ch.StartMic(out err);
        bool fault = false;
        Thread feeder = new Thread(delegate()
        {
            try { for (int i = 0; i < 80; i++) cap.Feed(Tone(300, i % 3 == 0 ? 8000 : 50, (long)i * N, N)); }
            catch (Exception) { fault = true; }
        });
        Thread receiver = new Thread(delegate()
        {
            try
            {
                string p = Payload(Tone(500, 4000, 0, N));
                for (int i = 0; i < 40; i++) { ch.Receive("r" + (i % 3), p); Thread.Sleep(5); }
            }
            catch (Exception) { fault = true; }
        });
        feeder.IsBackground = true;
        receiver.IsBackground = true;
        feeder.Start();
        receiver.Start();
        int taken = 0;
        while (feeder.IsAlive || receiver.IsAlive)
        {
            taken += ch.TakeOutgoing().Length;
            float l = ch.MicLevel;
            bool t = ch.Transmitting;
            bool s = ch.AnyoneSpeaking;
            ch.SpeakingIds();
            if (l < 0f || l > 1f) fault = true;
            Thread.Sleep(1);
        }
        taken += ch.TakeOutgoing().Length;
        Check(!fault, "concurrent capture / receive / polling without exceptions");
        Check(taken > 0, "frames were produced under concurrency");
        Stopwatch sw = Stopwatch.StartNew();
        ch.Dispose();
        Check(sw.ElapsedMilliseconds < 1000, "Dispose is prompt under load");
    }

    // ---- real playback, silence only ----

    static void RealPlaybackSmoke()
    {
        Console.WriteLine("real playback (silence)");
        if (!VoiceChannel.SpeakerAvailable)
        {
            Console.WriteLine("  SKIPPED: no audio output device");
            return;
        }

        bool completed = false;
        bool skipped = false;
        bool alive = true;
        Exception error = null;
        Thread t = new Thread(delegate()
        {
            try
            {
                WaveOutPlayback pb = new WaveOutPlayback();
                string e;
                if (!pb.Start(out e))
                {
                    Console.WriteLine("  SKIPPED: device could not be opened (" + e + ")");
                    skipped = true;
                    pb.Dispose();
                    return;
                }
                for (int i = 0; i < 20; i++) pb.Enqueue(new short[Rate / 20]); // 1 s of zeros in 50 ms pieces
                pb.Enqueue(null);
                pb.Enqueue(new short[0]);
                Thread.Sleep(1300);
                pb.Stop();
                pb.Dispose();
                pb.Enqueue(new short[100]); // after close: ignored, not an error
                pb.Stop();
                for (int i = 0; i < 50 && pb.WorkerAlive; i++) Thread.Sleep(20);
                alive = pb.WorkerAlive;

                // and the whole receive path: encoded silence from two senders through the real mixer
                VoiceChannel ch = new VoiceChannel();
                string silence = Payload(new short[N]);
                for (int i = 0; i < 6; i++) { ch.Receive("x", silence); ch.Receive("y", silence); Thread.Sleep(100); }
                Thread.Sleep(300);
                ch.Dispose();
                completed = true;
            }
            catch (Exception ex) { error = ex; }
        });
        t.IsBackground = true;
        Stopwatch sw = Stopwatch.StartNew();
        t.Start();
        bool finished = t.Join(5000);
        Check(finished, "playback smoke test finished within 5 s (" + sw.ElapsedMilliseconds + " ms)");
        Check(error == null, "no exception from real playback" + (error == null ? "" : ": " + error));
        if (finished && !skipped)
        {
            Check(completed, "playback smoke test ran to the end");
            Check(!alive, "playback worker thread exited after Dispose");
        }
    }

    static int Main()
    {
        CodecTests();
        QuietMicTests();
        LoudMicTests();
        ChannelTests();
        RateLimitTests();
        GarbageTests();
        SpeakingTests();
        MixTests();
        ContinuityTests();
        MuteAndDisposeTests();
        ConcurrencyTests();
        RealPlaybackSmoke();

        if (failures == 0) { Console.WriteLine("ALL VOICE TESTS PASSED"); return 0; }
        Console.WriteLine(failures + " VOICE CHECK(S) FAILED");
        return 1;
    }
}
