# unitypackage変換の性能診断

## 計測

`RESOPON_PROFILE=1` を設定すると、変換ログと標準出力に `[Profile]` を出す。
通常は無効。変換のアルゴリズム・保存形式は変更しない。

```powershell
$env:RESOPON_PROFILE = '1'
$env:RESOPON_NOPAUSE = '1'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-local-avatar.ps1 -CaseId <registered-id>
Remove-Item Env:RESOPON_PROFILE
```

`seconds` は同じ計測オブジェクトの前回チェックポイントからの経過秒数、
`total` はそのオブジェクトの開始からの累計秒数。`Stopwatch` による実時間で、CPU時間ではない。
ラベルはその時点までに終わった処理を表す。末尾の `end` は残りの処理とスコープ終了を表し、
成功判定ではない。終了コードと生成パッケージの `--inspect` を別途確認する。
各区間はスレッド切替やロック待ちも含む。特に `save-asset` はEncodeだけでなく
LocalDBへの取り込みも含むため、圧縮単体の時間は別の保存実験で切り分ける。

| スコープ | 主な対象 |
|---|---|
| `run` | ログ初期化後のエンジン起動、ワールド作成、変換全体 |
| `vrchat` | 展開、解析、FBX前処理・import、階層、表情補修、材質、FirstPerson、書き出し |
| `parse-input` / `parse-view` | 候補探索、選択範囲の合成、モデル、Renderer、表情、PhysBone、MA |
| `fbx-fileids:<asset>` | FBX参照解決器の構築。繰り返し回数と合計時間も調べる |
| `repair-mesh:<renderer>` | メッシュ複製、shape編集、LocalDB保存 |
| `materials` / `outline-mesh:<renderer>` | 材質生成、輪郭線補正の複製・三角形分類・保存 |
| `outline-exclusions` | 全メッシュの処理と保存後の再読み込み待機 |

親の時間に子の時間が含まれるため、`run`、`vrchat`、`repair-mesh` などを足し合わせない。
複数入力の場合は同じスコープ名が再登場するので、入力ごとに区切って読む。
`--no-avatar` のときはアバター設定とFirstPersonの区間が存在しない。
`--vrchat-dump` でも解析器の計測は有効だが、候補診断など通常変換にない処理を含むため、
dump全体の時間を変換の `avatar-parse` と同一視しない。

GUIの候補一覧は `avatar-list` で標準出力へ計測するが、子プロセスの変換ログには含まれない。
CLI実測にGUIの一覧取得やユーザーの選択待ち時間を加えたものがGUIの体感時間になる。

性能測定は実変換を並行実行せず、同じ入力・選択アバター・オプション・DLLで行う。
初回と再実行ではOSキャッシュ等が異なる。最低でも再実行して変動を確認する。
入力のSHA-256、変換器とResonite DLLの情報、工程別の結果は `.local/` / `.tmp_verify/` に保存し、
アバターや環境固有パス、測定結果をコミットしない。

## 実装済みの対策と残る増幅要因

### メッシュ全体の圧縮保存が複数回走る

ModelImporterの初回保存に加え、表情名・欠落shapeの補修、法線の再計算、輪郭線用submeshの分割、
FirstPerson用メッシュ作成が、それぞれメッシュを複製して保存・再読み込みする。
必要な工程は入力設定によって異なるが、変更が少数のshape名や三角形だけでも、保存対象は全メッシュ。
頂点数とblendshape frame数が大きいアバターほど、この繰り返しの影響が大きくなる。

VRChatの表情補修・法線再計算・輪郭線補正は `MeshAssetWriter.SaveAsync` を使う。
通常の `LocalDB.SaveAssetAsync(MeshX)` は `MeshX.Encode` の高圧縮LZ4を使うため、
ここでは `LZ4StreamFlags.None` の外側ストリームへ完全なPlain MeshXを直接Encodeする。
これは実DLLの `MeshX.Decode` が対応する形式で、ヘッダや頂点・frameのバイナリ構造は
引き続きMeshX自身が書く。独自のヘッダ解析・固定バージョン・全メッシュのbyte配列化は行わない。
ファイルを閉じてからLocalDBへ取り込み、失敗時は部分ファイルを削除する。

