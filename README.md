# Real-Time Subtitle Translator (Windows)

Windows環境で動作する、完全ローカル・GPU対応のリアルタイム字幕翻訳アプリケーションです。

## 概要

指定したアプリケーション（ゲーム、動画、配信など）の音声をプロセス単位でキャプチャし、英語音声をリアルタイムに認識・翻訳して日本語字幕をデスクトップにオーバーレイ表示します。

## 特徴

| 特徴 | 説明 |
|------|------|
| **完全ローカル動作** | クラウドAPIを使用せず、プライベート環境で動作します |
| **プロセス単位キャプチャ** | 対象プロセスの音声だけを抽出して翻訳できます |
| **Whisper ASR** | Whisper.netで音声認識（GPU: CUDA/Vulkan/HIP対応） |
| **LLM翻訳** | Mistral 7B Instruct (GGUF) をLLamaSharpでローカル翻訳 |
| **自動モデル取得** | ASR/翻訳モデルは必要に応じて自動ダウンロード |
| **WPFオーバーレイ** | 透過字幕を常に最前面へ表示（クリック透過対応） |
| **自動更新** | Velopack による更新チェック/適用（任意） |

## システム構成

```
音声キャプチャ → VAD → Whisper ASR → 翻訳 → オーバーレイ表示
```

### パイプライン詳細

1. **音声キャプチャ**: WASAPIプロセスループバックで対象プロセスの音声を取得
2. **音声前処理**: 16kHz/mono変換、ゲイン正規化
3. **VAD**: エネルギーベースの発話区間検出
4. **音声認識**: Whisper (ggml-medium) で英語を認識
5. **翻訳**: Mistral 7B Instruct (GGUF) で日本語へ翻訳
6. **オーバーレイ表示**: WPFで透過字幕を最前面表示

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/       # 共通インターフェース・モデル
│   ├── Interfaces/
│   │   ├── IAudioCaptureService.cs
│   │   ├── IVADService.cs
│   │   ├── IASRService.cs
│   │   ├── ITranslationService.cs
│   │   └── IUpdateService.cs
│   ├── Models/
│   │   ├── SubtitleItem.cs
│   │   ├── AppSettings.cs
│   │   └── UpdateNotifications.cs
│   └── Services/
│       ├── AudioCaptureService.cs
│       ├── ProcessLoopbackCapture.cs
│       ├── VADService.cs
│       ├── ModelDownloadService.cs
│       └── LoggerService.cs
├── RealTimeTranslator.Translation/ # ASR・翻訳関連
│   └── Services/
│       ├── WhisperASRService.cs
│       ├── MistralTranslationService.cs
│       ├── OnnxTranslationService.cs
│       ├── LocalTranslationService.cs
│       └── WhisperTranslationService.cs
└── RealTimeTranslator.UI/         # WPFアプリケーション
    ├── Views/
    │   ├── MainWindow.xaml
    │   ├── SettingsWindow.xaml
    │   └── OverlayWindow.xaml
    ├── ViewModels/
    │   ├── MainViewModel.cs
    │   ├── SettingsViewModel.cs
    │   └── OverlayViewModel.cs
    ├── Services/
    │   └── UpdateService.cs
    ├── settings.json
    └── App.xaml
```

## 開発環境

| 項目 | 要件 |
|------|------|
| OS | Windows 10 (Build 20348以降) / Windows 11 |
| IDE | Visual Studio 2022 (17.8以降) |
| SDK | .NET 10.0 (net10.0-windows) |
| GPU | 任意（ASR: CUDA/Vulkan/HIP、翻訳: CUDA12対応） |

## 依存ライブラリ

| ライブラリ | 用途 |
|-----------|------|
| [NAudio](https://github.com/naudio/NAudio) | 音声キャプチャ |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Whisper ASR（CUDA/Vulkan/HIP対応） |
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | Mistral 7B (GGUF) 翻訳 |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | ONNX翻訳エンジン（任意） |
| [Velopack](https://github.com/velopack/velopack) | 自動更新 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVMフレームワーク |

## ビルド手順

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```

2. Visual Studioでソリューションを開く
   ```
   RealTimeTranslator.slnx
   ```

3. NuGetパッケージを復元

4. ターゲットプラットフォームを `x64` に設定してビルド

## モデルの配置と自動ダウンロード

初回起動時にモデルが見つからない場合、自動でダウンロードします。

| 種別 | 既定の保存場所 | 既定のファイル名 | ダウンロード元 |
|------|----------------|------------------|----------------|
| Whisper ASR | `{Translation.ModelPath}/asr/` | `ggml-medium.bin` | [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp) |
| Mistral 翻訳 | `{Translation.ModelPath}` | `mistral-7b-instruct-v0.2.Q4_K_M.gguf` | [Hugging Face](https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF) |

> `Translation.ModelPath` は `settings.json` の翻訳モデルパスです。ASRモデルもこのディレクトリ配下に保存されます。

## 設定ファイル

起動時にアプリの配置フォルダにある `settings.json` を読み込みます。設定画面または直接編集で以下を調整できます：

- **ASR設定**（モデルパス、言語、Beam Search、GPU種別など）
- **翻訳設定**（モデルパス、ソース/ターゲット言語、キャッシュサイズ）
- **オーバーレイ設定**（フォント、色、表示時間、位置、最大行数）
- **VAD設定**（感度、最小/最大発話長、無音閾値）
- **ゲーム別プロファイル**（ホットワード、ASR補正辞書、翻訳前後辞書、初期プロンプト）
- **更新設定**（フィードURL、自動適用）

## 設定画面

メイン画面の「設定」から、翻訳・ASR・オーバーレイ・ゲーム別プロファイルの編集ができます。

## 公開手順

GitHub Actions による公開手順は `docs/release_guide.md` を参照してください。

## 連絡先

- 名前: ゆろち
- 連絡先: https://github.com/1llum1n4t1s

## ライセンス

MIT License
