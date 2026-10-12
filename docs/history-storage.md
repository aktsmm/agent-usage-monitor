# 日別履歴と保存先

0.2.0では常駐本体の5分取得と、オンデマンドWPFの履歴画面・保存設定をIPCで接続しています。
日本語/Englishを共通リソースで表示します。[架空データのUIモック](ui/history-storage-mock.html)。

## 記録と日別集計

5分取得を維持し、同じprovider・account identity hash・quota bucket・period・単位の
累積値から得た`UsageDelta`を記録します。累積snapshotの総和、推定料金・token数は作りません。
初回以前や既に削除された旧履歴は復元しません。旧stateの移行ではperiodの定義と
保存済み差分だけを使い、当時の累積使用率を捏造しません。

日境界はJST。観測差分が日境界をまたいだ場合は推測分割せず、両端の日を未計測/部分計測にします。
観測が全くない日は値`null`であり、観測差分`0`とは区別します。
0差分もAPI丸めによる可能性があり、厳密な無消費の証明ではありません。
観測間隔が10分を超える区間・起動前・reset・adjustmentは部分計測です。
plan・分母・単位・provider periodが変わった区間は別sliceにし、異なるptを合算しません。
`Coverage.CompleteToObservation`も、その日の24時間全体を測定できた意味ではありません。

日目安は既存`Budgets.Calculate`の固定配分を使用します。月枠÷実日数、正確な7日枠÷7。
provider periodの境界日はその日内のperiod区間に比例します。
使用率`%`と、日別消費÷日目安の消化率`%`を別の値として扱います。

## 保存の構造と所有権

設定・最新値・選択account・通知抑止・bootstrap・未転送journalは引き続き
`%LOCALAPPDATA%\AiUsageTray`に残します。内部namespaceやinstall先は変更しません。
`history-settings.json`はschema version・このprofile固有のwriter ID・履歴保存先だけを持ちます。
実際の履歴は既定ではそのprofileの`history\<writer ID>`に置きます。

選択した親folderには`AgentUsageHistory\<writer ID>`を生成します。通常のWindows folderと
OneDrive配下を同じ選択方法で扱います。ディスクrootを選んでもroot自体を履歴領域にしません。
他PCのwriter ID/未知の既存内容を上書きしません。file leaseとmanifestのwriter照合を行います。
同じprofile/configを複数PCへコピーした同時書き込み・自動合算は非対応です。
ネットワークFSのlock・atomic replace、クラウドsyncによる分散lockを保証しません。
この初版ではjunction/symlink越しの保存は対象外です。通常folderを選んでください。

```text
history.json                         schema / writer / JST / measurement metadata
catalog.json                         recorded provider / account hash / bucket index
months/
  2026-10/
    2026-10-12.json                   bounded daily raw shard
    summary.json                     compact monthly daily/period slices
archives/
  2026-09.zip                         raw daily shards + hash manifest
```

巨大な全期間JSONを毎回rewrite/readしません。rawは月内の日別shard、集計は月別です。
各JSONは8 MiB上限、queryは最大93日で必要な月の集計だけを読みます。
上限・未知schema・破損・他writerを明示エラーにし、空グラフへ置換しません。
長期間の取り込みはresident側で最大512件ずつ再送し、全archiveを常時読みません。
グラフ描画・範囲filterはオンデマンドWPF childで行います。

保存先が利用できない間はlocal journalにrecordを保持し、`HistoryStorageStatus`で
pending件数と安全なerror codeを返します。保存済みと偽りません。
rawとsummaryをatomic replacementしてからlocal receiptを保存し、確認済みqueue entryだけを
消します。raw保存直後のcrashはrecord IDによる再送でsummaryを修復します。
同じrecordの再送を二重加算しません。同期中の破損・conflictは自動修復で上書きせず止めます。
履歴directoryが消えた場合も空のdirectoryを勝手に再作成して成功扱いしません。

## アーカイブと保持

確定月だけをZIP化し、manifest・entry size・SHA256・schema・record IDを再読して検証します。
日別集計はZIPへ隠さず残します。ZIP作成後も元のrawは保持し、初期設定で自動削除しません。
未転送recordがある間はservice経由のarchiveを止めます。
archive済み月への新record追加は明示エラーです。既存archiveを自動上書きしません。
旧月の遅延recordがある場合は、先に未転送分を処理してからarchiveしてください。

旧`StateStore.Load/Save`による「90日を過ぎた唯一のrawの削除」は停止しました。
`HistoryRetention.Prune`は実際に検証したarchive内のrecord IDが与えられた場合に限り、
90日より古いstate内rawを整理できます。disk上の元rawは削除しません。
archive/保存確認ができない場合は90日を超えてrawを保持し、容量が増えることがあります。
現行provider periodのnotification dedupはそのまま保持し、archiveで再通知しません。
自動削除/保持期間の変更はまだ提供しません。追加する場合も対象previewと本人承認が必要です。

## 保存先変更

`HistoryMigration.PreviewAsync`は元/先・対象file数・byte量・既存一致file・空き容量・
書込可否を返します。空き容量を取得できないfilesystemでは`null`を表示し、無限とは扱いません。
書込試験には一時probeだけを使い、自分が作ったprobeを除去します。

利用者の確認後に`ExecuteAsync(..., userApproved: true)`を実行します。
preview後のsource変更とdestination競合を再確認し、コピー先を上書きしません。
個別fileを一時fileへコピーしてhash照合後に確定し、全fileのhashとcontent/schemaを照合した
場合だけ`HistoryService.ChangeLocationAsync`がローカル設定を切り替えます。
元データは削除・移動しません。取消や途中中断時も元の保存先を維持します。
中断後に残った完成済みcopyは一致を確認して再利用できます。
conflict/未知file/不完全copyがある場合は明示停止し、利用者が確認して別の空folderを選べます。
recursive root cleanup、他アプリ認証、installer、Run登録の変更はありません。

## 保存場所を開く

`HistoryLocations`はアプリ所有manifestを照合し、実際に作成済みの履歴folder、
archive folder、表示月のsummary、選択月のZIPだけを対象にします。
file表示はExplorerの`/select,`を使用します。任意path/URL/credential file/EXEを入力させません。
存在しない・権限がない・Explorer起動不能は画面へ明示feedbackを表示します。
開いただけでarchiveのhash検証済みと表示しません。モックのボタンは実Explorerを起動しません。

## 開発用APIの接続順

1. 本体だけが`HistoryService.LoadAsync(localProfile)`とローカル`StateStore`を所有。
2. 起動時は保存済み`DailyHistory.Import(state)`をqueueへ投入。初回前のUsageを捏造しない。
3. 通常pollでは`UsageEngine.Apply`が追加したdeltaと対応snapshotを`HistoryRecord.Create`へ渡す。
   journal保存失敗でもstateの唯一のrawを捨てず、取得/保存errorを明示。
4. UIへpending状態とwriter/正確な保存先を渡す。query/graphはchildに必要な月集計だけ渡す。
5. migration preview tokenと利用者確認を結び付け、本体のstate gate下でcopy/verify/settings切替。
   自動更新shutdownやlease解放と競合させない。

参考: [.NET 10 ZipArchive](https://learn.microsoft.com/ja-jp/dotnet/api/system.io.compression.ziparchive?view=net-10.0)。
