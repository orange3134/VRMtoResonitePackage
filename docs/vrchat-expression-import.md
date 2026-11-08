# VRChat表情の検出と割り当て

シーンの `VRCAvatarDescriptor` が `stripped` の場合、スクリプト参照だけが保存され、
顔メッシュ・Viseme・FXなどの設定は参照先Prefabにある。選択した配置の
`UnityPrefabGraph.Identity` から上書き反映済みDescriptorを解決し、その所属シーンで
ローカル参照を読む。アバタールートも解決先Descriptorの所有GameObjectから取得する。
Yuzuki 2.1.1はこの形式で、省略データを直接読むと顔メッシュが `Body` のフォールバックに
なり、MA由来の表情クリップを検出しても顔との照合で全件除外される。

アイザラ1.1.1の `AGS_01` では、Unityが省略するモデルルートをFBXインポータが保持する。
顔は `RootNode/AGS/mesh_Face`、アニメーションのbindingは `mesh_Face` で、
旧処理は基点を `RootNode` として全候補を除外していた。Humanoidの推定成否とは別問題。
現在はDescriptorルートと顔Rendererが同じFBXを参照する場合に、Unityのgeneration 2形式の
ルートfileIDと、bindingの全パスから計算したSkinnedMeshRendererのfileIDを実参照と照合する。
一意な参照が一致した場合だけ、FBX内の実パスからアニメーションの基点を求める。
アイザラでは `//RootNode/root/mesh_Face/SkinnedMeshRenderer` の参照から `RootNode/AGS` を得る。
この基点を欠落シェイプの修復と表情bindingの照合で共有し、Descriptorの配置やリグは移動しない。
同名・曖昧な参照、異なるモデル、名前だけの一致からは補正しない。対応しないID形式では既存処理を使う。
`mesh_FaceEmo_ADD` の汗などは追加顔メッシュであり、下記の単一顔メッシュ制限も別途適用される。

VRChatの表情読み込みは `VrchatFaceEmoExpressionImporter` に一本化している。
`VrchatExpressionParser` は顔カーブとFX入力を読み、候補検出を統一Importerへ渡す。
Expression Menu、Expression Parameters、MA Parametersから表情セットを選ぶ処理もない。
ハンド割り当ては、FaceEmoでClipsを確定した後、後述のグラフ解析でその中から選択する。
MA Absolute/Appendは入力FXを順番に並べるためだけに使い、追加FXも同じFaceEmo基準で読む。
VRMの規格上の表情定義と、変換後のResonite側の入力・出力グラフは別の責務として維持する。
以下はResoPonの取り込み仕様。操作と出力は[表情システム](expression-system.md)を参照。

顔はDescriptorのViseme用メッシュ、Blendshapesのまぶた用メッシュ、ルート直下の `Body` の
順に選ぶ。追加顔メッシュ・FaceEmo設定のExcludedBlendShapesは未対応。
`BlendshapeResolver.ExpressionFaceValues` が実Rendererの同一性と曲線のパスを照合する。
DescriptorのViseme名と既知の `vrc.v_*` 名を除外し、まばたきは含める。
アバターに設定された初期ウェイトは追跡ドライバーの設定前に保存する。
メッシュ照合前の `--vrchat-dump` は仮の結果で、最初のセットの確定と後続クリップの削除は
`VrchatExpressionDetection.FilterFaceCurves` で行う。比較にはUnityのApproximately相当を使う。

## 参照元とライセンス

FaceEmo 1.7.0（commit `829f9ddf5d75aa28cbfa93293ade8c901f191ea0`）のソースを基準に、
表情取り込みの判定処理をUnity YAML解析と固定ポーズ生成へ移植している。
名称の参照だけではないため、FaceEmo由来の部分について
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md#faceemo) に著作権表示
`Copyright (c) 2023 suzuryg` とMITライセンス全文を収録する。

