# RealTimeTranslator

Windows向けのローカル完結型リアルタイム字幕翻訳アプリケーションです。指定プロセスの音声をキャプチャし、Silero VADで発話区間を抽出、Whisperで文字起こし、LLMで翻訳した結果をWPFのオーバーレイに表示します。

## 概要

- **プロセス単位の音声キャプチャ**: WASAPIのプロセスループバックで対象アプリの音声を取得し、必要に応じてデスクトップ全体のキャプチャにフォールバックします。
- **発話区間検出**: Silero VAD (ONNX) により音声区間を自動抽出します。
- **音声認識**: Whisper.net (ggml-base) を用いた英語ASRを実行します。
- **翻訳**: LlamaSharpでGGUFモデルを推論し、Phi-3/Gemma/Qwen/Mistral形式を自動判定します。
- **字幕オーバーレイ**: 透過表示・最前面・クリック透過に対応したWPFウィンドウで字幕を表示します。
- **モデル自動ダウンロード**: 既定のモデルが無い場合、起動時に自動で取得します。

## システム構成

```
音声キャプチャ → Silero VAD → Whisper ASR → LLM翻訳 → WPFオーバーレイ
```

## 動作環境

| 項目 | 要件 |
| --- | --- |
| OS | Windows 10 / 11 |
| SDK | .NET 10.0 (net10.0-windows8.0) |
| IDE | Visual Studio 2022 (17.8以降推奨) |
| アーキテクチャ | x64 |

## 主要依存ライブラリ

| ライブラリ | 目的 |
| --- | --- |
| [NAudio](https://github.com/naudio/NAudio) | プロセスループバック音声キャプチャ |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Whisper ASR |
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | GGUF翻訳モデル推論 |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | Silero VAD 推論 (CUDA/DirectML) |
| [Velopack](https://github.com/velopack/velopack) | 自動更新 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVMユーティリティ |

## セットアップ

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```

2. ソリューションを開く
   - `RealTimeTranslator.slnx`

3. NuGetを復元してビルド
   - ターゲットプラットフォームは `x64` を選択してください。

## 設定

起動時にアプリ配置フォルダの `settings.json` を読み込みます。設定画面または直接編集で以下を調整できます。

- **ASR**: Whisperモデルパス、言語、GPU設定
- **Translation**: 翻訳モデルパス、言語、キャッシュサイズ、モデル形式の自動判定
- **Overlay**: フォント、色、表示時間、位置、最大行数
- **AudioCapture/VAD**: サンプルレート、感度、最小/最大発話長、無音判定
- **GameProfiles**: プロセス別ホットワード・辞書・初期プロンプト
- **Update**: 自動更新の有効化とフィードURL

## モデルの自動ダウンロード

初回起動時にモデルが存在しない場合は自動でダウンロードします。

| 種別 | 既定の保存場所 | 既定のモデル | ダウンロード元 |
| --- | --- | --- | --- |
| Silero VAD | `models/vad` | `silero_vad.onnx` | https://github.com/snakers4/silero-vad |
| Whisper ASR | `{Translation.ModelPath}/asr` | `ggml-base.bin` | https://huggingface.co/ggerganov/whisper.cpp |
| 翻訳 (LLM) | `{Translation.ModelPath}` | `Phi-3-mini-4k-instruct-q4.gguf` | https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-gguf |

> `Translation.ModelPath` は `settings.json` の翻訳モデルパスです。

## テスト

```bash
# すべてのテストを実行
dotnet test

# 統合テストを除外してユニットテストのみ実行
dotnet test --filter "TestCategory!=Integration"

# ビルドとテストを自動実行
.\build-and-test.ps1
```

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/          # 共通インターフェース・モデル・基盤サービス
├── RealTimeTranslator.Translation/   # Whisper/LLM翻訳関連
├── RealTimeTranslator.UI/            # WPF UI / オーバーレイ / 設定画面
└── RealTimeTranslator.Tests/         # テスト
```

## 公開手順

GitHub Actions による公開手順は `docs/release_guide.md` を参照してください。

## 連絡先

- 名前: ゆろち
- 連絡先: https://github.com/1llum1n4t1s

## ライセンス

MIT License
