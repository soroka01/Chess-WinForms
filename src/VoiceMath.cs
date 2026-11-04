using System;
using System.Diagnostics;

namespace ChessGame
{
    // One monotonic millisecond clock for all voice code.
    static class VoiceClock
    {
        static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static long Ms { get { return Watch.ElapsedMilliseconds; } }
    }

    static class VoiceMath
    {
        const float LimiterStart = 24000f;
        const float LimiterCeiling = 32700f;

        public static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

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
    }
}
