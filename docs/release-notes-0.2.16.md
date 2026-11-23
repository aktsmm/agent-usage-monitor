# Agent Usage Monitor 0.2.16

Windows 11 x64向けの使用量表示と履歴グラフの改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- 使用量画面で、枠の種類（Copilotのチャット、コード補完、プレミアムリクエストなど）を元のIDと併記します。API報告のクレジットは、使用率と別の値であることを明示して表示します。`pt`は使用率の変化であり、料金やtoken数ではないことも表示します。
- 履歴グラフは、日別消費の棒に、実測できたその日の最終累積値を別軸の線で重ねます。値のない日は線を切ります。
- 旧版で保存済みの履歴は、検証済みの日別記録とアーカイブから累積値を補います。存在する日別記録は常に検証し、欠けている日だけアーカイブで補います。破損した記録は黙って無視せず、明示的に失敗します。
- 日別グラフの全日付を押せるようにし、日ごとの隙間でクリックが外れないようにしました。Tabで1か所として扱い、左右の矢印キーで日を移動し、EnterまたはSpaceで詳細を選択できます。グラフ名と操作説明も支援技術へ伝えます。
- 観測消費が0の日と未計測の日が混在する場合、状態文で未計測日の存在を明示します。軸ラベルは、1,000以上でも指数表記にならず桁区切りで表示します。
- Windowsで履歴ファイルの置換が一時的に競合（共有違反・アクセス拒否）した場合、短い待機で再試行します。解消しない場合は従来どおり明示的に失敗し、保存待ちの記録と既存ファイルを保持します。

## 検証と限界

日英表示、累積値の復元、履歴ファイルの競合、グラフの操作範囲を、合成データの自動テストで確認しています。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。
履歴に追加した項目は旧版では無視されるため、旧版に戻しても履歴は読み込めます。

## English

Usage cards show the quota kind (for example Copilot chat, code completions and premium requests)
next to the raw id, show API-reported credits as a value separate from the percentage, and explain
that `pt` is a change in usage percentage, not cost or tokens.

The history graph overlays each day's last measured cumulative value as a line on a second axis and
breaks the line on days without a value. Existing history is backfilled from validated daily records
and archives: present daily records are always validated, and the archive fills only missing days.
A corrupt record fails explicitly instead of being ignored.

Every day in the graph is clickable without dead space. The graph is a single Tab stop, Left/Right
arrow keys move between days, and Enter or Space selects a day. The graph name and instructions are
exposed to assistive technology. Mixed measured-zero and unmeasured days now say so explicitly, and
axis labels no longer switch to exponent notation from 1,000 upward.

Transient Windows file-replacement conflicts (sharing violation or access denied) are retried briefly.
Persistent failures remain explicit and keep pending records and existing files.

Automated tests with synthetic data cover Japanese and English display, cumulative restoration, history
file conflicts and graph hit targets. The installed UI, keyboard and screen-reader operation, and
Codex/Claude multi-account setups have not been verified on a real machine. Older versions ignore the
added history fields, so history stays readable after a downgrade. Binaries are unsigned; verify
SHA256SUMS without disabling Windows protection.
