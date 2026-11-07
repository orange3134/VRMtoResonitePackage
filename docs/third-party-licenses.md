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
| Modular Avatar for Resonite | AvatarSetup の識別グラフ、SpringBoneSetup、MeshLoadingSetup、LoadingStandInSetup、LoadingFallback.png。構成・設定の移植と素材の再利用 |
| .NET | native application host。SelfContained=false でもEXEに含む。[公式ホストパッケージ](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.8)のMIT表示を確認 |
| xxHash | UnityModelFileIdResolver.XxHash64 の round・merge・avalanche。Unity互換の符号付き4バイト末尾処理を持つ。参照実装のBSD-2-Clause通知を保持 |

`LoadingFallback.png` は modular-avatar-resonite の
`Resonite~/ResoniteHook/MA.Gadgets/resources/loading_standin.resonitepackage` 内の画像とバイト一致する。
SHA-256 は `30a0bc21f298c2e75427245468405849b6c7ea67eb9ced14ecbc5dc1ef3eabb3`。
生成アバターへのライセンスコメント追加は行わず、出典と許諾文はソース・配布物側に保持する。

`Resources/ResoPon.png`、`.ico`、`logo.svg`、`loading.svg`、`arrow.svg` は作者が自作と確認済み。
Modular Avatar の `Editor/images` はMITの対象外だが、本アプリには含めていない。

## 配布経路と検証

- アプリに追加の NuGet PackageReference はない。FrooxEngine、AssimpNet 等は
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
