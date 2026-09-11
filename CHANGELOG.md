# 変更履歴

Git のバージョン記録・コミット差分と既存の変更履歴をもとに、確認できた版ごとの変更点をまとめています。「Git 記録日」は公開日ではありません。番号の欠番だけから未確認のリリースは補っていません。

## 未リリース

## [1.0.54] — Git 記録日: 2026-09-12

- OpenAI / Gemini / Soniox / Speechmatics / Azure の設定・導入案内を現行仕様に更新
- 利用者向け機能の変更はなく、音声処理・UI・テスト基盤の依存ライブラリを更新

出典: [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/1b2d5971144934d57d1a1785f7a55467300871e7...release/1.0.54)。

## [1.0.53] — Git 記録日: 2026-08-31

- キャプチャ異常停止とセッション統計を修正
- 依存関係を更新しリリースビルドを安定化

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/1b2d5971144934d57d1a1785f7a55467300871e7) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/041cdb2beee907c50043915f3c1e3f5087dadffb...1b2d5971144934d57d1a1785f7a55467300871e7)。

## [1.0.52] — Git 記録日: 2026-08-26

- NAudio本家移行とセッション停止処理を改善
- NuGet 依存を patch 更新

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/041cdb2beee907c50043915f3c1e3f5087dadffb) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/8894880b2a19e6a8d6365c8cf09a1ba47ea937c3...041cdb2beee907c50043915f3c1e3f5087dadffb)。

## [1.0.51] — Git 記録日: 2026-08-11

- 設定保存の値消失バグ修正と翻訳ログ・デバッグ録音の多プロバイダ対応
- Grok 監査指摘4件を修正 (設定保存の値消失・翻訳ログ言語・デバッグ録音レート)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/8894880b2a19e6a8d6365c8cf09a1ba47ea937c3) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/721990997c51848c997058a6150d11f9f67a1291...8894880b2a19e6a8d6365c8cf09a1ba47ea937c3)。

## [1.0.50] — Git 記録日: 2026-07-30

- ボイスチャットを拾う VAD 調整とレベルメーター改善

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/721990997c51848c997058a6150d11f9f67a1291) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/1045187b85524f6a3ae07ee5adcec84dc5c1890d...721990997c51848c997058a6150d11f9f67a1291)。

## [1.0.49] — Git 記録日: 2026-07-29

- 字幕の発話ごと分割を修正 (VAD 回帰 + アイドル確定)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/1045187b85524f6a3ae07ee5adcec84dc5c1890d) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/17c9b5e44d47af6de51f60655d94b7a3417c62e8...1045187b85524f6a3ae07ee5adcec84dc5c1890d)。

## [1.0.48] — Git 記録日: 2026-07-27

- 依存更新とドメイン移行の反映
- NuGet 依存を patch/minor 更新
- 配信ドメインを nephilim.jp から kagayoi.com へ移行

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/17c9b5e44d47af6de51f60655d94b7a3417c62e8) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/49ef3680c973e7947bca92285d4891224ed28d5d...17c9b5e44d47af6de51f60655d94b7a3417c62e8)。

## [1.0.47] — Git 記録日: 2026-07-21

- ショートカット移行と依存パッケージ更新

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/49ef3680c973e7947bca92285d4891224ed28d5d) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/559a569ce6758a3e762a5609a20a023641a0a2a5...49ef3680c973e7947bca92285d4891224ed28d5d)。

## [1.0.46] — Git 記録日: 2026-06-27