出力も圧縮済みのままで、非圧縮メッシュをパッケージへ大量に残さない。
高圧縮よりファイルが多少大きくなる可能性はあるが、形状や属性の精度は変わらない。
呼び出し元は背景スレッドで保存し、従来通りproviderの再読み込みを待つ。
ModelImporter内部の初回保存とFirstPerson生成はエンジン既定の保存経路を維持する。
DLL更新時は `MeshX.Decode` の外側LZ4対応を実DLLと回帰テストで再確認する。

表情名の正規化は名前だけを先に調べ、変更不要なら全メッシュの複製・全frameのsnapshotを省略する。
補正をまとめて保存回数自体を減らす余地は残る。ただし各処理が再読み込み後のrenderer/providerを
参照するため、単に待機を削除してはならない。作業メッシュと最終providerの責任を明確にし、
共有メッシュの変更を他の配置へ漏らさない。

保存形式の比較では、再読込したメッシュを同じPlain形式へ正規化して全バイトを比較できる。
保存回数の削減ではさらに、初期weight、skin、形状差分・法線、submesh/material割当、
FirstPerson、保存済み表情の再生を確認する。これらの補正を無効化して速度だけ比較しない。
新旧パッケージの表情比較では、両方のメッシュアセットの読み込み完了を待つ。
固定フレーム数だけの待機ではshape名が未取得のままになり、比較用キーが衝突する場合がある。

### FBXの参照解決器をソース単位で共有する

解析器の取得は `UnityPackage.ModelFileIds` に統一する。
キャッシュのキーはWindows上で正規化したソースファイルとmetaの絶対パスの組で、
非所有のPrefabビュー間でも共有する。同じFBXを繰り返し配置しても解析は再利用する一方、
異なるmetaを持つソースは分離する。見つからないアセットはキャッシュしない。
パッケージを開き直せば新しいキャッシュになるため、別の変換へ古い解析結果を持ち越さない。

解決器の構築は単なる辞書作成ではない。FBXのblendshape既定値を読み、Assimpでモデル全体を読み、
必要に応じてmaterial分割前のskin情報のため再importし、fileID/path候補を生成する。
同じソースの構築回数が参照数に比例すると、複雑なPrefab・物理設定で解析時間とメモリ消費が増える。

共有するのは変換中に不変なソース情報であり、配置ごとのoverride・削除・ボーン接続・合成済みsceneは
別に保持する。取得した解析器の辞書や配列を呼び出し元で変更しない。
同名・同一FBXの複数配置、短縮skin配列、variant、EditorOnly、PhysBoneの回帰テストを維持する。

### GUI前処理と全体の直列実行

GUIは候補選択時に `UnityPackage.Open` / `ListAvatars` を実行して破棄し、子プロセスが再度展開・解析する。
展開はgzip/tar全体が対象で、選択アバターに不要なアセットも書き出す。
ただし候補一覧では詳細解析を遅延しているため、「全アバターを完全変換している」とは判断しない。
`UnityPackageCache` はUnityプロジェクトの有効なPackageCache選択用で、unitypackage展開の永続キャッシュではない。

GUIはunitypackageごとに変換ジョブを作るため、複数packageではエンジン起動も繰り返す。
展開済みデータの子への引き渡しやバッチ化は候補だが、実測で展開・起動が占める割合を先に確認する。
入力変更の検出、キャッシュの寿命、キャンセル・異常終了時の削除、LocalDBのプロセス分離が必要になる。

FBX importと各加工工程は基本的に直列。まず重複解析と保存を減らし、その後で独立なCPU処理の
並列化を検討する。WorldのSlot/component操作を無制限に並列化せず、巨大メッシュの同時複製による
ピークメモリも測る。既定の30更新待ちだけを短縮しても、圧縮や解析が分単位なら効果は小さい。

## ログの読み違いを避ける

- 時刻付きログの空白区間は、その間の処理を示す候補であり関数単位の計測ではない。
- セッション取得などのネットワークタイムアウトはバックグラウンドでも記録される。
  タイムアウトの秒数をそのまま変換の待ち時間として足さない。
- `ModelImporter` の時間にはAssimp以外のメッシュ生成・保存・アセット読み込みも含まれる。
  import全体が遅いだけでAssimp単体が原因とは断定しない。
- `.prefab` 入力はUnityプロジェクトのアセット探索を行う。unitypackageの展開時間と分ける。
