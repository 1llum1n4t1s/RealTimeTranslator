# RealTimeTranslator

Windows向けのローカル完結型・リアルタイム字幕翻訳アプリケーションです。対象プロセスの音声をキャプチャし、Whisperで音声認識、LLMで翻訳してデスクトップへ字幕をオーバーレイ表示します。

## 概要

- **対象プロセスの音声だけを取得**し、リアルタイムで文字起こし・翻訳します。
- **Silero VAD (ONNX)** で発話区間を検出し、無音部分を自動で除外します。
- **Whisper.net (Whisper base)** により音声認識を行い、**LLamaSharp** で翻訳を行います。
- **WPFオーバーレイ**で、字幕を常に最前面表示できます。
- **モデルは自動ダウンロード**され、初回起動時に不足分を取得します。

## 主な機能

| 機能 | 内容 |
| --- | --- |
| プロセス単位の音声キャプチャ | WASAPIのプロセスループバックで対象アプリのみを取得（必要に応じてデスクトップ全体にフォールバック） |
| 発話区間検出 | Silero VAD (ONNX) による音声区間の自動抽出 |
| 音声認識 | Whisper.net（Whisper base）による英語音声の認識 |
| 翻訳 | LLamaSharpでGGUFモデルを実行（Phi-3/Gemma/Qwen/Mistralを自動判定） |
| オーバーレイ字幕 | 透過表示・常に最前面・クリック透過に対応 |
| 自動更新 | Velopackにより更新を検出・適用（無効化可） |

## システム構成

```
音声キャプチャ → Silero VAD → Whisper ASR → 翻訳 → WPFオーバーレイ
```

## 動作環境

| 項目 | 要件 |
| --- | --- |
| OS | Windows 10 / 11 |
| SDK | .NET 10.0 (net10.0-windows) |
| IDE | Visual Studio 2022 (17.8以降推奨) |
| GPU | 任意（Whisper/LLMはCUDA/HIP/SYCL/Vulkanの自動検出） |

## 依存ライブラリ

| ライブラリ | 目的 |
| --- | --- |
| [NAudio](https://github.com/naudio/NAudio) | プロセスループバック音声キャプチャ |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Whisper ASR |
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | GGUF翻訳モデルの推論 |
| [Microsoft.ML.OnnxRuntime.Gpu](https://github.com/microsoft/onnxruntime) | Silero VAD / ONNX推論 |
| [Velopack](https://github.com/velopack/velopack) | 自動更新 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM |

## テスト

プロジェクトには主要なユニットテストスイートが含まれており、Process Loopback APIの修正を確認できます。

### テスト実行

```bash
# すべてのテストを実行
dotnet test

# 統合テストを除外してユニットテストのみ実行
dotnet test --filter "TestCategory!=Integration"

# ビルドとテストを自動実行
.\build-and-test.ps1
```

### テストカテゴリ

| カテゴリ | 内容 |
| --- | --- |
| Unit | メモリレイアウト、GUID検証、PROPVARIANT構築などの基本機能 |
| Integration | Windowsオーディオサブシステムが必要なテスト（通常スキップ） |
| Performance | メモリ割り当てパフォーマンスの検証 |

### CI/CD

PowerShellスクリプト `build-and-test.ps1` で自動ビルド・テストを実行できます：

```powershell
# 基本的なビルドとテスト
.\build-and-test.ps1

# 統合テストをスキップ
.\build-and-test.ps1 -SkipIntegrationTests

# 詳細出力
.\build-and-test.ps1 -Verbose
```

## セットアップ手順

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```

2. Visual Studioでソリューションを開く
   ```
   RealTimeTranslator.slnx
   ```

3. NuGetパッケージを復元してビルド
   - ターゲットプラットフォームを `x64` に設定してください。

## モデルの自動ダウンロード

初回起動時、モデルが存在しない場合は自動でダウンロードします。

| 種別 | 既定の保存場所 | 既定のモデル | ダウンロード元 |
| --- | --- | --- | --- |
| Silero VAD | `models/vad` | `silero_vad.onnx` | https://github.com/snakers4/silero-vad |
| Whisper ASR | `{Translation.ModelPath}/asr` | `ggml-base.bin` | https://huggingface.co/ggerganov/whisper.cpp |
| 翻訳 (LLM) | `{Translation.ModelPath}` | `Phi-3-mini-4k-instruct-q4.gguf` | https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-gguf |

> `Translation.ModelPath` は `settings.json` の翻訳モデルパスです。ASRモデルはその配下 `asr` に保存されます。

## 設定ファイル

起動時にアプリの配置フォルダにある `settings.json` を読み込みます。設定画面または直接編集で以下を調整できます。

- **音声認識**: 言語、GPU設定、補正辞書、初期プロンプト
- **翻訳**: モデルパス、言語、キャッシュサイズ、モデル種別（自動判定対応）
- **オーバーレイ**: フォント、色、表示時間、位置、最大行数
- **音声キャプチャ/VAD**: 感度、無音判定、最小/最大発話長
- **ゲーム別プロファイル**: ホットワード、辞書（ASR補正・翻訳前後）
- **更新**: フィードURL、自動適用

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/          # 共通インターフェース・モデル・基盤サービス
├── RealTimeTranslator.Translation/   # Whisper/LLM翻訳関連
└── RealTimeTranslator.UI/            # WPF UI / オーバーレイ / 設定画面
```

## 公開手順

GitHub Actions による公開手順は `docs/release_guide.md` を参照してください。

## 連絡先

- 名前: ゆろち
- 連絡先: https://github.com/1llum1n4t1s

## ライセンス

MIT License
