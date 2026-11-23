# Agent Usage Monitor 0.2.14

Windows 11 x64向けの日別履歴の表示改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- 履歴画面で、サービス・アカウント・契約枠・期間定義ごとに観測消費を一覧表示し、項目から日別グラフを選べます。直近30日を初期表示し、7日・90日・指定月に切り替えられます。
- ゼロと未計測を区別し、観測が不足している日は部分計測として表示します。観測消費は取得した累積値の差分であり、推定料金やtoken数ではありません。
- グラフの日付はクリックまたはキーボードで選択でき、支援技術にも日別詳細を渡します。小さい正の値が表示上ゼロにならないよう精度を保ちます。
- 一覧の選択後は「日別グラフへ」で移動できます。読み込み中に表示位置を勝手に変更しません。
- 多数の期間定義を含む一覧・詳細は分割取得し、不整合な応答を検出するとエラーを表示します。一覧の単一項目が大きすぎる場合も、カタログを取得できればフィルターから他の枠を開けます。

## 検証と限界

日別集計、分割取得、日英のグラフ表示と操作を自動テストで確認しています。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。

## English

The history view lists observed consumption by service, account, quota and period, with selectable
daily graphs for the last 7, 30 or 90 days or a chosen month. Zero, missing and partial measurements
remain distinct. Observed consumption is based on differences between retrieved cumulative values,
not estimated prices or token counts. Graph days support keyboard selection and expose daily details
to accessibility tools. Paged responses are checked for consistency.
After selecting an entry, use Go to daily graph or scroll down; loading does not automatically move the viewport.

Automated tests cover aggregation, pagination, and Japanese and English graph behavior. The installed
UI, keyboard and screen-reader operation, and Codex/Claude multi-account setups have not been verified
on a real machine. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
