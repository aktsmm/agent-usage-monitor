# Agent Usage Monitor 0.2.9

Windows 11 x64向けの複数アカウント表示の仕上げ版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 修正

- 設定画面のチェックボックスと削除ボタンの読み上げ名で、アカウント名の`_`が欠ける問題を修正しました（例: `tatsumiy_microsoft`）。WPFがボタン系コントロールの名前の`_`をアクセスキー記号として削除するため、二重にして渡します。
- 上限なし（business等）のアカウントが、同じ内容のカードを3枚表示していた問題を修正しました。1枚（最も使用量の多い枠）にまとめます。
- 複数アカウントの説明（`docs/multi-account.md`）を、実機確認済みの動作に合わせて更新しました。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは157/157成功。UI Automationの実測テストを追加しました。
実機で確認済み: 0.2.7→0.2.8のアプリ内自動更新（download・適用・再起動）。
実機で未確認: 2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ。
GitHub Copilotデスクトップアプリの認証は連携していません。

## English

Polish release for multi-account display: automation names of checkboxes and buttons no longer lose
underscores in account names (WPF treats them as access-key markers; they are now doubled), an
account that only reports unlimited quotas shows one card instead of three identical ones, and
`docs/multi-account.md` matches the verified behavior. 157 console tests pass and the Release build
has zero warnings or errors; an automation-peer test was added. The in-app update from 0.2.7 to
0.2.8 was verified on a real machine. Not verified: a second `CODEX_HOME` and Claude snapshot
binding. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
