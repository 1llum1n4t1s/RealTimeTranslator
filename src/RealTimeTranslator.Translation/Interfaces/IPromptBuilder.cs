namespace RealTimeTranslator.Translation.Interfaces;

/// <summary>
/// プロンプト構築戦略のインターフェース
/// </summary>
public interface IPromptBuilder
{
    /// <summary>
    /// 指定されたモデルのフォーマットに合わせて翻訳用プロンプトを構築します
    /// </summary>
    string BuildPrompt(string inputText, string sourceLang, string targetLang);

    /// <summary>
    /// 生成されたテキストから余計な部分（プロンプトの残骸や終了タグ）を除去します
    /// </summary>
    string ParseOutput(string rawOutput);

    /// <summary>
    /// モデルタイプに応じたアンチプロンプト（早期終了用）を取得します
    /// </summary>
    List<string> GetAntiPrompts();
}
