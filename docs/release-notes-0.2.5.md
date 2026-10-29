# Agent Usage Monitor 0.2.5

Windows 11 x64向けの更新確認修正版です。配布物は**未署名**です。
SHA256SUMSで資産の整合性を確認し、Windowsの保護機能を有効なまま使用してください。

## 更新確認

- 最新安定版の確認とVelopack feed取得を、GitHub REST APIから公開Releaseの静的feedへ変更しました。
- GitHubの共有REST APIレート制限により、更新確認が失敗する問題を回避します。
- Release manifestとVelopack feedをpackage ID・安定version・filename・size・SHA1で照合し、適用前にSHA256を再検証します。
- GitHub Releaseと配信CDN以外へのredirect、draft・pre-release・不正なmanifestは拒否します。

## 検証

Release buildは警告0・エラー0、Console runnerは135/135成功。
公開中の`releases.win.json` / `RELEASES`の取得とSHA256SUMSを含むrelease package検証を実施しました。
インストール済みアプリからの実download/apply/restartは、この検証では行っていません。

## English

This update reliability release replaces GitHub REST API release discovery with the public
static release manifest and Velopack feed, avoiding shared API rate limits. The updater
validates the release metadata and feed together, restricts redirects to GitHub release
assets, and verifies the downloaded package SHA256 before applying it.

The Release build completed with zero warnings or errors, and all 135 console tests passed.
Public feed retrieval and package checksums were verified. An installed-app download/apply/
restart was not performed. Binaries are unsigned; verify SHA256SUMS without disabling
Windows protection.