- 依存パッケージ最新化 + 配信エッジキャッシュパージ追加
- 翻訳プロバイダに Soniox / Speechmatics / Azure を追加 (5 プロバイダ対応) (#88)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/559a569ce6758a3e762a5609a20a023641a0a2a5) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/3f755990e9c6847d0370f18eb904cbb561a41b74...559a569ce6758a3e762a5609a20a023641a0a2a5)。

## [1.0.45] — Git 記録日: 2026-06-14

- 翻訳プロバイダ切替 (OpenAI/Gemini) 公開
- OpenAI/Gemini 翻訳プロバイダ切替機能を追加 (#86)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/3f755990e9c6847d0370f18eb904cbb561a41b74) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/3e0e366bcd6330a06e84d8ffbf51e27cd3bc3a00...3f755990e9c6847d0370f18eb904cbb561a41b74)。

## [1.0.44] — Git 記録日: 2026-06-12

- 配布物にコード署名を導入し、依存ライブラリ・配布ツール・製品紹介ページを更新。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/3e0e366bcd6330a06e84d8ffbf51e27cd3bc3a00) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/3ed13b685d04c04fefba3b595e4c71511e406afd...3e0e366bcd6330a06e84d8ffbf51e27cd3bc3a00)。

## [1.0.43] — Git 記録日: 2026-06-01

- 自動更新ダイアログ更新 (VelopackUpdateDialog.Avalonia 1.0.6 / Velopack 1.1.1)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/3ed13b685d04c04fefba3b595e4c71511e406afd) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/ea279f3779ad5ff2603e11545a736fd13fc67594...3ed13b685d04c04fefba3b595e4c71511e406afd)。

## [1.0.42] — Git 記録日: 2026-05-31

- VAD プリセット調整 (threshold -0.1 シフト + preroll/hangover 再調整)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/ea279f3779ad5ff2603e11545a736fd13fc67594) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/1b5cb45da9e62814d5fb7ef81889bb405996bbb0...ea279f3779ad5ff2603e11545a736fd13fc67594)。

## [1.0.41] — Git 記録日: 2026-05-31

- 翻訳を継続したまま字幕オーバーレイだけを非表示にする設定を追加。
- 音声レベル監視の開始・停止・破棄が競合した場合のキャプチャ残留を修正。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/1b5cb45da9e62814d5fb7ef81889bb405996bbb0) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/ec82f121ab0ea321ca353bb8748edea1d0484720...1b5cb45da9e62814d5fb7ef81889bb405996bbb0)。

## [1.0.40] — Git 記録日: 2026-05-30

- フォント反映修正・送信音声WAV保存のライブ切替・音声処理タブのレベルメーター追加

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/ec82f121ab0ea321ca353bb8748edea1d0484720) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/1daa0207c80593ca3fea241a47319513faa89167...ec82f121ab0ea321ca353bb8748edea1d0484720)。

## [1.0.39] — Git 記録日: 2026-05-29

- VelopackUpdateDialog.Avalonia を 1.0.5 に更新 (NuGet 最新化)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/1daa0207c80593ca3fea241a47319513faa89167) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/1beeccb115ea972c17a386da2c39317d76e8d9ca...1daa0207c80593ca3fea241a47319513faa89167)。

## [1.0.38] — Git 記録日: 2026-05-29

- packages.lock.json の win-x64 RID セクションを atomic に復元

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/1beeccb115ea972c17a386da2c39317d76e8d9ca) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/e5af0bedd7eff3a06185405e0948dcaed03d1c09...1beeccb115ea972c17a386da2c39317d76e8d9ca)。

## [1.0.37] — Git 記録日: 2026-05-29

- 依存パッケージ更新バッチ

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/e5af0bedd7eff3a06185405e0948dcaed03d1c09) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/56125dd78ced913b3a51e26bbfe0ff53c9dba897...e5af0bedd7eff3a06185405e0948dcaed03d1c09)。

## [1.0.36] — Git 記録日: 2026-05-28

- デバッグ録音のファイル名と初期化失敗を安全に扱い、録音に失敗しても翻訳を継続。入力音声のサンプルレートが想定と異なる場合の警告を追加。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/56125dd78ced913b3a51e26bbfe0ff53c9dba897) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/584cc65069a2c12c1ce1ea9219083cbc87ea7e30...56125dd78ced913b3a51e26bbfe0ff53c9dba897)。

