namespace RealTimeTranslator.Core.Interfaces;

/// <summary>送話音声に合わせたゲーム用キーの保持と、注入入力を除く物理キーの監視。</summary>
public interface IGamePushToTalkController : IDisposable
{
    bool IsPhysicalKeyDown(int virtualKey);
    void Press();
    void Release();
}
