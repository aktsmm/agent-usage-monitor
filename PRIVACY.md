# プライバシー

AI Usage Tray は利用者のPC上で動作します。独自のクラウドバックエンド、解析telemetry、
ブラウザーCookieの収集、会話やコードの送信はありません。

## 接続するサービス

- Copilot: 指定アカウントを照合する `api.github.com/user` と非公開Usage endpoint。
- Codex: 指定された公式CLI認証のアカウントで `chatgpt.com/backend-api/wham/usage`。
  利用者が有効化した場合のみ、公式CLIのUsage読み取りを短時間実行します。
- Claude: 利用者が準備した公式statusline由来の数値JSONをローカルから読みます。
- アプリ更新確認: 利用者の操作時に、`aktsmm/ai-usage-monitor` のGitHub Releasesを確認します。
  インストーラー版の更新資産はGitHub配信先へ接続します。認証tokenは送信しません。

HTTPSを使用し、Usage取得ではリダイレクトに資格情報を渡しません。サービス側には
通常のAPI通信としてアカウント情報・IP等が伝わります。各サービスの規約・プライバシー条件が適用されます。

## 保存内容

`%LOCALAPPDATA%\AiUsageTray` に設定、最終確認値、差分、通知履歴、安全な失敗コードを保存します。
設定には指定したログイン名・メール・パスを含み、履歴にはアカウントidentityのハッシュを含みます。
これは匿名化の保証ではありません。ファイルを共有するときはアカウント・Usage・ローカルパスを除去してください。
差分・失敗は90日、現行期間の通知抑止状態はその期間を保持します。

既存の資格情報は対象アカウントの読み取りにのみ使い、アプリ独自のtoken保存・refresh・loginをしません。
トークンをConsole、ログ、スクリーンショット、Gitへ出しません。
公式CLIとVelopackの通常ログは各ツールが管理します。

アンインストールはアプリのUsage履歴・公式CLI認証を削除しません。削除したい場合は
アプリを終了後、`%LOCALAPPDATA%\AiUsageTray` を利用者が確認して削除してください。
