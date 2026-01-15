using RealTimeTranslator.Translation.Interfaces;

namespace RealTimeTranslator.Translation.Prompts;

/// <summary>
/// Gemma2形式のプロンプトビルダー
/// &lt;start_of_turn&gt;user ... &lt;end_of_turn&gt; &lt;start_of_turn&gt;model 形式
/// </summary>
public class GemmaPromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Gemma形式のプロンプトを構築
    /// </summary>
    public string BuildPrompt(string inputText, string sourceLang, string targetLang)
    {
        return $"<start_of_turn>user\nTranslate this {sourceLang} text to {targetLang}. Reply with ONLY the translation.\n\n{inputText}<end_of_turn>\n<start_of_turn>model\n";
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

        // Gemmaトークンを削除
        result = result.Replace("<start_of_turn>", "")
                       .Replace("<end_of_turn>", "");

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
    /// Gemma用のアンチプロンプトを取得
    /// </summary>
    public List<string> GetAntiPrompts()
    {
        return new List<string>
        {
            "<end_of_turn>", "<start_of_turn>",
            "\n\n", "Note:", "Explanation:", "Translation:", "Original:"
        };
    }
}
