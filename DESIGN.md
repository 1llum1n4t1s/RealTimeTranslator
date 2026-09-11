# RealTimeTranslator 設計

## 目的と範囲

RealTimeTranslator は、Windows 上で指定したプロセスの再生音声だけを取得し、選択したクラウド翻訳プロバイダへリアルタイム送信して、日本語などの字幕をオーバーレイと翻訳ログに表示するデスクトップアプリです。

音声認識・翻訳はローカル完結ではありません。ローカル側の責務は、対象音声の分離、音声形式変換、送信量の制御、provider 接続、字幕の安定化、表示・保存です。導入と操作は [README.md](README.md)、変更・検証規則は [AGENTS.md](AGENTS.md) を参照してください。

## コンポーネントと境界

| コンポーネント | 主な責務 | 境界 |
| --- | --- | --- |
| `AudioCaptureService` | NAudio の WASAPI Process Loopback で対象 PID の48 kHz / stereo PCMを取得し、mono float のチャンクを通知 | provider、VAD、字幕を知らない |
| `AudioLevelMonitor` | 翻訳開始前のプロセス音量プレビューと入力ゲイン後ピークの通知 | API へ接続・送信しない。本番 capture と同時実行しない |
| `TranslationPipelineService` | provider 選択、開始停止、DSP、リサンプル、VAD、送信、字幕分割、統計、デバッグ録音を統括 | View や Avalonia 型に依存しない |
| `IRealtimeTranscriber` と各 client | 音声ストリームを provider 固有のプロトコルへ変換し、delta / completed / error / state を共通イベントに正規化 | UI と音声 capture を知らない |
| `SileroVadDetector` | 16 kHz / 512 samples のフレームから発話確率を推論し、セッション状態を保持 | provider 課金や表示を判断しない |
| `SettingsService` | 設定の初期化・移行・原子的保存、API secret の DPAPI 暗号化 | 実行時 object では復号値、ディスクでは暗号化値を扱う |
| `TranslationLogService` | 確定字幕を日別 TSV に順序どおり保存し、保持期限・全削除を直列化 | partial 字幕を保存しない |
| `MainViewModel` | ユーザー操作、capture preview、本番 pipeline、設定、更新、翻訳ログを調停 | UI thread への反映を担当 |
| `OverlayViewModel` / `OverlayWindow` | `SegmentId` 単位の partial 更新と final 表示、位置・色・フォントの反映 | 翻訳処理や永続化を担当しない |
| `UpdateService` | 固定 R2 feed の確認、Velopack の download / apply、更新ダイアログ | 任意の外部 feed を設定から受け付けない |

`RealTimeTranslator.Core` は UI 非依存、`RealTimeTranslator.UI` は composition root と表示、`RealTimeTranslator.Tests` は両者の契約検証を担当します。

## 実行時のデータフロー

```text
選択プロセス
  → WASAPI Process Loopback (48 kHz / stereo)
  → mono float
  → bounded raw-audio channel
  → InputGainStage
       ├─→ peak dBFS → メイン画面のレベルメーター
       ├─→ StreamingResampler 48 kHz → 16 kHz → Silero VAD / 16 kHz provider
       └─→ StreamingResampler 48 kHz → 24 kHz → OpenAI
  → VAD の pre-roll / speech / hangover / silence-padding 制御
  → active provider の InputSampleRate を選択して PCM16 化
       ├─ OpenAI: 24 kHz
       └─ Gemini / Soniox / Speechmatics / Azure: 16 kHz
  → IRealtimeTranscriber.SendAudio
  → TranscriptDeltaReceived / TranscriptCompleted
  → 文境界・長さ・idle による字幕確定
       ├─ OverlayViewModel → OverlayWindow
       ├─ TranslationLogViewModel → TranslationLogService → TSV
       └─ MainViewModel → 状態・統計表示
```

`AudioCaptureService` の WASAPI callback は native buffer を `ArrayPool` へコピーし、source format から float / mono へ変換して診断と100 ms chunk 化を行い、イベントで通知します。`TranslationPipelineService` のイベントハンドラは受け取ったチャンクを bounded raw-audio channel へ enqueue するだけにし、DSP、VAD、ネットワーク送信は専用 processing task で行います。

処理が追いつかない場合は raw-audio channel と各 provider の送信 channel が古いチャンクを捨て、無制限な遅延とメモリ増加を防ぎます。`DroppedAudioChunkCount` が数えるのは provider 送信 channel 側の drop であり、raw-audio channel 側の drop は個別計測していません。

