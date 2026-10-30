# Agent Usage Monitor 0.2.8

Windows 11 x64向けの複数アカウント表示の修正版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 修正

実機で2つ以上のGitHubアカウントを追加して見つかった、「1サービス1アカウント」を前提にした集約の不具合です。

- 上限なし（business等）のアカウントが、他アカウントに制限付きの枠があると、取得できているのに表示されない問題を修正しました。
- メインアカウントの取得失敗・アカウント不一致が、同じサービスの他アカウントの値を消したり「失敗」扱いにしたりする問題を修正しました（概要画面・小型表示）。
- 値がまだないアカウントが何も表示されず消える問題を修正しました。値の待機中であることを表示します。
- 追加アカウントの値から、メインアカウントを誤って固定する可能性があった起動時の処理を修正しました。
- 同じサービスの複数アカウントで、通知と履歴のアカウント選択が区別できない問題を修正しました（アカウント名を表示）。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは156/156成功。新しい回帰テストを追加しました。
実機で未確認: 2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ、インストール済みアプリからの更新のdownload/apply/restart。
GitHub Copilotデスクトップアプリの認証は連携していません。

## English

Fixes for multi-account display found on a real machine with several GitHub logins, all caused by
single-account assumptions in aggregation: an account that only reports unlimited quotas was hidden
whenever another account had limited ones; a primary account's failure or mismatch cleared or
marked other accounts of the same service as failed (overview and compact strip); accounts without
a value yet vanished instead of showing a waiting message; startup could pin the primary account
from an additional account's values; and notices and the history account picker did not name the
account. 156 console tests pass and the Release build has zero warnings or errors. Not verified on a
real machine: a second `CODEX_HOME`, Claude snapshot binding, and an installed-app
download/apply/restart. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
