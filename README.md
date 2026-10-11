# AI Usage Tray

Windowsのトレイから、GitHub Copilot・Codex・Claude Codeの契約枠の使用量と
日/週の固定目安を確認する軽量アプリです。VS Code、ブラウザー常駐、Electron、WebView、
WSL、独自クラウドバックエンドを必要としません。

![架空データによるダーク表示](docs/images/usage-demo-dark.png)

画像は架空データです。実アカウントのUsage・認証・ローカルパスは含みません。
[ライト表示](docs/images/usage-demo-light.png)。

**0.1.0は公開準備中の候補です。** 署名・公開snapshotの確認と公開Releaseはまだ完了していません。
この文書は公開候補向けであり、未公開のダウンロードを案内するものではありません。

## できること

- サービス/アカウント/枠を合算せず、使用率・残量・取得時刻・resetを表示。
- Copilot月枠÷実際の日数を日目安、日×7を週目安に固定配分。
- Codex/Claudeの週間枠が得られる場合は枠÷7を日目安にする。
- 使用率の刻み、80/90/95%等のしきい値、日/週目安超過、確認済みresetを通知。
- 通知と観測差分を保存し、再起動後の同一通知の連発を抑止。
- ダーク/ライト、枠ごとの通知設定、本人が選ぶログオン時起動。
- インストーラー版のアプリ内更新確認、ポータブル版の公式Release案内。

## 使い始める

Windows 11 x64、利用するproviderの契約と公式CLIの本人認証が必要です。
Releaseのinstallerとportable ZIPは.NET 10 Windows Desktop Runtime同梱です。
ソースから開発する場合は.NET 10 SDKが必要です。

### インストーラー（公開後の推奨）

