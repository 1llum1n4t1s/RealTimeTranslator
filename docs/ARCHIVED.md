# 📦 ARCHIVED — このディレクトリの内容は旧アーキ時代の設計書です

このディレクトリ (`docs/`) に含まれる以下のファイルは、**OpenAI Realtime API 移行（コミット 5de5297）以前**の旧アーキテクチャ（Silero VAD + Whisper.net + LlamaSharp によるローカル完結型）を前提に書かれています。**現在の実装とは整合しません**。

## 対象ファイル

- `design_verification.md`
- `implementation_details.md`
- `missing_implementation_details.md`
- `project_structure.md`
- `release_guide.md`（リリース手順のみ参考可。Velopack の最新フローは `.github/workflows/velopack-release.yml` を正とする）
- `naudio_replacement_candidates.md`
- `process_loopback_thread_investigation.md`
- `setup_guide.md`

## 参照時の注意

- 「IASRService」「WhisperASRService」「LocalTranslationService」「Silero VAD」「LlamaSharp」「RealTimeTranslator.ASR」「RealTimeTranslator.Translation」など、**現存しないシンボル / プロジェクト** への言及が大量に含まれます。
- 設定項目（`Translation.ModelPath`、ASR 言語、VAD 感度、ホットワード、辞書、`InitialPrompt`）も多くが現実装で適用されません。

## 最新のドキュメントは

- アプリ全体の動作: ルートの [`README.md`](../README.md)
- 開発者向け規約: ルートの [`CLAUDE.md`](../CLAUDE.md)
- ビルド・公開手順: [`.github/workflows/`](../.github/workflows/)

## このアーカイブを残している理由

過去の設計議論・調査ログを履歴として保持するため。将来、ローカル ASR / 自前翻訳パイプラインを再導入する場合の参考資料として利用できます。
