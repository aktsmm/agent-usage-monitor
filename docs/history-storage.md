# 日別履歴と保存先

0.2.0では常駐本体の5分取得と、オンデマンドWPFの履歴画面・保存設定をIPCで接続しています。
日本語/Englishを共通リソースで表示します。[架空データのUIモック](ui/history-storage-mock.html)。

## 記録と日別集計

履歴画面は直近30日のサービス・アカウント・契約枠の一覧を初期表示し、
7日・90日または指定月へ切り替えられます。一覧から項目を選び、「日別グラフへ」またはスクロールでグラフを表示します。
期間定義が異なる値は別行にし、観測消費の合計は観測可能な範囲の下限です。
ゼロと未計測を区別します。一覧は読み込み開始時点の結果をページ間で共有し、
期限切れや別の一覧に置き換わった場合は再読み込みを求めます。
観測消費が0の日と未計測の日が混在する場合、グラフの状態文で未計測日の存在を明示します。
日別グラフは Tab で1か所として扱い、左右の矢印キーで日を移動し、Enter または Space で詳細を選択できます。
履歴のサービス・アカウント・契約枠を切り替えても、同じ名前の枠（なければCopilotのプレミアム リクエスト）を選び続けます。Copilotのチャットとコード補完は、既定では一覧と選択肢に出ません（設定でオンにすると表示）。記録そのものは変わりません。
スクリーンリーダーにはグラフ名と同じ操作説明を伝えます。
一覧の欠測日数には、指定月などのまだ到来していない日を含めません。
契約期間の途中で定義が変わった場合、履歴だけでは各定義の厳密な有効期間を確定できないため、
欠測判定は各定義の契約期間全体に対する保守的な判定です。定義別の正確な欠測日数を示すものではありません。
当日中に終了した短期枠も、終了まで観測できていなければ部分計測として示します。
詳細も期間定義が多い場合は分割取得し、同じ日が複数ページにまたがっても統合して表示します。
一覧・詳細ともページのスナップショットと連続性を検証し、不整合な応答は表示せずエラーにします。
通信の上限は応答全体で判定し、単一項目でも上限内なら表示します。
単一項目が応答に収まらない場合はエラーを表示し、保存済み履歴は変更しません。
一覧が大きすぎる場合はカタログを再取得し、取得できればフィルターから他の枠を開けます。
カタログ自体が取得できない場合もエラーを表示します。
日別詳細では、小さい正の日目安や消化率が表示上ゼロにならないよう精度を保ちます。
グラフの各日は棒の間も含めた日付ごとの領域をクリックまたはキーボードで選択でき、支援技術にも日別詳細を渡します。
棒は日別の観測消費を左軸で、線は選択した枠・期間のその日の最終累積スナップショットを右軸で示します。
線は日別差分の合算ではなく、観測値がない日は切れます。ゼロと未取得を区別し、リセットや調整も別の期間または実測値として扱います。
以前の月集計には累積値がないため、利用可能な日別raw、または検証済みZIPから読み取ります。
同じ月で一部のrawだけがない場合も、rawがある日はその内容を検証して使い、ない日だけZIPから読みます。
存在するrawが破損している場合は、ZIPで隠さずエラーを表示します。
どちらもない、または移行時に累積値が保存されていない場合は未取得です。
読み込み中に操作対象を無効化した場合は、画面がアクティブのままでフォーカスが移されていなければ元の操作対象へ戻します。
別ウィンドウに移動した場合はフォーカスを奪いません。
「サービス・アカウント別の使用状況」は常に折りたたんで開き、見出しに件数と選択中の項目を表示します。
期間の選択肢は観測のある日数が多いものを先に並べて既定で選び、記録が2日以下のものには「記録が少ない」と付けます。
単位は期間の見出しの下に説明します（pt=使用率の増加幅、クレジット=APIが報告した使用クレジットで分母なし）。

5分取得を維持し、同じprovider・account identity hash・quota bucket・period・単位の
累積値から得た`UsageDelta`を記録します。累積snapshotの総和、推定料金・token数は作りません。
同じperiodの累積値が一時的に0へ落ち、すぐ元の値へ戻ることがあります（APIの一時的な0応答と推測）。
その戻り分は新しい使用ではないため、期間内の最大値までは調整として扱い、超えた分だけを消費にします。
0へ落ちて6時間以内に戻らない場合は、通常のリセットとして数えます。0以外へ下がる場合は従来どおりです。
過去の記録は書き換えず、日別の集計を作るときに同じ規則で戻り分を除きます。前日の記録も読むため、日またぎの落ち込みも補正します。補正で消費が増えることはなく、除くだけです。
除いた量は日別詳細に表示し、その日は部分計測（下限）になります。古い月の集計は、次の履歴追記のときに1か月ずつ生のrawから作り直します（rawとrecord IDは変わりません）。
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
画面などの読み取りと置換が競合した場合は、既存の内容を保持して短時間だけ再試行します。
解消しない場合は保存失敗として通知し、未保存の記録を保持します。
書き込み前に対象日の封印済みarchive、観測区間の重複、既存summaryを検証し、
これらの事前検証に失敗した場合はcatalog・raw・summaryを変更しません。複数ファイルへの保存は
一括transactionではなく、I/O障害や中断後はjournalから再送を試みます。
同じrecordの再送を二重加算しません。同期中の破損・conflictは自動修復で上書きせず止めます。
履歴directoryが消えた場合も空のdirectoryを勝手に再作成して成功扱いしません。
観測区間の重複では `history_consumption_intervals_overlap` を表示し、該当recordを保存待ちに
残します。単純な再送では解消しないため、既存履歴とlocal journalを削除せず原因を確認してください。

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

## 状態ファイルの大きさ（実測と未対応の設計案）

- 5分ごとの取得で、枠（アカウント×枠）ごとに観測差分を1件追加します。変化がなくても「消費0」の差分として残り、月次アーカイブの検証が済むまで消せません（唯一のrawを捨てない設計）。複数アカウント・上限なし枠を使う環境では枠が増えるため、増え方が速くなります。ある実環境では`state.json`が約28MB、差分約6万件のうち約94%が消費0でした。
- 0.2.12で`state.json`の書き込みをインデントなしにし、1回の書き込みにまとめました（同じ実データで約28MBが約20MB）。`StateStore.JsonOptions`は履歴レコードIDの計算に使うため変更しません（固定IDのテストで保護）。
- 未対応の設計案: 連続する消費0の差分を1区間にまとめると、差分数は桁違いに減ります。ただし履歴レコードID、アーカイブ照合、重複区間の検査と整合させるデータ構造の変更なので、明示的な承認後に別変更で行います。取得のたびに全体を保存する頻度の見直しも同様に別途検討します。
- 増え方は `scripts/measure-history-growth.ps1`（読み取り専用）で確認できます。保存済みの差分数、消費0の割合、サービス・枠ごとの容量を表示し、アカウントIDは表示しません。0.2.17時点の実環境では、`state.json`が約25MB（差分約7.7万件）で、Copilotのチャットとコード補完の枠は消費0が100%でした。この2枠だけで日別履歴の容量の約6割を占めます。
