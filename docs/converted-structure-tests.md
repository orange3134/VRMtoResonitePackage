# Unity・変換前・変換後の階層比較

骨やスロットの増加を調べるときは、UnityとResoPonの**変換前解析が一致しただけで修正完了にしない**。
同じ入力・選択アバターを変換し、保存済み `.resonitepackage` から階層・骨接続を採取して比較する。
以下は開発用ツール。配布GUIの通常変換には影響せず、採取JSONや実アバターを配布物に含めない。

## 一括実行

[Unity参照データ](unity-reference-tests.md)で作った解決済みv2の `coverage.json` を使う。
Python 3.11以降、.NET 10 SDK、インストール済みResoniteが必要。Unityの再起動は不要。

```powershell
python -B scripts/check-converted-avatar.py .local/unity-resolved/<collection>/coverage.json `
  --case omaneko-110 --output .tmp_verify/structure-check/run-001
```

出力先は `.tmp_verify/` 以下の新規ディレクトリ。入力・期待値のSHA-256、選択Prefab GUID、
Prefab入力の依存ファイルと設定を検査する。既存の期待値やローカル登録一覧を上書きしない。
coverageの `conversionArguments` は `[]`（本番の既定選択）または `['--avatar', '名前']` が必須。
アーカイブ外の依存を補完したUnity基準は、通常変換と入力が異なるためこのrunnerでは拒否する。
その場合は依存が揃ったプロジェクト内Prefabで再採取する。

実行内容:

1. 現行ソースから解析・採取ツールをビルドする。
2. 本番と同じアバター選択を検証し、変換前解析を再採取してUnity基準と比較する。
3. 同じ入力を変換し、保存パッケージを `--inspect` で再読込する。
4. パッケージの保存値から構造を採取し、三段階の比較レポートを出す。
5. 入力・期待値・監視対象依存のハッシュを再照合する。

| 出力 | 用途 |
| --- | --- |
| `report.json` | コマンド、ログ、コード・DLL・入力のハッシュ、各工程の成否 |
| `parser.json` / `parser-comparison.json` | 変換前のv2解析とUnityとの差分 |
| `package/*.resonitepackage` / `inspect.log` | 実変換結果と保存形式の再読込確認 |
| `converted.json` | 全スロット・親子構造・姿勢・骨参照の保存値 |
| `converted-tree.tsv` | 表計算等で読めるID・親・有効状態・名前パス・コンポーネント型一覧 |
| `comparison.json` | 階層の対応候補、骨接続の差分、未対応スロットとその参照元 |

変換成功・解析一致・採取成功・構造比較は別の状態として記録する。解析不一致でも変換と採取を続け、
入力不整合では停止する。終了コードは、比較対象範囲の一致0、要調査・工程失敗1、事前条件不備2。
`captured` や `inspect=passed` は階層の正しさを保証しない。runnerはUnity基準を再採取しない。

## 保存済みパッケージから採取する

```powershell
dotnet build tests/PackageRegressionDump -c Release -o .tmp_verify/structure-tools
dotnet .tmp_verify/structure-tools/PackageRegressionDump.dll --structure `
  avatar.resonitepackage .tmp_verify/structure-check/converted.json
python -B scripts/compare-converted-structure.py unity.json parser.json `
  .tmp_verify/structure-check/converted.json --report .tmp_verify/structure-check/comparison.json
```

親ディレクトリは事前に作成し、JSONには新しいファイル名を指定する。
`--structure` なしの既存 `resopon.package-snapshot` v1形式は従来どおり。
新形式は `resopon.converted-structure` v1。エンジンを起動せずRecordPackage/DataTreeを読む。
単独比較は渡されたJSONを比較するだけなので、元入力と変換パッケージの同一性の確認は一括runnerを使う。

採取内容:

- 非アクティブを含む全スロットのID、親ID、兄弟index、名前パス配列、tag、有効状態。
- ローカル位置・回転（xyzw）・スケール。Resoniteの保存座標系をそのまま記録する。
- コンポーネント型とEnabledの保存値、SkinnedMeshRendererの順序付きBones・BlendShapeWeights。
- BipedRigの部位名からスロットへの参照。
- 各コンポーネントの保存フィールドにあるスロット参照候補（フィールドパス付き）。
  DynamicBone等の参照元調査にも使える。シリアライズされた値がスロットIDと一致するものを列挙するため、
  同じID文字列を持つ文字列フィールドと参照型の区別までは行わない。

スロットIDは `r/兄弟index/...`。同名スロットを区別するが、階層挿入で変わる。
`serializedId` は保存データ内の参照解決用で、別変換との照合キーにはしない。
骨参照は `resolved / null / unresolved` を区別し、順序、重複、nullを消さない。
骨かどうかは名前で推定せず、スキンまたはrigの参照で判断する。

## 比較結果から修正する手順

1. `parserParity` が不一致なら、先にUnityと変換前の `parser-comparison.json` を確認する。
   元のUnity基準を保持し、Prefab合成・FBX解析など原因の工程を修正する。
2. `transforms` の対応候補と `unmappedSlots` を確認する。完全なルート相対名前パスを
   変換後パスの末尾と照合する。変換用ラッパーの挿入を許容するが、同名の候補が複数なら
   `ambiguous` とし、自動選択しない。名前中の `/` は区切らず親IDからパスを復元する。
3. `unmappedSubtrees` で連続した未対応部分木を数え、`referencedBy` で利用箇所を追う。
   `sameNameSourceIds` は調査用で、対応判定には使わない。表情・装着・素材用の生成スロットも
   含まれる。参照がないことだけを削除理由にしない。
4. 曖昧な対応を入力・変換処理から確認できた場合だけ、明示的な対応表を作る。
   ファイルには `unitySha256`、`packageSha256`、`transforms`（Unity Transform ID→変換後Slot ID）を
   記録し、比較CLIへ `--mapping mapping.json` を渡す。ハッシュが変わった対応表は拒否する。
   多対一指定は認めず、自動候補との衝突も曖昧として残す。ルートは自動対応しない。
5. 対応候補上の有効状態、対応した祖先、同型Renderer、順序付きbone接続を比較する。
   `siblingOrders` は元の兄弟同士の相対順序を比較する。補助階層を経由したSlotの位置も考慮し、
   未対応の親・子や親関係の不一致がある群は `unverified` にする。差は `sibling-order` として
   報告する。生成補助Slotを含む絶対indexの一致や、skinのBones配列順とは区別する。
   `differences` と `unverified` を分け、未対応骨をnull一致と見なさない。
   bone数・順序の差にはインポータの未使用骨除去やスキン再構成もあり、差分だけで不具合とは断定しない。
6. 原因の変換工程を修正し、自作fixtureまたは明示したパッケージ期待条件を回帰検査に追加する。
   新しい出力ディレクトリで一括runnerを再実行し、修正前後の報告を保持する。
   スキン変更は[保存スキンの追従試験](local-avatar-tests.md)、外観と装着はクライアントで確認する。

完全に一意な名前対応も出典を証明するものではなく `unique-path-candidate` と表記する。
変換後のTRSは階層・単位・座標軸が異なるため、そのままUnityと数値一致判定しない。
RootBone相当、bind pose、頂点ウェイト、物理の挙動、実行時のEnabledを駆動するリンクも別検証。
補助階層を含む実アバターでは `needs-review` が通常であり、これはダンプ実行失敗とは別。
`matched-covered-fields` でもこれら未比較範囲が検証済みになるわけではない。

### Omanekoでの調査上の注意

FBXから読み込んだ骨格下の元メッシュスロット、Prefabから再構築したrendererスロット、
表情制御内の項目に同名が現れる。rendererを取り除いた元スロットが残る場合もあるので、
名前別の件数だけでは重複描画や余分な骨格を判定できない。骨格自体がスキン・rig・物理の
どこから参照されているかと、現在のrendererコンポーネントの有無を合わせて確認する。
実入力パス、採取結果、修正前後の件数は `.local/avatar-tests.json` とローカル結果に記録する。

Omanekoの余分な骨格には二つの原因があった。ウェイトを持たないFBX末端をPrefab由来の
新規helperとして重ねて生成する経路と、Unityで髪・耳の親を変更した後も元FBXの未使用の枝を
残す経路である。前者は完全な元pathを検証して再利用し、後者は実参照とPrefab上の存在を
保護したうえで骨格参照用templateに限って削除する。単なるSlot数削減を期待条件にしない。

眼球PivotやAligned Handが挿入された部分は名前pathだけの自動照合では未対応になる。
EyeRotationDriverのRoot、VRIKのhand参照などから補助Slotの用途を確認し、元Transformへの
対応表を作る。補助Slot自体を元の親へ多対一対応させない。元骨の一意な対応・祖先関係、
順序付きskin bone接続、Unityにある末端の欠落と重複を確認する。保存Enabledの差は
実行時driverの値と区別し、補助階層・外観・物理などの未検証項目は報告に残す。

未使用のラッパーを畳んだ出力では、元Prefabルートの対応先が残る親Slotへ変わり得る。
古い対応表を流用せず新しいパッケージに対して採取し直す。子のワールド基底、スキンの
renderer相対基底・bind pose、視点、Root追従、兄弟順を確認する。元ルートのTRS自体は
子へ合成するため数値一致を求めない。`CenteredRoot` のIK参照とcolliderは保持する。

## ツールの回帰検証

```powershell
dotnet .tmp_verify/structure-tools/PackageRegressionDump.dll --check-structure .tmp_verify/structure-check/synthetic.json
python -B -m unittest discover -s tests/ConvertedStructureComparison -v
python -B -m unittest discover -s tests/PackageRegressionComparison -v
```

自作階層で同名の曖昧性、骨配列の順序、null・未解決参照、入力不整合、対応表の期限、
既存レポートの保護を確認する。実DLLの保存形式は実変換後の採取・再読込でも検証する。
