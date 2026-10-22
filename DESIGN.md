# RealTimeTranslator 設計

## 目的と範囲

RealTimeTranslator は、Windows 上で指定したプロセスの再生音声をクラウド翻訳し、字幕をオーバーレイと翻訳ログに表示するデスクトップアプリです。独立したボイス通訳では、マイク音声を OpenAI で翻訳し、Wave Link 3 の仮想チャンネルと送話用ミックスを経由してゲームなどのマイク入力へ送ります。両セッションは同時に利用できます。

音声認識・翻訳はローカル完結ではありません。ローカル側の責務は、対象音声の分離、音声形式変換、送信量の制御、OpenAI 接続、字幕の安定化、表示・保存です。導入と操作は [README.md](README.md)、変更・検証規則は [AGENTS.md](AGENTS.md) を参照してください。

## コンポーネントと境界

| コンポーネント | 主な責務 | 境界 |
| --- | --- | --- |
| `AudioCaptureService` | NAudio の WASAPI Process Loopback で対象 PID の48 kHz / stereo PCMを取得し、mono float のチャンクを通知 | OpenAI、VAD、字幕を知らない |
| `AudioLevelMonitor` | 翻訳開始前のプロセス音量プレビューと入力ゲイン後ピークの通知 | API へ接続・送信しない。本番 capture と同時実行しない |
| `TranslationPipelineService` | 開始停止、DSP、リサンプル、VAD、送信、字幕分割、統計、デバッグ録音を統括 | View や Avalonia 型に依存しない |
| `IRealtimeTranscriber` / `OpenAIRealtimeClient` | 音声ストリームを OpenAI Translation プロトコルへ変換し、delta / completed / error / state をイベントに正規化 | UI と音声 capture を知らない |
| `FireRedVadDetector` | 16 kHz / 512 samples の波形から 10 ms 刻みの特徴量と発話確率を計算し、セッション状態を保持 | OpenAI 課金や表示を判断しない |
| `SettingsService` | 設定の初期化・移行・原子的保存、API secret の DPAPI 暗号化 | 実行時 object では復号値、ディスクでは暗号化値を扱う |
| `TranslationLogService` | 確定字幕を日別 TSV に順序どおり保存し、保持期限・全削除を直列化 | partial 字幕を保存しない |
| `MainViewModel` | ユーザー操作、capture preview、本番 pipeline、設定、更新、翻訳ログを調停 | UI thread への反映を担当 |
| `OverlayViewModel` / `OverlayWindow` | `SegmentId` 単位の partial 更新と final 表示、位置・色・フォントの反映 | 翻訳処理や永続化を担当しない |
| `UpdateService` | 固定 R2 feed の確認、Velopack の download / apply、更新ダイアログ | 任意の外部 feed を設定から受け付けない |

`RealTimeTranslator.Core` は UI 非依存、`RealTimeTranslator.UI` は composition root と表示、`RealTimeTranslator.Tests` は両者の契約検証を担当します。

## ボイス通訳の独立セッション

`VoiceTranslationService` は、字幕用の `TranslationPipelineService` と別の接続・入力キュー・リサンプラ・開始停止状態を所有する。`App` は音声送信用の `OpenAIRealtimeClient` を都度生成し、字幕用 singleton を再利用しない。ボイス通訳の送信先言語は `AppSettings.VoiceTranslation` に保存し、字幕側の接続・言語と独立させる。API キーは既存の OpenAI 設定を使い、保存用 clone の DPAPI 保護を維持する。

```text
物理マイク（取得形式）
→ VoiceAudioService（ネイティブ buffer のコピー、mono float 化）
→ bounded channel（取得時の PTT 許可とルーティング世代を保持）
→ VoiceTranslationService（入力ゲイン、PTT、マイク VAD、24 kHz / PCM16 化）
→ 専用 IRealtimeSpeechTranslator / OpenAI Translation session
→ session.output_audio.delta（24 kHz / PCM16 / mono）
→ VoiceAudioService（出力形式変換、2 秒上限のリング、無音補填）
→ Wave Link 3 の翻訳専用再生チャンネル → その音声だけを含む Chat Mix → ゲーム
```