## 翻訳プロバイダ

| Provider | 実装 | 入力 | provider 固有事項 |
| --- | --- | --- | --- |
| OpenAI | `OpenAIRealtimeClient` | 24 kHz / mono / PCM16 | Realtime Translation WebSocket。累積 transcript を client 側で差分化する |
| Gemini | `GeminiLiveClient` | 16 kHz / mono / PCM16 | Live WebSocket。target-language echo を設定可能 |
| Soniox | `SonioxRealtimeClient` | 16 kHz / mono / PCM16 | WebSocket の binary 音声。源言語は自動判定 |
| Speechmatics | `SpeechmaticsRealtimeClient` | 16 kHz / mono / PCM16 | WebSocket の binary 音声。源言語を明示する |
| Azure | `AzureSpeechTranslationClient` | 16 kHz / mono / PCM16 | Speech SDK の push stream。源言語ロケールと region が必要 |

全 client は `IRealtimeTranscriber` の状態、字幕イベント、送信サンプル数、drop 数を公開します。provider 固有設定は各 client の具体的な `ConnectAsync` に渡し、pipeline は開始時に active client と設定をスナップショットします。設定画面で provider を変更しても実行中接続は差し替えず、次の開始から反映します。これにより、音声レートや字幕分割条件がセッション途中で混在するのを防ぎます。

OpenAI / Gemini / Soniox / Speechmatics は、API key を送る前に endpoint の `wss` scheme、userinfo が空であること、provider ごとの許可 host を検証します。Azure は任意 endpoint を受け取らず、公式 Speech SDK を subscription key と region で構成します。

OpenAI の `/v1/realtime/translations` は標準 Realtime endpoint と wire contract が異なります。session update では server 既定の VAD を使い、音声は `session.input_audio_buffer.append` として送ります。複数世代の transcript event を受理しつつ、同一 response の text / audio-transcript 二重通知は client で抑制します。

## 音声処理と VAD

入力ゲインはリサンプル前の mono float に一度だけ適用し、VAD と送信の双方が同じ信号を見るようにします。自動 limiter は置かず、ゲイン後ピークと clip 表示を見てユーザーが調整します。

OpenAI セッションでは VAD と送信用のリサンプラを48 kHz入力から並列に処理します。16 kHz を経由して24 kHzを作ると8 kHzより上の帯域を復元できず、OpenAIへ送る情報量を減らすためです。provider が16 kHzの場合はVAD経路の出力を送信にも利用し、不要な24 kHzリサンプラは回しません。

VAD gate は次の情報を組み合わせます。

- pre-roll: speech 判定直前の音声を保持し、発話冒頭を復元する。
- hangover: speech 確率低下後もしばらく送信し、語尾と発話末尾の間を残す。
- silence padding: silence 遷移後も有限時間の無音 PCM を送り、provider 側に保留された出力を押し出す。
- auto pause: 設定時間以上 speech がなければ capture を停止し、無人時の送信継続を防ぐ。

Silero VAD を初期化できない場合は `NullVoiceActivityDetector` にフォールバックし、翻訳機能を継続します。この場合は全音声を speech として扱うため、UI で送信量増加を警告します。

## 字幕の状態モデル

provider の出力は partial と completed の表現が異なるため、pipeline で以下へ正規化します。

1. delta を現在の `SegmentId` の partial として蓄積し、短い間隔で表示更新する。
2. `。！？.!?` などの文境界を検出したら、文ごとに final を通知して新しい `SegmentId` へ進む。
3. 句点が来ない長文は `MaxPartialChars` で読点、空白、固定位置の順に分割する。
4. provider の completed 通知では、累積 transcript なら既確定 prefix を除いた新規部分を、hard finalize 境界なら現在の未確定部分を確定する。
5. completed 通知も文境界も来ない場合は、delta が途絶えた後の idle finalize で残りを確定する。

idle finalize は無音 padding の処理中に発火させません。実効値は、無効値を除き `max(設定値, SilencePaddingMs + 1000 ms)` とします。padding 中に確定すると、その後に押し出された続きが別 `SegmentId` へ分断されるためです。

字幕イベントは内部 lock の外で通知し、UI やログからの再入による deadlock を避けます。類似する再送は直近履歴で抑制し、provider の再送や累積応答で字幕が重複するのを防ぎます。

## 状態、並行性、ライフサイクル

