# RealTimeTranslator 作業ガイド

このリポジトリを変更するエージェント向けの実行規則です。システム構成と設計上の理由は [DESIGN.md](DESIGN.md)、利用者向けの導入・操作は [README.md](README.md) を参照してください。

## 前提

- UI、コメント、テスト名、ドキュメント、コミットメッセージは日本語を基本とする。
- 対象は Windows x64、`.NET 10`、`net10.0-windows10.0.20348.0`。x86 や他 OS を暗黙に追加しない。
- `TreatWarningsAsErrors` とビルド時コードスタイル検査が有効。警告を残さない。
- 通常の修正では `Directory.Build.props` の `Version` を変更しない。バージョン更新はリリース作業として扱う。
- API キーを含む `settings.json`、ログ、デバッグ WAV、ビルド・リリース成果物をコミットしない。

## リポジトリ構成

| パス | 責務 |
| --- | --- |
| `src/RealTimeTranslator.Core` | 音声キャプチャ、DSP、VAD、各翻訳クライアント、設定・ログの永続化。UI へ依存しない |
| `src/RealTimeTranslator.UI` | Avalonia の View / ViewModel、DI、更新 UI、アプリの起動・終了制御 |
| `src/RealTimeTranslator.Tests` | Core と UI の MSTest。実機依存テストは `Integration` カテゴリ |
| `scripts/release-local.ps1` | win-x64 の publish、署名、Velopack パッケージ作成・配信 |
| `.github/workflows/ci.yml` | Release/x64 の restore、build、非 Integration テスト |

## 正規の restore・build・test

PowerShell でリポジトリルートから実行する。

```powershell
# 依存更新後、初回、または assets が不足するときだけ実行
dotnet restore RealTimeTranslator.slnx -r win-x64 --force-evaluate -p:Configuration=Release -p:Platform=x64

# restore 後は暗黙 restore を発生させない
dotnet build RealTimeTranslator.slnx -c Release -p:Platform=x64 --no-restore
dotnet test src/RealTimeTranslator.Tests/RealTimeTranslator.Tests.csproj -c Release -p:Platform=x64 --no-restore --no-build --filter "TestCategory!=Integration"

# 実機 WASAPI テストが必要な変更だけ、音声を再生する対象プロセスがある環境で実行
dotnet test src/RealTimeTranslator.Tests/RealTimeTranslator.Tests.csproj -c Release -p:Platform=x64 --no-restore --no-build --filter "TestCategory=Integration"

# アプリ起動
dotnet run --project src/RealTimeTranslator.UI -c Release -p:Platform=x64 --no-restore

# 配布物の手動確認。release 自体は scripts/release-local.ps1 を使う
dotnet publish src/RealTimeTranslator.UI/RealTimeTranslator.UI.csproj -c Release -r win-x64 --self-contained --no-restore
```

solution の build / test に `-r win-x64` を付けると `NETSDK1134` になるため、RID は restore とプロジェクト単位の publish にだけ指定する。

### lockfile の不変条件

3つの `packages.lock.json` は `net10.0-windows10.0.20348/win-x64` ターゲットを保持する。RID なしの暗黙 restore はこのセクションを除去し、CI の locked restore / publish を `NU1004` で失敗させる。

- NuGet 参照を変更したら、上記の `restore -r win-x64 --force-evaluate` で3つの lockfile を同時に更新する。
- 以後の build / test / run / publish は `--no-restore` を使う。
- lockfile を含む変更の終了前に、次を実行して3ファイルすべてに RID があることを確認する。

```powershell
rg -n --fixed-strings '"net10.0-windows10.0.20348/win-x64"' src -g packages.lock.json
```

## 変更時に守る境界

### 音声と翻訳プロバイダ

- `AudioCaptureService` はプロセス loopback を 48 kHz / stereo で取得し、mono float へ変換する。リサンプルは `TranslationPipelineService` に集約する。
- OpenAI セッションでは、入力ゲイン後の同じ 48 kHz 信号から Silero VAD 用16 kHzと送信用24 kHzを並列生成する。16 kHz provider では16 kHz経路だけを判定と送信に共用し、24 kHz経路を回さない。16 kHzを経由して24 kHzを作らない。
- 選択中の `IRealtimeTranscriber.InputSampleRate` に従い、OpenAI は24 kHz、Gemini / Soniox / Speechmatics / Azure は16 kHzを送る。
- provider の変更は次の `StartAsync` から有効にし、実行中セッションの client と設定スナップショットを途中で差し替えない。
- provider を追加・変更するときは `TranscriptionProvider`、設定型、`SettingsViewModel`、DI、`TranslationPipelineService` の解決表、接続テスト、設定 clone、クライアントテストを一組で更新する。

### VAD と字幕確定

