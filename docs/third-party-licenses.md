# 外部ソース・素材のライセンス管理

配布の著作権表示と許諾文は [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) に集約する。
ソースの移植、画像、生成物へ再構成したグラフも確認する。
通知のコミットは照合した参照元の版であり、最初に参照した版を断定するものではない。

## 使用範囲

| 参照元 | 対象と確認範囲 |
|---|---|
| Avatar Expression Editor | コントローラー判定。著作権表示と許諾文をソース・配布物に保持。[入力資料](controller-gestures.md)参照 |
| FaceEmo | VRChat表情の検出と選択処理の移植。[取り込み資料](vrchat-expression-import.md)参照 |
| Resonite.UnitySDK | MaterialTuner、LilToonConverter、VrchatMaterialBuilder、ShaderPropertyFallbackConverter、ベイク計画。MToon・lilToon変換を照合 |
| lilToon | VrchatMainTextureBaker とレイヤー処理。lil_common_functions.hlsl 等の色補正・グラデーション・アルファマスク・合成 |
| UniVRM / MToon | VrmParser の MToon 0.x → 1.0 変換。MToon10Migrator のシェーディング範囲・中心変換 |
| Modular Avatar | VrchatSkinRetargeter の bind pose 補正。数式の互換実装と大規模なコード移植は区別し、参照元 MeshRetargeter のMIT通知を保持 |
| Modular Avatar for Resonite | AvatarSetup の識別グラフ、SpringBoneSetup、MeshLoadingSetup、LoadingStandInSetup、LoadingFallback.png、UnityNativeMeshのMeshX変換規約。構成・設定の移植と素材の再利用 |
| AssetsTools.NET | UnitySerializedAssetsのバイナリSerializedFile・type tree読み取り。NuGet 3.0.5を同梱しMIT通知と内包LZ4のBSD-2-Clause通知を保持 |
| .NET | native application host。SelfContained=false でもEXEに含む。[公式ホストパッケージ](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.8)のMIT表示を確認 |
| xxHash | UnityModelFileIdResolver.XxHash64 の round・merge・avalanche。4バイト末尾は符号なしで扱い、UTF-8名での符号拡張を防ぐ。参照実装のBSD-2-Clause通知を保持 |

`LoadingFallback.png` は modular-avatar-resonite の
`Resonite~/ResoniteHook/MA.Gadgets/resources/loading_standin.resonitepackage` 内の画像とバイト一致する。
SHA-256 は `30a0bc21f298c2e75427245468405849b6c7ea67eb9ced14ecbc5dc1ef3eabb3`。
生成アバターへのライセンスコメント追加は行わず、出典と許諾文はソース・配布物側に保持する。

`Resources/ResoPon.png`、`.ico`、`logo.svg`、`loading.svg`、`arrow.svg` は作者が自作と確認済み。
Modular Avatar の `Editor/images` はMITの対象外だが、本アプリには含めていない。

## Unity参照データの採取ツール

