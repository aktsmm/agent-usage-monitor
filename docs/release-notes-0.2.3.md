# Agent Usage Monitor 0.2.3

Windows 11 x64向けのClaude Code接続改善版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## Claude Code の自動検出

- 公式 `claude auth status` のemailと、既定のstatusline snapshot
  (`%USERPROFILE%\.claude\aiusage-exporter\claude-snapshot.json`) のaccount_idが一致した場合だけ、
  再検出時にラベルとファイルを自動で設定して有効化します。
- ログイン未済、snapshotなし、emailとaccountの不一致では有効化せず、従来の案内を表示します。
- 手動設定・無効化した接続は上書きしません。
- statuslineの設定やexporterの配置は引き続き本人が行います。アプリは既存設定を変更しません。
- 独自OAuth、Cookie取得、ダミー推論は追加していません。

## 検証

Console runner 132テストを確認しました。email一致の自動有効化と別accountの非有効化を含みます。
実機でClaude Code(Pro)のstatusline出力からsnapshotが生成されることを確認しました。
全環境での動作保証ではありません。

自動更新は通常起動時の確認です。公開直後に稼働中のhostが直ちに更新するとは保証しません。

## English

Claude Code discovery now binds the official `claude auth status` email to the default
statusline snapshot and enables the connection automatically only when they match.
Missing login, missing snapshot or a different account is never enabled, and manual or
disabled settings are not overwritten. Statusline setup remains the user's action.
Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
