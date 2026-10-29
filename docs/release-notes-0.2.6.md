# Agent Usage Monitor 0.2.6

Windows 11 x64向けのマルチアカウント対応版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 複数アカウント

- 同じサービスの複数アカウントを、合算せず個別に監視できます（Copilot・Codex・Claude Code）。
- アカウントごとに名前（自動生成・編集可）、表示/非表示、小型表示への表示を設定できます。小型表示は既定でメインのアカウント1件のみです。
- Copilotは「アカウントを追加」画面で、`gh`にログイン済み（状態が成功）のGitHubアカウントを検出して選べます。
- 同じGitHubアカウントはログイン名とGitHub IDの両方で重複を拒否し、手編集した設定ファイルの重複も読み込み時に拒否します。
- Codexの追加`CODEX_HOME`とClaude Codeの追加snapshotは自動検出せず、パスを自分で指定します。
- 追加画面と設定画面で、Enter/Esc、読み上げ名、テーマ対応の色、削除確認へのアカウント名表示を改善しました。

[設定方法と制限](multi-account.md)。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは146/146成功。
実機で未確認: `gh`の非アクティブなログインでのUsage取得、2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ、
追加画面・設定画面の目視とキーボード確認、インストール済みアプリからの更新のdownload/apply/restart。
GitHub Copilotデスクトップアプリの認証は連携していません。

## English

This release adds multi-account monitoring: several accounts of the same service are monitored
separately and never summed, with editable names and per-account visibility and compact-strip
display. Copilot accounts can be discovered from successful GitHub CLI (`gh`) logins in the Add
Account dialog. The same GitHub account is rejected by login and by GitHub ID, including in a
hand-edited settings file. Additional Codex homes and Claude snapshots are entered by hand.
The dialogs gained keyboard, screen-reader and theme-contrast improvements.

The Release build completed with zero warnings or errors and all 146 console tests passed.
Not verified on a real machine: usage for a non-active `gh` login, a second `CODEX_HOME`,
Claude snapshot binding, visual and keyboard checks of the new screens, and an installed-app
download/apply/restart. The GitHub Copilot desktop app is not integrated. Binaries are unsigned;
verify SHA256SUMS without disabling Windows protection.
