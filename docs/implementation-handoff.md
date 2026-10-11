# AI Usage Tray の実装設計

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

## 更新・アンインストール

Velopack startupを最初に実行し、auto-applyを無効にします。
更新helperは固定の公式GitHub Releaseのみ読み、明示同意でdownload/applyします。
適用前に常駐本体とUIを終了し、状態leaseを解放してから再起動します。
portableは自己上書きしません。
アプリinstallディレクトリーとUsageデータディレクトリーを分離し、
uninstallは自分のRun登録のみを条件付きで除去します。

配布URLは `https://github.com/aktsmm/ai-usage-monitor/releases` のみです。
開発起源のPrivate履歴を移さず、公開先にはsource/docs/assetsの初期snapshotだけを準備します。

承認済みUIの大幅変更や独自認証導入、別スタックへの変更は追加の判断対象です。