1. [公式Releases](https://github.com/aktsmm/ai-usage-monitor/releases)から
   `AiUsageTrayDesktop-win-Setup.exe` と `SHA256SUMS.txt` を取得。
2. SHA256を確認してinstallerを実行。
3. スタートメニューの **AI Usage Tray** を開き、**設定 → 接続**で対象アカウントを指定。
4. 常駐する場合はトレイの **ログオン時に常駐** または **外観・起動**設定で選択。

installerは現在のユーザーの `%LOCALAPPDATA%\AiUsageTrayDesktop` に配置します。
スタートメニューのショートカットを作成しますが、自動起動は勝手に有効にしません。

### ポータブル ZIP

`AiUsageTrayDesktop-win-Portable.zip` を安定した書き込み可能フォルダーへ展開し、
`AiUsageTray.Desktop.exe` を実行してください。ZIP内部のruntime一式を保持してください。
ポータブルでもデータはアプリのフォルダーでなく `%LOCALAPPDATA%\AiUsageTray` に保存します。
自己上書き更新はせず、新ZIPまたは公式installerへ案内します。

```powershell
Get-FileHash -LiteralPath '.\AiUsageTrayDesktop-win-Setup.exe' -Algorithm SHA256
```

`SHA256SUMS.txt` はinstaller/ZIP/更新package/feed等の全資産のhashを記載します。
hashは整合性を確認するもので、署名の代替ではありません。現候補は未署名のため
SmartScreen等で警告される場合があります。Defender除外や保護機能停止は推奨しません。

## トレイと更新

左クリックで利用状況、右クリックで **Usageを更新 / 設定 / アプリの更新を確認 /
ログオン時に常駐 / 終了**。画面を閉じてもUsage取得と通知は続きます。
アプリを止める場合は **終了** を選びます。

アプリ更新は本人がメニューを選んだときだけ確認します。GitHubの
`aktsmm/ai-usage-monitor` の安定Releaseのみを使用し、tokenは送信しません。
installer版はダウンロード・hash検証後に、画面/取得子を閉じて履歴を保存し、更新して再起動します。
自動適用は無効です。portable版は公式Releaseページを開くだけで、任意URLのEXEを実行しません。
ネットワーク/更新失敗は表示し、Usageや設定をゼロ/初期値へ戻しません。

## 接続と非公開APIの注意

初期値は全provider無効です。利用者の指定した既存アカウントだけを使用します。

| サービス | 認証と取得 | 制限 |
| --- | --- | --- |
| Copilot | 選択済み`gh`認証、または指定した正確なWindows Credentialターゲット1件。GitHubログインを照合してUsage-only GET | `copilot_internal/user`は非公開API。plan名から旧premium request回数やクレジット分母を推定しない |
| Codex | 指定`CODEX_HOME\auth.json`のChatGPT認証。メールmetadataを照合し選択account_idでUsage-only GET | `wham/usage`は非公開API。取得したwindow durationから短期/週を区別 |
| Claude Code | 本人が準備した公式statuslineのscalar snapshot | CLI導入済みでも本人login・対応する契約・statusline連携が必要。未取得は0%でない |

GitHubログイン/Codexメールが一致しなければ止めます。無差別なcredential探索、
VS Code secret storage、browser Cookie、独自login/token保存/refresh、ダミー推論はありません。
JWTのdecodeはローカル選択metadataを読む処理であり、署名検証ではありません。
providerの契約、企業SSO/ポリシー、各サービスの規約を確認してください。

Codexの限定fallbackを有効化すると、公式 `codex app-server` で
`initialize / initialized / account/read(refreshToken:false) / account/rateLimits/read` のみ実行します。
推論やcredit消費は呼びません。CLIは取得時だけ起動し期限・終了を管理します。
**fallbackも同じbackendを使うためサービス障害には効きません。**

5分ポーリング、20秒取得期限、連続失敗backoff、429 Retry-Afterを尊重します。
401・403・ネットワーク・スキーマ変更を区別し、古い最終確認値は日時とともに表示します。
非公開APIの変更・分母/reset欠落時に数値を捏造しません。

### Claude statusline

CLIの公式loginは利用者が行います。既存statuslineをアプリが自動で上書きすることはありません。
`AiUsageTray.Diagnostics.exe claude-export <ラベル> <JSONパス>` は公式statusline JSONのstdinから
`five_hour`/`seven_day`の`used_percentage`・`resets_at`だけを保存します。
会話、transcriptパス、session/tokenは保存しません。

既存statuslineと同じstdinをexporterへ渡す連携は、設定差分を確認してから行ってください。
最初のAPI応答後だけ更新されるため停止中のライブ値ではなく、古いsnapshotは新通知に使いません。
statuslineに確実なaccount identityがないため、利用者が公式CLIで確認したラベルを指定し、
アカウントを変えた場合はラベルも変えてください。
Claude.ai資格情報を集める直接OAuth取得は実装していません。Admin/API費用は契約ライブ枠の代替ではありません。

## 目安と未計測

目安は利用を止める制限ではなく、固定の消費ペースです。繰越・残量再配分はありません。
使用率だけなら同一quota内のpercentage-point（pt）を使い、token/金額を作りません。
日境界はJST、暦週は月曜開始。provider resetをまたぐ週は枠のperiodごとに分けます。

初回起動前や未観測の境界をまたいだ使用量は **未計測/部分計測** です。
0差分はproviderの累積観測値の差が0だっただけで、丸めがあるAPIの厳密な無消費を証明しません。
減少/top-up相当とreset/plan変更は消費と分けます。
通知候補は表示前に保存するat-most-once方式で、直後のcrashやWindows集中モードにより
配送されない可能性があります。Windows通知の配送は保証しません。

## データとアンインストール

設定・Usage差分・通知履歴は `%LOCALAPPDATA%\AiUsageTray` に保存します。
差分/失敗は90日保持し、現行periodの通知抑止キーを保持します。
設定には本人のアカウント名/メール/パスを含むため、共有する際は除去してください。

Windowsのアプリ設定からアンインストールすると、installer管理の本体/ショートカットと、
そのinstall配下を指すアプリ自身のRun登録を除去します。Usage履歴・公式CLI認証は消しません。
portableを移動/削除する場合は先にログオン時起動を無効にしてください。
[プライバシー](PRIVACY.md) / [セキュリティ](SECURITY.md) / [第三者NOTICE](THIRD-PARTY-NOTICES.txt)。

## 軽量性

常駐本体はWindows標準トレイ/メッセージループと直接HTTP、WPF画面は表示時だけの子プロセスです。
画面を閉じると子を終了します。IPCはcurrent-user-only named pipeと起動したPID照合。
Windows NLSを使用し、不要なICU/WinForms/WPF描画の常駐を避けます。

初版の短時間検証では、表示/終了を3回繰り返して終了30秒後の本体＋全生存子孫の
Working Setは約70〜74MiB、表示中は合算約214〜224MiBでした。
最終runtime同梱候補は画面終了30秒後約93MiB、追加60秒後も約93MiB（子0）、
表示中約256MiBでした。**常駐と表示ピークは別**です。
runtime同梱配布の容量、長期運用、別PC、更新/CLI取得ピークは別に検証します。
強制trim/GCやPrivate Bytesへの指標差し替えは行いません。

## 開発と公開準備

```powershell
dotnet build .\AiUsageTray.slnx -c Release
dotnet run --project .\tests\AiUsageTray.Tests -c Release
dotnet run --project .\src\AiUsageTray.Desktop -c Release
.\scripts\package.ps1
```

テストはConsole runnerです。`dotnet test`だけではこのテスト群を実行できません。
[配布手順と公開ゲート](docs/distribution.md) / [設計](docs/implementation-handoff.md)。
UI参考: [ダーク](docs/ui/usage-tray-dark.html) / [ライト](docs/ui/usage-tray-light.html)（架空値）。
`AiUsageTray.Desktop.exe --demo`（または`--demo --light`）は公開画像作成用の
架空データ表示です。認証・接続・保存・常駐は行いません。
## ライセンス

本プロジェクトが作成したソースコード、アプリbinary、スクリプト、テスト、文書と画像は
[CC BY-NC-SA 4.0＋Microsoft Corporationおよび関連会社への追加許諾](LICENSE)です。
Microsoft向けはNonCommercial制限だけを免除し、Attribution・ShareAlike・no-additional-restrictionsは維持します。
他の利用者には標準の非商用条件が適用されます。第三者依存・商標・取得したサービスデータはこの許諾の対象外です。

非商用制限があるためOSI定義のオープンソースではなく、**ソース公開アプリ**として提供します。
Creative Commonsは一般にソフトウェアへのCCライセンス適用を推奨していません。
このライセンスはソース提供を義務付けるソフトウェア専用の条項を持たず、特許権・商標権も許諾しません。
それらの限界を踏まえたオーナーの選択です。依存の元ライセンスは保持します。

## 公式資料

- [Codex App Server](https://developers.openai.com/codex/app-server)
- [Claude statusline](https://code.claude.com/docs/en/statusline#rate-limit-usage)
- [Claude認証規定](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use)
- [Windows .NET導入](https://learn.microsoft.com/ja-jp/dotnet/core/install/windows)
- [Windows NLS/ICU](https://learn.microsoft.com/ja-jp/dotnet/core/extensions/globalization-icu)
- [Velopackの統合](https://docs.velopack.io/integrating/overview)