## [1.0.35] — Git 記録日: 2026-05-27

- リリース時に R2 旧 nupkg を自動削除する cleanup step 追加

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/584cc65069a2c12c1ce1ea9219083cbc87ea7e30) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/6a44e656d9e164d6e62c75451c54969c537b4110...584cc65069a2c12c1ce1ea9219083cbc87ea7e30)。

## [1.0.34] — Git 記録日: 2026-05-27

- Microsoft.NET.Test.Sdk 18.5.1 → 18.6.0 アップデート

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/6a44e656d9e164d6e62c75451c54969c537b4110) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/5f797d8903f8b091d4bf0ab261718171baade020...6a44e656d9e164d6e62c75451c54969c537b4110)。

## [1.0.33] — Git 記録日: 2026-05-27

- 音声受信と字幕更新の不要なメモリ割り当て・UI 通知を削減し、終了時の資源解放と通信エラーの記録を改善。
- Velopack を更新。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/5f797d8903f8b091d4bf0ab261718171baade020) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/61dfc1dd2fc18928bb1e8678448505c10e0988aa...5f797d8903f8b091d4bf0ab261718171baade020)。

## [1.0.32] — Git 記録日: 2026-05-27

- 設定保存で無音補完時間と字幕文字数の上限が失われる問題を修正。
- 認証・録音停止のエラー表示を改善し、重複する音量正規化処理を削除。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/61dfc1dd2fc18928bb1e8678448505c10e0988aa) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/d96eaf5ae546fac0d6349d163f57e5394fc8c62e...61dfc1dd2fc18928bb1e8678448505c10e0988aa)。

## [1.0.31] — Git 記録日: 2026-05-27

- VAD 閾値調整 + 字幕重複表示バグ修正
- 類似重複抑制 only 時の _accumulatedText 削除漏れ修正
- VAD プリセットの preroll/hangover を一律 +200ms シフト
- packages.lock.json を win-x64 RID 込みで再生成

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/d96eaf5ae546fac0d6349d163f57e5394fc8c62e) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/07dc7d89bf6c710a09ccfbf50196252f28f2039c...d96eaf5ae546fac0d6349d163f57e5394fc8c62e)。

## [1.0.30] — Git 記録日: 2026-05-26

- 入力プリプロセス DSP 4 段の追加と VAD 閾値シフト
- packages.lock.json を win-x64 RID 込みで再生成

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/07dc7d89bf6c710a09ccfbf50196252f28f2039c) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/c87c4be67e454849456dd2a9257ad06c717afd40...07dc7d89bf6c710a09ccfbf50196252f28f2039c)。

## [1.0.29] — Git 記録日: 2026-05-25

- 音声入力を 48 kHz からの二段階リサンプリングへ変更し、再接続回数の競合と配布後の確認処理を改善。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/c87c4be67e454849456dd2a9257ad06c717afd40) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/397343ee08db2408d106f8a46c19c7ae5c3bd45e...c87c4be67e454849456dd2a9257ad06c717afd40)。

## [1.0.28] — Git 記録日: 2026-05-24

- 句点のない長い字幕の分割と、再接続後に字幕が停止する問題を修正。
- 翻訳ログの表計算向け出力を保護し、音声検出の代替動作やログの取り扱いを画面に表示。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/397343ee08db2408d106f8a46c19c7ae5c3bd45e) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/060e2c872c19411dd3c0995df7efd1f1a3d8c215...397343ee08db2408d106f8a46c19c7ae5c3bd45e)。

## [1.0.27] — Git 記録日: 2026-05-24

- 対症療法 612 行棚卸し + 1 系統二段リサンプラ + 無音 PCM 5 秒継続送信

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/060e2c872c19411dd3c0995df7efd1f1a3d8c215) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/b6a1f4057fd011074c6374e17589a37d7db258e8...060e2c872c19411dd3c0995df7efd1f1a3d8c215)。