送話・試聴とも、変換後の受信音声と待機キューの両方が完全無音の場合は、追加の無音を積まず再生側の無音補完へ任せる。無音入力も先にリサンプルしてフィルタの実音末尾を保ち、既存キューは消さない。実音が待機する間の無音、単一受信チャンクと待機バッファの2秒上限、実音の過負荷停止は維持する。停止診断には受信側だけでなく待機側の実音サンプル数・ピークも含める。

マイクを native callback からネットワークへ直接送らず、処理 task で変換・送信する。ボイス通訳は独立した PTT ゲートを使用し、字幕用 VAD の状態・個別設定を変更しない。発話キーは UI が実際のキー入力から通常キー1つと修飾キーを登録し、`PushToTalkVirtualKey` と `PushToTalkModifiers` に保存する。旧設定は修飾キーなしとして引き継ぐ。マイク取得時にすべての指定キーの押下を確認して送信許可を固定する。キーを離した後も無音 PCM を送り、翻訳の末尾を出力させる。リサンプラに残った実音もキー閉鎖中は送らない。原音モードは API を経由せず、ミュートは出力しない。モード変更時は一旦ミュートしてキューを消去し、古い接続の遅延出力を破棄する。

翻訳モードのマイク VAD は専用 factory から作る FireRed detector と16/24 kHzリサンプラを所有し、字幕の検出器・cache・設定を共有しない。入力ゲイン後の同じ native mono から16 kHz判定用と24 kHz送信用へ並列変換する。`VoiceTranslationSettings` の `EnableVad` / `VadThreshold` / `VadPreRollMs` / `VadHangoverMs` を開始時 clone から固定し、既定を true / 0.8 / 50 / 200 ms とする。原音モードと VAD 無効時は PTT と24 kHz変換だけの経路を使用する。

マイク VAD は pre-roll と hangover を24 kHzのサンプル数で保持し、発話後は専用の5000 ms無音 padding を送ってから append を休止する。接続と訳音声の受信は継続する。判定は32 ms単位で、開ゲート中の許可済み末尾は判定を待たず送るため、hangover=0でも次の判定まで最大32 ms未満の音声が先行し得る。padding 中も次の冒頭を保持して再検出時に一括復元する。PTT 許可・世代を優先し、キー開閉時には特徴量・DSP・pre-roll をリセットして閉鎖前の実音を次の押下へ持ち越さない。開始・停止・モード・入力レート変更でも専用状態をリセットまたは破棄する。有効時の初期化・推論失敗はボイス通訳だけを停止し、処理失敗後の停止予約もセッション・世代・gate の一致を再確認してから適用する。これにより旧モードの失敗で切替後のモードを停止しない。

訳音声の任意のライブ試聴は `IVoicePlaybackService` / `VoicePlaybackService` の独立した WASAPI 再生を使い、訳音声の同じ PCM16 をゲーム送話と試聴へ渡す。試聴先 ID だけを設定へ保存し、オン状態は永続化しない。試聴は利用可能な Windows の再生機器を対象とし、hardware ID で確認できた Wave Link 関連機器と、送話先と同じ ID を除外する。列挙・開始で同じ条件を使い、既定機器には fallback しない。RDP や仮想ヘッドホンを物理 bus がない理由だけで拒否しない。送話側の厳密な Wave Link 判定は変更しない。試聴用バッファは最大2秒とし、機器消失・過負荷は試聴のみ停止させる。世代付きエラーを確認し、通訳停止・モード変更時は試聴の残留音も消去する。試聴だけの停止や失敗で送話・字幕の client を停止しない。