| FaceEmo側（Packages/jp.suzuryg.face-emo/以下） | 使用している処理とResoPon側の対応 |
|---|---|
| Editor/Detail/AV3/Importers/ExpressionImporter.cs | 通常・CAC分岐の収集、条件の除外、片手Neutralの補完、レイヤー・分岐順序、最初のパターン選択、顔カーブの初期値との差分。主にVrchatFaceEmoExpressionImporterとVrchatExpressionDetection |
| Editor/Detail/AV3/Importers/ImportUtility.cs | 顔bindingによるMotion判定、BlendTreeの最初・最後の子の扱い。VrchatExpressionParserとVrchatFaceEmoExpressionImporterの候補検出 |
| Editor/Detail/AV3/AV3Utility.cs | Viseme→まぶた→直下Bodyの顔メッシュ選択と既知viseme名の除外。VrchatAvatarParserとVrchatExpressionDetection |
| Runtime/Domain/Mode.cs | 左右64組ごとの先勝ち評価と到達しない分岐の判定。VrchatFaceEmoExpressionImporter.SelectFirstSet |

FaceEmoのUnity Editorパッケージ・UI・画像・同梱ライブラリを取り込む構成ではない。
`FaceEmoGestureConditions` のAnimatorグラフ解析やResonite用の出力は別の処理であり、
上記のソース使用範囲と、後述の変換仕様・制限を区別する。

通知ファイル全体はEXEへ埋め込み、build・publishの出力先にもコピーする。
生成アバターにはライセンス用のスロット、Comment、Licenseコンポーネントを追加しない。
出典と許諾文の管理は[外部ソース・素材のライセンス管理](third-party-licenses.md)を参照する。

## 通常形式の最初の表情セット

FaceEmoの `ImportNormal` / `GetBranches` / `GetBranch` / `DivideMode` に合わせて読む。

- 各FXレイヤーのDefault、Entry、Any State、各Stateの遷移先と子StateMachineを調べる。
  muteされた遷移、直接Stateを指さない遷移、顔Motionを持たない分岐は除く。
- 条件は左右GestureのEqualsだけを残す。片手Neutral条件には反対の手のNeutralも加える。
  ジェスチャー条件がなければbool条件だけのトグル、Contact、PhysBone由来の分岐を除く。
- レイヤー内では最初の条件の手（左→右）とジェスチャー番号で安定ソートし、
  レイヤー間では後ろのFXを優先する。元のウェイト・マスク・Write Defaultsは評価しない。
- 顔の初期値との差分がない通常分岐は除外する。各左右組合せで最初に一致する分岐を選び、
  全組合せで隠れる条件付き分岐は後続パターンに相当するため出力しない。
- 条件なしの表情はFaceEmoと同様、アニメーションの同一性で重複を除いて第1パターンの
  Clips候補に残す。条件なし分岐はハンドサインのフォールバックにはならない。

最初のパターンだけをResoPonの表情セットにする。通常形式の分岐数は必ずしも14ではない。
未一致のジェスチャーは表情解除として出力のBaseへ戻す。表情名は元クリップから取得する。

## ジェスチャー入力から検出済みClipsを選択する

候補検出と入力の割り当てを分離する。まず `VrchatFaceEmoExpressionImporter` が顔Renderer・初期値との差分・
FaceEmoの分岐順序から先頭セットのClips候補を確定する。その後 `FaceEmoGestureConditions` が左右64通りの
入力についてAnimatorの到達先を解析し、既に選ばれた候補のClip IDと照合する。解析器自身はClipを読み込んで
Clipsへ追加しない。状態名やパラメーター名、アバター名、固定の番号対応は判定に使わない。

- Parameter Driverは必須ではない。Entry／Default、Any State、通常のState遷移、子StateMachineへの進入、
  Exitと親の明示的なStateMachine遷移を辿る。比較条件、リスト順、Mute／Solo、Any Stateの自己遷移設定を読む。
  状態数、遷移段数、Driverの位置を特定の8状態構造へ固定しない。