## [1.0.26] — Git 記録日: 2026-05-24

- 能動的区切り (VAD Silence + input_audio_buffer.commit) + リスケ無限ループ修正

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/b6a1f4057fd011074c6374e17589a37d7db258e8) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/a4dccc1dd6a063604ec71a2102ef402248420830...b6a1f4057fd011074c6374e17589a37d7db258e8)。

## [1.0.25] — Git 記録日: 2026-05-24

- partial 字幕の中途消去バグ修正 + デバッグログ詳細化

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/a4dccc1dd6a063604ec71a2102ef402248420830) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/f054bbd48519721309a7c5cdd3cd36b1c2cf177c...a4dccc1dd6a063604ec71a2102ef402248420830)。

## [1.0.24] — Git 記録日: 2026-05-24

- 字幕の中途切れバグの構造的解決 (partial 連結方式)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/f054bbd48519721309a7c5cdd3cd36b1c2cf177c) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/a63fca57513847740773eb931107bc64f7f6f5bc...f054bbd48519721309a7c5cdd3cd36b1c2cf177c)。

## [1.0.23] — Git 記録日: 2026-05-24

- 音声処理パイプラインを 48k→24k 直結に大改修 + 入力側境界アーティファクト排除

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/a63fca57513847740773eb931107bc64f7f6f5bc) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/3c40b25f83c7771cd87583da017d82d0926c0981...a63fca57513847740773eb931107bc64f7f6f5bc)。

## [1.0.22] — Git 記録日: 2026-05-23

- ProcessLoopbackMode enum バグ修正 + ウィンドウリサイズ対応 + 翻訳前処理整理

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/3c40b25f83c7771cd87583da017d82d0926c0981) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/09783cf566cb1f74382333e6700e99d54d3755e5...3c40b25f83c7771cd87583da017d82d0926c0981)。

## [1.0.21] — Git 記録日: 2026-05-23

- 字幕途切れ修正 + 翻訳遅延の加速 + ホットパス最適化

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/09783cf566cb1f74382333e6700e99d54d3755e5) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/c421cdfd1ea801e844edbb5cfd7faf3a6952278b...09783cf566cb1f74382333e6700e99d54d3755e5)。

## [1.0.20] — Git 記録日: 2026-05-22

- Bigram Jaccard 類似重複抑制を追加

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/c421cdfd1ea801e844edbb5cfd7faf3a6952278b) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/aceb39d58c03fec95e9bc5b28d8858a1baf1c963...c421cdfd1ea801e844edbb5cfd7faf3a6952278b)。

## [1.0.19] — Git 記録日: 2026-05-21

- OnTranscriptDelta O(n²)→O(n) 最適化 + ProcessLoopbackMode 修正 + テスト287件追加 + ランディングページ

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/aceb39d58c03fec95e9bc5b28d8858a1baf1c963) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/55f44b258ff8ec0157c7569eb8deef0cb217c3ac...aceb39d58c03fec95e9bc5b28d8858a1baf1c963)。

## [1.0.18] — Git 記録日: 2026-05-21

- 翻訳ログオートスクロール + 未確定確定 + 60分セッション再接続修正

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/55f44b258ff8ec0157c7569eb8deef0cb217c3ac) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/abfe4ef8b7ae8efa39675a8563de99e697251200...55f44b258ff8ec0157c7569eb8deef0cb217c3ac)。

## [1.0.17] — Git 記録日: 2026-05-20

- ドキュメント更新

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/abfe4ef8b7ae8efa39675a8563de99e697251200) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/8ec7b98bd5ae12e2e0fc935f6c8167d0c0d010ae...abfe4ef8b7ae8efa39675a8563de99e697251200)。

## [1.0.16] — Git 記録日: 2026-05-20