[vrctomのUnity用エクスポーター](https://github.com/halby24/vrctom/tree/983995e2372c30b07828b88644b971912ffb68a1/tools/unity)
（commit `983995e2372c30b07828b88644b971912ffb68a1`、[MIT](https://github.com/halby24/vrctom/blob/983995e2372c30b07828b88644b971912ffb68a1/LICENSE)）は
Unityの解決値を比較用JSONに採取する設計の参照元。ResoPon側の採取・比較は独立実装で、
vrctomのコード、依存物、アバター素材、取得済みの期待値は移植・同梱していない。
開発用エクスポーターはUnity Editor APIを利用し、VRChat SDKはローカル検証用プロジェクト側で利用する。
いずれもResoPonの配布EXEには同梱しない。検証手順は[Unity参照データとの比較](unity-reference-tests.md)。

実アバターを照合する際の素材・採取JSON・利用条件の確認記録は `.local/` または
`.tmp_verify/` に保持する。検証専用にPython公式のembeddable packageを展開する場合も、
同梱の `LICENSE.txt`（PSF Licenseと内包コードの通知）を残し、版と入手元をローカルに記録する。
これらを製品・リポジトリへ取り込まない検証では、配布用の第三者通知の追加対象にはしない。
規約PDFのローカル確認に使うpypdf等の補助ツールも同様に、取得版・出典・ハッシュと
同梱ライセンスをローカルに保持する。再検査スクリプトはPython標準ライブラリだけを使い、
規約確認用ライブラリを製品依存やテストの必須依存へ追加しない。

解決済みv2の採取はUnity 2022.3の
[InstantiatePrefab](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PrefabUtility.InstantiatePrefab.html) と
[GetCorrespondingObjectFromOriginalSource](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PrefabUtility.GetCorrespondingObjectFromOriginalSource.html)
等の公開API仕様を参照した独立実装。ドキュメントのサンプルコードやUnity実装を転載していない。
Variantのスモークテスト用メッシュ・材質・Prefabもテスト内で自作し、第三者のアバターは同梱しない。
ResoPon側のv2ダンプ・再検証runnerも独立実装で、本リポジトリのPrefab graphと既存の
FBXメタデータ解析APIを呼ぶ。外部の実装コードや採取したアバターデータをソースへ取り込まない。
既存参照のAssimpNet等は引き続き利用者のResoniteからロードし、新しい配布依存は追加しない。
FBX診断用の階層・skin接続情報は既存の本番解析から取得する独立実装で、fileIDの修正も含め
新しい外部依存・コピーした外部コードはない。自作FBX fixtureの書き出しにも既存参照のAssimpを使う。
採取・比較ツールは開発専用で配布EXEへ含めない。追加の第三者通知対象はない。
Prefab入力から採取・比較を起動する `check-prefab-unity.py` も、本リポジトリの既存採取ツールと
Python標準ライブラリによる独立実装。Unityプロジェクトへ一時配置するのは本リポジトリの
採取コードだけであり、新たな外部コード・配布依存・通知対象は追加しない。
登録一覧の一括検証もPython標準ライブラリだけで実装する。生成Meshの比較は既存の
`ReadAssetDocument` を呼び出し、回帰用のバイナリtype treeは既存テストと同様に
AssetsTools.NETで自作する。新たな外部コード・モデルデータ・配布依存は追加しない。
ModelImporterのDeformPercent設定、Prefabのルート命名、座標軸・行列分解、暗黙の物理rootの修正は、
インストール済みUnityでの実測と既存の自作fixtureに基づく独立実装。外部ソースのコピーはない。
依存ファイルのハッシュ採取、Editor実行ファイルの版照合、BlendShapeインポート設定の解析も
既存のUnity Editor API、OSのバージョン情報、リポジトリ内のFBXパーサを使用し、配布依存や第三者通知の追加はない。
実アバターのローカル採取では、素材ごとの版・作者・利用条件と導入範囲を `.local/` に記録する。

共有Meshのv2回帰検査は自作の最小FBX・Prefabを既存のAssimpとパーサで読み取る。
一括変換の相対パス回帰検査もPython標準ライブラリと自作データのみを使う。
外部コード・素材の取り込み、新しい配布依存・第三者通知の追加はない。

保存パッケージの `PackageRegressionDump` とPython比較器も独立実装で、既存の
RecordPackage/DataTree APIを呼び出す。FrooxEngine 2026.11.16.1330のCollider調査は
インストール済みDLLの型・フィールドの反射確認のみで、逆コンパイル実装のコピーはない。
開発用ツール・実アバター由来の期待条件・採取データは製品EXEへ同梱しない。
新しい外部コード・配布依存・通知対象は追加しない。

EkuのRenderTexture判定とNagmaのFBX骨階層採取の修正、保存スキンの追従テストは、既存の
本リポジトリ実装とUnity 2022.3.22f1・インストール済みFrooxEngineのAPIを用いた独立実装。
Unity側の骨行列・bind pose・ウェイト採取にも外部サンプルのコピーはない。
素材ごとの既存の利用条件確認を引き継いでローカル検証だけに利用し、新たな配布物や通知対象は増えない。

骨格参照用FBXの余分なrendererを除く修正は既存Prefab解析・FrooxEngine APIによる独立実装。
回帰シーンはテスト内で自作し、Omanekoの利用条件確認済みデータはローカル検証だけに使う。
外部コード・素材・配布依存の追加はなく、既存の第三者通知を保持する。

全アバター変換のUnity比較工程は、既存の参照採取・比較ツールをPython標準ライブラリで
接続した独立実装。回帰テストの入力は自作で、実データは既存の利用条件確認済みローカル参照のみ。
新たな外部コード・素材・依存の取り込みや配布範囲の変更はなく、通知対象は増えない。

同梱規約の探索はunitypackage直下だけでなく、その商品のダウンロードディレクトリ内の
Readme・Licenseフォルダーと `.url` も対象にする。リンク先は作者の公式販売ページと照合する。
PDFの文字抽出結果が空でも規約がないとは判断せず、表示して確認する。ダウンロードが無効で
公開プレビューが提供されている資料は、プレビューを読む。商品ページが指定する規約とPDFの
表題等に不整合がある場合は、確認できた根拠・利用範囲・残る不整合を記録し、解消済みと扱わない。
これらの資料・確認記録もローカルに保持し、製品へ同梱しない。

## 配布経路と検証

- AssetsTools.NETをNuGet PackageReferenceとして同梱する。単一ファイルpublishではEXEに含まれる。
  FrooxEngine、AssimpNet 等は
  `Private=false` の参照で、利用者のResoniteから読み込み、配布に含めない。
- .NET / WPF の共有ランタイムは別途インストールする。native host の通知は残す。
- `THIRD-PARTY-NOTICES.md` は出力フォルダーへのコピーに加え、EXE内のリソースに埋め込む。
  ReleaseにEXEだけを添付する場合も通知は含まれる。
- 埋め込みリソース名は `ResoPon.ThirdPartyNotices`。通知全文を保持し、生成グラフには転記しない。
- Release publish の同梱ファイル・EXE内通知とMarkdownリンクを確認する。
  生成アバターの動作・複製・保存・inspect・再読込は
  [ExpressionSmoke](../tests/ExpressionSmoke/README.md) で確認する。

## 調査の境界

API、シェーダーのプロパティ名、Unityの列挙値、VRMの座標規約など、
互換性のための名称や仕様だけの利用を、ライブラリの同梱とは扱わない。
Resonite.UnityShaders のシェーダーソースやテクスチャ、NDMF のライブラリは配布物に含めていない。
入力アバターの権利は各作者の条件に従う。

この一覧はソース・素材・配布ファイルの調査結果であり、利用許諾全般の確定判断ではない。
別途確認する箇所は次のとおり。

- [AvatarSetup](../src/VrmToResonitePackage/AvatarSetup.cs) の `SetupAwayIndicator` は、
  ローカルの逆コンパイル済み `AvatarCreator.SetupAwayIndicator` と処理手順・接続が一致する。
  `SetupVoiceOutput` にも元処理を明示するコメントがある。
  Resonite本体由来のコードの利用条件は、UnitySDKのMITライセンスとは分けて確認する。
- [VrchatExpressionDetection](../src/VrmToResonitePackage/Vrchat/VrchatExpressionDetection.cs) の
  `Approximately` は Unity Mathf.Approximately と同じ比較式を使う。
  [Unity Reference-Only License](https://unity.com/legal/licenses/unity-reference-only-license) は
  一般的なOSSの改変・再配布許諾ではない。一行の数式を用いた互換実装がソースの転載・改変に当たるかは、
  この調査だけでは断定しない。

これらにOSSライセンスを推定して付けたり、通知追加だけで利用条件を満たしたと扱ったりしない。
