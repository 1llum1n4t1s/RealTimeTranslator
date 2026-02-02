# セットアップガイド

## 開発環境の要件
- Visual Studio 2022 (17.8以降)
- .NET 8.0 SDK
- NVIDIA GPU (CUDA 12.x) または AMD GPU (Vulkan対応)

## ビルド手順
1. リポジトリをクローンします。
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```
2. ソリューションファイル (`RealTimeTranslator.sln`) をVisual Studioで開きます。
3. NuGetパッケージを復元します。
4. ターゲットプラットフォームを `x64` に設定してビルドします。

## モデルの配置
Whisperモデルファイル (.bin) を `models/` ディレクトリに配置してください。
- `base.bin` (低遅延用)
- `large-v3.bin` (高精度用)

## デバッグ時にアプリが2つ起動する場合
出力ログで `Microsoft.Extensions.DotNetDeltaApplier.dll` が読み込まれている場合、Visual Studio の Hot Reload が有効です。Hot Reload が二重起動の原因になることがあります。

**対処（いずれか）:**
1. **Visual Studio**: **デバッグ** → **オプション** → **.NET/C++ Hot Reload** の **Hot Reload を有効にする** のチェックを外す。
2. デバッグ開始後、ツールバーの **Hot Reload（炎アイコン）** をオフにする。
3. 出力ウィンドウで `[SingleInstance] PID=xxx: 既存のインスタンスを前面に表示して終了します` が出ていれば、2つ目のプロセスは自動で終了している。表示されるウィンドウは1つ（先に起動したインスタンス）になる。

## 依存ライブラリ
- [NAudio](https://github.com/naudio/NAudio): 音声キャプチャ
- [Whisper.net](https://github.com/sandrohanea/whisper.net): Whisperランタイム
- [Argos Translate](https://github.com/argosopentech/argos-translate): 翻訳エンジン
