using System.Threading.Tasks;
using RealTimeTranslator.Core;

namespace RealTimeTranslator.Translation
{
    public class LocalTranslationService : ITranslationService
    {
        public async Task<string> TranslateAsync(string text, string targetLanguage = "ja")
        {
            // TODO: ローカル翻訳エンジン（ArgosTranslate等）の実装
            await Task.Delay(50); // シミュレーション
            return $"[翻訳]: {text}";
        }
    }
}
