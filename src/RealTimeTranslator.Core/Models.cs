using System;

namespace RealTimeTranslator.Core
{
    public class SubtitleItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string OriginalText { get; set; }
        public string TranslatedText { get; set; }
        public bool IsFinal { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public interface IAudioCaptureService
    {
        void StartCapture(int processId);
        void StopCapture();
        event EventHandler<byte[]> AudioDataAvailable;
    }

    public interface IASRService
    {
        Task<string> TranscribeAsync(byte[] audioData, bool highAccuracy);
    }

    public interface ITranslationService
    {
        Task<string> TranslateAsync(string text, string targetLanguage = "ja");
    }
}