- DriverはStateまたはStateMachineへの進入時に評価し、親から子への順序を保つ。
  Set、既知値のCopy（範囲変換なし）、既知値へのAddに対応し、複数書き込み・途中状態の連鎖も扱う。
  条件に必要なパラメーターを書き込むレイヤーを依存関係として集め、追加FXやウェイト0のレイヤーも含める。
  同じControllerを複数回参照した場合も、FX入力の各インスタンスを別に識別する。
- 各組の計算はNeutralで初期化して収束させ、そこから左右の入力を同時に変更して再び収束させる。
  各反復では元のレイヤー順に処理する。Neutralのままの状態は再進入しないため、Legniaでは非Neutralの手の
  Setが有効になり、両手が変わる場合は後ろの右手入力が優先される。任意の実行履歴や入力時刻差は保存しない。
- 初期値として与えるのは左右GestureとWeightだけ。非ハンドのトグルなどを既定値で固定して推測しない。
  ただしAND条件に既知のfalseがあれば、その遷移は未知の条件が残っていても不成立と確定できる。
- 1D BlendTreeはしきい値に一致する子、または範囲外の端の子を選べる場合だけClip IDを返す。
  補間中の姿勢や2D／Direct BlendTreeから新たな表情を生成しない。Fistは該当する手のWeightを1として扱う。

到達先を確定できたレイヤーでは、途中で通過した表情を採用しない。空MotionやClips外のClipに到達した場合は
そのレイヤーを通過して下の候補を調べる。後続FaceEmoセットやbool専用候補をグラフ解析によって追加しない。
未解決のレイヤーには元の直接Gesture条件によるFaceEmo割り当てを残し、確定済みの下位レイヤーより優先順位を
下げない。最終的にどの候補にも一致しなければ、表情解除として出力のBaseへ戻す。

未解決になる例は、未知の条件値、未宣言または所有元不明の書き込み、Random／範囲変換Copy、同期レイヤー、
未知のBehaviour、実行対象の時間待ち・割り込み、Animatorパラメーターを書き換えるAnimation、Animation Event、
未接続の親Exit、循環・非収束など。解析は256反復とEntry／Exitの256段で打ち切る。
未知のBehaviourやAnimation Eventを実際に実行することはない。

FaceEmo候補の保持と既存の直接割り当てへのフォールバックは、このグラフ解析の成否と独立している。
ログにはClips内で解決できた組数と、未解決のレイヤー・理由を残す。CACは次節の番号による割り当てを使う。

## CAC形式の最初の表情セット

FaceEmoの `GetCacLayer` を基準に、子StateMachineのEntry条件が `SYNC_EM_EMOTE` を参照する
最初のレイヤーを見つけたら通常形式の収集を置き換える。
ResoPonではレイヤー直下のEntryに番号分岐を置くCACも対象にする。
条件は `SYNC_EM_EMOTE` のEquals単独、またはそれと `AFK` のIfNotの組だけを許可する。
条件の並び順には依存せず、他のトグル、AFK中だけの表情、余分な条件を無視して採用しない。
直接State・未muteの遷移を番号順に並べ、`(番号 - 1) / 14` ごとにセットを作る。
入力の健全性確認としてEqualsと正の整数番号を要求する。顔Motionを含む最初のセットだけを使い、
1〜7を右手、8〜14を左手のFist〜ThumbsUpへ割り当てる。右手が優先で、欠番は後続セットで補わない。
CACではFaceEmo同様、顔Motionがあるが初期値との差分がない分岐も初期姿勢として有効にする。

Lina v2.00はルートEntryで番号と `!AFK` を組み合わせる形式。子StateMachineの単一条件だけを
対象にすると通常形式に誤分類され、顔クリップは残ってもGestureTableが空になる。
この拡張はAFKではないときの固定表情セットを取り出すためのもので、汎用Animator解析へ
非ハンドパラメーターの既定値を渡す変更ではない。AFK動作・表情ロック・接触ギミックは再現しない。

