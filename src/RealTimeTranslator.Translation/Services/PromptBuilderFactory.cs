using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Translation.Interfaces;
using RealTimeTranslator.Translation.Prompts;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// プロンプトビルダーのファクトリクラス
/// 翻訳モデルタイプに応じて適切なIPromptBuilder実装を返す
/// </summary>
public class PromptBuilderFactory
{
    /// <summary>
    /// モデルタイプに応じた適切なプロンプトビルダーを取得
    /// </summary>
    /// <param name="modelType">翻訳モデルタイプ</param>
    /// <returns>対応するIPromptBuilder実装</returns>
    public IPromptBuilder GetBuilder(TranslationModelType modelType)
    {
        return modelType switch
        {
            TranslationModelType.Phi3 => new Phi3PromptBuilder(),
            TranslationModelType.Gemma => new GemmaPromptBuilder(),
            TranslationModelType.Qwen => new QwenPromptBuilder(),
            TranslationModelType.Mistral => new MistralPromptBuilder(),
            TranslationModelType.Auto => new Phi3PromptBuilder(),
            _ => new Phi3PromptBuilder()
        };
    }
}
