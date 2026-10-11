# セキュリティ

公開Issueにはtoken、認証JSON、メール、会話、スクリーンショット中の実アカウントやUsageを貼らないでください。
公開後にGitHubのPrivate vulnerability reportingを有効化した場合は、その非公開報告先を利用してください。
非公開報告機能が未提供の場合は機微データを含むIssueを作成しないでください。

## 境界

- 独自ログイン・ブラウザーCookie採取・推論によるquota probeはありません。
- 別の読めるアカウントへ勝手に切り替えません。指定されたGitHubログイン/Codexメールを照合します。
- 更新確認は公式 `https://github.com/aktsmm/ai-usage-monitor/releases` のみ。
  ポータブル版の自己置換や任意URLのEXE起動はありません。
- WPF子とのIPCは現在ユーザー限定named pipeかつ起動したPIDを照合します。
  同じWindowsユーザーの侵害から保護するsandboxではありません。
- 環境変数のAPI keyは画面/更新helperへ渡しません。秘密値をIPCへ含めません。

## 配布と検証

全配布資産（Setup、Portable ZIP、更新package、feed等）のSHA256を `SHA256SUMS.txt` に含めます。
hashはファイル整合性の確認であり、配布元の真正性を単独で保証しません。
署名証明書を用意するまではEXE/installerは未署名で、SmartScreenの警告が出る場合があります。
Defender除外やセキュリティ機能の停止は推奨しません。

非公開Usage APIは保証された公開contractではなく、形式変更・認証/SSO制限で取得できなくなる場合があります。
その場合は古い値・失敗を示し、0%に置換しません。
