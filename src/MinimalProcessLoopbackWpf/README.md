# Minimal Process Loopback Wpf

Process Loopback の**最小検証用** WPF アプリです。  
RealTimeTranslator と同じ 1llum1n4t1s.NAudio を使い、**UI スレッドでだけ** CreateForProcessCaptureAsync → StartRecording を呼ぶ構成で、実音が取れるか比較できます。

## 使い方

1. **ビルド**
   - ソリューションで `MinimalProcessLoopbackWpf` をスタートアップにし F5、または  
     `dotnet run --project src/MinimalProcessLoopbackWpf` で起動。

2. **プロセスの選択**
   - ドロップダウンに、オーディオセッションを持つプロセスが表示されます。  
     一覧が空の場合は、音を再生しているアプリを起動して「更新」をクリック。
   - キャプチャには「オーディオセッションを持つプロセス」（Chrome なら親 PID）が使われます。

3. **キャプチャ開始**
   - 「キャプチャ開始」をクリック。  
     - ログに `ThreadId`, `SyncContextNull`, `sameThread` が出力されます。  
     - 続けて `#1` 以降で `max`, `avg`, `raw16=[min,max]` が出力されます。

4. **比較のポイント**
   - **Minimal で raw16 に幅がある（例: raw16=[-32000,31000]）のに、RealTimeTranslator では raw16=[-1,1] のまま**  
     → RealTimeTranslator 側のスレッド／コンテキストや他処理の影響を疑う。
   - **Minimal でも raw16=[-1,1] のまま**  
     → 環境（デバイス／ドライバ／WASAPI／対象アプリ）の要因が大きい。

## NAudio の参照

- **現状**: ローカルの `1llum1n4t1s.NAudio` を **ProjectReference** で参照しています。  
  NuGet の 1.0.22 には Process Loopback の STA 修正（IntPtr 返却・STA での RCW 構築・COM 呼び出しの syncContext 実行）が入っていない可能性があり、パッケージ参照のままでは `raw16=[-1,1]` のプレースホルダーのみになることがあります。
- **切り分け**: この状態で実音が取れれば、本番は「STA 修正を含んだ NAudio の新バージョン」をパッケージで参照するか、必要な間だけ ProjectReference のままにしてください。修正入りパッケージが出たら `MinimalProcessLoopbackWpf.csproj` の ProjectReference を PackageReference に戻して問題ありません。

## 参照

- 1llum1n4t1s.NAudio の Process Loopback ドキュメント（同一リポジトリの Docs/ProcessLoopbackCapture.md 等）
