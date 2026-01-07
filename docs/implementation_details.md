# 実装詳細

## 音声キャプチャ (NAudio)
- `WasapiLoopbackCapture` を使用してシステム音声をキャプチャ。
- 特定プロセスの音声のみを取得するために、WindowsのオーディオセッションAPIを利用。

## 音声認識 (Whisper.net)
- `Whisper.net` ライブラリを使用して、ローカルでGGUF形式のモデルを実行。
- NVIDIA GPU (CUDA) または AMD GPU (Vulkan) を利用するためのランタイムを同梱。
- **二段構え構成**:
  - `base` または `small` モデルによる低遅延認識（仮字幕）。
  - `large-v3` モデルによる高精度認識（確定字幕）。

## 翻訳 (Argos Translate / CT2)
- `Argos Translate` または `CTranslate2` を使用したローカル翻訳。
- 英語から日本語への翻訳モデルをローカルに保持。

## UI (WPF)
- `AllowsTransparency="True"` および `WindowStyle="None"` を設定した透過ウィンドウ。
- `Topmost="True"` で常に最前面に表示。
- マウスクリックを透過させるために `SetWindowLong` Win32 APIを使用。
