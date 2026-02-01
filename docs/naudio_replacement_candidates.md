# NAudio で置き換え可能な自前実装の調査結果

## 1. すでに NAudio に移行済み

| 箇所 | 内容 | 状態 |
|------|------|------|
| プロセスループバックキャプチャ | `AudioCaptureService` のキャプチャ開始 | `WasapiCapture.CreateForProcessCaptureAsync(processId, includeProcessTree: true)` を直接使用するように修正済み |
| オーディオデバイス列挙 | `MainViewModel.GetActiveAudioProcessIds` | 既に NAudio の `MMDeviceEnumerator` / `AudioSessionManager` を使用 |
| WAV デバッグ出力 | `AudioCaptureService` の debug_audio*.wav | 既に NAudio の `WaveFileWriter` を使用 |

---

## 2. NAudio で置き換え可能な自前実装

### 2.1 byte[] → float[] 変換（`AudioCaptureService.ConvertToFloat`）

**現状**: PCM 16/32bit および IeeeFloat の byte 配列を自前で float 配列に変換している。

**NAudio での代替**:
- `RawSourceWaveStream` に byte 配列＋`WaveFormat` を渡し、`ToSampleProvider()` で `ISampleProvider` に変換してから Read する。

```csharp
// 例: 自前 ConvertToFloat の代わり
var waveStream = new RawSourceWaveStream(bufferCopy, 0, bytesRecorded, sourceFormat);
var sampleProvider = waveStream.ToSampleProvider();
var sampleCount = bytesRecorded / sourceFormat.BlockAlign; // またはチャンネル等から算出
var samples = new float[sampleCount];
sampleProvider.Read(samples, 0, sampleCount);
```

**効果**: フォーマットごとの分岐や変換ロジックを NAudio に任せられ、Extensible などにも対応しやすい。

---

### 2.2 リサンプリング（`AudioCaptureService.Resample`）

**現状**: 線形補間による簡易リサンプリング（float 配列を別サンプルレートの float 配列に変換）。

**NAudio での代替**:
- `WdlResamplingSampleProvider`（WDL リサンプラによる高品質リサンプリング）。
- 入力は `ISampleProvider` なので、現在の float 配列を「読み取り専用の ISampleProvider」でラップする必要がある（小さなアダプタークラスを作成するか、既存の `RawSourceWaveStream`＋`ToSampleProvider` と組み合わせる）。

```csharp
// 例: float[] を一度 ISampleProvider として扱い、リサンプリング後に読む
// 入力用の ISampleProvider（float[] を Read するだけの薄いラッパー）を用意し、
var resampler = new WdlResamplingSampleProvider(inputSampleProvider, targetSampleRate);
// resampler.Read(buffer, 0, count);
```

**効果**: 音質・周波数特性の面で WDL リサンプルの方が有利。実装はストリーム型のため、上記の「float 配列用 ISampleProvider」を用意する小さな refactor が必要。

---

### 2.3 ステレオ→モノラル（`AudioCaptureService.ConvertToMono`）

**現状**: マルチチャンネルのサンプルを平均してモノラル化している。

**NAudio での代替**:
- `StereoToMonoSampleProvider`（2ch 用）や、`WaveExtensionMethods.ToMono(this ISampleProvider)`。
- 入力が `ISampleProvider` のため、現在の float 配列を「ISampleProvider として読める形」で渡す必要がある（2.2 と同様のラッパーで対応可能）。

**効果**: 左右ボリュームの指定や、NAudio 側の仕様変更に追従しやすくなる。2ch 以外の多チャンネルは NAudio の対応範囲要確認。

---

## 3. 残しておいてよい／そのままの実装

| 箇所 | 理由 |
|------|------|
| `ProcessLoopbackCapture.cs` クラス本体 | メインのキャプチャ経路は `CreateForProcessCaptureAsync` に移行済み。Tests がリフレクションで参照しているため、クラスは残している。将来的にテストを NAudio 前提に変更すれば削除検討可能。 |
| `VADService` 周りの float 配列処理 | ONNX VAD 用の前処理であり、NAudio の対象外。 |
| キュー・バッファ管理（`_audioBuffer` / `_processingQueue`） | アプリ固有のチャンク単位の制御のため、NAudio のストリームにそのまま置き換える必要はない。 |

---

## 4. 実装の優先度の目安

1. **高**: `ConvertToFloat` を `RawSourceWaveStream` ＋ `ToSampleProvider` に置き換え  
   - 変更範囲が局所的で、既存の byte 配列・WaveFormat の流れを活かしやすい。
2. **中**: `Resample` を `WdlResamplingSampleProvider` ベースに変更  
   - 音質向上のメリットはあるが、float 配列 ↔ ISampleProvider の橋渡しを用意する必要あり。
3. **中**: `ConvertToMono` を `StereoToMonoSampleProvider`（または ToMono）に置き換え  
   - 2ch が主であれば、float 配列用の簡単な ISampleProvider ラッパーで対応可能。

上記の順で段階的に NAudio に寄せていくことを推奨する。