ボイス通訳の入力・出力ゲインは `VoiceTranslationSettings` の独立した2フィールドに保存し、clone に含める。範囲は各−24～+24 dB、既定0 dBとし、非有限値は0 dBへ正規化する。入力ゲインは処理 task でマイクの取得レートの mono float へ適用し、その後24 kHzへリサンプルする。PTT閉鎖中はリサンプラの残留も含めて無音化し、ゲインで送信許可を変えない。出力ゲインは受信元配列を変更せずPCM16へ適用し、同じ調整済みPCMを送話・試聴へ渡す。原音モードにも両ゲインを適用する。ゲイン変更は実行中に反映し、clientの再接続や字幕設定の変更を伴わない。UIは適用後ピークのdBFSメーター、1.5秒保持のclip表示、各0 dBリセットを備える。

機器は ID で明示選択し、入力から Wave Link の録音ミックスを除外、出力は Wave Link 3 の仮想再生チャンネルに限定する。利用者が変更できる endpoint 表示名や adapter の表示文字列を識別の正本にせず、endpoint の `DeviceTopology.GetConnector(0).ConnectedToDeviceId` で接続先音声アダプターを解決し、その `PKEY_Device_InstanceId` から Windows Configuration Manager で PnP 親を辿る。`USB\VID_0FD9&PID_0096` または `TUSBAUDIO_ENUM\VID_0FD9&PID_0096&KS` の hardware ID を確認し、元の endpoint の方向と `PKEY_Device_DeviceDesc` を使う。render は `System` / `Music` / `Browser` / `Voice Chat` / `SFX` / `Game` / `Aux 1` / `Aux 2` だけを許可し、`Wave Mic 1`〜`4` と録音ミックスを送信先にしない。同じ仮想機器の capture は入力候補から一括で除外する。列挙時・開始時に同じ判定を使い、接続先・親・属性を確認できない機器は許可しない。

`WaveLinkDeviceIdentity` がこの識別を集約する。識別値は Audio Driver 3.0.0.466 の `ElgatoUsbAudio.inf` / `ElgatoUsbAudioks.inf` に基づく。表示名の変更や同名の別機器で制約が緩まないよう、ハードウェア ID の完全一致とチャンネル用途を組み合わせ、PnP 親を root まで確認できない場合も候補から除外する。

以前の出力 ID が候補にない場合は未選択とし、保存済み ID を保持したまま Wave Link の明示的な再選択を求める。既定機器への fallback や Windows 全体の既定変更は行わない。停止は接続待ちもキャンセルでき、音声を消去してから capture/player を解放する。通信・機器・キュー超過はミュートして停止し、原音への自動切替や保留音声付きの自動再接続は行わない。WASAPI へ既に渡した短い再生単位は source キューの消去だけでは回収できない。

`VoiceTranslationViewModel` と `VoiceTranslationView` は専用タブで機器・言語・PTT・モード・メーター・翻訳文を表示し、サービスイベントを UI thread へ dispatch する。終了処理では音声送信を明示停止した後に DI の非同期破棄を行う。Wave Link 3 の本体・Audio Driver の導入、翻訳専用チャンネルだけを含む送話用ミックスの設定と、ゲーム側の `Chat Mix (Elgato Virtual Audio)` 等の録音入力の指定は利用者が行う。Wave Link が Windows 11 を必要とすることと、字幕翻訳だけでは導入不要であることを初回案内に表示する。公式ダウンロードと導入説明へのリンクは固定 URL とし、アプリからドライバーの取得・実行や Windows 全体の既定変更は行わない。

### ゲーム送信用PTT

ゲーム送信用PTTはマイク取り込み用PTTと独立した3設定（`GamePushToTalkEnabled` / `GamePushToTalkVirtualKey` / `GamePushToTalkModifiers`）を開始時 clone で固定する。既定は false / F9 / None。同一主キーの二重PTT設定は開始時に拒否する。`VoiceAudioService` の送話セッションだけが `WindowsGamePushToTalkController` を所有し、試聴・字幕経路はキーを操作しない。

`OutputBuffer.Read` が変換済みPCMの実音を検出し、`GamePushToTalkOutputGate` を介して押下する。最初の100 msはゼロを返してキューを保持する。最後の実音位置から計算した読み出し時間＋300 msまで保持し、無音だけでは期限を延長しない。この待機は機器の厳密な drain 通知ではなく、20 ms再生バッファと後段ルーティングのための余裕である。解放期限は25 ms周期のタイマーで確認する。

通常の原音マイクPTT閉鎖では `ClearPendingOutput` で未再生PCMだけを破棄し、すでに `Read` した実音の保持期限は残す。初回100 ms待機中に破棄した場合は実音をまだ渡していないため解放する。停止・モード切替・異常時の `Clear` / `Mute` / `Dispose` は世代を跨ぐ保留を残さず解放し、キー制御失敗は当該ボイス通訳セッションを停止して通知する。

Windowsの低レベルフックは機能有効時だけ専用メッセージスレッドに置く。マイクPTTはそこで記録した物理キー状態を参照し、注入入力で送信許可を開かない。キーは修飾キーから順に注入し、解放は逆順とする。利用者が物理的に保持しているキーは重ねて押さず、ゲームPTT保持中に物理解放されたキーはアプリが保持を引き継ぐ。終了時にも物理保持中のキーを勝手に解放せず、アプリが所有する注入キーだけを解放する。これによりマイクPTTとの修飾キー共有と、利用者の操作を両立させる。

## 実行時のデータフロー

```text
選択プロセス
  → WASAPI Process Loopback (48 kHz / stereo)
  → mono float
  → bounded raw-audio channel
  → InputGainStage
       ├─→ peak dBFS → メイン画面のレベルメーター
       ├─→ StreamingResampler 48 kHz → 16 kHz → FireRed VAD
       └─→ StreamingResampler 48 kHz → 24 kHz → OpenAI
  → VAD の pre-roll / speech / hangover / silence-padding 制御
  → 24 kHz / mono / PCM16 化
  → IRealtimeTranscriber.SendAudio
  → TranscriptDeltaReceived / TranscriptCompleted
  → 文境界・長さ・idle による字幕確定
       ├─ OverlayViewModel → OverlayWindow
       ├─ TranslationLogViewModel → TranslationLogService → TSV
       └─ MainViewModel → 状態・統計表示
```

`AudioCaptureService` の WASAPI callback は native buffer を `ArrayPool` へコピーし、source format から float / mono へ変換して診断と100 ms chunk 化を行い、イベントで通知します。`TranslationPipelineService` のイベントハンドラは受け取ったチャンクを bounded raw-audio channel へ enqueue するだけにし、DSP、VAD、ネットワーク送信は専用 processing task で行います。

処理が追いつかない場合は raw-audio channel と OpenAI client の送信 channel が古いチャンクを捨て、無制限な遅延とメモリ増加を防ぎます。`DroppedAudioChunkCount` が数えるのは OpenAI client の送信 channel 側の drop であり、raw-audio channel 側の drop は個別計測していません。

## OpenAI 翻訳クライアント

`OpenAIRealtimeClient` は Realtime Translation WebSocket へ24 kHz / mono / PCM16 を送信します。`IRealtimeTranscriber` は字幕クライアントと検証用実装の境界として、状態、字幕イベント、送信サンプル数、drop 数を公開します。

pipeline は開始時に OpenAI 設定をスナップショットし、実行中接続へ設定変更を混在させません。変更は次の翻訳開始から反映します。

API key を送る前に endpoint の `wss` scheme、userinfo が空であること、許可 host を検証します。

OpenAI の `/v1/realtime/translations` は標準 Realtime endpoint と wire contract が異なります。session update では server 既定の VAD を使い、音声は `session.input_audio_buffer.append` として送ります。複数世代の transcript event を受理しつつ、同一 response の text / audio-transcript 二重通知は client で抑制します。

字幕用接続は約55分でセッションを更新し、60分の上限による切断を避けます。予定更新の初回は待機せず、失敗後は通常の backoff を使います。

