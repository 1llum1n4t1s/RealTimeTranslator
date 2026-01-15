using RealTimeTranslator.Translation.Interfaces;

namespace RealTimeTranslator.Translation.Prompts;

/// <summary>
/// Qwen形式のプロンプトビルダー
/// &lt;|im_start|&gt;user ... &lt;|im_end|&gt; &lt;|im_start|&gt;assistant 形式
/// </summary>
public class QwenPromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Qwen形式のプロンプトを構築
    /// </summary>
    public string BuildPrompt(string inputText, string sourceLang, string targetLang)
    {
        return $"<|im_start|>user\nTranslate this {sourceLang} text to {targetLang}. Reply with ONLY the translation.\n\n{inputText}<|im_end|>\n<|im_start|>assistant\n";
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

        // Qwenトークンを削除
        result = result.Replace("<|im_start|>", "")
                       .Replace("<|im_end|>", "");

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
    /// Qwen用のアンチプロンプトを取得
    /// </summary>
    public List<string> GetAntiPrompts()
    {
        return new List<string>
        {
            "<|im_end|>", "<|im_start|>",
            "\n\n", "Note:", "Explanation:", "Translation:", "Original:"
        };
    }
}
