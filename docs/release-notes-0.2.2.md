# Agent Usage Monitor 0.2.2

Windows 11 x64向けのnative小型表示UX改善版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 小型表示

- 一度も有効なpercentageを取得できていないサービス、percentage不明、複数枠が曖昧な項目は
  小型表示の数値行から除きます。有効な **0% は表示**します。
- 古い値・取得失敗後の直前値は数値を残し、グレー文字と斜線の網掛けで区別します。
  長い「古い値」ラベルを数値行に足さず、hover tooltipで状態と最終取得日時JSTを確認できます。
- 全項目が不明なら「取得待ち / Waiting for usage」を残します。
  ダブルクリックでアプリを開く導線は保持します。
- 概要・設定の未設定、認証必要、エラー表示は隠しません。
  無効化やアカウント変更で選択外となった値は小型表示に残しません。
- 既存の観測時刻・最大経過時間・period start/resetによる鮮度判定を再利用します。
  新しい取得失敗をfresh成功や0%として扱いません。

## 保持と検証

設定・履歴・ログオン時起動の選択と0.2.1のローカル終了診断・WM_CLOSE保護を保持します。
外部telemetry、watchdog、常時WPF、独自認証は追加しません。
native tooltipのウィンドウ・文字列とGDI brush/fontは所有者が解放します。

Console runner 131テスト、日英・暗明の合成描画、tooltip文字列・hit-test・非前面化、
実画素のグレーとハッチ・コントラスト、全unknown導線、DPI/WM_CLOSEとGDI/USER解放を確認しました。
架空scalarだけの専用profileでは0%・stale・取得失敗・account mismatch・復旧を通し、
660秒のnative callback検証で応答継続・11 heartbeat・正常終了を確認しました。
全PC/DPI構成、実OS睡眠・ログオフや長期運用を網羅した保証ではありません。
過去に報告された停止の原因やWindows Job lifetimeとの因果は未確定のままです。

## 自動更新の確認タイミング

自動確認は通常起動時です。公開直後に稼働中の0.2.1が直ちに更新を始めるとは保証しません。
次回の通常起動時に、設定のopt-in、画面表示・接続確認中の延期、
安定version・package ID・サイズ・SHA256確認と正常終了の既存ゲートを通します。
今回のRelease公開は、各端末のdownload/apply/restart成功の証明ではありません。
0.2.0→0.2.1で確認した自動更新結果とも区別します。

## English

The native strip hides unknown percentages while retaining a valid 0%.
Stale or failed last-known values keep their number with gray text and a diagonal hatch.
Hover shows status and the last-fetch time. An all-unknown strip retains a neutral
“Waiting for usage” state and the double-click app entry point.

Overview/settings errors remain visible, and disabled or unselected-account values are excluded.
Existing settings, history, logon preferences and 0.2.1 lifecycle diagnostics are retained.
Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.

Automatic checking occurs at normal startup. Publishing this release does not prove immediate
updating of an already-running host or download/apply/restart success on any particular PC.
