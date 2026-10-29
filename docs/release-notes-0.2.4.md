# Agent Usage Monitor 0.2.4

Windows 11 x64向けの小型表示・説明改善版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 小型表示

- 表示する%が残量ではなく、消費済みの使用率であることを明記しました。
- 取得済みの`0%`は有効な値です。表示時に丸められた結果の場合もあります。
- 未取得は「取得待ち」、古い値は別の状態として表示します。未取得を`0%`として扱いません。

## Claude Code

READMEの接続制限を実装に合わせました。公式`claude auth status`のemail由来IDと
既定statusline snapshotの`account_id`が一致した場合だけ自動有効化します。

## 検証

Console runner 132テスト、Desktop Release build、リソース検証を実施しました。

## English

This release clarifies the native usage strip: percentages show consumed quota, not remaining
quota. A retrieved `0%` is valid and may be rounded for display; missing usage is shown as
Waiting, and stale values are identified separately. Claude auto-discovery is documented to
match the existing account-ID check. Binaries are unsigned; verify SHA256SUMS without disabling
Windows protection.
