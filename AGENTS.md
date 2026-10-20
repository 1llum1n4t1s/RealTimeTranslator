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
| `src/RealTimeTranslator.Core` | 音声キャプチャ、DSP、VAD、OpenAI 翻訳クライアント、設定・ログの永続化。UI へ依存しない |
| `src/RealTimeTranslator.UI` | Avalonia の View / ViewModel、DI、更新 UI、アプリの起動・終了制御 |
| `src/RealTimeTranslator.Tests` | Core と UI の MSTest。実機依存テストは `Integration` カテゴリ |
| `scripts/release-local.ps1` | win-x64 の publish、署名、Velopack パッケージ作成・配信 |
| `.github/workflows/ci.yml` | Release/x64 の restore、build、非 Integration テスト |

## 正規の restore・build・test

このPCのローカル検証は `D:\Codex\RealTimeTranslator` が正本。`Directory.Build.local.props` はこのcheckoutだけの設定でGit対象外。D不在/使用不可、C出力、祖先reparseは開始前に拒否し、Cへfallbackしない。CI/他利用者にはDを強制しない。OS全体TEMPと製品利用者の保存先は変更しない。

PowerShellでrepo rootから同じ作業別runを指定する。bin/obj、results、fixtures、TEMP/TMP、CLI/NuGet作業cacheは同run配下。ログの出力pathも同runへ明示する。初期targetより前に開くlogger/responseを検査するため正規wrapperを使う。外部props/targets/runsettings差替えは拒否する。

```powershell
$work = 'D:\Codex\RealTimeTranslator\run-<一意な作業名>'
# 初回/依存更新時。lockfileのRIDを保つ。
& .\scripts\Dev.ps1 -WorkDirectory $work -Tool dotnet -ToolArguments @('restore', 'RealTimeTranslator.slnx', '-r', 'win-x64', '-p:Configuration=Release', '-p:Platform=x64', '--locked-mode')
& .\scripts\Dev.ps1 -WorkDirectory $work -Tool dotnet -ToolArguments @('build', 'RealTimeTranslator.slnx', '-c', 'Release', '-p:Platform=x64', '--no-restore')
& .\scripts\Dev.ps1 -WorkDirectory $work -Tool dotnet -ToolArguments @('test', 'src/RealTimeTranslator.Tests/RealTimeTranslator.Tests.csproj', '-c', 'Release', '-p:Platform=x64', '--no-restore', '--no-build', '--filter', 'TestCategory!=Integration')
# 実機/音声/モデル検証は変更に必要な場合だけ判断する。
& .\scripts\Dev.ps1 -WorkDirectory $work -Tool dotnet -ToolArguments @('run', '--project', 'src/RealTimeTranslator.UI', '-c', 'Release', '-p:Platform=x64', '--no-restore')
& .\scripts\Dev.ps1 -WorkDirectory $work -Tool dotnet -ToolArguments @('publish', 'src/RealTimeTranslator.UI/RealTimeTranslator.UI.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', '--no-restore')
```

CI/他利用者では従来の直接 `dotnet` が使用できる。依存更新時の `--force-evaluate` はlockfile変更を意図した場合だけ使用する。

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

### 音声と OpenAI 翻訳

- `AudioCaptureService` はプロセス loopback を 48 kHz / stereo で取得し、mono float へ変換する。リサンプルは `TranslationPipelineService` に集約する。
- OpenAI セッションでは、入力ゲイン後の同じ 48 kHz 信号から FireRed VAD 用16 kHzと送信用24 kHzを並列生成する。16 kHzを経由して24 kHzを作らない。
- `IRealtimeTranscriber.InputSampleRate` は OpenAI 字幕音声の24 kHz / mono / PCM16 契約を表す。
- OpenAI 設定の変更は次の `StartAsync` から有効にし、実行中セッションの client と設定スナップショットを途中で差し替えない。

### ボイス通訳

