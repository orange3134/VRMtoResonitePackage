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

## 構造上の増幅要因

### メッシュ全体の圧縮保存が複数回走る

ModelImporterの初回保存に加え、表情名・欠落shapeの補修、法線の再計算、輪郭線用submeshの分割、
FirstPerson用メッシュ作成が、それぞれメッシュを複製して保存・再読み込みする。
必要な工程は入力設定によって異なるが、変更が少数のshape名や三角形だけでも、保存対象は全メッシュ。
頂点数とblendshape frame数が大きいアバターほど、この繰り返しの影響が大きくなる。

`VrchatBlendShapeRepair` と `VrchatOutlineExclusions` の `save-asset` が大きいときは、
shape編集やマスク判定の高速化だけでは改善しにくい。
対象DLLの `LocalDB.SaveAssetAsync(MeshX, ...)` と `MeshX.Encode` を確認する。
現在確認した実装は既定がLZ4で、内部では `LZ4StreamFlags.HighCompression` を使う。
`SaveAssetAsync` という名前でも、ファイルへのEncodeはTaskを返す前に同期実行される。
将来のDLL更新で実装が変わり得るため、デコンパイル済みソースだけで判断しない。

改善方針は次の順で検討する。

1. 中間保存の圧縮コストを分離して測る。同じ実メッシュをLZ4とPlainで保存し、サイズ・所要時間・
   再読み込みを比較する。Plainはサイズが増えるので、配布パッケージの形式変更とは分けて判断する。
2. 表情補修・法線・輪郭線などの編集を同じ作業メッシュへ集約し、確定時の保存を減らす。
   現状は各処理が再読み込み後のrenderer/providerを参照するため、単に待機を削除してはならない。
   作業メッシュと最終providerの責任を明確にし、共有メッシュの変更を他の配置へ漏らさない。
3. 名前の正規化などで変更がない場合は、全frameの複製やsnapshotを作る前に判定する。
   コピー削減はメモリ・GCにも有効だが、保存が支配的なら最優先ではない。

保存形式の比較では、再読込したメッシュを同じPlain形式へ正規化して全バイトを比較できる。
保存回数の削減ではさらに、初期weight、skin、形状差分・法線、submesh/material割当、
FirstPerson、保存済み表情の再生を確認する。これらの補正を無効化して速度だけ比較しない。

### FBXの参照解決器が何度も構築される

`UnityPackage.ModelFileIds` にはパッケージ内キャッシュがある一方、
`VrchatAvatarParser` の各所には `new UnityModelFileIdResolver(...)` と局所辞書が残っている。
特に `ResolvePhysicsTarget` は対象ごとに新しい辞書を渡し、骨・ignoreTransforms・colliderの解決が
同じFBXの再解析につながり得る。`ResolveFbxNodeName` も呼び出しごとに解決器を構築する。

解決器の構築は単なる辞書作成ではない。FBXのblendshape既定値を読み、Assimpでモデル全体を読み、
必要に応じてmaterial分割前のskin情報のため再importし、fileID/path候補を生成する。
同じソースの構築回数が参照数に比例すると、複雑なPrefab・物理設定で解析時間とメモリ消費が増える。

最初の対策候補は、パッケージのキャッシュを迂回する箇所を統一すること。
さらにソースFBXの不変な解析情報を共有できるが、Prefabの配置IDと元アセットGUIDを混同しない。
共有するのはソース情報であり、配置ごとのoverride・削除・ボーン接続は別に保持する。
同名・同一FBXの複数配置、短縮skin配列、variant、EditorOnly、PhysBoneの回帰テストが必要。

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
