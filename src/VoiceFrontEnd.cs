using System;
using System.Collections.Generic;

namespace ChessGame
{
    // DC blocker -> voice activity detection -> AGC -> pre-roll. The detector looks at the signal BEFORE the AGC,
    // otherwise the gain would amplify the room noise until it looks like speech.
    // Not thread safe: only the capture thread of one session calls Process.
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
        float gain = DefaultGain, lastGain = DefaultGain;
        float speechPower;
        int silentBlocks;
        short[] prev;
        bool prevSent;

        public bool GateOpen { get { return open; } }
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
                    float desired = VoiceMath.Clamp(TargetRms / Math.Max((float)Math.Sqrt(speechPower), 1f), MinGain, MaxGain);
                    g = desired < g ? desired : Math.Min(desired, g * RiseStep);
                    // a sudden shout must not wait for the average to catch up
                    g = Math.Min(g, VoiceMath.Clamp(PeakRms / Math.Max(e, 1f), MinGain, MaxGain));
                }
                else if (candidate)
                {
                    // speech is starting but the gate is still counting: just protect the ears
                    silentBlocks = 0;
                    float desired = VoiceMath.Clamp(TargetRms / e, MinGain, MaxGain);
                    if (desired < g) g = desired;
                }
                else
                {
                    silentBlocks++;
                    // after a long pause the gain slowly goes back to a moderate value (never while the gate is open)
                    if (!open && silentBlocks > RelaxAfterBlocks)
                        g = VoiceMath.Clamp(g * (float)Math.Pow(DefaultGain / g, 0.005), MinGain, MaxGain);
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
    }
}
