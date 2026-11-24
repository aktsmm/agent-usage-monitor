# Agent Usage Monitor 0.2.19

Windows 11 x64向けの履歴の改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- 上限なし枠の累積クレジットが一時的に0へ落ち、すぐ元の値へ戻ったとき、戻り分を消費として二重に数えていた問題を直しました。値が6時間以内に元の値まで戻る間は調整として扱い、元の値を超えた分だけを消費にします。戻らなければ本当のリセットとして数えます。上限なし枠の金額通知も、この落ち込みで再送されません。
- 過去の履歴も、表示するときに同じ規則で補正します。履歴ファイルと記録IDは書き換えません。補正して除いた量は、日別の詳細に表示します。手元の実データでは、あるアカウントの月の観測消費が約316,000から約112,000に下がり、現在の累積値と一致しました。
- 履歴画面の「サービス・アカウント別の使用状況」は、開くたびに折りたたんだ状態で始まります。見出しに件数と選択中のサービス・アカウント・枠を表示します。
- 期間の選択肢を、観測日数が多い順に並べ、既定で先頭を選びます。観測が少ない期間には注記を付け、pt（使用率の増加幅）とクレジット（APIが報告した使用量）の意味を画面に表示します。
- 古い月の要約は、次の記録のときに補正済みの内容へ自動で作り直します。初回は1か月あたり数秒かかることがあります。日が欠ける場合は、古い要約を残します。

## 検証と限界

合成データの自動テストと、ビルドした実アプリの画面、手元の履歴のコピーで補正を確認しています。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。
0に落ちる原因（APIの一時的な応答）は推測で、API側の仕様は確認していません。0以外の値への減少は、これまでどおり調整として扱います。
前日の記録が無い（アーカイブ済みなど）日をまたぐ落ち込みは、補正されずに記録のまま表示されます。

## English

When an unlimited quota's cumulative credits briefly drop to 0 and climb back, the climb is no longer counted as
consumption a second time. While the value is within 6 hours of returning to its earlier peak, the change is an
adjustment, and only the amount above the peak counts. If it does not return, it is treated as a genuine reset. Amount
notifications are not re-sent after such a dip. Past history is corrected the same way when displayed; history files and
record IDs are not rewritten, and the excluded amount is shown in the day detail. On the author's data, one account's
monthly observed consumption dropped from about 316,000 to about 112,000, matching the live counter.

The "usage by service and account" list in History now starts collapsed, with the count and the selected item in the
header. Periods are ordered by observed days, sparse periods are flagged, and the meaning of pt (percentage-point
increase) and credits (as reported by the API) is shown. Older monthly summaries are rebuilt in the background on the
next append (a few seconds per month the first time); a rebuild that would drop a day keeps the old summary.

Synthetic-data tests, the built app's screens, and a copy of the author's history verify this. The installed UI,
keyboard and screen-reader operation, and Codex/Claude multi-account setups have not been verified on a real machine.
The cause of the transient 0 is inferred, not confirmed against the API. Decreases to non-zero values are still
treated as adjustments. A dip that crosses a day whose previous records are missing is shown as recorded. Binaries are
unsigned; verify SHA256SUMS without disabling Windows protection.
