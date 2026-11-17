# Agent Usage Monitor 0.2.12

Windows 11 x64向けの修正と小さな追加です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 修正

- 設定のアカウント別名を付けて保存した後、変更せずにもう一度保存すると既定名へ戻る問題を修正しました。別名欄の初期値（現在の表示名）を「既定名」と取り違えていたのが原因です。開いた時点から変更がなければ保存済みの別名を維持し、変更したときだけ、空欄または自動名と同じ入力を既定へ戻します。同名の衝突で付く`#1`のような表示も別名として保存されません。

## 改善

- `state.json`の書き込みをインデントなしの1回書き込みにしました。取得のたびに全体を書き直すため、書き込み量が減ります（実測の一例で約28MB→約20MB）。差分の件数そのものは変わりません。大きな環境では引き続き状態ファイルが大きくなります（設計案は`docs/history-storage.md`）。

## 追加

- 設定のアカウント欄で、常駐ストリップのサービス名を短縮できます（例: Copilot→`cp`。ストリップは`cp·aktsmm 6.1%`）。空欄で既定名に戻ります。アカウントが1つだけのサービスにも反映され、メイン画面と通知の見出しは正式名のままです。旧設定ファイルはそのまま読み込めます。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは181/181成功です（別名の保存・往復、短縮名の保存・検証・旧設定の互換・ストリップ表示のテストを追加）。
設定画面の目視確認は、インストール後に行ってください。
実機で未確認: 2つ目の`CODEX_HOME`、Claudeのsnapshotとアカウントの紐づけ。

## English

Fix and small feature release. Saving the Settings screen again without editing an account alias no
longer resets it to the default name (the field's initial text, the current display name, was mistaken
for the default). An untouched row keeps its stored alias; only a changed field that is empty or equals
the automatic name resets. Settings also lets you shorten the service name used in the strip (for
example Copilot to `cp`, shown as `cp·aktsmm 6.1%`); empty keeps the default, it applies to single-account
services too, and the main window and notices keep the full name. Old settings load unchanged. 179
console tests pass and the Release build has zero warnings or errors. Not verified on a real machine yet:
the Settings screen after install, a second `CODEX_HOME`, and Claude snapshot binding. Binaries are
unsigned; verify SHA256SUMS without disabling Windows protection.