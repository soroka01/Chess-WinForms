// Fake audio devices for the tests: nothing here ever touches a real microphone or speaker.
using System;
using System.Collections.Generic;
using ChessGame;

sealed class FakeCapture : IAudioCapture
{
    public bool Started;
    public bool Disposed;

    public event Action<short[]> FrameCaptured;

    public bool Start(out string error) { error = null; Started = true; return true; }
    public void Stop() { Started = false; }
    public void Dispose() { Started = false; Disposed = true; }

    /// <summary>Pretends the microphone delivered one frame.</summary>
    public void Feed(short[] pcm)
    {
        Action<short[]> handler = FrameCaptured;
        if (handler != null) handler(pcm);
    }

    public static short[] Loud()
    {
        short[] f = new short[VoiceFormat.FrameSamples];
        for (int i = 0; i < f.Length; i++) f[i] = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / VoiceFormat.SampleRate));
        return f;
    }

    public static short[] Silence()
    {
        return new short[VoiceFormat.FrameSamples];
    }
}

sealed class FakePlayback : IAudioPlayback
{
    public readonly List<short[]> Frames = new List<short[]>();
    public bool Started;
    public bool Disposed;

    public bool Start(out string error) { error = null; Started = true; return true; }
    public void Stop() { Started = false; }
    public void Dispose() { Started = false; Disposed = true; }

    public void Enqueue(short[] pcm)
    {
        lock (Frames) Frames.Add(pcm);
    }

    public int Count { get { lock (Frames) return Frames.Count; } }
}
