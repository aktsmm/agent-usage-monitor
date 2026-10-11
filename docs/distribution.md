# 配布・公開の手順

## 候補の作成

Windows 11 x64、.NET SDK `global.json` の固定版、ローカルtool manifestのvpk 1.2.161を使用します。
依存は `packages.lock.json` で固定します。

```powershell
.\scripts\package.ps1
```

LICENSEと標準CC本文、追加許諾と第三者NOTICEを配布物へ含めます。
出力先は新しい空フォルダーに限り、既存の配布物は上書きしません。

`local\distribution\release` にSetup、Portable ZIP、full update package、feedとSHA256SUMSを出力します。
ZIPもinstallerも.NET 10 Windows Desktop Runtime同梱のwin-x64 self-containedです。
開発用framework-dependent約0.6MiBとruntime同梱資産のサイズは異なります。
`VersionPrefix`、EXE、vpk package、ZIP内部のversionが同じ値であることを検証してください。

## Windows CI

`windows-packages.yml` はWindowsでbuild、実行型Console tests、self-contained packaging、artifact uploadを行います。
`dotnet test`だけを実行してテストが成功したと扱いません。
通常push/PRはartifactだけです。artifactの作成は一般公開Releaseの承認ではありません。

手動`draft_release=true`、main、LICENSEの存在が揃った場合だけdraft jobを実行します。
`release-approval` environmentに必要なreviewerをGitHub上で設定してください。
release/tagが存在したら上書きせず停止し、作る場合もdraftだけです。
公開・private→public・pushはこのローカル準備の範囲に含みません。

## 公開前のゲート

- LICENSEはCC BY-NC-SA4.0とMicrosoft Corporation/affiliatesへのNC免除だけの追加許諾。
  依存のMIT/.NET noticesは変更しない。
- 公開先は新しい `aktsmm/ai-usage-monitor`。元のPrivate履歴は公開先へ移さず、
  一般向けソースsnapshotだけを移す。元履歴を勝手に書き換えない。
- README/公開画像は架空の数値・アカウントだけにする。内部検証証跡は配布物に含めない。
- clean outputでinstaller/portable、Start Menu、更新確認、アンインストール、データ保持を確認する。
- 署名証明書/SmartScreen、Private vulnerability reporting、immutable releases、draftのreviewを判断する。
- 同じversionのrelease/assetを後から差し替えない。修正時はversionを上げる。

正式更新URL: https://github.com/aktsmm/ai-usage-monitor/releases

## 履歴を含まない公開snapshot

```powershell
.\scripts\export-public-snapshot.ps1
```

`local\public-snapshot` に明示したsource/docs/workflow/license/assetsだけをコピーし、
`SNAPSHOT-FILES.json` に各ファイルの相対パス・サイズ・SHA256を記録します。
`.git`、元の履歴、local/bin/obj、認証JSON、実測証跡、内部セッションartifactは含めません。
公開先でこのsnapshotを初期commitにする判断とpush/public/release操作は別の承認です。
パターン検査で秘密が見つからなかったことを公開安全の保証とは扱わず、ファイル一覧と文書・画像を確認してください。

## ローカル候補の検証結果

0.1.0 / Velopack SDK・vpk 1.2.161 / .NET 10 self-contained win-x64で、
clean outputからSetupとPortable ZIP、full update package、feed、全資産SHA256を生成しました。
Release buildは警告0・エラー0、Console test runnerは51/51成功です。
同じファイル一覧の履歴なしsnapshotを別フォルダーでbuild/testしても51/51成功しました。

現在ユーザーの一時インストール・削除を明示許可した環境で、Setup、スタートメニュー、
6項目トレイ、表示中の子を含む正常終了、アンインストール、既存の検証用設定保持を確認しました。
実ログオン登録・認証・標準Usageデータは変更しませんでした。
installerの通常起動と`--silent`を使用してください。検証したVelopack Setupでは
追加EXE引数を`--`以降に転送すると上流の引数parserで停止したため、
検証profileはインストール後のアプリEXE起動時に指定しました。

installer版とportable版の実更新確認は、正式な新repoがまだPrivateで安定Release未公開のため
「公開済みの安定リリースを確認できない」を返すところまで確認済みです。
更新あり・403などの扱いは合成テストで確認。公開された更新資産からの実download/apply/restartは、
将来最初の安定Releaseと後続バージョンを用意した後の確認事項です。未公開の更新成功は主張しません。

最終runtime同梱版のメモリ確認では、本体＋生存子のWorking Setは画面表示時約256MiB、
画面終了30秒後約93MiB、追加60秒後も約93MiB（子0）でした。
開発用framework-dependentの初期測定とは条件が異なります。更新helper動作中の一時ピークも常駐と区別します。
署名証明書は未設定でSmartScreen警告の可能性があります。

公開snapshotの内容は一般向けdocs、標準LICENSEと追加許諾、source/tests、固定tool/actionのCI、
架空データのスクリーンショットのみです。元Privateリポジトリの履歴は公開しません。

installerの標準install先は `%LOCALAPPDATA%\AiUsageTrayDesktop`、データ保存先は
別の `%LOCALAPPDATA%\AiUsageTray` です。アンインストールhookは自分のinstall配下に
向く正確なRun値だけを除去し、データ・CLI認証は削除しません。
検証プロファイルからログオン時起動を設定しない保護があります。

参照（2026-10-12確認）:

- https://docs.velopack.io/integrating/overview
- https://docs.velopack.io/integrating/hooks
- https://docs.velopack.io/integrating/uninstalling
- https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases
