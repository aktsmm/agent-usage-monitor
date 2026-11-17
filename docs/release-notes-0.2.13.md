# Agent Usage Monitor 0.2.13

Windows 11 x64向けの修正リリースです。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 修正

- トレイメニューの「アプリの更新を確認」を押しても何も起きないように見える問題を修正しました。更新確認用の別プロセスが出す確認ダイアログが他のウィンドウの背後に開き、見えないまま待機していたのが原因です。更新確認・エラー・通知のダイアログを前面かつ最前面で表示します。
- 更新確認がすでに実行中のときに、もう一度「アプリの更新を確認」を押すと、何も表示されず無視されていました。実行中であることと、ダイアログが後ろに開いていないか確認するよう通知します。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは成功です（ダイアログの前面表示フラグの固定テストを追加）。
実機の目視確認は、インストール後に行ってください（更新確認の前面表示、実行中の再押下の通知）。

## English

Fix release. Choosing "Check for app updates" from the tray menu appeared to do nothing because the
update helper's confirmation dialog opened behind other windows. Update, error and notice dialogs now
open in the foreground and on top. Pressing the menu item again while a check is already running now
shows a notice (check behind other windows or the taskbar) instead of being silently ignored. A fixed
test guards the dialog flags. Not verified on a real machine yet: the foreground dialog and the
repeat-click notice after install. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.