# Agent Usage Monitor 0.2.0

Windows 11 x64向けの初回統合リリースです。**未署名**のためSmartScreenで警告される場合があります。
Defender除外や保護機能停止は推奨しません。SHA256SUMSで資産の整合性を確認してください。

## 追加した機能

- Copilot/Codexの公式CLI選択を初回だけ確認し、取得できた同一アカウントを固定。旧手動設定と無効化を保持。
- 日本語/Englishと明暗テーマ。言語はシステム基準または本人選択を保存。
- JST日別履歴と7/30/90日・任意月のグラフ。provider/account/quota/period/単位を合算しない。
- 月内日別raw＋月別集計、local保存待ち・record-ID再送、検証付きZIP、保存先変更のpreview/copy/verify。
- アプリが生成した保存フォルダー・archiveフォルダー・対象ファイルだけをExplorerで開く。
- タスクバー上の小さなnative表示は初期ON。右クリックで切替、OFF選択とドラッグ位置を保存。
  Copilot月間・Codex/Claude週間の契約使用率であり、日目安の消化率ではない。
- installer起動時の安全な自動更新、画面/接続検出中の適用延期、安定版・サイズ・SHA256の照合。

## 制限とデータ保護

日境界はJST (UTC+9)、週は月曜開始。料金・token数は推定しません。
起動前・既に削除された旧履歴・未観測の日境界を捏造せず、未計測と部分計測を明示します。
0差分はAPI丸めの可能性があり、無消費の保証ではありません。
ZIP作成や保存先変更でも元rawを自動削除しません。複数PCの同時書込・自動合算は未対応です。

設定・通知抑止・認証の参照先は従来のローカル領域に維持します。
内部package ID、EXE、install/data folderとRunキーは互換性のため変更しません。
ログオン時起動を勝手に有効にせず、公式CLI認証をコピー保存しません。
**Claudeの本人loginとstatusline数値連携は利用者が行います。**

初回の0.1.0→0.2.0更新は、署名なしの新SetupをSHA256確認後に通常upgradeしてください。
公開Releaseの存在だけでは、将来の新versionの自動download/apply/restart成功を保証しません。
真の自動適用E2Eは後続の正常な安定版がある場合の確認事項です。

## English

Initial integrated Windows 11 x64 release. Binaries are **unsigned**; SmartScreen may warn.
Verify assets with SHA256SUMS. Do not disable protection or add antivirus exclusions.

Includes pinned official-CLI setup, Japanese/English UI, daily quota history, verified archives,
copy-and-verify location changes, generated-path Explorer actions and a lightweight native usage strip.
The strip defaults ON; explicit OFF and placement survive restart/upgrade. It shows monthly Copilot
and verified weekly Codex/Claude quota usage—not daily-guidance usage.

Day boundary: JST (UTC+9) / Week starts Monday. Missing observations are not zero.
Different quota definitions and periods are never summed. No cost/token estimates are invented.
Source history is retained, credentials are not copied, and logon startup remains your choice.
Claude login/statusline integration is user-managed. Multi-PC writing is unsupported.
Real automatic update application requires a later stable release; publishing this release is not
claimed as proof of download/apply/restart end-to-end success.
