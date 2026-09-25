# FaceEmo の既存表情検出

調査対象は FaceEmo 1.7.0、commit `829f9ddf5d75aa28cbfa93293ade8c901f191ea0`。
以下のソースパスは FaceEmo の `Packages/jp.suzuryg.face-emo/` からの相対パス。
Unity Editor 上の実装を静的に確認した記録であり、ResoPon の実装仕様とは区別する。

## 入口と入力

- `Editor/Detail/View/InspectorView.cs` の `Field_ImportButtons` が
  `ExpressionImporter.ImportExpressionPatterns(VRCAvatarDescriptor)` を呼ぶ。
- 中心実装は `Editor/Detail/AV3/Importers/ExpressionImporter.cs`。
- `ImportUtility.GetFxLayer` は Descriptor の `baseAnimationLayers` から
  FX の `AnimatorController` を取得する。見つからなければ結果は空。
  この入口は Expression Menu や MA Merge Animator を直接列挙しない。
- 出力は FaceEmo の `List<IMode>`。分岐にジェスチャー条件、アニメーションの GUID、
  目・口の Tracking Control、まばたき設定などを格納する。
  生成クリップは `Assets/Suzuryg/FaceEmo/Imported/yyyyMMdd_HHmmss/` に保存する。

## 顔メッシュと表情クリップの判定

1. `Editor/Detail/AV3/AV3Utility.cs` の `GetFaceMesh` が次の優先順で1メッシュを選ぶ。
   VisemeBlendShape モードの `VisemeSkinnedMesh`、まぶたが Blendshapes の
   `eyelidsSkinnedMesh`、アバタールート直下の名前が `Body` の SkinnedMeshRenderer。
2. `ImportUtility.GetAllFaceBlendShapeValues` がそのメッシュと
   `AV3Setting.AdditionalSkinnedMeshes` の現在のウェイトを収集する。
   キーはアバタールートからのパスとシェイプ名。`ExcludedBlendShapes` は取り除く。
3. 呼び出しは `excludeBlink: false, excludeLipSync: true`。
   Viseme 設定に列挙されたシェイプに加え、`IsLipSyncBlendShapeName` が
   `vrc.v_aa` などの既知名を含むと判定したシェイプも除外する。
4. `ImportUtility.IsFaceMotion` がクリップの float curve binding を調べる。
   顔シェイプのパス、`blendShape.{name}`、`SkinnedMeshRenderer` 型が一致する
   binding が1つでもあれば候補。クリップ名から喜怒哀楽などを分類する処理ではない。
5. 通常の `GetFaceAnimation` はクリップを複製し、対象外の float curve、空の曲線、
   全キーがメッシュの現在値とほぼ等しい曲線を削除する。
   差分のある顔曲線が1つも残らなければ取り込まない。残る曲線は時系列のまま保持する。
   同じ Motion はキャッシュして生成済みのアニメーションを再利用する。

`GetFaceAnimation` の削除対象は `GetCurveBindings` で列挙する float curve。
ObjectReference curve や AnimationEvent を明示的に削除する処理はここにはないため、
「顔以外のデータをすべて除去する」と解釈してはいけない。

## 通常の Animator の走査

`ImportNormal` → `GetBranches` → `GetBranch` の順に処理する。

- 各 FX レイヤーについて Default State、Entry 遷移、Any State 遷移、各 State の
  遷移先を走査し、子 StateMachine も再帰処理する。
- mute された遷移、State への直接の遷移先を持たない遷移、顔 Motion でないものは除く。
- 条件として取り込むのは `GestureLeft` / `GestureRight` の `Equals` のみ。
  片手だけの Neutral 条件には反対の手の Neutral 条件も加える。
- 取り込めるジェスチャー条件がない場合、全条件が bool の If / IfNot である分岐、
  Contact Receiver のパラメーターを使う分岐、PhysBone パラメーターを条件または
  Motion Time に使う分岐を除く。PhysBone は `_IsGrabbed`、`_IsPosed`、`_Angle`、
  `_Stretch`、`_Squish` を登録する。ジェスチャー条件があればこの除外処理は通らない。
- 遷移先の `VRC_AnimatorTrackingControl` から目・口の追跡とまばたきの設定を読む。
- 後ろの FX レイヤーから分岐を登録する。条件なしの候補は、既存分岐の Base／使用中の
  左右アニメーションに同じ GUID がなければ後から追加する。
- 優先する分岐に隠れて FaceEmo 側で到達不能になった条件付き分岐は、別の表情パターンへ
  分割する。これは `Runtime/Domain/Mode.cs` による左右のジェスチャー組合せの評価であり、
  元 Animator の状態遷移グラフの到達可能性検証ではない。

## BlendTree・握り込み・専用形式

- `IsFaceMotion` は BlendTree の最初と最後の子を AnimationClip にキャストして調べる。
  中間の子とネストした BlendTree はこの候補判定では辿らない。
- 通常取り込みの `GetFaceAnimation` は最後の子を再帰的に選ぶ。BlendTree の補間や
  パラメーター条件を評価しないため、候補判定を通っても最終的に取り込めない場合がある。
- Fist 条件と `timeParameterActive` がある State は先頭・末尾を Base／左右トリガー用に
  分ける。Motion Time のパラメーター名が GestureWeight かどうかまでは検証しない。
  BlendTree の場合、Base は最初の子の末尾、トリガー側は最後の子の末尾を使う。
  この `GetFirstFrame` / `GetLastFrame` 経路は通常の `GetFaceAnimation` と異なり、
  顔以外の float curve を一律には削除しない。
- 子 StateMachine の Entry 条件に `SYNC_EM_EMOTE` を持つレイヤーを見つけると
  `ImportCac` 専用経路へ切り替え、通常経路は実行しない。
  `threshold - 1` を14個単位で分割し、右7種・左7種のジェスチャーに対応させる。
  `CN_BLINK_ENABLE` / `CN_MOUTH_MORPH_CANCEL_ENABLE` の Parameter Driver も読む。
- 別の `ImportOptionalClips` は Blink と Mouth Morph Canceller を名前などの規則で探す。
  Blink はレイヤー名の単語 `blink`、ループ、顔 binding、3キー以上かつ値10以上を含む
  blendShape 曲線を条件にする。通常の表情候補判定とは別処理。

## 再利用時の判断

顔 binding による候補検出と、ハンドジェスチャーへの割り当てを分離する設計が参考になる。
ただし、非ジェスチャー条件の大半、遷移履歴、レイヤーウェイト・マスク、BlendTree の補間を
再現する処理ではない。分岐の重複除去も顔ポーズの内容比較ではなく GUID の比較である。
ResoPon に適用する場合も、候補抽出と元 Animator の挙動再現の保証を分けて考える。

## 確認した既存テスト

`Tests/Editor/Detail/AV3/Impoters/ExpressionImporterTests.cs` の期待値をソースと照合した。

- `Import_HandsLayer`: 1パターン・13分岐。追加顔メッシュ、握り込み、追跡設定などを確認。
  `blink_2frame` が条件なし候補に入るケースには除外すべきという TODO がある。
- `Import_MimyLabBasic`: 1パターン・14分岐。
- `Import_CAC`: 3パターン・14／4／4分岐。

今回の調査では Unity EditMode テストは実行していない。上記件数は実行結果ではなく、
既存テストに書かれた期待値である。

## ResoPonへの適用

検出処理の実装とFaceEmoとの違いは [表情システムの実装](expression-system.md#faceemo-に合わせた表情候補の検出) を参照。
