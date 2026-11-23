# Agent Usage Monitor 0.2.18

Windows 11 x64向けの表示の改善です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 改善

- 使用量画面と設定画面の日・週の目安、目安との比較の数値も、1,166.67のように桁区切りで表示します。割合（%）の表記と、常駐ストリップの短縮表記は変わりません。
- `--demo`で起動する架空データの表示が「未取得」になっていた問題を直しました。架空の有効アカウントを持たせ、サンプルの使用量が表示されます。認証情報や実在のアカウントは含みません。
- READMEの表示例の画像を、現在の画面に合わせて撮り直しました。
- 履歴とstate.jsonの保存量を測る読み取り専用のスクリプト（`scripts/measure-history-growth.ps1`）と、実測値を`docs/history-storage.md`に追加しました。アプリ本体の保存の仕組みは変わりません。

## 検証と限界

桁区切りと架空データの表示を、合成データの自動テストと、ビルドした実アプリの画面で確認しています。
インストール後の実機画面・キーボード・スクリーンリーダー操作は未確認です。
CodexとClaudeの複数アカウントも実機では未確認です。

## English

Daily and weekly guidance amounts in the usage view and Settings, and the numbers compared with the guidance, now use
digit grouping (1,166.67). Percentages and the compact taskbar strip text are unchanged.

The synthetic `--demo` view no longer shows "not fetched" cards: it now carries synthetic enabled accounts and displays
the sample usage. It contains no credentials or real accounts. The README screenshots were regenerated from the current
screens. A read-only script (`scripts/measure-history-growth.ps1`) and measured values in `docs/history-storage.md`
describe how much state and history are stored; how the app stores data is unchanged.

Automated tests with synthetic data and the built app's real screens cover the number formatting and the demo view. The
installed UI, keyboard and screen-reader operation, and Codex/Claude multi-account setups have not been verified on a
real machine. Binaries are unsigned; verify SHA256SUMS without disabling Windows protection.
