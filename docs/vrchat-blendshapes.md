# VRChatのブレンドシェイプと顔の追跡

FBXのチャンネル・頂点変位、Prefabの初期値、DescriptorとFXの接続を扱う。表情セットの選択と操作は[VRChat表情の検出と割り当て](vrchat-expression-import.md)、変換の呼び出し順は[VRChat変換の全体設計](vrchat-conversion.md)を参照する。

## FBXの旧形式ShapeとオブジェクトID

古いバイナリFBXでは、各シェイプが `Geometry`（Mesh）の直下に、名前付きの `Shape` として
格納される。Assimpはこの形式のメッシュ本体とスキンを読めてもシェイプを取り込まない。
解析と実変換の両方で、一時FBXに各メッシュの `BlendShape` とシェイプ順の
`BlendShapeChannel`・`Geometry`（Shape）を作って接続する。疎な頂点番号、位置・法線の差分は
元データを引き継ぎ、メッシュの同名プロパティから既定ウェイトを読む。その後で頂点溶接を保護する。

Rengyo v1.9 同梱の `rengyo_v1.3` はこの形式のFBX 7.1を使う。Unity Editorでの基準値は
`Body` が135個、素体の `sotal` が4個で、素体の初期ウェイトは順に 0、100、0、100。
保存後の名前・順序・初期値に加え、Descriptorの15音素と、FXの顔カーブが実際のRendererを駆動することを確認する。

`UnityFbxMorphSeams` のID索引は、Shape → BlendShapeChannel → BlendShape → Meshを辿るための
`Geometry` と `Deformer` に限定する。NagmaのFBXには同じIDの`Texture`が複数記録されており、
全Objectsを一意なIDで辞書化するとインポート前に失敗する。テクスチャを含む無関係なノードは
索引に入れず、書き出し時は元のレコードを保持する。回帰テストは`FbxMorphSeamChecks`で、
FBX 7400/7500、既存頂点色、圧縮配列、重複Texture IDを組み合わせ、Assimpによる頂点溶接後の
表情と元ファイルの保持を確認する。

## 同位置の頂点で異なる変形を保持する