両形式とも、後続セットと無関係なFX・メニュー由来のクリップはClipsに混ぜない。
採用したセット・ジェスチャー分岐数・手動候補数は `FaceEmo normal:` / `FaceEmo CAC:` に残す。
元クリップとコンパイルした固定ポーズが両方Clipsに存在することはある。
対応表への割り当てをメニュー項目の選択条件にはしない。

## 固定ポーズへの変換と制限

選ばれた分岐を順序付き条件と初期姿勢のレイヤーへ正規化し、GesturePairCompilerが64通りの
固定ポーズを作る。同じ結果は再利用する。初期姿勢はコンパイルの中間データとして使い、
生成時にはL0R0が参照するClipと自動生成の基準Clipを一覧から除く。同じClip IDを参照する
他のセルもnullにする。出力bindingは保持し、実行時の解除ではOutputs/Baseへ戻る。

BlendTreeの候補判定は直下の最初と最後のAnimationClipを使い、通常ポーズは末尾の子を辿る。
FistかつMotion Time有効の分岐は先頭・末尾も調べ、離散ハンドサインには握り切った末尾を使う。
Motion Timeのパラメーター名は限定しない。連続的な握り込みやBlendTreeの補間は出力しない。

材質・物体・Transform・参照値・イベント・再生設定が混在していても有効な顔カーブを取り込むが、
顔以外の処理は実行しない。不正なbinding、非有限値、重複bindingは除外する。
重み付き接線は検証・正規化を通す。初期値と同じ顔カーブは独立候補から省くが、
割り当て済み固定ポーズのリセット用には保持する。

各トラックの最後のキー値だけを保存する。元Animatorの遷移時間・再開始・ループ・再生位相、
表情ごとの追跡制御は再現せず、既存のResoniteの瞬き・口パクと合成する。
解決できない出力や対応できない駆動を含むClipは、部分的な顔として出力せず診断を残して除外する。

## 回帰確認の観点

| 条件 | 確認する内容 |
|---|---|
| 通常形式 | 後ろのFX優先、最初のパターンだけを採用、bool専用・後続セットの候補を混入させない |
| CAC形式（Sanatia等） | 14番号単位、右優先、欠番を後続セットで補わない |
| 間接的なハンド入力（Legnia等） | ウェイト0の入力レイヤー、複数段のSet、Neutralからの収束を考慮する |
| 元FaceEmo設定とCACの違い（12B等） | CACの番号割り当てを元の手の設定と同一視しない |
| 非ゼロ初期ウェイト | 明示した0・カーブ欠落・解除のBase復帰を区別する |
| 顔以外のトラックを含むClip | 顔だけを取り込み、イベントや材質の効果を実行しない |
| パス・Rendererの不一致 | 同名の別メッシュに適用せず、解決不能として診断する |

合成データの回帰はExpressionSmokeのNormalExpressionPatternChecks、CacExpressionChecks、
FaceExpressionDetectionChecks、MixedExpressionClipChecksなどで確認する。
実アバターのパス・個別結果は `.local/avatar-tests.json` とローカルの結果へ記録し、
[ローカルアバター回帰テスト](local-avatar-tests.md)で再利用する。
過去の変換成功や旧版との一致を、現行版の検証結果として引用しない。

## 実装の参照先

- [VrchatExpressionParser.cs](../src/VrmToResonitePackage/Vrchat/VrchatExpressionParser.cs)：FX入力とカーブ。
- [VrchatFaceEmoExpressionImporter.cs](../src/VrmToResonitePackage/Vrchat/VrchatFaceEmoExpressionImporter.cs)：通常・CAC候補の抽出。
- [VrchatExpressionDetection.cs](../src/VrmToResonitePackage/Vrchat/VrchatExpressionDetection.cs)：実メッシュ照合と先頭セットの確定。
- [FaceEmoGestureConditions.cs](../src/VrmToResonitePackage/Vrchat/FaceEmoGestureConditions.cs)：検出済み候補へのハンド割り当て。
