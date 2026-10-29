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
- 公開先は `aktsmm/agent-usage-monitor`（旧ai-usage-monitor）。初回の履歴なしsnapshot公開後は、
  通常のPR・patch commitでmainを更新します。元のPrivate履歴や公開mainを初期化し直しません。
- README/公開画像は架空の数値・アカウントだけにする。内部検証証跡は配布物に含めない。
- clean outputでinstaller/portable、Start Menu、更新確認、アンインストール、データ保持を確認する。
- 署名証明書/SmartScreen、Private vulnerability reporting、immutable releases、draftのreviewを判断する。
- 同じversionのrelease/assetを後から差し替えない。修正時はversionを上げる。

正式更新URL: https://github.com/aktsmm/agent-usage-monitor/releases

0.2.0の表示名はAgent Usage Monitor、アイコンはオリジナルのゲージです。
更新互換性のためpackId `AiUsageTrayDesktop`、資産名、EXE、既存データ/インストール先は維持します。
起動時の自動更新は初期有効、画面表示中は適用延期、portable自己置換なし。
0.1.0に残る旧更新URLは、この候補への安全な更新後に新canonicalへ変わります。

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

## 0.2.5 更新確認のレート制限対策

最新安定版の確認とVelopack feed取得は、GitHub Releaseの
`/releases/latest/download/releases.win.json` と `/releases/latest/download/RELEASES` を使います。
GitHub REST APIを呼ばないため、未認証APIの共有レート制限には依存しません。
manifestはpackage ID・安定version・full package名・サイズ・SHA256を検証し、
GitHub Release / 配信CDN以外へのredirectを拒否します。
通常のネットワーク拒否は更新失敗として区別し、429または残数0を示す403は
Retry-After / rate-limit resetに基づく待機情報を保持します。

参照（2026-10-12確認）:

- https://docs.velopack.io/integrating/overview
- https://docs.velopack.io/integrating/hooks
- https://docs.velopack.io/integrating/uninstalling
- https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases

## 0.1.1 自動接続・更新候補の確認

Console runnerは75/75、Release buildは警告0・エラー0。
専用の検証profileで現在の公式CLI選択を確認し、Copilot/Codexを名前・パス入力なしで
自動設定、API由来の枠を表示、Claudeの公式auth statusは「ログインが必要」と表示しました。
tokenをsettingsへ保存せず、再起動後の検出結果時刻とsettings byte hashが同一で、
CLIの初回検出を繰り返さないことを確認しました。
旧設定・手動・無効化・pin mismatch・検出中の編集・限定RPC fallbackは合成テストで区別します。

.NET 10 self-contained win-x64本体＋全生存子孫のWorking Setは、表示3回の間は
約227～233MiB、各画面終了30秒後は約77.55～78.03MiB、
追加60秒後は約77.99MiB・子0でした。正常終了によるlease解放・保存保持を確認。
これは短時間・このPCでの観測であり、長期や全契約/別PCの保証ではありません。
表示ピーク、検出/CLI/更新の一時ピークを常駐値に混ぜず、強制trim/GCは使いません。

canonical新repoの更新確認は「公開済みの安定リリースを確認できない」まで実経路で確認しました。
現在Private・安定Release未公開なので、実資産のdownload/apply/restart E2Eは未検証です。
候補は未署名です。既存の常用0.1.0をこの検証で変更・削除・更新せず、
履歴機能を統合した最終版の適用前に本人の更新承認を取得します。
候補のファイル名・SHA256・version/metadataは同梱SHA256SUMSと検証用artifactへ記録します。

## 0.2.0 統合版の検証

履歴・保存・日本語/English・native小型表示を本体/IPC/更新helperへ接続しました。
Console runnerは115/115、Release buildは警告0・エラー0。
日英のresource key・placeholder対応、設定migration、period/record ID/通知抑止の言語不変を実行確認。
同じsourceからself-contained publishし、`ja\AiUsageTray.Core.resources.dll`を含む実EXEで
`--validate-resources`を実行して全keyを読みました。

架空データの実Overview・設定・履歴を日本語/English×明暗で確認しました。
検証用`--demo --no-activate`は認証・取得・保存をせず、利用者の前面を奪いません。
native小型表示は非前面化、120回の表示/非表示・言語/明暗切替、複数回の生成/解放後の
GDI資源数が増加しないことを確認しています。画面構成の座標補正はtargeted testを含みます。
別DPIの実モニター全配置や全契約形態・長期sync運用を網羅した保証ではありません。

.NET 10 self-contained、小型表示ON、合成履歴、全provider無効の専用profileでは、
起動30/60/90秒後の本体Working Setは49.45/49.47/49.44MiB、子0。
通常終了とstate lease解放を確認し、強制trim/GCは行っていません。
実アカウント取得・CLI/更新helper・画面表示ピークとは測定条件を分けています。

配布物は未署名です。真正な公開更新のauto-apply E2Eは後続の安定versionがある場合の
確認事項であり、この初回Releaseの作成だけで成功とは記載しません。

## 0.2.1 常駐修正・診断

[0.2.1の変更内容と検証の限界](release-notes-0.2.1.md)。
Console runnerは122テストです。小型表示のcloseで本体まで落ちる再現経路を修正し、
bounded・redactedなローカル終了診断を追加しました。
過去の実停止の根因確定や永久再発解消とは扱いません。

公開手順はPRのWindows CI成功、expected head確認、通常merge、mainからの
`draft_release=true`、既存の `release-approval` reviewer承認、draft全資産の
hash/version/runtime NOTICE確認、stable公開、匿名downloadのSHA256/readbackです。
既存のversion/tag/assetsは上書きしません。自動更新のdownload/apply/restartは、
実際に観測できた範囲だけを別途報告します。
