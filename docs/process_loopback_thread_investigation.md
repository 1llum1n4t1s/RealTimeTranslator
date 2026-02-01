# Process Loopback スレッド・SynchronizationContext 調査

## 調査結果サマリ

- **CreateForProcessCaptureAsync / StartRecording をどのスレッドで呼んでいるか**
- **キャプチャ開始前に SynchronizationContext が変わっていないか**

をコード上で追い、必要対策と診断ログを入れました。

## 1. CreateForProcessCaptureAsync / StartRecording の呼び出しスレッド

### 経路

| 処理 | 実行箇所 | スレッド |
|------|----------|----------|
| 「翻訳開始」クリック | MainViewModel.StartAsync (RelayCommand) | **UI スレッド**（WPF のボタンから） |
| uiContext 取得 | StartAsync 先頭の `SynchronizationContext.Current` | **UI スレッド**（通常は WPF コンテキスト） |
| キャプチャ開始 | StartCaptureWithRetryAsync(..., uiContext) | 呼び出し時は UI（そのあと await で切り替わる） |
| 実体の create/start | RunFullCaptureStartOnContextAsync → **context.Post(...)** | **Post コールバック = UI スレッド** |
| CreateForProcessCaptureAsync | Post 内の async void Run() 内で await | **呼び出しも継続も UI スレッド**（ConfigureAwait(true) のため） |
| AttachCaptureEvents / StartRecording() | 上記 await の直後 | **同じ UI スレッド** |

- `captureCreationContext != null` のときは、**CreateForProcessCaptureAsync も StartRecording() も、渡した SynchronizationContext（UI）上で実行される**設計です。
- NAudio の `WasapiCapture` は、**CreateForProcessCaptureAsync の await の継続が走るスレッド**の `SynchronizationContext.Current` を保持するため、上記の流れなら UI スレッドのコンテキストが保存されます。

### 分岐（captureCreationContext == null）

- `StartCaptureWithRetryAsync(..., null)` のときは、**呼び出し元スレッド**で `CreateForProcessCaptureAsync(...).ConfigureAwait(false)` と `StartRecording()` を実行しています。
- この経路で UI 以外（スレッドプール等）から呼ぶと、Process Loopback の要件を満たさず、E_NOINTERFACE やプレースホルダー音声になる可能性があります。現在の UI からの開始経路では `uiContext` を渡しているため、この分岐は使っていません。

## 2. SynchronizationContext の取得と「変わっていないか」

### 取得タイミング

- **StartAsync の先頭**で `var uiContext = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Application.Current.Dispatcher);` を実行しています。
- ボタンクリックで StartAsync が動くため、通常は **UI スレッドで Current が WPF のコンテキスト**になります。
- **Current が null の場合**（別スレッドからコマンドが実行された場合など）は、**Dispatcher から明示的に DispatcherSynchronizationContext を生成**して使い、常に「UI 用の 1 つのコンテキスト」でキャプチャ開始コードが動くようにしました。

### キャプチャ開始前にコンテキストが変わらないか

- **uiContext は変数に保持**しているため、`await _pipelineService.StartAsync(...)` の後や、その他の await の後で `SynchronizationContext.Current` が別スレッドで null や別のコンテキストになっても、**キャプチャ開始には「最初に取得した uiContext」が使われます**。
- したがって、「キャプチャ開始時点で別のコンテキストにすり替わっている」ことはありません。**常に同じ UI 用コンテキストに Post されている**かどうかは、追加した診断ログで確認できます。

### 無音フォールバック時（TryFallbackToWindowPidAfterSilenceAsync）

- `RunOnUiThreadAsync` 内で `StartCaptureWithRetryAsync(..., SynchronizationContext.Current ?? new DispatcherSynchronizationContext(...))` を呼んでいます。
- ここは **RunOnUiThreadAsync により UI スレッドで実行されている**ため、同じく UI のコンテキストで create/start が行われます。

## 3. 追加した診断ログ

- **MainViewModel.StartAsync**  
  - `SynchronizationContext.Current` が null だった場合に限り、  
    `[Capture] StartAsync: SynchronizationContext.Current was null, using Dispatcher-based context` を 1 回出力。
- **AudioCaptureService.RunFullCaptureStartOnContextAsync**（Post コールバック内）  
  - Post コールバック開始時:  
    `[Capture] Post callback started: ThreadId=..., SyncContextNull=...`  
  - CreateForProcessCaptureAsync の await 継続直後:  
    `[Capture] CreateForProcessCaptureAsync continuation: ThreadId=..., SyncContextNull=..., sameThread=...`  

これで以下を確認できます。

- CreateForProcessCaptureAsync を **await しているスレッド** = Post コールバックのスレッド（UI）
- **await の継続**が同じスレッドで動いているか（sameThread=true が望ましい）
- その時点で **SynchronizationContext.Current が null か**

## 4. 参照

- リポジトリ内: `1llum1n4t1s.NAudio/Docs/ProcessLoopbackCapture.md`
- 要件: CreateForProcessCaptureAsync の **await の継続**と **StartRecording()** を同一 STA（UI）スレッドで実行し、そのスレッドの SynchronizationContext を WasapiCapture に使わせる。
