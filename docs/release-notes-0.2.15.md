# Agent Usage Monitor 0.2.15

Windows 11 x64向けの履歴保存と上限なし枠の改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- アーカイブ済み月、観測区間の重複、既存集計の破損などは、履歴の書き込み前に検出します。拒否された記録は保存待ちに残し、重複の場合は画面に原因を表示します。複数ファイルの書き込み全体は一括トランザクションではありません。
- 上限なし枠の小さな使用量をゼロと区別して表示し、詳細や通知では小数の精度を保ちます。
- 大きな使用量でも上限なし枠の通知が進むようにしました。極端に細かい刻みで通知額を正確に表せない場合は、その枠の通知だけを停止して警告します。使用量の取得と履歴の記録は続きます。
- 上限なし枠の通知済み記録を圧縮し、細かい刻みでの保存量の増加を抑えます。従来の記録は新版で引き継ぎますが、旧版に戻すと圧縮済み記録を認識できず、通知が再送される場合があります。

## 検証と限界

履歴保存の拒否・再送、日英表示、通知の境界値を自動テストで確認しています。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。

## English

History writes validate archived months, overlapping observation intervals and existing summaries
before modifying history files. Rejected records remain pending; overlapping intervals are explained
in the UI. A write across several files is not a single transaction.

Small unlimited-quota amounts remain distinguishable from zero, and large milestone counts no longer
stop amount notifications. If an extremely fine step cannot be represented exactly, only notifications
for that quota are paused and a warning is shown; usage collection and history recording continue.
Amount-notification deduplication records are compacted, and existing records are retained on upgrade.
Older versions cannot interpret compacted records and may repeat notifications after a downgrade.

Automated tests cover history rejection and replay, Japanese and English display, and notification
boundaries. The installed UI, keyboard and screen-reader operation, and Codex/Claude multi-account
setups have not been verified on a real machine. Binaries are unsigned; verify SHA256SUMS without
disabling Windows protection.
