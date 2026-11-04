// The one audio format of the voice chat: 16 kHz, mono, 16-bit.
namespace ChessGame
{
    internal static class VoiceFormat
    {
        // 16 kHz keeps the 4-8 kHz band that makes speech sound natural; ADPCM at 4 bits costs 64 kbps
        public const int SampleRate = 16000;
        // 100 ms of mono 16-bit audio: 804 bytes of ADPCM, 1072 base64 characters
        public const int FrameSamples = 1600;
    }
}
