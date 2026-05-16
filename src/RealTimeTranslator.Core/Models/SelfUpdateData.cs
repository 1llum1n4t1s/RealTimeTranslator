using System;

namespace RealTimeTranslator.Core.Models;

/// <summary>
/// 更新チェックの結果が「最新版」であることを示すマーカークラス。
/// SelfUpdateWindow の DataTemplate で「利用できる更新はありません。」表示に分岐させるために使う。
/// </summary>
public class AlreadyUpToDate
{
}

/// <summary>
/// 自動更新の失敗を表すクラス。失敗理由のメッセージを保持する。
/// SelfUpdateWindow の DataTemplate で赤色のエラー表示に分岐させるために使う。
/// </summary>
public class SelfUpdateFailed
{
    /// <summary>失敗理由のメッセージ</summary>
    public string Reason { get; }

    /// <summary>
    /// 例外情報から失敗メッセージを生成する。内部例外があればそちらを優先する。
    /// </summary>
    public SelfUpdateFailed(Exception e)
    {
        Reason = e.InnerException?.Message ?? e.Message;
    }

    /// <summary>
    /// メッセージから直接生成する。
    /// </summary>
    public SelfUpdateFailed(string reason)
    {
        Reason = reason;
    }
}
