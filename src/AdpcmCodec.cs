using System;

namespace ChessGame
{
    // IMA ADPCM with a self-contained header per frame, so a lost frame never breaks the next one.
    internal static class AdpcmCodec
    {
        public const int MaxSamples = 4000;
        // 16-bit predictor, step index, one reserved byte that stays 0
        public const int HeaderSize = 4;

        static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88,
            97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660,
            4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818,
            18500, 20350, 22385, 24623, 27086, 29794, 32767
        };
        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };

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
            data[2] = (byte)index; // data[3], the reserved byte, stays 0

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

                pred = VoiceMath.Clamp(pred + ((code & 8) != 0 ? -vpdiff : vpdiff), short.MinValue, short.MaxValue);
                index = VoiceMath.Clamp(index + IndexTable[code & 7], 0, 88);

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
                    pred = VoiceMath.Clamp(pred + ((code & 8) != 0 ? -vpdiff : vpdiff), short.MinValue, short.MaxValue);
                    index = VoiceMath.Clamp(index + IndexTable[code & 7], 0, 88);
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
}
