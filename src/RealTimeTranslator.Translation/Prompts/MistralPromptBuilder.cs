using RealTimeTranslator.Translation.Interfaces;

namespace RealTimeTranslator.Translation.Prompts;

/// <summary>
/// Mistral/Llama形式のプロンプトビルダー
/// &lt;s&gt;[INST] Instruction [/INST] Answer&lt;/s&gt; 形式
/// </summary>
public class MistralPromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Mistral形式のプロンプトを構築
    /// </summary>
    public string BuildPrompt(string inputText, string sourceLang, string targetLang)
    {
        return $"<s>[INST] Translate this {sourceLang} text to {targetLang}. Reply with ONLY the translation, no explanations or notes.\n\n{inputText} [/INST] ";
    }

    /// <summary>
    /// 生成されたテキストをクリーンアップ
    /// </summary>
    public string ParseOutput(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return rawOutput;
        }

        var result = rawOutput;

        // Mistralトークンを削除
        result = result.Replace("[INST]", "")
                       .Replace("[/INST]", "");

        // 共通のトークンを削除
        result = result.Replace("</s>", "")
                       .Replace("<s>", "")
                       .Replace("<unk>", "")
                       .Replace("<pad>", "");

        // 改行をスペースに統一
        result = result.Replace("\n", " ").Replace("\r", "");

        // 複数のスペースを1つに統一
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        // 末尾の句点が複数ある場合は1つに統一
        while (result.EndsWith("。。"))
        {
            result = result[..^1];
        }

        return result.Trim();
    }

    /// <summary>
    /// Mistral用のアンチプロンプトを取得
    /// </summary>
    public List<string> GetAntiPrompts()
    {
        return new List<string>
        {
            "</s>", "[INST]", "[/INST]",
            "\n\n", "Note:", "Explanation:", "Translation:", "Original:"
        };
    }
}