- R2 移行後初リリース + 旧クライアント救済の踏み台 publish
- Velopack 配信元を Cloudflare R2 (rtt.nephilim.jp) に切替
- 翻訳ログ機能 + APIキー取得ガイド + VelopackUpdateDialog 日本語化 + CostEstimator 料金更新

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/8ec7b98bd5ae12e2e0fc935f6c8167d0c0d010ae) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/f7d5f3312f4f0d9d4ba1609b13074f41ebf09595...8ec7b98bd5ae12e2e0fc935f6c8167d0c0d010ae)。

## [1.0.15] — Git 記録日: 2026-05-19

- 音声検出のプリセット、字幕の背景色・濃さ・太字設定、統計表示を改善。
- 翻訳の開始・停止の競合、更新先の検証、音声検出モデルがない場合の動作を修正。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/f7d5f3312f4f0d9d4ba1609b13074f41ebf09595) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/6dac81569edf3840ecfb956152c7a05399effb49...f7d5f3312f4f0d9d4ba1609b13074f41ebf09595)。

## [1.0.14] — Git 記録日: 2026-05-18

- 音声バッファの再利用と画面のデータバインドを改善し、音声検出のフレーム長を動的に計算するよう変更。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/6dac81569edf3840ecfb956152c7a05399effb49) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/fd36ac63bda590ec79f4ae9762260fe86a6da612...6dac81569edf3840ecfb956152c7a05399effb49)。

## [1.0.13] — Git 記録日: 2026-05-17

- 字幕の表示時間を設定 UI から変更可能に
- 同じ翻訳が二重表示されるバグ修正 (response_id ベースの重複検出)
- 翻訳遅延の追従性をさらに詰める (Channel 容量半減 + Throttle 短縮)
- revert(pipeline): 字幕単位 +1 (SentencesPerSegment=2) を撤回
- アップデート確認フローを Komorebi 互換に揃える

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/fd36ac63bda590ec79f4ae9762260fe86a6da612) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/7123b2317024c6d68b6093f947dfb79a31111df6...fd36ac63bda590ec79f4ae9762260fe86a6da612)。

## [1.0.12] — Git 記録日: 2026-05-17

- 字幕の背景色と再接続をまたぐ字幕処理を修正し、日本語フォントを同梱。
- 設定の暗号化失敗と音声キャプチャ停止の競合を安全に処理。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/7123b2317024c6d68b6093f947dfb79a31111df6) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/84cb39c1c8e28038f212c315b714c621320f111a...7123b2317024c6d68b6093f947dfb79a31111df6)。

## [1.0.11] — Git 記録日: 2026-05-16

- SelfUpdateWindow XAML 型解決バグ修正 (致命的 update path 破損の解消)

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/84cb39c1c8e28038f212c315b714c621320f111a) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/8346bd2ad798b8e6f9724d62b6f031ac65e9551e...84cb39c1c8e28038f212c315b714c621320f111a)。

## [1.0.10] — Git 記録日: 2026-05-16

- trailing emit ロジックの SegmentId / _lastFinalizedTranscript バグ修正

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/8346bd2ad798b8e6f9724d62b6f031ac65e9551e) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/77038a17291a977318439f3f2dc9f06d4b0e7167...8346bd2ad798b8e6f9724d62b6f031ac65e9551e)。

## [1.0.9] — Git 記録日: 2026-05-16

- 自前更新ダイアログ Komorebi 流統一 + 字幕長文化バグ修正

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/77038a17291a977318439f3f2dc9f06d4b0e7167) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/4bdba66fb2146487eb3892d99a47ec8259e03aa7...77038a17291a977318439f3f2dc9f06d4b0e7167)。

## [1.0.8] — Git 記録日: 2026-05-16

- 文区切り診断ログ追加 & Velopack ロック競合対策
- v1.0.7 統合: turn_detection 削除 / OutputLanguage 最新化 / プロセス終了強化 / タイトルバー version

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/4bdba66fb2146487eb3892d99a47ec8259e03aa7) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/3ffd4cfb01cf76536f7d30f896c6287e92fc40b6...4bdba66fb2146487eb3892d99a47ec8259e03aa7)。

