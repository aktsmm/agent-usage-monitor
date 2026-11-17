# Agent Usage Monitor 0.2.10

Windows 11 x64向けの小さな追加版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 追加

- 上限なし（business等）の枠は、「上限なし（API報告）」だけでなく、API報告の使用量を絶対値で表示します（例: 「上限なし · 今期の使用量 1,234.5 クレジット」）。分母・使用率・日/週の目安は作りません。使用量が取得できない場合は従来どおり「上限なし（API報告）」です。日本語・英語の両方に対応します。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは158/158成功です（表示文言の日英テストを追加）。
実機の画面（上限なしアカウント）での目視確認は、インストール後に行ってください。
実機で未確認: 2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ。
GitHub Copilotデスクトップアプリの認証は連携していません。

## English

Small feature release: quotas reported as unlimited now show the absolute usage amount the API
reports (for example "Unlimited · usage this period 1,234.5 credits") instead of only
"Unlimited (API reported)". No denominator, percentage or daily/weekly guidance is invented; when
no usage value is available the previous label is kept. Japanese and English are both covered by
tests (158 console tests pass, Release build has zero warnings or errors). Not verified on a real
machine yet: the unlimited-account screen after install, a second `CODEX_HOME`, and Claude snapshot
binding. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