- ボイス通訳は字幕用 client・キュー・リサンプラ・開始停止状態と共有しない。DI の専用 client factory と `AppSettings.VoiceTranslation` の clone を維持する。構造と機器判定は [DESIGN.md の独立セッション](DESIGN.md#ボイス通訳の独立セッション) を参照する。
- PTT の許可とルーティング世代はマイク取得時に記録する。キー閉鎖中はリサンプラの残留音も含めて送信 PCM を無音にし、訳音声の末尾は受信・出力できるようにする。
- PTT の通常キーと修飾キーは UI の登録・設定の検証・実行時の押下判定で一致させる。入力・出力ゲインは字幕設定と独立させ、同じ出力ゲイン適用済み PCM を送話と試聴へ渡す。
- 試聴は `IVoicePlaybackService` の独立した再生経路を使う。列挙・開始で Wave Link 関連機器と送話先を除外し、既定機器へ fallback しない。試聴の失敗は試聴だけを停止し、旧世代のエラーを新しい試聴へ適用しない。試聴のオン状態を永続化せず、停止・モード変更で保留音声を消去する。
- 機器の列挙と開始で同じ Wave Link 判定を使い、表示名や既定機器への fallback で選択制約を緩めない。録音側のミックスを入力マイクとして再取り込みせず、翻訳専用の再生チャンネルだけを送話用ミックスへ含める案内を維持する。停止・モード変更時のキュー消去と旧セッション通知の排除を維持する。
- 字幕側の `DropOldest` をボイス通訳へ一律に適用しない。入力キュー超過・送信 drop・出力バッファ超過は停止させ、`MaxReconnectAttempts = 0` を維持する。原音への自動切替や自動再接続を追加しない。
- 変更時は字幕との同時実行、PTT の修飾キー登録・解放後の末尾、実行中のゲイン変更、送話と試聴の同時出力、停止・モード切替時の残留音、接続待ち中の停止、送話・試聴それぞれの機器消失・過負荷時の停止範囲を必要範囲で実機確認する。実機確認ができない項目は未検証として報告する。

### VAD と字幕確定

- FireRed VAD のストリーミング用 ONNX と CMVN を同梱する。外部フレームは 16 kHz / 512 samples を維持し、内部では 25 ms 窓 / 10 ms 刻みの 80 bin Fbank と推論 cache を使う。`StartAsync` ごとに detector の特徴量履歴・cache と両リサンプラをリセットする。前処理は公式 Kaldi の窓・振幅・CMVN と照合する。
- 無音 PCM の padding 送信中も実音を pre-roll へ保持し、次の発話検出時に現在フレームと一つの PCM バッチで復元する。ゼロ PCM 送信と実音保存を両立させ、OpenAI client の bounded queue への連続 enqueue で復元した冒頭を押し出さない。
- VAD の threshold / pre-roll / hangover は独立して選択・保存し、sanitize で別項目へ連動させない。旧 `VadPreset` は一度限りの読込移行専用とし、非 Custom は閾値を保って保持時間を新既定 50 / 200 ms へ移行、Custom は3値を保持する。移行後は旧キーを保存せず、一覧外の有効な既存値も選択肢として保持する。
- VAD の pre-roll は発話冒頭、hangover は発話末尾、silence padding はOpenAI に保留出力を押し出させる役割を持つ。値を単独で削る前に `VadGate.test.cs` と文分割テストで相互作用を確認する。
- delta の確定順は句読点、`MaxPartialChars`、OpenAI の完了通知、idle finalize。`ResolveIdleFinalizeMs` の不変条件 `idle <= 0` または `idle >= SilencePaddingMs + 1000` を維持する。
- 字幕更新は `SegmentId` ごとに partial を置換し、final で確定する。通知は生成順にキューへ積み、ロック外で購読者へ渡す。停止時の通知完了待ちと、同期 callback からの Stop / Dispose 再入時に自身を待たない経路を維持する。

### OpenAI Translation endpoint

- `wss://api.openai.com/v1/realtime/translations` は標準 Realtime endpoint と契約が異なる。`session.update` に `turn_detection` を追加せず、音声 append の type は `session.input_audio_buffer.append` を維持する。
- transcript の event 名には現行・translation 専用・legacy の互換経路があり、同じ response 内の text / audio-transcript 二重通知を client で抑制する。event 対応を変えるときは `OpenAIRealtimeClient.adversarial.test.cs` を更新する。
- completed transcript は累積全文になり得る一方、delta だけで completed が来ないセッションもある。pipeline の既確定 prefix 差分化、長さ分割、idle finalize を外さない。

### 設定・シークレット・永続化

- 設定の正本は `%APPDATA%/RealTimeTranslator/settings.json`。API キーは保存用 clone だけを DPAPI CurrentUser で暗号化する。
- `AppSettings` に永続化フィールドを追加したら `SettingsService.CloneWithEncryptedSecrets` にも追加し、`SettingsServiceClone.test.cs` で保存時の欠落と secret 暗号化を検証する。
- overlay 背景の編集用正本は `BackgroundColorBase` + `BackgroundOpacityPercent`、`BackgroundColor` は表示・旧設定互換用の派生 `#AARRGGBB`。sanitize と各 setter の compose / split 同期を維持し、`BackgroundColorRoundTrip.test.cs` で検証する。
- 配布物には `settings.default.json` だけを含める。`settings.json` を publish へ含めない。
- OpenAI の endpoint は、credential を送る前に `wss`、userinfo なし、許可 host を検証する。endpoint の既定値や接続処理を変えるときは allowlist と adversarial テストも更新する。
- 翻訳ログの書き込み・保持期限処理は `TranslationLogService` の単一 channel worker を通す。並行 append / clear の順序を崩さない。
- デバッグ WAV のヘッダーは OpenAI へ送る24 kHz / mono / PCM16 と一致させる。

### UI、非同期、終了処理

- Avalonia の compiled binding が有効。新規・変更する AXAML には正しい `x:DataType` を設定する。
- UI collection / property の変更は Avalonia UI thread へ dispatch する。
- `AudioCaptureService` の WASAPI callback は native buffer のコピー、float / mono 変換、診断、100 ms chunk 化、イベント通知までを担当し、pipeline のイベントハンドラは bounded channel への enqueue だけを行う。DSP、VAD、ネットワーク送信は processing task で行う境界を維持する。
- 字幕経路の bounded channel の `DropOldest`、開始停止の直列化、CancellationToken、`IAsyncDisposable` の所有関係を維持する。Start / Stop / Dispose の競合を変更した場合は adversarial テストを追加する。
- プレビュー用 `IAudioLevelMonitor` と本番キャプチャを同時に動かさない。翻訳開始前はプレビュー、開始中は本番メーターを使う。

### 更新と配布

- 更新元は `UpdateSettings.CanonicalUpdateBaseUrl` の `https://rtt.kagayoi.com`、channel は `win-x64`。利用者設定から任意 URL を受け取る設計へ変えない。
- Velopack 操作は `UpdateService` の単一直列化境界を通す。
- リリースを依頼された場合は `scripts/release-local.ps1` とリリース用手順を確認し、通常の build / publish を配信完了と見なさない。

## 検証の選び方

- 文書のみ: 記載したパス・コマンド・設定名を `rg` と現行コードで照合し、対象文書の diff と ignore 状態を確認する。
- Core / UI の通常変更: Release/x64 build と非 Integration テストを実行する。
- 音声形式・VAD・OpenAI client・字幕分割: 対応する個別テストに加え、`TranslationPipelineService.Happy.test.cs` と `.Adversarial.test.cs` を確認する。
- WASAPI の構築・停止・実音取得: `ProcessLoopbackCaptureTests.cs` を確認し、実機検証が可能な場合だけ Integration テストを実行する。
- 設定・API キー・更新・ログ: clone / round-trip / URI 防御 / 並行順序の該当テストを実行する。
- 変更終了時は `git diff -- <対象>` と `git status --short -- <対象>` で、意図したファイルだけが変わったことを確認する。
