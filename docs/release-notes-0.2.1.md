# Agent Usage Monitor 0.2.1

Windows 11 x64向けの常駐終了経路の修正・診断追加版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認してください。Defender除外や保護機能停止は推奨しません。

## 修正

- native小型表示への `WM_CLOSE` でそのウィンドウが破棄され、次のポーリング時の
  描画更新例外で常駐本体まで終了する経路を、架空データの0.2.0で再現して修正しました。
  小型表示は本体の終了操作として扱いません。本体はトレイの「終了」で停止します。
- 小型表示の既知の描画失敗を通知・記録します。予期しないnative/async callback例外は
  managedメッセージループへ戻し、記録とエラー表示を残して異常終了します。
  故障後のqueued workを継続せず、更新helperにも終了成功と伝えません。

## ローカル終了診断

プロファイル内の `diagnostics` に起動ID、PID、版、時刻、明示終了理由、処理段階、
例外型とアプリ内メソッド名を保存します。ログは各128 KiBの2ファイルへローテーションし、
別の小さなファイルに1分ごとのメッセージループ応答または終了状態を保存します。

例外メッセージ、ソースパス、アカウント、Usage、token、provider応答、clipboardは
記録しません。外部送信、無条件watchdog、自動再起動、常時WPF表示は追加しません。
正常な保存・後処理とメッセージループ終了を確認した場合だけ `CleanExit` を記録します。
前回の終了を確認できない場合は次回起動で `PreviousRunIncomplete` を記録します。
これは強制終了・電源断・クラッシュの原因を一意に特定する情報ではありません。
heartbeatもUsage取得成功を示すものではありません。

## データ保持と検証の限界

既存の設定、履歴、認証参照先、明示的な小型表示OFF、ログオン時起動の本人選択を保持します。
内部package ID、EXE、install/data folderは変更しません。アンインストールは不要です。

Console runnerの122テストに、strip close、native/async例外、終了コード、診断の
redaction・rotation・前回終了不明の検出を含めました。架空profileで修正候補の
11分常駐と、self-contained本体の正常終了・強制終了後の次回検出を確認しました。
実OSの睡眠・ログオフ、全PC/契約/長期運用を網羅した検証ではありません。

過去に報告された実端末の停止は0.2.0に終了証跡がなく、今回再現した経路と同じ原因だったか
**未確定**です。再発しない保証はせず、次回停止時にローカル証跡で終了理由を確認できるようにしました。
起動元のWindows Job lifetimeとの因果も未確定です。

## English

Fixes a reproduced native usage-strip `WM_CLOSE` crash that could terminate the background host
on the next polling update. Unexpected callback faults now stop explicitly with local evidence
and a nonzero exit code rather than escaping the native callback boundary or silently continuing.

Adds bounded, local-only lifecycle records and a one-minute message-loop heartbeat.
Exception messages, source paths, accounts, usage values, tokens, provider responses and clipboard
contents are excluded. No watchdog, external telemetry or always-on WPF process is added.
Existing settings, history, authentication references and logon-startup preferences are retained.

The cause of previously reported installed-app exits remains unconfirmed. Synthetic tests and a
bounded soak do not guarantee permanent resolution or validate real OS sleep/logoff behavior.
Binaries are unsigned; verify SHA256SUMS and keep Windows security protections enabled.