ボイス通訳は `VoiceTranslationService` が専用接続の成功から約55分後に予定更新します。開始停止・モード切替と同じ直列化境界で一時的に送受信を閉じ、旧接続・保留音声・VAD/DSP履歴・ゲームPTTの保持を破棄して、新しい専用clientへ接続します。試聴のオン状態と入出力機器は保持し、更新中のマイク音声と旧clientの通知は持ち越しません。停止操作は更新中の接続待ちもキャンセルし、モード切替は更新と直列に実行します。接続設定は開始時のスナップショットを使い、新接続の成功から次の55分を数えます。障害時の自動再接続は `MaxReconnectAttempts = 0` のまま維持し、予定更新に失敗した場合はボイス通訳だけを停止します。

## 音声処理と VAD

以下は字幕経路の設計です。マイク側の保持時間の計数・失敗時の停止方針は [ボイス通訳の独立セッション](#ボイス通訳の独立セッション) を参照してください。

入力ゲインはリサンプル前の mono float に一度だけ適用し、VAD と送信の双方が同じ信号を見るようにします。自動 limiter は置かず、ゲイン後ピークと clip 表示を見てユーザーが調整します。

OpenAI セッションでは VAD と送信用のリサンプラを48 kHz入力から並列に処理します。16 kHz を経由して24 kHzを作ると8 kHzより上の帯域を復元できず、OpenAIへ送る情報量を減らすためです。

VADの閾値・pre-roll・hangoverは独立したドロップダウンで編集し、一括プリセットによる上書きを廃止する。既定は0.8 / 50 ms / 200 ms。時間は[FireRed VAD公式ストリーミング設定](https://github.com/FireRedTeam/FireRedVAD/blob/c30ec49e8cc69642b0ee65362eba11b9d11c6e54/fireredvad/stream_vad.py#L20-L29)の pad_start_frame=5 / min_silence_frame=20（10 ms単位）に合わせるが、本アプリの最大確率判定・32 msゲートは公式の平滑化・最短発話判定とは異なる。pre-rollは不足しないようフレーム数を切り上げ、50 msで2フレーム=64 msを保持する。hangoverは既存の最初の無音フレーム＋追加フレームの計数を維持し、200 ms設定では合計224 ms相当となる。旧 `VadPreset` は読込時の移行専用nullableフィールドとし、Custom以外では閾値を維持して時間を新既定値へ一度だけ移行する。Customは3値を維持し、移行後はnullを保存から省略して個別選択を尊重する。既存の独自値も選択肢へ含める。

VAD gate は次の情報を組み合わせます。

- pre-roll: speech 判定直前の音声を保持し、発話冒頭を復元する。
- hangover: speech 確率低下後もしばらく送信し、語尾と発話末尾の間を残す。
- silence padding: silence 遷移後も有限時間の無音 PCM を送り、OpenAI 側に保留された出力を押し出す。
- auto pause: 設定時間以上 speech がなければ capture を停止し、無人時の送信継続を防ぐ。

FireRed VAD はストリーミング用 ONNX と CMVN を同梱します。16 kHz の連続波形を int16 相当の振幅へ戻し、25 ms の Povey 窓、10 ms 刻み、80 bin の log Fbank と CMVN でモデルへ渡します。モデルの 8 層分の cache と特徴量用の波形履歴は `StartAsync` ごとにリセットします。既存ゲートの 32 ms フレームと 24 kHz 送信用フレームの対応を保ち、そのフレームで生成できた 10 ms の発話確率の最大値を判定に使います。モデル側の最短発話や平滑化は重ねません。

silence padding 中も実音を pre-roll に保持します。送信する padding はゼロ PCM のまま、次の発話を検出したときだけ保持した実音と現在フレームを一つの PCM バッチで送るため、閾値未満の冒頭を補えます。復元するフレームを連続 enqueue して OpenAI client の bounded queue から冒頭を押し出すことも避けます。発話全体が閾値未満の場合は検出できないため、モデルの切り替えだけで全音源の取りこぼしがなくなるとは限りません。

FireRed VAD を初期化できない場合は `NullVoiceActivityDetector` にフォールバックし、翻訳機能を継続します。この場合は全音声を speech として扱うため、UI で送信量増加を警告します。

## 字幕の状態モデル

OpenAI の partial と completed の出力を、pipeline で以下へ正規化します。

1. delta を現在の `SegmentId` の partial として蓄積し、短い間隔で表示更新する。
2. `。！？.!?` などの文境界を検出したら、文ごとに final を通知して新しい `SegmentId` へ進む。
3. 句点が来ない長文は `MaxPartialChars` で読点、空白、固定位置の順に分割する。
4. OpenAI の completed 通知では、累積 transcript から既確定 prefix を除き、文境界までを確定する。未完の末尾は同じ `SegmentId` の partial として保持し、後続 delta・idle・停止時の確定へ引き継ぐ。hard finalize 境界なら現在の未確定部分を確定する。
5. 文境界や hard finalize 境界が来ない場合は、出力が途絶えた後の idle finalize で残りを確定する。

idle finalize は無音 padding の処理中に発火させません。実効値は、無効値を除き `max(設定値, SilencePaddingMs + 1000 ms)` とします。padding 中に確定すると、その後に押し出された続きが別 `SegmentId` へ分断されるためです。

字幕は内部 lock で生成順にキューへ積み、単一の配信担当が lock の外で購読者へ通知します。通知ごとの購読者 snapshot と配信位置を保持するため、同期 callback から停止が再入しても、残りの購読者を後続字幕より先に処理できます。通常の停止は未確定字幕の確定と通知完了を待ちます。callback 内の停止が開始停止の lock を取得できない場合は後続へ引き継ぎ、callback 内の Dispose も破棄完了を待たずに戻ることで、自身の終了待ちによる deadlock を避けます。callback 外からの Dispose は通知終了と破棄完了を待ちます。

類似する再送は直近履歴で抑制し、OpenAI の再送や累積応答で字幕が重複するのを防ぎます。

## 状態、並行性、ライフサイクル

- Start / Stop は semaphore で直列化し、開始中の停止や素早い再開で capture と client が競合しないようにする。
- セッション開始時に字幕累積、VAD hidden state、pre-roll、両リサンプラ、統計、無音 buffer を初期化する。
- 字幕の raw audio と OpenAI への送信は bounded channel を使い、遅延上限を優先して `DropOldest` とする。ボイス通訳の過負荷時停止は独立セッションの節を参照。
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

設定保存では API キーを暗号化した clone を作り、DI と実行中 client が参照する元 object を変更しません。OpenAI設定のコピーは保存と接続で `OpenAIRealtimeSettings.Clone` を共用し、両経路のコピー項目のずれを防ぎます。ボイス通訳設定は文字列と値型のみで構成し、`MemberwiseClone` で開始時の独立スナップショットを作ります。保存対象は `AppSettings` のフィールドに限定され、廃止した翻訳プロバイダの選択値や固有設定は保存しません。フィールド追加時の作業規則は [AGENTS.md](AGENTS.md#設定シークレット永続化) を参照してください。配布物は `settings.default.json` のみを含み、利用者の `settings.json` を含めません。

overlay 背景色は `BackgroundColorBase` (`#RRGGBB`) と `BackgroundOpacityPercent` を編集用の正本とし、表示で使う `BackgroundColor` (`#AARRGGBB`) を派生させます。旧設定の `BackgroundColor` だけがある場合は sanitize 時に分解し、以後は setter と保存前 sanitize で3フィールドを同期します。

翻訳ログの append、保持期限 cleanup、全削除は単一の channel worker で直列化します。これにより、確定字幕の順序と clear 後に到着した append の意味を維持します。

## 更新設計

更新 feed は `https://rtt.kagayoi.com` の `win-x64` channel に固定し、設定ファイルから変更できません。HTTPS、絶対 URI、userinfo なしを検証したうえで Velopack に渡します。自動確認は起動時の1回だけで、周期確認は行いません。check / download / apply は同じ lock で直列化し、起動時確認と手動確認の競合を防ぎます。

## 開発出力の境界

ローカル専用の `Directory.Build.local.props` が作業ルートを指定し、`Directory.Build.props` が作業別ディレクトリへビルド・中間生成物・テスト結果・パッケージキャッシュを振り分けます。製品利用者の AppData 保存先と OS 全体の一時ディレクトリは変更しません。CI と他の checkout ではローカル専用設定なしで従来の出力先を利用できます。

MSBuild の初期 target より前に logger や response file が扱われるため、`scripts/Dev.ps1` が子プロセスの環境と引数を先に検査し、`Directory.Build.targets` が評価後の実効出力先を検査します。両者は `scripts/DevelopmentOutput.ps1` のパス検証を使い、リンク経由の逸脱を拒否します。この checkout では専用設定がなくても既定出力へ戻さず失敗させます。実行コマンドとこの PC の保存先は [AGENTS.md](AGENTS.md#正規の-restorebuildtest) を参照してください。

## 依存関係更新の境界

`.github/dependabot.yml` は GitHub Actions と3プロジェクトの NuGet 更新を毎週月曜09:00（日本時間）に確認します。minor/patch はまとめ、major は個別 PR とし、特定パッケージの除外は設けていません。

`.github/workflows/dependabot-auto-merge.yml` は既定ブランチ側の処理で GitHub API を使い、PR のコードを checkout・実行せずにマージ可否を判定します。同一リポジトリの Dependabot PR に限定し、コミット署名と作成者、許可した依存関連ファイルの差分、最新 head に対する `ci.yml` の成功、他の check/status を確認します。GitHub Actions の差分は `uses` の参照更新に限定します。マージ直前にも head/base とマージ可能状態を再確認し、GitHub のマージ要件に従います。これにより権限付き処理から PR コードを実行する経路を避け、依存更新の検証を通常の CI に分離します。更新種別の major/minor/patch 自体はマージ判定の除外条件ではありません。

## 採用した判断とトレードオフ

| 判断 | 理由 | トレードオフ |
| --- | --- | --- |
| Process Loopback を採用 | ゲームやプレーヤーなど対象 PID の音だけを翻訳できる | Windows と対応 WASAPI 環境に限定される |
| Core と UI を分離 | 音声・接続・永続化を Avalonia なしで検証できる | composition root で具体 client をまとめて構築する必要がある |
| 字幕 client とボイス通訳 client を分離 | 同時実行しても接続・言語・停止状態を混在させない | ボイス通訳用の専用 client factory とライフサイクル管理が必要 |
| OpenAI用のVADと送信を48 kHzから並列リサンプル | VAD の16 kHz制約を守りつつOpenAI向け高域を維持する | OpenAIセッションではリサンプラを2つ進めるCPUコストがある |
| 字幕経路の bounded channel + `DropOldest` | メモリと遅延を有限に保つ | 過負荷時は古い音声が欠落する。OpenAI送信側は計測するが、raw-audio側は個別計測しない |
| 字幕 VAD 初期化失敗時は全送信へフォールバック | VAD asset / native runtime 障害で字幕翻訳が起動不能になることを避ける | 無音抑制が失われ、送信量が増える。マイク VAD の失敗はボイス通訳を停止する |
| subtitle finalization を多段化 | 句読点・長さ・完了通知・idle を組み合わせて字幕を確定できる | 分割条件が相互依存するため回帰テストが必要 |
| API key を DPAPI CurrentUser で保存 | 平文保存を避け、追加サービスを不要にする | 別ユーザー・別PCへ暗号文を移しても復号できない |
| 更新 URL をコードで固定 | feed 差し替えによる任意配布物への誘導を防ぐ | mirror や private feed を利用者が選べない |
