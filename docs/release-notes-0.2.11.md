# Agent Usage Monitor 0.2.11

Windows 11 x64向けの追加版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 追加

- **常駐ストリップ**: 上限なし（business等）だけのアカウントも、`Copilot·名前 67k`のように報告クレジットを短縮表記（k/M、数字のみ）で表示します。単位と全桁（66,987クレジット）はツールチップに出ます。上限ありの枠があるアカウントは従来どおり使用率です。
- **通知**: 上限なし枠は使用率が無いため、報告クレジット（AIC）が刻みごとに増えるたびに通知します。既定は5,000 AICごと。設定の通知タブ「上限なし枠の刻み」で変更でき、空欄で無効、枠ごとの設定も使えます。初回の観測で既に超えている刻みは1回にまとめて通知します（使用率の刻みと同じ挙動）。1回の通知で扱う刻みは最大200件です。
- **履歴**: 上限なし枠の消費も、他の枠と同じ日別履歴にクレジット単位で記録されます（目安は作りません）。
- **修正**: 画面を閉じた直後などに、クライアント側の切断が「操作に失敗しました（ipc_failed:EndOfStreamException）」として画面下に残る問題を修正しました。それ以外のIPCエラーは従来どおり表示します。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは173/173成功です（短縮表記の境界、ストリップ、通知の重複抑止・上限・無効化、旧設定の読み込み、履歴、日英文言のテストを追加）。
実機の画面（常駐ストリップと通知）での目視確認は、インストール後に行ってください。
AICと円の対応はAPIからは分からないため、円換算はしません。
実機で未確認: 2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ。

## English

Feature release for quotas without a ceiling. The always-on strip now shows the reported credits of
an account that only has unlimited quotas in a compact form (for example `Copilot·name 67k`; the
full amount and unit are in the tooltip), and accounts with a limited quota keep their percentage.
Unlimited quotas notify every N reported credits (AIC; default 5,000, configurable or disabled in
Settings, also per quota); levels already reached on the first observation are sent as one notice,
capped at 200 levels. Their consumption is recorded in the daily history in credits, with no
guidance values invented. Also fixed: a client that disconnected early no longer leaves
"ipc_failed:EndOfStreamException" in the window; other IPC errors are still shown. 173 console
tests pass and the Release build has zero warnings or errors. Not verified on a real machine yet:
the strip and notifications after install, a second `CODEX_HOME`, and Claude snapshot binding.
No currency conversion is made. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