[ModelImporterの共通制約](avatar-conversion.md#modelimporterの共通制約)に対し、
バイナリFBXでは一時的な頂点カラーでcontrol pointを区別する。

`UnityFbxMorphSeams` は、主FBX・追加FBXをインポートする前にバイナリFBXの
`Shape → BlendShapeChannel → BlendShape → Geometry` 接続と疎な頂点差分を調べる。
同位置のcontrol point間で位置または法線の差分が異なるgeometryだけに、
control point IDを持つ一時的な頂点カラー層を追加し、誤った統合を防ぐ。
既存のカラー層は保持し、空きチャンネルを使う。元FBXは変更せず、作業ディレクトリ内の
コピーだけを書き換える。元のproperty bytesを保持してnode offsetを再計算するため、
圧縮配列やモデル・ボーン・マテリアル等の定義は変更しない。

この一時カラーは統合の識別用であり、VRChat経路では主・追加FBXの両方で
`ImportVertexColors = false` を維持する。ASCII FBXはこの前処理の対象外。
`PrefabInputSmoke` は7400/7500形式、圧縮配列、既存カラー層の有無を使い、
通常の頂点統合での破損再現と、対策後の全ポリゴン頂点の変形一致を検証する。

Shipilkaの「汗」のように、特定の表情だけで頂点がずれる場合は、元FBXのポリゴン頂点の変位と
再読込したMeshXのblendshape差分を照合する。

## 変形後の法線

FBXの `ModelImporter.tangentSpace.blendShapeNormalImportMode: 1` はUnityの
Calculate指定。Assimpから取得したshapeの頂点位置が正しくても、折り畳まれた補助面の
通常時の法線がゼロや不適切な向きなら、展開後に黒い面として見える。
Shipilka 1.02のL0R5 (`Cry` motion、出力クリップ名 `RockNRoll`) は、
「照れ」で展開する `Facial` の補助面がこのケースに該当する。

`VrchatBlendShapeNormals` はshape復元後、UnityのCalculate指定があるFBXに対して
変形後の三角形から法線差分を再生成する。面積・角度の重みは `normalCalculationMode`
に従う。移動した頂点に接する面とその頂点を対象にし、通常時の位置・法線・
全shapeの位置差分は保持する。同じ通常時位置・通常時法線・変形先を持つ頂点間では
UV境界を平滑化する。Unityの全smoothing group処理を再現するものではない。

無関係な面まで再計算すると、複数shapeを同時適用したときに通常時の法線補正が
重複して加算されるため、非影響部分と空shapeの法線は保持する。
Import / None / 指定なしにはこの再計算を適用しない。共有mesh/providerは直接変更せず、
補正済みproviderを生成して同じ設定のrendererだけに割り当てる。
検証では保存後の表情を合成し、展開した面にゼロ法線や逆向きの法線がないことも確認する。

参考: [Unity ModelImporterNormals](https://docs.unity.com/en-us/engine/7000.0/script-reference/unityeditor/modelimporternormals)、
[normalCalculationMode](https://docs.unity3d.com/jp/current/ScriptReference/ModelImporter-normalCalculationMode.html)。

## 名前・初期値・表情への接続

Resoniteは空または微小なshapeを除去するため、Unityのindex参照がずれる場合がある。

- FBX内の元shape順を記録する。
- ModelImporterの `meshes.importBlendShapes: 0` では、FBX由来のshape名・既定weight・
  アニメーション候補を解析結果から除く。主FBXと追加FBXの設定を個別に保持し、Resoniteが読む
  一時FBXでBlendShapeデフォーマからメッシュへの接続を外す。バイナリ・ASCIIともに
  スキンとボーンの接続は維持され、保存メッシュのshape数とrendererのweight数は0になる。
  ASCII FBXの接続元は符号付き64ビット整数として照合する。宣言の `30` と接続の `030` は同じIDを指す。
- FBXのGUIDとrendererの元path、またはPrefabの明示的なメッシュ参照で元のチャンネル表を特定する。
  表の名前が一意で、インポート済みの全チャンネルが別名解決後にその表にある場合は、欠けた名前を
  空frameで補い、区切りや無音のshapeを含めてFBXの順に並べる。既存frameの頂点変位を保持し、
  ウェイトを名前で対応させる。メッシュを共有する別のrendererには専用の変更を波及させない。
  空frameの補完で、インポーターが捨てた微小な頂点変位までは復元できない。
- 名前だけの候補ではblink・prefab weightが参照するindexまでと、対象rendererに解決できた
  表情・口形名を補う。元の順序と矛盾する数値参照の修復は中止する。
- FBX `BlendShapeChannel.DeformPercent` はModelImporterの `importBlendShapeDeformPercent` が
  有効な場合だけモデルprefabの既定weightとして読み込む。明示的な0では取り込まず、設定省略時は従来どおり有効。
  無効でもチャンネル名・順序・aliasは保持し、Prefabの明示的なweightは通常どおり適用する。
- モデル由来のRendererではFBXのGUIDと元の階層pathで既定ウェイトを引き、未指定の項目を補う。
  明示された0や、別のインスタンスの上書きは区別する。
- 明示的なprefab `m_BlendShapeWeights` は0を含めて既定値より優先する。
  Prefab自身が定義した独立したRendererでは、空配列や配列末尾より先のチャンネルも0になる。
  Shipilkaの髪とRitoriの制服は、参照先FBX上の既定値が100でも、Prefab上でこの値を継承しない。
  SkinnedMeshRenderer の配列を拡張する上書きでは、既存の値を保持し、追加された未指定要素を
  数値の0で初期化する。MANUKA DR_05 Variant 1では、未指定要素を空ノードにすると
  Unity参照との比較が途中で失敗する。元PrefabをUnityで採取し、修正後の値と照合した。
- 初期weightはblendshape修復直後と最終scene setupの両方で適用する。
  同じFBX内に同名rendererが複数ある場合、複製元のblendshape名・順序とdescriptorの瞬きindexは
  rendererの完全なFBX pathで解決する。pathが判明している参照を名前だけの表へfallbackしない。

VRChatの15 visemeはResonite enumへ対応させ、Unityの0〜100をResoniteの0〜1へ変換する。
Lyphos 1.10 の Ex・Fa は無音と母音 aa に同じ `Body/lipsync.a` を割り当て、初期値は 0。
無音の強さが 1 のときに開口を防ぐため、VRChat の無音と母音が同じ Renderer の同じ
weight に解決された場合、発話音素だけをその weight に接続する。専用の無音シェイプは
通常どおり駆動する。保存パッケージの検証では、発話後に無音だけを 1 に戻し、母音が閉じることも確認する。
customEyeLookSettingsの左右の目はprefab配置・Transform fileIDまたはFBXの完全なpathを保持する。
通常のhumanoid骨も主FBXの配置IDとnode pathを保持し、向き補正・リグ設定で同じ参照解決を使う。
ローカルの目Transformも配置を生成し、Merge Armature後の参照表をAvatarSetupのリグ割り当てまで渡す。
同名の目があっても指定されたobjectを使用し、欠落・破棄された明示参照を名前検索で置き換えない。
視線には共通の `modular_avatar/EyeSwing`（初期値10）と値を共有する `EyeRotationDriver.MaxSwing` を使う。
詳細は[共通アバター設定](avatar-conversion.md#アバターセットアップ)を参照する。
瞬きは `eyelidsBlendshapes[0]` だけを使い、LookingUp / LookingDownはblinkとして扱わない。
Blendshape repair selects original tables by imported model identity or the authored
copy's GameObject identity. Same-named copies must not replace the primary model's
table. Descriptor blink indices resolve through the referenced renderer's source mesh,
so an accessory with a different shape order retains its own blink name.

Descriptor に有効な Blink 番号と renderer があれば、既存の Parser → ModelAdapter → AvatarSetup の経路で OpenCloseTarget まで設定する。
未割当の -1 は設定情報の欠如として扱い、名前検出で補うかは別の方針になる。

## 瞬きが未設定の場合

Descriptorの `eyelidsBlendshapes` はBlink / LookingUp / LookingDownの順。
先頭の-1は瞬き未割当であり、残りの番号を瞬きの代わりに使わない。
例えばPlum v1.0.1は `[-1, 19, 20]` なので、メッシュにvrc.Blinkが存在しても
Descriptorから瞬きは作られない。

Animatorからの補完はFX layerの初期状態を `VrchatAnimatorDefaults.Resolve` で解決し、
そこで再生されるループClipを `VrchatBlinkLoop` で調べる。Descriptorの明示設定を優先する。

- `blink`（大文字小文字不問）、`まばたき`、`瞬き`、`瞬目` を名前に含むshapeが対象。
  `eye_blink_1` も対象になるが、名前だけでは採用しない。
- キー値が0～100の範囲で、先頭・末尾が0、途中の最大値が100となるカーブを瞬きとして登録する。
  途中で閉じない固定ポーズ、半開き、非ループ、無関係な顔shapeが混在するClipは除外する。
- 同じClip内の瞬きshapeの常時0カーブは初期リセットとして保持し、EyeLinearDriverには登録しない。
  0カーブでも接線が非ゼロならリセットとは見なさない。
- 複数shapeを登録できる。左右の名前（左/右、left/right、区切り文字付きL/R）を判別でき、
  全接続が左右のどちらかに分かれ両側が揃う場合は `blinkLeft` / `blinkRight` に分ける。
  それ以外は `blink` として両目共通の入力につなぐ。
- 部分weight、Additive、対象rendererや祖先の表示切り替えを含む候補は除外する。
  後段の有効layerが初期状態で同じshapeへ書く場合も除外する。
  後段の状態を確定できない場合やBlendTreeは、到達可能motionのbinding重複を保守的に調べる。
  BlendTreeはcontroller内と外部 `.asset` の両方を辿り、`m_Childs` / `m_Children` を読む。
  入れ子の参照元を保持し、循環や欠落参照を「顔カーブなし」と扱わない。
  前段の表情layerとの重複は許可し、取り込み後は既存の表情・瞬き合成で扱う。
- 候補ループが一意の場合だけ採用する。従来の単一 `blink`・他layerとの非競合による検出も残す。

動く瞬きループを検出できない場合は、中立表情の初期ループからの補完も行う。
全blendshapeカーブが定数で、`blink` / `vrc.blink`（大文字小文字不問）または
`まばたき` / `瞬き` / `瞬目` と完全一致する両目用shapeが一意に0へ設定されている場合だけ、
そのshapeをResoniteの自動瞬きへ接続する。これは元の瞬きアニメーションの移植ではなく補完である。
片目用・ウィンク・番号付きshapeから閉眼の意味を推測しない。他の表情shapeの定数値は変更しない。
複数候補、非ゼロ値、動くカーブや対象の表示切り替えを含む場合は補完しない。
Descriptorと動く瞬きループを優先し、後段layerの初期上書きも通常のループ検出と同じように確認する。

元Clipの再生タイミングや、Animatorパラメーターによる瞬きの停止・接触ギミックは再現せず、
初期状態の接続をResoniteのEyeManager / EyeLinearDriverへ置き換える。
初期リセットはBlendshapeResolverが初期値を記録する前と、最終prefab状態復元の後に適用する。
初期値の変更は対象の瞬きshapeだけに限定し、表情選択中の固定ドライブにはしない。

Linaの回帰条件は、FXの左右の瞬き2本と、両目用shapeの初期リセットを区別すること。
`importBlendShapeDeformPercent: 0` のときはFBXの閉眼値を初期ウェイトに取り込まず、
表情を解除した後も両目用shapeが0に戻ることを確認する。

Shipilka 1.02はDescriptorの瞬き設定が空で、初期 `Default Shape / ShapeDefault` のループが
Bodyの `まばたき` を0に保つ。動く瞬きループではないため、中立表情からの補完で両目1本を登録する。
`blink.anim` は非ループで `Fist` の `GestureRightWeight` によって時間を制御する握り込み表情なので、
自動瞬きの再生元として扱わない。初期IdleのSDK `proxy_hands_idle.anim`
（GUID `14980fc5fe40191418954549174fe63e`、fileID `7400000`）は手のmuscleカーブだけを持つ。
この参照がunitypackageから欠落している場合だけ、顔カーブを持たない既知のSDK motionとして扱う。
同じGUIDの実体が同梱されていればそちらを解析し、その他の不明な欠落motionは非競合と決めつけない。
保存パッケージをExpressionSmokeで再生するときは `RESOPON_TEST_EXPECTED_BLINK_BINDINGS` を
Shipilkaでは `1`、Linaでは `2` に設定し、瞬き接続が0本でも開閉テストが通ることを防ぐ。

Alconia 1.1.1 の `Alconia.prefab` はDescriptorのまぶたが未設定で、FXの初期状態と同じ
ステートマシンの `brink` 状態が参照するモーションもパッケージに含まれない。
一方、同梱の `brink_base.anim` をUnity Editorでサンプリングすると、動くのは
`body/eyelashe_sleep` の1本で、開始時0、13/60秒で100、終了時0になる。
この場合、Resoniteの自動まばたきにこの閉眼先を使う。

補完には、初期状態と瞬きを明示する状態が同一の欠落クリップを参照し、コントローラーと同じ
ディレクトリ以下で状態名またはその `_base` に一致する `.anim` が一意であることを使う。
対象は開眼から完全閉眼し再び開く両目用のカーブ1本に限る。DescriptorのGameObjectから
口パク用Rendererまでの相対パスを解決し、カーブのパスと完全一致することを確認する。
たとえば `Face/Body` を指定した場合、直下の `Body` のカーブは使わない。パスを特定できない場合や、
FXレイヤーの `m_AvatarMask` または `m_Mask` がAvatarMaskを参照する場合は補完を見送る。
同じクリップに含まれるほかの表情のゼロ値はリセットに適用しない。既存の瞬き設定を優先し、
後段の初期モーションが閉眼先を上書きする場合も補完しない。

共通の追跡設定ではAvatarCreatorのBlendShape名ヒューリスティクスによる補完を行わないため、
瞬きがないアバターを再変換するだけで接続先が増えるとは限らない。
取り込み後の接続順は[表情システムへの接続](avatar-conversion.md#表情システムへの接続)を参照する。

## FX基底レイヤーの中立表情

PrefabのBlendShape値は、VRChatで実行されるFX基底レイヤーの初期値と異なる場合がある。
`VrchatAnimatorFaceParser` は解決済み起動stateの定数カーブを `VrchatExpressionDefaults` で読み、
取り込む表情Clipに含まれるbindingだけを `BaseExpressionInitialValues` に残す。
先頭レイヤーのweightはUnityの規則に従い、保存値が0でも1として扱う。
後続のジェスチャー・メニュー・衣装レイヤーを中立表情へ固定する処理ではない。
一般的なAnimatorの実行やレイヤー全体の合成は再現しない。

未解決の起動state/motion、パラメーター条件分岐、BlendTree、AvatarMask、同期レイヤー、
motion time parameterは対象外。Expression Parametersや保存値の違いを初期値で推測しない。
値が動くカーブ、非有限値、同一bindingの重複、等しい端点でも区間の接線が非0のカーブは除外する。
対象外のbindingはPrefab/FBXの値を保つ。BlendshapeResolverで実際の顔Rendererを一意に確認し、
Descriptorのvisemeや別Rendererの同名shapeを除いてから基準値を更新する。
最後のPrefab状態復元後にも適用し、瞬きループのopen/reset値があればそちらを優先する。

回帰例: TolassAndWedge 4.7の `Tolass4.5` はPrefabのBody `Mouth.KibaSmile` が100だが、
FXの `DefaultExp / exp_Default` は0にする。Prefab値だけを採用すると、保存された口のweightと
表情出力のBaseが1になり、解除時にも口が開く。修正後は両方0となり、表情の選択中は引き続き
口を開ける。無関係な `Adj.Face.Adjust=50`、Body2や衣装の調整値は維持する。
ただし、これは表情レイヤーの下地であり、両手Neutralの最終表情そのものではない。
Tolassの `Hands` レイヤーは `Idle L / Idle R` のどちらも `exp_Niko` を参照し、
両手Neutralでは `Mouth.Niko=100` と `Sp.Cheek=100` に上書きする。表情出力を生成するときは、コンパイル済みL0R0を
解除用のnullへ変換する前にこのポーズをデフォルトへ保存する。Nikoを名前で決め打ちしない。
検証では保存パッケージの再生に `RESOPON_TEST_INITIAL_SHAPES` を使い、元データから求めた
`Mouth.KibaSmile=0`、`Mouth.Niko=1`、`Sp.Cheek=1` と調整値0.5を、読み込み直後と全表情再生後のResetで確認する。

## Viseme・Blink推定の成立条件

- Face inference rejects motions that also animate the target renderer's `m_Enabled`
  or `m_IsActive` on its GameObject or any ancestor (including the empty root path).
  Even constant visibility curves can differ from prefab defaults; face drivers cannot
  reproduce them. Sibling/child activation and ancestor renderer enable curves do not
  control the target renderer and do not disqualify its face binding.
- Automatic blink inference requires both a fully open (0) key value and a fully closed
  (100) peak. A looping constant closed-eye pose or partially open curve must not install
  a driver that overrides the authored eye state between blinks.
- Animatorのbinding pathはdescriptor root基準で解決する。追加layerの部分weightは、
  full-weightのdriverへ誤変換しないよう推論対象から除く。
  Additive layerも最終的な絶対weightを表さないため、viseme・blinkの推論対象から除く。
  viseme推論はentry/defaultとtransitionの接続を辿り、未接続のchild state machineを含めない。
  Entry が全 Viseme 値を処理する場合、default state を無条件に到達可能としない。
  muted Entry や一部の値だけを処理する Entry では default fallback を残す。
  Nested Entry routing carries the Viseme values allowed by the incoming transition,
  intersecting each Entry condition and its default fallback. Track visited values per
  node so another route can enter the same machine with additional values. State and
  Any State transitions may run later after Viseme changes, so their value domain resets.
  Equality transitions targeting a nested machine resolve its ordered, Solo/Mute-filtered
  Entry route or default state for that same Viseme before checking the constant shape.
  A missing direct destination is not itself an unsupported motion; unresolved or cyclic
  nested routes remain unsupported.
  Solo/Mute適用後の同一transition listではViseme条件と順序を確認し、先行transitionが
  必ず成立する値について後続transitionを辿らない。exit time付きの先行transitionは遮断と見なさない。
  Viseme以外の条件を持つlayerは、toggleの初期値に関係なく保守的に推論対象から除く。
  この判定も実際に到達可能な遷移だけを対象にし、未接続stateの条件では口パクを除外しない。
  永続的なDirectVisemeDriverでは条件付きの有効化・無効化を保持できないため。
  同じ Viseme 値のまま遷移先 state から離脱できる場合も、その phoneme の推論を除外する。
  無条件・exit time 付きの離脱と silence fallback にも適用し、Mute/Solo と Viseme 条件を尊重する。
  所属 state machine と祖先の Any State 遷移も離脱判定に含める。
  Viseme motion must also keep each blendshape curve constant: changing key values or
  nonzero interpolation slopes cannot become a permanent full-weight binding. Blink
  inference still uses the peak of its animated curve.
  Before accepting a viseme or legacy single-shape blink, check its renderer path and blendshape against bindings
  in reachable states of other nonzero-weight Animator layers, including zero-valued curves and BlendTree
  motions. Reject overlapping bindings conservatively because permanent drivers cannot
  reproduce the combined layer result; unrelated bindings do not block inference.
  Retain zero-valued curves when counting viseme and blink shape requirements. Initial prefab
  and FBX weights are collected before face inference, but a neutral curve cannot be
  assumed redundant across Animator states. Reject multi-curve viseme and legacy blink motions conservatively: a single-shape driver
  cannot also clear another authored shape, even when its required value is zero.

  Clonka の FX は `VisemeParameterOnly` を使い、Entry で15音素を選んで有声音のクリップ時刻を
  `Voice` で駆動する。各クリップが同じ15項目のうち1個を開き、残り14個をゼロに戻す場合、
  ResoPon はこの対応からネイティブの `DirectVisemeDriver` を設定する。口パク停止用の状態を
  持つ場合は、全15項目を消すクリップと逆のスイッチ条件で無音へ復帰する遷移も確認する。
  変換するのは口パク有効時の割当で、VRChat のキャンセルスイッチと音量カーブは再生しない。
  クリップが衣装など別の項目もリセットする場合は、この推定の対象外となる。

  Ricorine は14個の明示的な Entry とデフォルトの無音状態を持つ。音素に応じて選ばれる
  非ループクリップの最終ポーズを使う。後段の表情レイヤーは、通常の待機状態で口形を
  上書きするかも確認する。Bel Albino は話者モードの分岐先で、顔の Renderer に対する
  各音素のクリップを読む。このモードでウェイトが上がるのは5母音だが、Descriptor は顔の
  Renderer に15音素を割り当てている。FX から接続先が確定した音素はその結果を優先する。
  残りは指定 Renderer に実在する宣言名を使い、ウェイトがゼロでも接続を保存する。

  FBX 取り込みで省略された無変形の口形は、その Renderer の元 FBX にある名前を照合して
  空のチャンネルを復元する。複数の音素が同じ口形を使う場合は、それぞれを独立した入力に
  接続し、最大値で一つのウェイトを駆動する。通常の一対一の口形は直接接続する。
