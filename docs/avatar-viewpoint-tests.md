# Descriptor視点とHead Proxy／Targetの比較

視点はUnityの `VRC Avatar Descriptor.ViewPosition` の数値だけで判定しない。
インポート時の向き補正・センタリング・スケールを考慮し、頭部に対する視点位置を比較する。
Descriptorが親配置を持たない主FBXのルートにある場合は、FBXの単位・ローカル姿勢・Prefabの上書きから
人体4骨のUnity座標を再構成する。選択したPrefabのルートにDescriptorがあり、人体4骨がそのPrefabに
保存されている場合は、主FBXの人体骨の完全パスを同じPrefab内のTransformに照合し、各骨からルートまでの
保存姿勢を合成する。衣装やアクセサリーの追加FBXがある場合も、この判定はDescriptorの所属先で行う。
骨の座標にはルートの回転・スケールを含め、位置はDescriptor原点からの相対値にする。
Hipsを原点に頭と両上腕で座標軸を求め、HipsとHeadの距離比でスケールを合わせて、
セットアップ時のリグへ視点を写す。4骨の位置誤差が3mmを超える場合や元姿勢が不明な場合は
従来のDescriptor視点を使う。明示的な前方・上方オフセットは位置補正後に適用する。
以下の診断ではUnity Editorが採取した姿勢を独立した基準として使う。

## Unityで設定を採取する

Unity採取の前提と既存スクリプトの更新方法は [Unity参照テスト](unity-reference-tests.md) を参照。
`--viewpoints` を指定すると通常の階層解析に加えて、視点用JSONを独立に2回採取する。
入力、依存物、2回の採取結果のハッシュを確認し、比較中の改変も拒否する。

```powershell
python -B scripts/check-prefab-unity.py "D:\Unity\Project\Assets\Avatar.prefab" `
  --case-id example --viewpoints --output .tmp_verify/viewpoint-example/unity
```

`unity/<case-id>/viewpoint.json` と `viewpoint-repeat.json` に次を保存する。

- Descriptorの型、階層ID、Prefabのsource/originalSource、TransformのID。
- `viewPosition`：継承・Variant上書き解決後の `ViewPosition` 設定値。
- `descriptorWorldPosition / Rotation / Scale`：Unity Edit ModeでのDescriptor姿勢。
- `viewWorldPosition`：Descriptorのworld position + ViewPosition。TransformPointで回転・拡縮しない。
- `anchors`：同じGameObjectのHumanoid Animatorから取得したHips・Head・左右UpperArmの
  Transform ID、完全な相対path、Unity world position。

SDKコードの同梱やPrefabの再保存は行わない。AnimatorがHumanoidでない、必要な骨がない、
Descriptorがない場合も欠落を残し、後段で未検証とする。複数Descriptorを先頭の1件へ丸めない。
単独採取は `RESOPON_REFERENCE_PROJECTION=viewpoint` と既存のExporterを利用できる。
従来のv2階層JSONは変更せず、視点未採取の過去データを視点一致と扱わない。

## 変換前解析と保存後データを比較する

```powershell
dotnet build tests/UnityReferenceDump -c Release -o .tmp_verify/viewpoint-tools
dotnet build tests/PackageRegressionDump -c Release -o .tmp_verify/viewpoint-tools

dotnet .tmp_verify/viewpoint-tools/UnityReferenceDump.dll --viewpoint `
  "D:\Avatars\Avatar.unitypackage" .tmp_verify/viewpoint-example/parser.json
# 対象選択が必要なら末尾に --avatar "AvatarName" を指定する。

dotnet .tmp_verify/viewpoint-tools/PackageRegressionDump.dll --structure `
  "D:\Output\Avatar.resonitepackage" .tmp_verify/viewpoint-example/converted.json

python -B scripts/compare-avatar-viewpoint.py `
  .tmp_verify/viewpoint-example/unity/unity/example/viewpoint.json `
  .tmp_verify/viewpoint-example/parser.json .tmp_verify/viewpoint-example/converted.json `
  --report .tmp_verify/viewpoint-example/comparison.json
```

各出力は新規ファイルにする。変換前ダンプは本番パーサから設定・選択Prefab GUID・入力SHA-256を
取得し、Unityの期待値を入力にしない。保存構造はSlotのTRSとコンポーネントの実参照を採取する。
GUIの表示名や `Head Proxy` という名前だけで探さず、`VRIKAvatar._headProxy` が指すTargetと、
その親にあるAvatarPoseNodeのSlotを使う。名前が同じだけの別オブジェクトを採用しない。

同一入力を使ったことは変換実行の `report.json`、入力SHA-256、パッケージSHA-256、選択引数で
確認する。UnityプロジェクトのPrefabを参照しアーカイブを変換した場合は、アーカイブから採取した
解決済み階層も今回のUnity v2データと比較し、プロジェクトにだけ存在する変更を見落とさない。
この比較CLIだけではパッケージの変換元までは証明しない。

## 判定と限界

Hipsを原点に、Head方向と左右UpperArmから直交座標系を作り、両環境の対応する骨の距離から
一様スケールを推定する。視点自身を位置合わせに使わないため、視点のずれを吸収しない。
4本すべての骨で残差を確認し、既定3mmを超える姿勢差や非一様変形は `unverified` とする。
この方法は保存初期姿勢の比較用であり、Animator再生中・着用中の追跡状態の比較には使わない。

| 検査 | 期待値 |
|---|---|
| `parserViewPosition` | Unityと変換前解析のDescriptor設定値（許容差1µm） |
| `headProxy` | Unity視点を人体座標系で保存アバター側へ変換した位置 |
| `headTarget` | 保存BipedRigのHead骨の位置。視点位置とは異なる |

Proxyはヘッドセット位置、Targetは頭ボーンを駆動するIK接続点なので、両者に同じ値を期待しない。
`localPosition`、保存アバター空間での実位置・期待位置、XYZ差分、距離、座標変換の残差を記録する。
位置許容差は既定1mm（`--tolerance`）。`--anchor-tolerance` で骨の残差許容値を指定できる。
明示的な `--view-forward` / `--view-up` や実行時の視点上書きを使った変換は別条件として扱う。

終了コード0はこの比較範囲の一致、1は `different` または `unverified`、2はCLI・入出力の不備。
骨・Descriptor・参照の欠落や曖昧さを一致にせず理由を残す。NagiyaRuriのように設定値とTargetが
一致してもProxyだけがずれるケースを検出できる。外観・装着後の視点は別途確認する。

展開済みPrefabや追加FBXを含むVariantでは、Descriptorを所有する配置の基準姿勢を使う。
設定値とTargetが一致していても、人体骨で位置合わせした後のProxyにずれが残る場合がある。

VRMは保存したパッケージを再読込し、左右の目ボーンの中点からHead Proxyまでの変位を測る。
HipsからHeadへの方向を上とし、左上腕から右上腕への方向と上方向の外積を前方とする。
前方・上方オフセットの既定値は[VRM変換の実装資料](vrm-conversion.md#変換パイプライン)を参照する。
Targetは保存後のHeadと照合する。AvatarSample_Cでは0.x版・1.0版の両方をこの手順で確認できる。
