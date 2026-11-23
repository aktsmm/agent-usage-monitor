# Agent Usage Monitor 0.2.17

Windows 11 x64向けのCopilot表示の改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- 履歴画面でアカウントやサービスを切り替えると、枠がチャットに変わってしまう問題を直しました。切替後も同じ名前の枠を選び続け、なければCopilotのプレミアム リクエストを選びます。
- Copilotは既定でプレミアム リクエストだけを表示します（使用量画面・常駐ストリップ・履歴）。設定の「Copilotのチャットとコード補完も表示する」をオンにすると、チャットとコード補完も表示します。履歴を開いている場合は、ウィンドウを再度アクティブにしたときに反映されます。
- 上限なし枠だけのアカウントで、使用量が同じときにチャットではなくプレミアム リクエストを選びます。
- プレミアム リクエストの枠が無いアカウントは、報告された枠をそのまま表示し、画面から消えません。CodexとClaudeの枠は変わりません。
- 使用量画面のAPI報告クレジットと、履歴の観測消費・累積値を、121,006のように桁区切りで表示します（常駐ストリップは幅が限られるため、従来どおり短縮表記で、ツールチップに全桁を表示します）。
- 通知文と設定の枠の一覧でも、元のID（`premium_interactions`など）ではなく、表示名（プレミアム リクエストなど）を使います。
- 表示だけの切替です。使用量の取得、履歴の記録、通知の判定は変わりません。非表示の枠の履歴も保存され、設定をオンにすると表示されます。

## 検証と限界

アカウント切替、表示範囲、設定の保存と旧設定ファイルの読み込みを、合成データの自動テストで確認しています。
非表示にしたチャットやコード補完に対する通知は、これまでどおり判定されます。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。

## English

Switching the account or service in History no longer jumps to Chat: the same bucket name is kept, and Copilot
Premium requests is selected when it is not available.

Copilot now shows only Premium requests by default in the usage view, the taskbar strip and History. A new setting,
"Also show Copilot Chat and Code completions", brings them back. An open History window applies the change when it
is activated again. An account that reports only unlimited buckets prefers Premium requests when usage ties.
Accounts without a Premium requests bucket keep showing what they report, and Codex and Claude are unchanged.

This only changes what is displayed. Usage collection, history records and notification rules are unchanged, and
history of the hidden buckets stays stored and reappears when the setting is turned on. Notification texts and the
quota list in Settings now use the display names (for example Premium requests) instead of raw bucket ids. Reported
credits in the usage view and observed or cumulative amounts in History are shown with digit grouping (121,006); the
taskbar strip keeps its compact form and its tooltip shows every digit.

Automated tests with synthetic data cover account switching, the display scope, settings persistence and loading of
older settings files. Notifications for hidden Chat or Code completions are still evaluated as before. The installed
UI, keyboard and screen-reader operation, and Codex/Claude multi-account setups have not been verified on a real
machine. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
