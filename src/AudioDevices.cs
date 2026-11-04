using System;

namespace ChessGame
{
    internal interface IAudioCapture : IDisposable
    {
        // Raised on a worker thread, always with exactly FrameSamples samples.
        event Action<short[]> FrameCaptured;

        // True once the device stopped by itself (unplugged, driver error). A failed device never recovers:
        // the owner drops it and opens a new one.
        bool Failed { get; }

        bool Start(out string error);

        // May return before the device is released; a device opened afterwards waits for that (DeviceCloseGate).
        void Stop();
    }

    internal interface IAudioPlayback : IDisposable
    {
        // True once the device stopped by itself (unplugged, driver error); see IAudioCapture.Failed.
        bool Failed { get; }

        bool Start(out string error);

        // Takes ownership of pcm: the caller must not touch the array afterwards, the playback does not copy it.
        void Enqueue(short[] pcm);

        // May return before the device is released; a device opened afterwards waits for that (DeviceCloseGate).
        void Stop();
    }

    // The texts shown to the user when an audio device cannot be used.
    static class VoiceErrors
    {
        const string MicOpenFailedStem = "Не удалось открыть микрофон";
        const string SpeakerOpenFailedStem = "Не удалось открыть устройство вывода звука";

        public const string MicOpenFailed = MicOpenFailedStem + ".";
        public const string SpeakerOpenFailed = SpeakerOpenFailedStem + ".";
        public const string MicNotFound = "Микрофон не найден.";
        public const string SpeakerNotFound = "Устройство вывода звука не найдено.";
        public const string MicBusy = "Микрофон занят другой программой.";
        public const string SpeakerBusy = "Устройство вывода звука занято другой программой.";
        public const string BadFormat = "Звуковое устройство не поддерживает нужный формат.";
        public const string MicClosed = "Микрофон уже закрыт.";
        public const string SpeakerClosed = "Вывод звука уже закрыт.";
        public const string ChannelClosed = "Голосовой чат закрыт.";

        // Short Russian texts for the most common open failures (winmm result codes).
        public static string Describe(int code, bool capture)
        {
            switch (code)
            {
                case 2: // MMSYSERR_BADDEVICEID
                case 6: // MMSYSERR_NODRIVER
                    return capture ? MicNotFound : SpeakerNotFound;
                case 4: // MMSYSERR_ALLOCATED
                    return capture ? MicBusy : SpeakerBusy;
                case 32: // WAVERR_BADFORMAT
                    return BadFormat;
                default:
                    return (capture ? MicOpenFailedStem : SpeakerOpenFailedStem) + " (код " + code + ").";
            }
        }
    }
}