- Silero VAD v6.2.2 の入力は 16 kHz、1フレーム512 samples 固定。`StartAsync` ごとに detector と両リサンプラの状態をリセットする。
- `VadPreset` が `Custom` 以外なら、`SettingsViewModel` の preset 定義を正本として threshold / pre-roll / hangover を sanitize 時にも強制同期する。preset 値を変えるときは3値の組み合わせと既存設定の移行挙動をまとめて検証する。
- VAD の pre-roll は発話冒頭、hangover は発話末尾、silence padding はプロバイダに保留出力を押し出させる役割を持つ。値を単独で削る前に `VadGate.test.cs` と文分割テストで相互作用を確認する。
- delta の確定順は句読点、`MaxPartialChars`、provider の完了通知、idle finalize。`ResolveIdleFinalizeMs` の不変条件 `idle <= 0` または `idle >= SilencePaddingMs + 1000` を維持する。
- 字幕更新は `SegmentId` ごとに partial を置換し、final で確定する。イベントハンドラをロック保持中に呼ばない。

### OpenAI Translation endpoint

- `wss://api.openai.com/v1/realtime/translations` は標準 Realtime endpoint と契約が異なる。`session.update` に `turn_detection` を追加せず、音声 append の type は `session.input_audio_buffer.append` を維持する。
- transcript の event 名には現行・translation 専用・legacy の互換経路があり、同じ response 内の text / audio-transcript 二重通知を client で抑制する。event 対応を変えるときは `OpenAIRealtimeClient.adversarial.test.cs` を更新する。
- completed transcript は累積全文になり得る一方、delta だけで completed が来ないセッションもある。pipeline の既確定 prefix 差分化、長さ分割、idle finalize を外さない。

### 設定・シークレット・永続化

- 設定の正本は `%APPDATA%/RealTimeTranslator/settings.json`。API キーは保存用 clone だけを DPAPI CurrentUser で暗号化する。
- `AppSettings` に永続化フィールドを追加したら `SettingsService.CloneWithEncryptedSecrets` にも追加し、`SettingsServiceClone.test.cs` で保存時の欠落と secret 暗号化を検証する。
- overlay 背景の編集用正本は `BackgroundColorBase` + `BackgroundOpacityPercent`、`BackgroundColor` は表示・旧設定互換用の派生 `#AARRGGBB`。sanitize と各 setter の compose / split 同期を維持し、`BackgroundColorRoundTrip.test.cs` で検証する。
- 配布物には `settings.default.json` だけを含める。`settings.json` を publish へ含めない。
- OpenAI / Gemini / Soniox / Speechmatics の endpoint は、credential を送る前に `wss`、userinfo なし、provider ごとの許可 host を検証する。endpoint の既定値や接続処理を変えるときは allowlist と adversarial テストも更新する。Azure は公式 SDK と region を使う。
- 翻訳ログの書き込み・保持期限処理は `TranslationLogService` の単一 channel worker を通す。並行 append / clear の順序を崩さない。
- デバッグ WAV のヘッダーは active provider の `InputSampleRate` を使う。24 kHz 固定にしない。

### UI、非同期、終了処理

- Avalonia の compiled binding が有効。新規・変更する AXAML には正しい `x:DataType` を設定する。
- UI collection / property の変更は Avalonia UI thread へ dispatch する。
- `AudioCaptureService` の WASAPI callback は native buffer のコピー、float / mono 変換、診断、100 ms chunk 化、イベント通知までを担当し、pipeline のイベントハンドラは bounded channel への enqueue だけを行う。DSP、VAD、ネットワーク送信は processing task で行う境界を維持する。
- bounded channel の `DropOldest`、開始停止の直列化、CancellationToken、`IAsyncDisposable` の所有関係を維持する。Start / Stop / Dispose の競合を変更した場合は adversarial テストを追加する。
- プレビュー用 `IAudioLevelMonitor` と本番キャプチャを同時に動かさない。翻訳開始前はプレビュー、開始中は本番メーターを使う。

### 更新と配布

- 更新元は `UpdateSettings.CanonicalUpdateBaseUrl` の `https://rtt.kagayoi.com`、channel は `win-x64`。利用者設定から任意 URL を受け取る設計へ変えない。
- Velopack 操作は `UpdateService` の単一直列化境界を通す。
- リリースを依頼された場合は `scripts/release-local.ps1` とリリース用手順を確認し、通常の build / publish を配信完了と見なさない。

## 検証の選び方

- 文書のみ: 記載したパス・コマンド・設定名を `rg` と現行コードで照合し、対象文書の diff と ignore 状態を確認する。
- Core / UI の通常変更: Release/x64 build と非 Integration テストを実行する。
- 音声形式・VAD・provider・字幕分割: 対応する個別テストに加え、`TranslationPipelineService.Happy.test.cs` と `.Adversarial.test.cs` を確認する。
- WASAPI の構築・停止・実音取得: `ProcessLoopbackCaptureTests.cs` を確認し、実機検証が可能な場合だけ Integration テストを実行する。
- 設定・API キー・更新・ログ: clone / round-trip / URI 防御 / 並行順序の該当テストを実行する。
- 変更終了時は `git diff -- <対象>` と `git status --short -- <対象>` で、意図したファイルだけが変わったことを確認する。
