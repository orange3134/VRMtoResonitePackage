# Unityの解決結果を使う回帰テスト

Unity EditorがPrefabを実体化したときの状態を期待値として採取し、
ResoPonが同じ入力のUnity YAMLを解決した結果と照合する。Unityが必要なのは
期待値を新規採取・更新するときだけ。配布版ResoPonや通常の変換にUnityは不要で、
診断ツールも配布EXEには含めない。採取後のJSON比較だけならPython 3で実行できる。
ResoPon側のJSONを生成し直すには、通常の開発ビルドと同じWindows、.NET 10、
Resoniteのインストールが必要だが、UnityやFrooxEngineの起動は不要。

## 手順

1. 依存関係（VRChat SDK、必要なシェーダー・アセット等）を揃えた信頼できる
   Unityプロジェクトに、対象の `.unitypackage` をインポートする。
   [エクスポーター](../tools/unity/Editor/ResoPonReferenceExporter.cs)を
   プロジェクト内の `Assets/Editor/` にコピーする。
2. Unityを起動するシェルで、対象Prefab（`Assets/` からの相対パス）と出力先を指定する。
   次を実行するか、同じ環境変数を継承してEditorを起動し、Edit Modeで
   **Tools → ResoPon → Export Unity Reference** を選ぶ。出力は上書きしない。
3. 同一の保存済みPrefabと依存アセットからResoPon側のスナップショットを作り、比較する。
   入力にはそのUnityプロジェクトの `.prefab`、または同じGUIDのPrefabを含む
   `.unitypackage` を指定できる。パッケージ入力時も期待値内のGUIDで対象を特定する。

PowerShell例（パスは手元の環境に合わせて変更。Unityプロジェクトを別のEditorで開いていない状態で実行する）:

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
$project = 'C:\work\trusted-avatar-project'
$env:RESOPON_REFERENCE_PREFAB = 'Assets/Example/Avatar.prefab'
$env:RESOPON_REFERENCE_OUT = 'C:\work\private-tests\unity-expected.json'
$process = Start-Process -FilePath $unity -ArgumentList '-batchmode', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'ResoPonReferenceExporter.Run', '-logFile', '-' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Unity reference capture failed: $($process.ExitCode)" }

$env:RESONITE_PATH = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite'
$actual = 'C:\work\private-tests\resopon-actual.json'
dotnet run --project tests/UnityReferenceDump -c Release -- 'C:\work\Example.unitypackage' $env:RESOPON_REFERENCE_OUT $actual
if ($LASTEXITCODE -ne 0) { throw "ResoPon snapshot failed: $LASTEXITCODE" }
python scripts/compare-unity-reference.py $env:RESOPON_REFERENCE_OUT $actual
```

入力に `.prefab` を使う場合は、Unityプロジェクト内の保存済みファイルを指定する。
期待値の採取後にUnity上でPrefabの上書きや依存の更新をした場合は、古いパッケージ
と比較せず、期待値と入力が同じ状態になるように揃える。出力JSONはどちらも
新しいファイル名を指定し、市販・非公開アバターのパッケージやJSONはコミットしない。

## 比較する範囲

- 選択した通常Prefab自体に記録された `MeshRenderer` / `SkinnedMeshRenderer`:
  有効状態、GameObjectの有効状態（自分と階層全体）、メッシュと材質スロットの参照。
- 選択したPrefab自体の `VRCPhysBone` / `VRCPhysBoneCollider`: 有効状態、階層の
  有効状態、root参照、PhysBoneのコライダー参照。rootが未指定なら所有Transformを記録する。
  これはUnity側の参照であり、ResoPonが出力時にコライダーを親ボーンへ付け替える処理とは別。
- 選択PrefabのGUIDと、符号付き64bit fileID（文字列）を使うため、同名GameObjectを
  同一視しない。材質とコライダーの配列順も比較する。未対応の階層を参照する場合や、
  比較できるコンポーネントがゼロ件の場合は失敗する。

Prefab Variant、ネストPrefabやFBX由来の暗黙のコンポーネントとそれらへの参照、
Play Mode・Animator・VRChatで初めて適用される挙動は対象外。FBXを**メッシュ資産として
参照する**ことは対象範囲。Unityのプレビューシーンを使うが、Prefabに含まれる
`ExecuteAlways` 等のコードが動作し得るため、信頼できるプロジェクトに限る。

ResoPon側は本番と同じPrefab合成・YAML読み取りを使う。この比較はその**解析段階**の
差を検出するもので、Resonite向けに変換した後のskin、揺れものの係数、見た目や装着動作
までは保証しない。それらは既存の生成物inspectとクライアント検証を併用する。
比較ツールの終了コード・合成fixtureのテストは
[UnityReferenceComparison](../tests/UnityReferenceComparison/README.md) を参照。

## 出典

UnityからJSONを採取して照合する設計は [vrctom](https://github.com/halby24/vrctom/tree/983995e2372c30b07828b88644b971912ffb68a1/tools/unity) を参考にし、
エクスポーターと照合処理は独立実装した。詳細は
[ライセンス管理](third-party-licenses.md#unity参照データの採取ツール)を参照。