## [1.0.7] — Git 記録日: 2026-05-16

- settings.json と logs を Roaming AppData に移行（API キー消失問題）

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/3ffd4cfb01cf76536f7d30f896c6287e92fc40b6) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/cfa10a01bc30587fef438b1b31d2a2e1c8ea36d9...3ffd4cfb01cf76536f7d30f896c6287e92fc40b6)。

## [1.0.6] — Git 記録日: 2026-05-16

- Velopack 自動更新を GithubSource 化 + latest リリースに全 assets 配置
- Update.Enabled のデフォルトを true に（自動更新が走らない問題）

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/cfa10a01bc30587fef438b1b31d2a2e1c8ea36d9) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/7648f047ef05cdaacc5ac3ee1eb05114f401c800...cfa10a01bc30587fef438b1b31d2a2e1c8ea36d9)。

## [1.0.5] — Git 記録日: 2026-05-16

- 起動時更新チェックで AutoApply=false が無視されアプリが固まる重大バグ修正

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/7648f047ef05cdaacc5ac3ee1eb05114f401c800) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/c8f209396471e2e47ff01dcd05e7a14a0c2c1a12...7648f047ef05cdaacc5ac3ee1eb05114f401c800)。

## [1.0.4] — Git 記録日: 2026-05-16

- Window アイコン + turn_detection パス修正
- 停止フリーズタイムアウト多重防御
- API キー必須化 (UI 警告 + CanStart ガード)
- メイン画面アクリル化 + 拡張タイトルバー (Lhamiel 統一)
- ConnectionState.cs 追加 + .gitignore の models/ パターン修正
- ProcessMessage 型変更 + VirtualAudioDevice 削除 + Logger 不正パス耐性

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/c8f209396471e2e47ff01dcd05e7a14a0c2c1a12) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/6e164235b90378d64dfebdd7865c9dd9362168ea...c8f209396471e2e47ff01dcd05e7a14a0c2c1a12)。

## [1.0.3] — Git 記録日: 2026-05-16

- API キーを DPAPI で透過暗号化（DpapiHelper, SettingsService 経由）
- settings.json を %LocalAppData% に移行（Velopack 更新時の消失防止）
- atomic write 化（temp + File.Replace でクラッシュ耐性）
- Endpoint / FeedUrl をホスト allowlist で検証 （wss=api.openai.com / https=github.com）
- 単一インスタンス Mutex を Local\ 化（別ユーザー DoS 防止）
- DisconnectAsync で _cts.Cancel() を先に呼んで停止フリーズ解消

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/6e164235b90378d64dfebdd7865c9dd9362168ea) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/313649c4ad3f9925135e5574010597585eb1d29d...6e164235b90378d64dfebdd7865c9dd9362168ea)。

## [1.0.2] — Git 記録日: 2026-01-26

- ライブラリ差し替え
- プロセスループバック初期化の再試行を追加
- 字幕設定の保存と画面クローズを改善
- 設定画面を簡素化しドロップダウン化
- モデル読み込み表示と初期選択を改善
- CsWin32 (C# Source Generator for Win32 API) を導入して音声キャプチャの堅牢性を向上

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/313649c4ad3f9925135e5574010597585eb1d29d) / [変更差分](https://github.com/1llum1n4t1s/RealTimeTranslator/compare/6fcad9151fb16e6b73cf3ab3c47fb2b9074fd813...313649c4ad3f9925135e5574010597585eb1d29d)。

## [1.0.0] — Git 記録日: 2026-01-12

- 版情報を Directory.Build.props へ集約し、Velopack の配布設定を更新。

出典: [版の記録](https://github.com/1llum1n4t1s/RealTimeTranslator/commit/6fcad9151fb16e6b73cf3ab3c47fb2b9074fd813)。
