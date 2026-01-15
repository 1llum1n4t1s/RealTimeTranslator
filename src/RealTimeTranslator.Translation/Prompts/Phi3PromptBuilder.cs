using RealTimeTranslator.Translation.Interfaces;

namespace RealTimeTranslator.Translation.Prompts;

/// <summary>
/// Phi-3 Mini形式のプロンプトビルダー
/// &lt;|system|&gt; ... &lt;|end|&gt; &lt;|user|&gt; ... &lt;|end|&gt; &lt;|assistant|&gt; 形式
/// </summary>
public class Phi3PromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Phi-3形式のプロンプト（シンプルで高品質）を構築
    /// </summary>
    public string BuildPrompt(string inputText, string sourceLang, string targetLang)
    {
        return $"<|user|>\nTranslate this {sourceLang} text to {targetLang}. Reply with ONLY the translation, no explanations.\n\n{inputText}<|end|>\n<|assistant|>\n";
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

        // Phi-3トークンを削除
        result = result.Replace("<|end|>", "")
                       .Replace("<|user|>", "")
                       .Replace("<|assistant|>", "")
                       .Replace("<|system|>", "");

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
    /// Phi-3用のアンチプロンプトを取得
    /// </summary>
    public List<string> GetAntiPrompts()
    {
        return new List<string>
        {
            "<|end|>", "<|user|>", "<|assistant|>",
            "\n\n", "Note:", "Explanation:", "Translation:", "Original:"
        };
    }
}