- Start / Stop は semaphore で直列化し、開始中の停止や素早い再開で capture と client が競合しないようにする。
- セッション開始時に字幕累積、VAD hidden state、pre-roll、両リサンプラ、統計、無音 buffer を初期化する。
- raw audio と provider 送信は bounded channel を使い、遅延上限を優先して `DropOldest` とする。
- pipeline からのイベントは UI thread とは限らない。ViewModel が Avalonia dispatcher を使って表示状態へ反映する。
- preview capture は翻訳開始時に停止し、停止後に再開する。1つの対象プロセスへ preview と本番を重ねない。
- `Program` のユーザーセッション単位 mutex により単一インスタンスだけを動かし、二重起動時は既存ウィンドウを復元して前面化してから新しいプロセスを終了する。
- shutdown は capture 停止と DI singleton の非同期破棄を先に試み、native handle が残る場合にもプロセス終了を保証する。

## 設定、データ、セキュリティ

設定と生成データは Velopack のインストール先から分離し、更新で消えない Roaming AppData に保存します。

| データ | 保存先 | 方針 |
| --- | --- | --- |
| 設定 | `%APPDATA%/RealTimeTranslator/settings.json` | temp file を書いて原子的に置換。旧配置から初回移行 |
| API キー | 上記 settings 内 | DPAPI CurrentUser で暗号化。実行中 object のみ平文 |
| アプリログ | `%APPDATA%/RealTimeTranslator/logs/` | 日別ローテーション。診断用の字幕断片を含み得る |
| 翻訳ログ | `%APPDATA%/RealTimeTranslator/logs/translations/` | 確定字幕だけを日別 TSV として保存 |
| デバッグ音声 | `%APPDATA%/RealTimeTranslator/debug/` | 明示的に有効化したセッションだけ、実送信レートの WAV を保存 |

設定保存では API キーを暗号化した clone を作り、DI と実行中 client が参照する元 object を変更しません。永続化フィールドを追加するときは clone へのコピーも必須です。配布物は `settings.default.json` のみを含み、利用者の `settings.json` を含めません。

overlay 背景色は `BackgroundColorBase` (`#RRGGBB`) と `BackgroundOpacityPercent` を編集用の正本とし、表示で使う `BackgroundColor` (`#AARRGGBB`) を派生させます。旧設定の `BackgroundColor` だけがある場合は sanitize 時に分解し、以後は setter と保存前 sanitize で3フィールドを同期します。

翻訳ログの append、保持期限 cleanup、全削除は単一の channel worker で直列化します。これにより、確定字幕の順序と clear 後に到着した append の意味を維持します。

## 更新設計

更新 feed は `https://rtt.kagayoi.com` の `win-x64` channel に固定し、設定ファイルから変更できません。HTTPS、絶対 URI、userinfo なしを検証したうえで Velopack に渡します。自動確認は起動時の1回だけで、周期確認は行いません。check / download / apply は同じ lock で直列化し、起動時確認と手動確認の競合を防ぎます。

## 採用した判断とトレードオフ

| 判断 | 理由 | トレードオフ |
| --- | --- | --- |
| Process Loopback を採用 | ゲームやプレーヤーなど対象 PID の音だけを翻訳できる | Windows と対応 WASAPI 環境に限定される |
| Core と UI を分離 | 音声・接続・永続化を Avalonia なしで検証できる | composition root で具体 client をまとめて構築する必要がある |
| provider client を常駐登録し開始時に選択 | 再起動なしで provider を切り替え、セッション内の一貫性も保てる | 未選択 client も DI singleton として存在する |
| OpenAI用のVADと送信を48 kHzから並列リサンプル | VAD の16 kHz制約を守りつつOpenAI向け高域を維持する | OpenAIセッションではリサンプラを2つ進めるCPUコストがある |
| bounded channel + `DropOldest` | メモリと遅延を有限に保つ | 過負荷時は古い音声が欠落する。provider送信側は計測するが、raw-audio側は個別計測しない |
| VAD失敗時は全送信へフォールバック | VAD asset / native runtime 障害で起動不能にしない | 無音抑制が失われ、送信量が増える |
| subtitle finalization を多段化 | provider ごとの句読点・完了通知の差に耐える | 分割条件が相互依存するため回帰テストが必要 |
| API key を DPAPI CurrentUser で保存 | 平文保存を避け、追加サービスを不要にする | 別ユーザー・別PCへ暗号文を移しても復号できない |
| 更新 URL をコードで固定 | feed 差し替えによる任意配布物への誘導を防ぐ | mirror や private feed を利用者が選べない |
