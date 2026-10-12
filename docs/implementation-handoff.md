# Agent Usage Monitor の実装設計

この文書は公開用設計メモです。実アカウントのPoCレスポンス、個人のローカル絶対パス、
内部チャット引き継ぎは含めません。

## 構成

- Core: 型付きquota、JST日/月曜週、固定目安、部分計測、reset/adjustment、通知抑止とJSON履歴。
- Diagnostics: read-only Usage HTTP、既存の選択アカウント認証、期限付き公式Codex fallback、
  Claude statusline scalar exporter。
- Desktop: Windows標準nativeトレイ、オンデマンドWPF子と更新helper。
  本体のみがcredential/保存履歴を所有し、UIへはscalarサマリーだけを渡す。
- IPC: current-user-only named pipe、起動したUI/更新helperのPIDと操作種別を照合。
- 配布: .NET 10 self-contained win-x64、Velopack 1.2.161、installer/portableと全資産SHA256。

VS Code、browser、Electron、WebView、WSL、常駐CLI、HTTPサーバーに依存しません。
アカウント・サービス・bucket・periodが異なる消費は合算しません。

## 固定予算

月枠は実periodの日数で日別に等分、日×7が週の目安。
正確な7日枠の総量÷7が日別目安。百分率だけの場合はquota内のptを単位にします。
分母/開始日が不明なら目安不明、初回前や未観測境界は未計測/部分計測です。
繰越や残量再配分は行いません。

## 通知と復旧

使用率刻み、指定しきい値、日/週目安超過、確認済みresetを別理由で記録します。
複数しきい値は理由ごとにまとめ、候補を表示前に保存します。
古い値・取得失敗・不明値でUsage通知を作りません。
provider減少とreset/plan変更を区別し、timeout/cancel/backoff/Retry-Afterを扱います。

## 接続bootstrap（0.1.1）

DesktopSettings Version=1、サービス別ConnectionMode（Pending / Automatic / Manual / Disabled /
Attempted / NeedsConfirmation）、GithubPinnedAccount / CodexPinnedAccount、GithubExecutable、
ConnectionResults（秘密を含まないmetadata）を保存します。
旧版の未設定はNeedsConfirmation、指定済みaccount/pathはManual。非接続設定だけの保存で
手動の無効化や固定identityを上書きしません。検出前にAttemptedを保存し、以後自動再試行しません。

トレイ本体が期限付きConnectionDiscoveryを所有し、UIにcredentialを渡しません。
初回のみ検出結果を自動採用、再検出では候補の確認だけ。
保存後はSelectionIdと取得開始時のsettings比較、ID pin、stateGateを保ち、
不一致のproviderのLatestを消して古いアカウントの表示・通知を防ぎます。
手動設定がまだpinを持たない場合は、移行時のLatest、または最初の照合済み応答で固定します。
再起動後の保存済みLatestは、今回の起動で同じ選択の取得が成功するまでUIへ出しません。
アカウント未確認の間に古いUsageを復活させないため、ホストに揮発性のverifiedSelectionsゲートを持ちます。

UIは接続結果を主タブ、手入力をExpanderの詳細へ。IPCにdetect / detect-cancel / update-checkを追加。
DesktopViewはDiscoveringとUpdateを加え、設定編集中もstatusだけ更新します。
ConnectionResultsの再検出は保存済みaccount/pathを変更しません。

## 更新・アンインストール

Velopack startupを最初に実行し、auto-applyを無効にします。
更新helperは固定の公式GitHub Releaseのみ読み、明示同意でdownload/applyします。
適用前に常駐本体とUIを終了し、状態leaseを解放してから再起動します。
portableは自己上書きしません。
アプリinstallディレクトリーとUsageデータディレクトリーを分離し、
uninstallは自分のRun登録のみを条件付きで除去します。

0.1.1はAutoUpdateEnabled=trueを既定とし、起動時に専用helperで非同期確認します。
update-status / update-permission / update-exitは起動した更新helperのPIDだけに許可。
適用直前に設定・UI/検出の生存を再確認し、新UIの起動を予約ゲートで止めます。
UI表示中は延期しhelperを終了、閉じた後にfeedと検証済みpending packageを再確認します。
自動更新を無効にすれば適用しません。404はNoStableRelease、失敗はFailed、延期はDeferred。
metadata/feedは1MiB、packageは300MiBまで。HTTPS固定repoとGitHubの配信redirect先だけ、
SHA256とpackage/version/size binding、期限、429待機を適用します。
hostの正常終了を確認できなければ更新適用しません。

オリジナルのゲージSVGと16/20/24/32/48/64/128/256px ICOを同梱。
EXE・installer・Start Menu・WPF・native trayで同じ資産を使用します。
nativeは一度だけロードしたHICONを所有して破棄。読込失敗時のみ通知付きで
shared標準アイコンへfallbackし、そのshared handleは破棄しません。

配布URLは `https://github.com/aktsmm/agent-usage-monitor/releases` のみです。
表示名だけを変更し、packId・EXE・データ/インストール先・Run登録名・namespaceは更新互換性のため維持します。
開発起源のPrivate履歴を移さず、公開先にはsource/docs/assetsの初期snapshotだけを準備します。

承認済みUIの大幅変更や独自認証導入、別スタックへの変更は追加の判断対象です。
