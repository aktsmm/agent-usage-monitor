# Agent Usage Monitor 0.2.7

Windows 11 x64向けの更新確認修正版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 更新確認の修正

- インストール済みの0.2.5/0.2.6が、更新確認で`update_source_not_allowed`により失敗する問題を修正しました。
- 原因: 更新ライブラリが更新情報の取得時に付けるクエリ（`arch`/`os`/`rid`/`id`/`localVersion`）を、信頼URLの判定が拒否していました。
- 更新情報ファイルに限り、固定したキーと形式の値だけを許可します。パッケージのダウンロードにクエリは許可しません。
- 更新に失敗したとき、例外の種類名ではなくエラーコードを表示します。

**0.2.5/0.2.6を使用中の方へ:** それらの版は自力で更新できないため、0.2.7のインストーラー（Setup.exe）を一度手動で実行して上書きしてください。0.2.7以降は自動更新が使えます。

## 検証と限界

Release buildは警告0・エラー0、Console runnerは150/150成功。更新ライブラリが実際に作るURLを捕捉する回帰テストを追加し、公開中の更新情報の読み取りが成功することを確認しました。
インストール済みアプリからの実download/apply/restartは、この検証では行っていません。

## English

This release fixes the update check of installed 0.2.5 and 0.2.6 copies, which failed with
`update_source_not_allowed`. The update library appends query parameters (`arch`, `os`, `rid`,
`id`, `localVersion`) when it reads the feed, and the trusted-source rule rejected them. The
rule now accepts only those exact keys with strictly shaped values, on the feed files only;
package downloads still accept no query. Failures now show the error code instead of the
exception type name.

If you run 0.2.5 or 0.2.6, run the 0.2.7 installer (Setup.exe) once by hand, because those
versions cannot update themselves. Later versions can.

The Release build completed with zero warnings or errors and all 150 console tests passed,
including a regression test that captures the URLs the update library really builds. Reading the
public feed succeeded. An installed-app download/apply/restart was not performed. Binaries are
unsigned; verify SHA256SUMS without disabling Windows protection.
