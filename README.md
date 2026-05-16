# RealTimeTranslator

Windows 向けのリアルタイム字幕翻訳デスクトップアプリです。指定したプロセス（ゲーム / 動画プレーヤー / 配信視聴クライアント等）の音声を WASAPI Process Loopback でキャプチャし、OpenAI Realtime Translate API（WebSocket）にストリーミング送信して、翻訳字幕を半透明オーバーレイで表示します。

> ⚠️ **データ送信について**: 本アプリは BYOK（Bring Your Own Key）モデルです。キャプチャした音声はあなたが指定した OpenAI アカウントの Realtime API に送信され、課金もあなたの OpenAI アカウントから発生します。完全なローカル処理ではありません。

## 概要

- **プロセス単位の音声キャプチャ**: WASAPI Process Loopback API で対象プロセスの音声のみを取得します（Windows 11 / Windows Server 2022 以降）。
- **ストリーミング翻訳**: OpenAI Realtime Translate API (`gpt-realtime-translate`) に PCM16 / 24kHz の音声をストリーミング送信し、`response.output_audio_transcript.delta` / `.done` イベントで翻訳テキストを逐次受け取ります。
- **半透明オーバーレイ字幕**: 透過・最前面・クリック透過対応の Avalonia ウィンドウに字幕を表示します。フォント・色・表示時間・行数を設定で調整可能。
- **自動アップデート**: Velopack による GitHub Releases 経由の自動更新に対応します（既定では無効、設定で有効化）。
- **設定ホットリロード**: `settings.json` の変更は `IOptionsMonitor` で検出し再起動なしで反映されます。

## システム構成

```
プロセス音声 (WASAPI Loopback)
  ↓ 16kHz mono float32
TranslationPipelineService
  ↓ resample 16k→24k + Float32→PCM16
OpenAIRealtimeClient (WebSocket / wss://api.openai.com/v1/realtime/translations)
  ↓ response.output_audio_transcript.delta / .done
SubtitleGenerated event
  ↓
OverlayViewModel → 半透明オーバーレイに描画
```

## 動作環境

| 項目 | 要件 |
| --- | --- |
| OS | Windows 11 / Windows Server 2022（Build 20348 以降。WASAPI Process Loopback 必須） |
| ランタイム | .NET 10 (`net10.0-windows10.0.20348.0`) |
| アーキテクチャ | x64 のみ |
| 必須 | OpenAI API キー（Realtime API へのアクセス権限つき） |

## 主要依存ライブラリ

| ライブラリ | 目的 |
| --- | --- |
| [Avalonia 12](https://avaloniaui.net/) | UI フレームワーク（Fluent テーマ） |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | `[ObservableProperty]` / `[RelayCommand]` 等の MVVM ユーティリティ |
| [1llum1n4t1s.NAudio](https://www.nuget.org/packages/1llum1n4t1s.NAudio) | NAudio フォーク（WASAPI Process Loopback 拡張） |
| [SuperLightLogger](https://www.nuget.org/packages/SuperLightLogger) | 軽量ロガー |
| [Velopack](https://github.com/velopack/velopack) | 自動更新（GitHub Releases バックエンド） |
| [Microsoft.Extensions.{DI, Options, Configuration, Logging}](https://learn.microsoft.com/dotnet/core/extensions/) | DI / 設定 / オプション |

## セットアップ

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/RealTimeTranslator.git
   cd RealTimeTranslator
   ```

2. ソリューションを開くかコマンドラインでビルド
   - VS で開く場合: `RealTimeTranslator.slnx` を開く
   - CLI でビルド: 後述の [Build & Test](#build--test) を参照

3. OpenAI API キーを準備
   - [OpenAI Platform](https://platform.openai.com/) でアカウント作成・課金設定・API キーを発行
   - Realtime API（`gpt-realtime-translate`）へのアクセス権限があることを確認

4. 初回起動後、設定画面の「OpenAI Realtime」セクションに API キーを入力

## Build & Test

```pwsh
# Restore + Build (Platform は常に x64 を指定)
rtk dotnet restore RealTimeTranslator.slnx
rtk dotnet build RealTimeTranslator.slnx -c Release -p:Platform=x64

# ユニットテストのみ実行
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64 --filter "TestCategory!=Integration"

# アプリ実行
rtk dotnet run --project src/RealTimeTranslator.UI -c Release -p:Platform=x64

# 自己完結発行 (win-x64)
rtk dotnet publish src/RealTimeTranslator.UI -c Release -r win-x64 --self-contained
```

> プラットフォームは常に `x64` を指定してください（x86 / ARM はサポートしていません）。

## 設定（settings.json）

初回起動時、`%APPDATA%\RealTimeTranslator\settings.json`（Roaming AppData）にコピーされる設定ファイルを編集するか、アプリ内の設定画面から変更できます。

| セクション | 主な項目 |
| --- | --- |
| `OpenAIRealtime` | `ApiKey`（BYOK）、`OutputLanguage`（`ja`/`en`/`zh`/`ko` 等 15 言語）、`Model`（既定: `gpt-realtime-translate`）、`Endpoint`、`ReconnectDelayMs`、`MaxReconnectAttempts` |
| `Overlay` | フォント、色、表示時間、フェードアウト、画面下からのマージン、最大行数 |
| `AudioCapture` | `SampleRate`（既定 16000） |
| `Update` | `Enabled`、`FeedUrl`、`AutoApply` |

> ⚠️ **API キーの取り扱い**: `ApiKey` は Windows DPAPI（CurrentUser scope）で暗号化されたうえで `settings.json` に `dpapi:` プレフィックス付き base64 として保存されます。別ユーザー / 別 PC では復号できないため、他環境への持ち出しはできません。

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/             # インターフェース / モデル / Infrastructure サービス（UI 非依存）
│   ├── Interfaces/                      # IAudioCaptureService, IOpenAIRealtimeClient, ITranslationPipelineService, IUpdateService
│   ├── Models/                          # AppSettings, SubtitleItem, UpdateNotifications
│   └── Services/                        # AudioCaptureService, AudioFormatConverter, OpenAIRealtimeClient, SettingsService, LoggerService
├── RealTimeTranslator.UI/               # Avalonia デスクトップアプリ
│   ├── Views/                           # MainWindow, OverlayWindow (axaml + cs)
│   ├── ViewModels/                      # MainViewModel, OverlayViewModel, SettingsViewModel
│   ├── Services/                        # TranslationPipelineService（パイプラインオーケストレーター）, UpdateService
│   └── App.axaml.cs / Program.cs        # DI 構成 / エントリポイント
├── RealTimeTranslator.Tests/            # MSTest ユニットテスト
└── MinimalProcessLoopbackWpf/           # WASAPI Process Loopback 試作（プロトタイプ・Production には未統合）
```

## リリース・公開手順

- リリースは `release/x.y.z` ブランチに push すると GitHub Actions が起動し、Velopack で nupkg を作成して GitHub Releases にアップロードします（`.github/workflows/velopack-release.yml`）。
- バージョンは `Directory.Build.props` の `<Version>` に従います。

## 連絡先

- 開発者: ゆろち
- GitHub: https://github.com/1llum1n4t1s

## ライセンス

MIT License
