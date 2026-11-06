# 表情システムの DynamicVariable・定数リファレンス

生成実装の変数を記載する。本文のVersion番号は過去の変更記録であり、現行版はVersion変数を生成しない。構成・操作方法は[表情システム](expression-system.md)を参照。

2026-11-06: DVのキーとスロット名を `Static.*`、`Config.*`、`State.*` に整理した。
旧名との対応は[表情システム冒頭の対応表](expression-system.md)を参照。末尾のVersion別変更記録には当時の名前を残す。

## 名前・型・編集区分

変数名は `空間名/項目名`。以下の項目一覧では空間名を省略する。
例えば状態変数の `State.LeftGesture` は `ExpressionSystem/State.LeftGesture`、Catalog の表情値 `Body.Smile` は `ExpressionSystem.Catalog.Clip/Body.Smile`。
システム内で単一のモジュールは ExpressionSystem 空間を共有する。状態変数には `State.`、設定には `Config.`、固定参照には `Static.`、対応表の行には `GestureTable.` を付ける。
複数インスタンスを持つレコードだけに別の空間を作り、空間名もドットで階層を表す。
キーボードの左右の手や各表情は、それぞれ独立した DynamicVariableSpace を持つため、同じ名前でも値は別になる。ハンドジェスチャーの各手の処理状態はFlux内に保持する。

| 配置先（Expressions からの相対位置） | 空間名 | 定義の役割 |
|---|---|---|
| Expressions 自身 | `ExpressionSystem` | システム共通の変数空間 |
| DV | `ExpressionSystem`（`State.*`・`Config.*`・`Static.*`） | 入力・選択・再生状態、設定、固定参照 |
| DV/GestureTable | `ExpressionSystem`（変数名 `GestureTable.*`） | 左右64通りの表情参照 |
| Catalog/各表情 | `ExpressionSystem.Catalog.Clip` | シェイプごとの固定値 |
| Outputs/各項目 | `ExpressionSystem.Output` | BlendShape の基礎入力・混合・最終出力 |
| Inputs/Keyboard/Left・Right | `ExpressionSystem.Input.Keyboard` | 各手の共通設定と10キーの割当 |
| Inputs/HandGestures/各機種 | `ExpressionSystem.Input.HandGestures` | しきい値と安定待ち時間 |

`Record()` は空間名を必須引数で受け取り、`OnlyDirectBinding=true` の空間を作る。
例外は Expressions 自身（false）で、API・Event・Internal・Method と GestureTable には空間を追加せず、Expressions/DV の状態変数と `ExpressionSystem/GestureTable.LnRm` を共有空間へ登録する。
DynamicVariable（値・参照・DynamicField）は、所属する空間の Slot 直下の `DV` に、1変数1子 Slot で配置する。対応表だけは `DV/GestureTable` の子へまとめ、Clip の表情値はDV直下に置く。
`DV/GestureTable` 自体には自己参照変数や独立したDynamicVariableSpaceを追加しない。
子 Slot 名は `/` 以降の変数名。対応表の子 Slot 名は `LnRm`。例：`Expressions/DV/State.LeftGesture`、`Expressions/DV/GestureTable/L0R0`、
`Catalog/各表情/DV/Body.Smile`、`Outputs/各項目/DV/Result`。
以下の配置先は論理的な所属を示し、変数の実体は各空間の `DV/変数名`（対応表は `DV/GestureTable/LnRm`、Clip の固定値は `DV/<Output.Id>`）に置く。
単なる整理用の Catalog・Outputs には空間を追加しない。表情値はClip空間へOutput.Idと同じ名前のfloat変数として配置する。
名前の定義は [ExpressionSpaces.cs](../src/VrmToResonitePackage/Expressions/ExpressionSpaces.cs) に集約する。
ProtoFlux の読み書きは対象の空間名と変数名を明示し、変数生成は配置先の空間名を使う。単一モジュールの接頭辞は Slot 表示名から推測せず、明示的に付ける。
固定の読み取り先がノード自身の祖先と同じ名前付き空間を指す場合は Dynamic Variable Input にする。
各手のキーボード設定・装着状態、コントローラーの状態と親機種の設定、Internal 内の UpdateSelection／ApplyExpression の状態・表情参照、各 Output 内の入力値が対象。
実行時に対象が変わるレコードと GestureTable.LnRm の可変名は ReadDynamicVariable を使う。UpdateSelectionの対応表読み取りはSource未接続で、自身の階層からExpressionSystem空間を検索する。状態変数と固定の表セルは、各モジュールから共通の ExpressionSystem 空間の Dynamic Variable Input で読める。

Version 17 で単一モジュールを統合・空間名を階層化し、Version 18 で DV 配下の1変数1スロット配置に統一した。旧形式の空間は新規生成しない。既存パッケージは再変換・再インポートで更新する。
DynamicVariable を直接読む外部処理は新しい名前へ変更する。公開 Dynamic Impulse の現行Tag・引数は末尾の定数一覧を参照。Version 85でTagの命名を統一した。

- **設定**：動作を調整する編集用の値。固定的に使われても、実装上は変更可能な変数。
- **定義**：生成時の識別子・参照・メタデータ。編集時は関連データとの整合が必要。
- **状態**：ロジックが更新する値。診断用に読み、操作には公開 API を使う。

値型と string は `DynamicValueVariable<T>`、Slot・User・フィールドなどへの参照は `DynamicReferenceVariable<T>`。
以下の型はその T を示す。初期値は生成直後の値で、実行開始・初期化後の値とは限らない。

## Expressions 直下と対応表

| 配置先 | 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|---|
| Expressions | `Static.API` | Slot | API | 定義。公開・内部の全DynamicImpulseの共通送信先。子のMethod・Internalからタグに一致する受信処理を呼ぶ |
| Expressions | `Static.Catalog` | Slot | Catalog | 定義。表情一覧への参照。Fluxの一覧走査もこの変数を読む |
| Expressions | `Static.*` | Slot | 対応する内部Slot | 定義。Outputs、内部Impulseの宛先、追跡出力などの共有参照。実際に使うものだけ生成 |
| Expressions/DV/Config.SmoothingSpeed | `Config.SmoothingSpeed` | float | 10 | 設定。全Rendererの表情用SmoothValue.Speedをまとめて変更。変数名は `ExpressionSystem/Config.SmoothingSpeed` |
| Expressions/DV/GestureTable/LnRm | `L0R0`〜`L7R7` | Slot | コンパイルした表情、または null | 設定。n は左、m は右の int 値（生成時は各0〜7）。`FormatString` の `ExpressionSystem/GestureTable.L{0}R{1}` に左・右の順で渡し、変数名を直接検索する |

Version 22で左右を明示する参照名に変更し、Version 28で `.Pair` を除いた `GestureTable.LnRm` に変更した。例：左1・右2は
`GestureTable.L1R2`。選択時に左×8＋右の番号へ変換せず、左右を文字列化して直接参照する。
状態の診断値も数値の `PairIndex` から文字列の `PairKey` へ変更したが、実行処理には使わないためVersion 79で削除した。
Select APIは表情Slotを受け取り、自身のCatalog直下で有効な表情かを検証し、左右のGesture値を0にしてからApplyExpressionへ渡す。State.CurrentExpressionの設定はApplyExpressionが担当する。nullによる解除も左右値をクリアする。
生成アバターは旧 `Pair.0`〜`Pair.63` を参照しない。旧パッケージの変更には再変換が必要。

Version 20から `Config.SmoothingSpeed` を共有する。各SmoothValueと同じSlotの
`DynamicValueVariableDriver<float>` がこの変数を読み、Speedを駆動する。未解決時の既定値も10。
このDriverはDynamicVariableBaseを継承するが変数の定義ではなく参照側なので、DVではなく駆動対象と同じSlotに置く。
値は補間秒数ではなく追従速度で、小さいほどゆっくり、大きいほど速い。各Speedの直接編集ではなく、
共通変数のValueを編集する。通常の表情と追跡を合成した出力の両方へ適用される。
複製・保存再読込後もそれぞれのアバター内の変数を参照し、ほかのアバターの速度には影響しない。

## Catalog/各表情

`ExpressionSystem/Version` は実行時に使われないため生成しない。旧パッケージ比較では
Versionがある場合だけ旧形式の判別に使い、ない場合は以下の現行Clip形式として扱う。

Clipにはシェイプごとのfloat変数だけを置く。Id・DisplayNameは生成せず、メニューの名前は
`ContextMenuItemSource.Label` に直接保存する。Labelは直接編集でき、Slot名の変更では自動更新しない。

Version 21ではメニュー項目の Enabled を対応表の参照有無や表情の Active・Enabled から駆動しない。
`MenuAvailable`、`GestureTable/Logic`、`Inputs/ContextMenu/Logic`、内部 `MenuRefresh` を生成しない。
直接選択と Imported menu の項目には自動の選択可否制御を付けず、表の変更を監視してメニューを走査しない。
Select APIはGestureTableへの割り当てを条件にせず、Catalogの表情をSlotのアクティブ状態に関わらず選択する。
自身のCatalog直下ではない非null Slotは受理せず、選択状態・左右値・許可を変更しない。nullは解除として受理し、左右値を0にしてから表情を解除する。

各表情の `ExpressionSystem.Catalog.Clip` 空間に、Output.Idと同名の
`DynamicValueVariable<float>` を保存する。配置先は各表情の `DV/<Output.Id>`。
Binding接頭辞・グループスロット、独立したBinding空間・参照、旧Output参照・Valueレコードは生成しない。

| VariableName の例 | 型 | 値 |
|---|---|---|
| `ExpressionSystem.Catalog.Clip/Body.Smile` | float | 対応するカーブの最後のキー値 |
| `ExpressionSystem.Catalog.Clip/Body.Blink_L` | float | 対応するカーブの最後のキー値 |

`Outputs/各項目/DV/Id` は表情値の変数名と同じキー（例：`Body.Smile`）。
ApplyExpressionはOutputsを1回走査し、State.CurrentExpression自身をSourceとして
`ExpressionSystem.Catalog.Clip/` + Output.Id を読む。FoundValue=falseならBaseを使う。
値が0であることと、変数が存在しないことを区別する。表情値の子スロットを走査する処理はない。
通常出力の値・名前・子レコードの編集後は表情を再選択する。
追跡出力は同じ名前を継続的に読み、値・追加・削除・名前変更をDynamicVariable更新後に反映する。
表情Slotを引数としてExpressionSystem/Internal.ApplyExpressionを送る。ApplyExpressionが受信SlotをState.CurrentExpressionへ設定してから通常出力へ適用する。nullは表情解除。通常出力は参照の直接編集だけでは更新されない。ApplyExpressionの値選択ではClip・Binding・Outputスロットのアクティブ状態を判定しない。

### 読めるキーと禁止文字の変換（Version 30）

生成キーは `メッシュ名.BlendShape名`。メッシュ名にはSkinnedMeshRendererのSlot名を使い、
BlendShape名は対象ウェイトから実メッシュの名前を解決する。元bindingが数値インデックスでも数値キーにはしない。
メッシュ以外のテスト用IFieldは元bindingのパス末尾とShapeを使う。

- 両方の名前の前後の空白を除去する。空名はそれぞれ `Mesh`・`Shape` にする。
- 半角スペース・`-`・`.`・`_` は保持する。日本語・英数字も保持する。
- それ以外のUnicode記号・句読点・空白文字は `_` に置換する。
  制御文字・書式文字・UTF-16サロゲートも `_` にする。
- 例：`Body/Face` と `Smile(1)` → `Body_Face.Smile_1_`。
- 全表情で使う出力をまとめ、元bindingのPath・ShapeのOrdinal順で名前を割り当てる。
  置換後の同名、同名メッシュ、区切りの曖昧さは `.2`・`.3` … の末尾番号で区別する。
  元から末尾番号を含む名前とも衝突しないよう、割り当て済みの名前を確認する。
- キーは生成時に固定する。生成後にSlotの表示名を変えてもキーは変わらない。
  Output.Idを編集する場合は各表情のVariableNameも一致させる。同一空間に重複名の変数を作らない。

2026-09-28にインストール済みFrooxEngine.dllのDynamicVariableHelperを逆コンパイルし、
IsValidName・ProcessName・ParsePathを128ケースで直接検証した。
名前は最初の `/` で空間と項目に分割され、それぞれを検証してからTrimする。
従って変数名内部の `/`・`\`・`:`・括弧・改行・タブ・全角スペースは使えない。
名前は大文字小文字を区別し、空名・空白のみは変数として登録されない。
実DLLはUTF-16のchar単位で判定するため制御文字や一部の絵文字を通すが、上記の生成規則では置換する。
公開仕様は[Resonite Wikiの命名制限](https://wiki.resonite.com/Dynamic_variables#Naming_restrictions)も参照。

表情を追加するには既存のCatalogエントリーをCatalogへ複製する。Version 83ではAPI/TemplatesとAPI/Examplesを生成しない。
コピー後はDV直下のfloat値とメニューのLabelを整える。Select APIには複製先のSlotを送る。GestureTableへの割り当ては任意。選択にはSlot参照を使い、識別子変数は持たない。

## 入力・選択・再生の状態（Expressions/DV）

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `Config.AllowHandGesture.Left` / `Config.AllowHandGesture.Right` | bool | 各true | **設定**。該当する手のハンドジェスチャーとTrySetGesture APIだけの受付可否。SetGesture・メニューはこの設定で停止しない。表情選択・解除・初期化・リセット・再着用・複製・保存読込では各設定値を維持する。VRではサブメニューの4択（両手有効・左手のみ・右手のみ・両手無効）、デスクトップでは両手トグル、または変数の直接編集で変更可能 |
| `State.ContextMenuSelected` | bool | false | ContextMenuで非nullの表情を選ぶとtrue。選択解除またはResetStateでfalse。条件判定はApplyExpressionだけで行い、trueの間は表情適用（nullによる解除を含む）を拒否する。左右Gestureの更新・対応表の評価は継続する。許可設定とは独立 |
| `State.LeftGesture` / `State.RightGesture` | int | 0 | API が受理した各手の int 値。範囲制限なし。SelectExpressionでは表情の適用前に両方0へ戻す |
| `State.CurrentExpression` | Slot | null | ApplyExpressionが受信した表情Slot。UpdateSelectionは対応表の参照をそのまま渡し、Select APIは非nullの場合に自身のCatalog直下か確認する。解除はnull |

状態の DynamicVariable に保持する表情参照は State.CurrentExpression だけ。UpdateSelection は対応表から読み取ったSlot（未割当・削除済みならnull）をそのままApplyExpressionへ送り、ApplyExpressionがState.CurrentExpressionへ設定する。
MappedExpression／CandidateExpression の診断用 DynamicVariable は生成しない。
左右入力の参照先確認にはState.LeftGesture・State.RightGestureとGestureTableの `L{左}R{右}` 行を使う。実際の選択結果はState.CurrentExpressionで確認する。

Config.AllowHandGesture.Left・Right 以外は状態として扱う。SelectionStatus は生成しない。入力モードは左右の許可設定、再生対象は State.CurrentExpression で確認する。未割当・参照先の削除は State.CurrentExpression=null となる。非アクティブな表情も選択できる。
Version 64では旧 `ExpressionSystem/AllowHandGestures` を定義しない。両手を同じ値へ設定するbool APIはVersion 75まで維持し、Version 76で廃止した。
Version 66の両手Toggle APIは、両手とも許可なら両手を停止し、それ以外は両手を許可する。
Version 67では `NAND_Bool` の判定を `ValueWrite<bool>` で `LocalValue<bool>` に一度保存し、
`OnWritten` から左右へ同じLocal値を書き込む。片手を書き換えた後の再判定を避ける。
PlaybackStart・PlaybackElapsed・AnimationTime は生成しない。表情の時間再生は行わない。

API/Event/OnAvatarWornはOnStartと着用状態の変更時に動き、現在の装着者がローカルユーザーで未初期化の場合にフラグを立て、最後にInternal/ResetStateへ引数なしDynamicImpulseを送る。初期化でUpdateSelectionは実行しない。
初期化済みかはOnAvatarWorn内の保存されない `StoredValue<bool>` で管理し、PreviousOwner は保持しない。
AvatarWornLocal=falseのときはOnAvatarWorn内の初期化済みフラグだけを解除する。OnAvatarRemovedとイベント間の通知タグは生成しない。
ResetStateには着用監視や初期化済みフラグ、AvatarWornLocal条件を置かず、Impulse受信時の状態リセットだけを行う。
公開APIは暗黙の初期化を行わず、初期化前の入力は後続のResetState初期化でクリアされる。Version 77以降はAPI内で着用判定せず、未着用でも左右値・許可を更新する。
現在は公開Reset・直接のResetStateも未着用時に受理する。Internalの選択・表情適用には従来の着用条件が残る。

初期化時は左右値を0へ戻し、Config.AllowHandGesture.Left・Rightは変更せず、
ApplyExpressionへnullを送り、選択中ならState.CurrentExpressionの解除と通常出力のBase復帰をまとめて実行する。すでにnullなら通常出力の走査は省略する。追跡出力は専用DriveがBaseを反映する。
ApplyExpressionはImpulse受信だけで実行する。OnStart・着用状態の変更監視は置かず、起動・着用開始の初期化はOnAvatarWorn → ResetStateへ集約する。
その後の UpdateSelection と ApplyExpression の同期Writeで現在の対応表に応じた状態になる。

装着が終了しても左右値・入力許可・State.CurrentExpressionは保持し、ResetStateは呼ばない。
未装着で生成・複製したインスタンスや閲覧者のクライアントも自動クリアを書き込まない。
未装着中はUpdateSelectionと通常出力のWriteを実行しない。通常出力は最後の値を保持し、追跡出力は専用DriveがBaseに追従する。
再装着・複製・読み込み後の最初の処理では再び初期化する。

## Outputs/各 BlendShape

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | 禁止文字を変換したメッシュ名.BlendShape名。衝突時は末尾番号付き | 定義。選択表情のDynamicVariableを読むキー |
| `Base` | float | 元フィールド値 | 状態／基礎入力。既存の瞬き・viseme ドライバーがあれば出力先をここへ移す。表情にトラックがない場合の値でもある |
| `TrackingWeight` | float | 0 | 設定。表情値から Base へ寄せる割合。使用時に 0〜1 に制限。0=表情値、1=Base。自動更新処理はない |
| `BlinkMode` | int | 通常0、既存の OpenCloseTarget は1または2 | 設定。0=通常の混合、1=max(追跡混合値, Base)、2=min(追跡混合値, Base)。生成時の Eye.ClosedState が OpenState より小さい場合は2。それ以外の瞬きは1 |
| `Result` | float | 元フィールド値 | 状態。DynamicField<float> が SmoothValue<float>.TargetValue を参照する。生成時に瞬きが接続された出力はDynamicBlendShapeDriver.BlendShapes[].Valueを直接参照する。通常出力は共有ApplyExpressionがWriteし、既存の追跡がある出力だけTrackingのDriveが駆動する |
| `OriginalDriver` | ISyncRef | 元のドライバー | 定義。既存 ActiveLink が ISyncRef の場合だけ作成。Baseへ付け替えた駆動参照の記録。生成時は変数の存在でTrackingを作り、実行時は参照がnullでない出力をApplyExpressionのWrite対象から除外する |

Version 34では実行処理に使わないPath・Shape・Target・BaselineのDVを生成しない。
元の基準値はCatalogのresopon:neutral表情に保持し、実際の出力先はResultからSmoothValue（瞬き出力は省略）とDynamicBlendShapeDriverの接続を辿って確認する。

Result 以外の数値レコードは DynamicValueVariable、Result だけは外部フィールドを参照する DynamicField。
DynamicVariable としてのパスと float 型は同じなので、Read Dynamic Variable で引き続き読み取れる。
メッシュ以外の単独 IField を使う内部テスト等では、そのフィールドをWriteまたは追跡用Driveの対象とし Result から参照する。

表情なし・該当トラックなしの場合は sample の代わりに Base を使う。
選択イベントで受信SlotとState.CurrentExpressionを比較し、異なる場合だけ参照を更新して各出力のIdと同名のClip変数を読み、通常出力のResultをWriteする。同一Slotとnull同士では参照更新・ForEachを省略する。Binding参照・HasPose・Poseコピーは生成しない。
元から追跡ドライバーがある出力だけTrackingのValueFieldDriveで継続合成する。
出力ごとのFireOnLocalChange・通知イベント、LocalUpdate・アセット検索・サンプリング・切り替え補間は生成しない。
未装着時は追跡出力のみ Result=Base とし、通常出力は最後の値を保持する。装着中は以下の式で評価する。通常出力のWriteはローカル装着者だけが値が異なる場合に行う。

```text
sample  = 選択表情に<Output.Id>名のfloat変数がある ? その値 : Base
desired = lerp(sample, Base, clamp01(TrackingWeight))
Result  = BlinkMode == 1 ? max(desired, Base) : BlinkMode == 2 ? min(desired, Base) : desired
通常出力: ApplyExpression → WriteDynamicValueVariable(Result) → SmoothValue.TargetValue → SmoothValue.Value → DynamicBlendShapeDriver.BlendShapes[].Value → BlendShape
Result (DynamicField<float>) ──参照──> SmoothValue.TargetValue
瞬き出力: EyeLinearDriver → Base → Trackingのmax/min合成 → DynamicBlendShapeDriver.BlendShapes[].Value → BlendShape
瞬きのResult (DynamicField<float>) ──参照──> DynamicBlendShapeDriver.BlendShapes[].Value
```

Version 78では生成時にOpenCloseTargetの接続を検出したメッシュ出力のSmoothValueを省略する。
瞬きの閉眼・開眼と、このシェイプを共有する表情の切り替えはConfig.SmoothingSpeedに影響されない。
後からBlinkModeを変更してもSmoothValueの有無は変わらないため、瞬き接続を設定して再生成する。

Trackingボードのある出力で EyeLinearDriver の OpenCloseTarget を変更する場合は、該当 Output の Base の Value フィールドへ接続し、
BlinkMode を閉じる方向に合わせて1または2にする。TrackingWeight=0でも瞬きが合成される。
同じ BlendShape を DynamicBlendShapeDriver と EyeLinearDriver の両方から直接 Drive しない。
BlinkMode は生成時に閉じる方向を設定する。後から OpenState／ClosedState を反転した場合は BlinkMode も変更する。
追跡用DriveはState.CurrentExpressionから<Output.Id>名の値を読み、最新のBaseと合成し続ける。
通常出力のBase・TrackingWeight・BlinkMode・Clipのfloat変数の編集は同一Slotの再送では反映しない。別の表情またはnullへ切り替えてから選び直すと反映する。
追跡対象ではBase・TrackingWeight・BlinkModeと選択中の表情の名前付き変数の変更が自動反映される。
Trackingのない出力に追跡を後付けする場合、Baseに接続するだけでは足りず、追跡元を設定してシステムを再生成する。
OriginalDriverは元コンポーネント自体ではなく、元のActiveLink（出力先を保持するISyncRef）への参照。生成時にそのTargetをBase.Valueへ付け替える。実行時にも通常Writeを除外する判定で読むが、編集してもTrackingグラフの作成・削除や駆動先の再接続は行わないため、追跡の切り替え設定としては使わない。

## Inputs/Keyboard/Left・Right

Version 85のTagの既定値は `ExpressionSystem/Method.SetGesture.Left`・`ExpressionSystem/Method.SetGesture.Right`。TrySetGesture APIと違い、AllowHandGestures=falseでも受け付ける。Version 72～84は `ResoPon/Expression/SetGesture/Left`・`Right`、Version 24～71は `ResoPon/Expression/Keyboard/Left`・`Right`。

各手の DynamicVariableSpace は `ExpressionSystem.Input.Keyboard`。
Version 63の変数は `DV/Tag`、`DV/Modifier`、`DV/Key.0`〜`DV/Key.9` の各 Slot に置く。

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Tag` | string | 左右の SetGesture API Tag | 設定。イベントの送信先 Tag |
| `Key.0`〜`Key.9` | Renderite.Shared.Key | Keypad0〜Keypad9 | 設定。添字が送信する手の状態（0=Neutral、1=Fist、7=ThumbsUp、8・9は拡張用）。None は未割当 |
| `Modifier` | Renderite.Shared.Key | 左=Shift、右=Control | 設定。その手の全キーに共通で押下を要求するキー。Noneは未割当 |

キーごとの Enabled・Gesture は持たない。Flux は各手の `Logic` にまとめる。
`KeyHeld(Modifier)` で指定したキーの押下を調べる。Shift・Controlは左右どちらの物理キーも扱う。
他の修飾キーは判定しないため、既定値ではCtrl+Shift+テンキーが両手を更新する。
Modifierは各手で独立して編集でき、Altなどに変更したり、両手を同じキーへ割り当てたりできる。
10個の KeyHeld を `IndexOfFirstValueMatch<bool>` に渡し、最初の true の添字をペイロードにする。
`modular_avatar/AvatarWornLocal`、Modifierの押下、FoundMatch の AND を
`FireOnLocalValueChange<bool>` で監視し、true になった時だけ送信する。Inputs配下にはOnStartを生成しない。
着用判定は既存の Avatar Root Identification が提供し、FirstPerson 設定がなくても表情生成時に用意する。

条件が成立したまま別キーを追加・解放・切り替えしても再送しない。
同時押しは最小の添字を採用する。一度条件を false に戻すと、次の成立時に送信できる。
キーを離しても Neutral は送らない。入力禁止中の押下は API が拒否し、押したまま再許可しても再送しない。
未着用では送信せず、押したまま着用した場合は条件成立時に送信する。
Held や前回マスクなどの DynamicVariable は作らず、変更検出の状態はローカルに保持する。

## Inputs/HandGestures：機種別入力

Version 80ではModulesスロットを廃止し、各機種をHandGestures直下に置く。

Touch・Index・Vive・WindowsMR・Cosmosの各モジュールに、使用する設定を置く。
Version 48では各手の状態用DynamicVariableを廃止し、Left/RightのLogic内で判定・保持する。
各手のDV・ExpressionSystem.Input.HandGestures.Hand空間・Static.Left/Rightは生成しない。

| 名前 | 型 | 初期値 | 役割 |
|---|---|---|---|
| `StabilitySeconds` | float | 0.05 | 全機種。候補が変わらず続く必要時間（秒） |
| `FingerThreshold` | float | 40 | Indexの人差し指〜小指の近位関節X角度。以上なら曲げた指 |
| `ThumbThreshold` | float | 25 | Indexの親指Y角度。左はこの値以下、右は符号を反転した値以下 |
| `FingerNeutralRange` | float | 20 | Version 59。Indexの4指の中間域幅（度）。OpenはX < FingerThreshold - 幅 |
| `ThumbNeutralRange` | float | 20 | Version 59。Indexの親指の中間域幅（度）。OpenはY > 左右符号適用済みThumbThreshold + 幅 |
| `Direction.0`〜`Direction.7` | int | 各添字の0〜7 | Vive・WindowsMRの下・左下・左・左上・上・右上・右・右下への割り当て |

Version 59の負の中間域幅は0として扱い、0なら従来の2値判定になる。Closedしきい値は変更しない。
Version 19でGrip/Triggerの押下・解放しきい値とGripHeld/TriggerHeldを廃止した。
Touch／CosmosはControllerの接触とClick出力、Indexは指の姿勢、Vive／MRはパッド方向を使う。
詳細は[コントローラー別ジェスチャー判定](controller-gestures.md)を参照。
各モジュールの Left / Right は独立したLogicを持ち、次の状態をFlux内で扱う。

| ノード・状態 | 初期化時の値 | 役割 |
|---|---|---|
| `Store<int> : Candidate`（実型 `StoredValue<int>`） | -1 | 最新の入力判定候補。候補が変わると更新し、ElapsedTimeFloatのResetを呼ぶ |
| `ElapsedTimeFloat` | 候補変更時に0秒へリセット | 現在の候補が続いた秒数。StabilitySeconds以上で安定と判定。Since変数は生成しない |
| `Store<int> : Stable (last sent)` | -1 | 最後に送信した手形。同じ手形の再送を抑えるために維持する |
| `Store<bool>` | false | ローカル装着時に状態を一度だけ初期化するフラグ。非装着時はfalseへ戻す |

Storeは別のインパルス実行にも値を保持するが、状態値はDVとして同期・保存しない。
LocalValueは実行コンテキスト内の一時値なので、候補のフレーム間保持には使わない。
ElapsedTimeFloatは実DLL上でSyncTimeを持つProxyを使用する。候補の時刻を保持する独自DVはなくなるが、ノード内部の時計は同期される。
-1は未確定・未送信で、公開左右APIの有効値ではない。
判定した手形・受付状態の変化時に処理し、経過時間がStabilitySecondsに達した際も処理するため、指を止めたまま確定できる。
候補を変えたイベントでは古い経過時間を使わず待ち直す。StabilitySecondsが0以下ならその場で送信する。
候補が一瞬変わって元の手形へ戻っても、最後の送信値と同じなら再送せず、後から行った手動選択を維持する。
機器の非アクティブ時や入力禁止中は、ローカル装着者のCandidate・Stableを-1に戻し、入力イベントは送らない。
再装着時もStoreを初期化し、現在の候補から計測をやり直す。User参照は保持しない。
Core は左右それぞれで最後に受理した値を保持し、更新番号は保持しない。
再接続・入力再許可後に安定した手形を検出すると、その手の値を新しい入力で更新する。
モジュール自体の削除・無効化でも手の状態は残る。明示的な Neutral（0）で解除できる。
アバターの装着解除・再装着・複製に伴う Core の初期化は、機器の切断とは別に行う。

## Diagnostics（変換ログのみ）

Version 35以降はアバター内にDiagnosticsスロット・診断用DynamicVariableSpace・DVを生成しない。
従来の記録は変換ログ（EXEと同じディレクトリのLogs/convert_*.log）へ出力する。

| ログの接頭辞 | 内容 |
|---|---|
| `Expression diagnostics:` | 警告・インポート結果・自動設定できなかった理由・キーボード割当の案内 |
| `Expression graph:` | Expressionsからのボードの相対パスと生成時点のProtoFluxノード数 |
| `Expression logic:` | ボード総数と最大ノード数 |

いずれも変換時点の情報で、Resonite上で編集した後の状態を監視するログではない。

## DynamicVariable 以外の定数・一時値

ExpressionFluxのConstant<T>()はValueInput、Text()はValueObjectInputを生成する。Ref<Slot>()で組み立てた仮の入力は、生成の最後に非nullならDynamicReferenceVariable<Slot>とDynamicVariableObjectInput<Slot>へ置換し、nullなら接続を外して削除する。保存するグラフにRefObjectInput<Slot>は残さない。
数値・文字列の定数はグラフ内の固定入力で、DynamicVariableの変数一覧には現れない。使用中のSlot参照はExpressions/DVのReceiver・Catalog・Static.*に保存する。同じ空間内の読み書きだけに使う参照にはDVを生成しない。
Dynamic Variable Input の名前や Receiver の Tag には GlobalValue<string> も使う。
同じセクション内の定数は共有するが、ロジックボードは独立している。

| 定数・値 | 役割 |
|---|---|
| `ExpressionSystem`、`ExpressionSystem.Catalog.Clip` など | 上記のレコード定義別の空間名。変数パスは空間名 + `/` + 項目名 |
| `ExpressionSystem/GestureTable.` | `L{左}R{右}` を付けて対応表を検索するパスの接頭辞 |
| 0〜7 | 0=Neutral、1=Fist、2=HandOpen、3=FingerPoint、4=Victory、5=RockNRoll、6=HandGun、7=ThumbsUp |
| 8 / 64 | 生成時の片手の状態数／左右の組合せ数。左右 API の値は制限しない |
| -1 | 手の未確定（機種別入力の内部状態） |
| 0 / 1（float） | 追跡混合率の端点 |
| null | 未選択 Slot、未記録 User など参照なし |

公開イベント Tag は変数名とは別の仕組みで、Receiver へ値を送る。

| C# 定数 | Tag | 引数・役割 |
|---|---|---|
| `TrySetGestureLeftTag` / `TrySetGestureRightTag` | `ExpressionSystem/Method.TrySetGesture.Left` / `ExpressionSystem/Method.TrySetGesture.Right` | int（範囲制限なし）。対応するConfig.AllowHandGesture.Left・Right がtrueの場合だけSetGestureへ転送 |
| `SetGestureLeftTag` / `SetGestureRightTag` | `ExpressionSystem/Method.SetGesture.Left` / `ExpressionSystem/Method.SetGesture.Right` | int（範囲制限なし）。メニュー保持中もジェスチャー許可に関係なく左右値を更新。フラグは維持。表情適用の可否はApplyExpressionが判定 |
| `SelectTag` | `ExpressionSystem/Method.SelectExpression` | Slot。自身のCatalog直下の表情をアクティブ状態に関わらず受け取り、左右Gestureを0にしてからApplyExpressionへ渡す。nullも同じ順序、非nullの範囲外は無視。メニュー保持中は表情適用だけが拒否される。許可設定と保持boolは変更しない |
| `MenuSelectTag` | `ExpressionSystem/Internal.SelectMenuExpression` | Slot。ContextMenu専用。ローカル着用中、nullまたは自身のCatalog直下の表情を受理。保持を一時解除し、Method.SelectExpressionを同期呼び出し、最後にState.ContextMenuSelected=(Slotが非null)を設定。左右Gestureのクリアと表情適用は共通メソッドが担当。許可設定は維持 |
| `ToggleHandGesturesTag` | `ExpressionSystem/Method.ToggleHandGestures` | 引数なし。両手とも許可なら両手を停止、それ以外は両手を許可。左右値と表情は維持 |
| `ResetTag` | `ExpressionSystem/Method.Reset` | 引数なし。ResetStateへ転送し、未着用時も左右値・表情を初期状態へ戻す。許可設定は維持する |
| `ResetStateTag` | `ExpressionSystem/Internal.ResetState` | 引数なし。公開Resetと着用開始のEventから共用する。着用判定なしでState.ContextMenuSelected=false、左右値=0に戻し、ApplyExpressionへnullを送る。許可設定は維持する。表情解除はApplyExpressionの着用条件に従う |
| `SelectionTickTag` | `ExpressionSystem/Internal.UpdateSelection` | 引数なし。メニュー保持中もローカル着用中なら対応表を評価し、ApplyExpressionへ送信 |
| `PlaybackTickTag` | `ExpressionSystem/Internal.ApplyExpression` | Slot。State.ContextMenuSelected=falseのとき、受信SlotをState.CurrentExpressionへ設定し、終端ポーズを同期適用。nullは解除。非nullはローカル装着者だけが受理し、未装着時もnullは受理する |

Internal の4つは公開操作用ではない。メニューボタンの送信値はコンポーネントの PressedData に保持され、DynamicVariable ではない。
イベント間の通知タグはOnAvatarRemovedの削除とともに廃止した。
Version 74で未使用の `ResoPon/Expression/Internal/Initialize` を廃止した。現在の自動初期化はAPI/Event/OnAvatarWornからInternal/ResetStateを呼び出して実行する。
Version 75の個別ハンドメニューは `ButtonToggle.TargetValue` から自身の `Config.AllowHandGesture.Left`・`Right` のValueを直接変更する。両手メニューは引き続きToggle APIを使う。
Version 76で未使用のAllowHandGestures両手/左右とToggleHandGestures左右の5つのAPIを廃止した。設定用のDynamicVariableは維持する。
UpdateSelectionは対応表から読み取った表情Slotを送信ノードへ直接渡し、一時候補のLocalObjectは使わない。
初期化済みフラグは StoredValue<bool> を使い、DynamicVariable の保存変数とは区別する。

現行版は `PreviousOwner`、`Override`、`LeftInput`、`RightInput` という項目を生成しない。
メニュー選択は左右のGesture値を0にした後、State.ContextMenuSelected=trueとState.CurrentExpressionで保持する。許可設定は変更しない。保持中も左右Gestureの更新と対応表の評価は継続し、ApplyExpressionだけが表情適用を拒否する。選択解除またはResetStateは左右Gestureを0に戻し、次の要求から表情適用を再開する。拒否した適用要求は解除後に再生しない。追跡出力の瞬き等の合成は継続する。

## 実装の参照先

- [ExpressionSystemSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionSystemSetup.cs)：Internal、Catalog、Outputs、対応表の生成と診断ログ出力。
- [ExpressionFlux.cs](../src/VrmToResonitePackage/Expressions/ExpressionFlux.cs)：スコープ、変数・定数ノード、読み書き。
- [ExpressionApiSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionApiSetup.cs)：入力検証、左右値の更新、モード変更、受信した表情Slotの検証。
- [ExpressionLifecycleSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionLifecycleSetup.cs)：装着状態による初期化・終了処理。
- [ExpressionPlaybackSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionPlaybackSetup.cs)：選択検証、終端ポーズの取得・通常出力のWrite・追跡専用Drive。
- [ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)：メニュー、キー割当、機種別入力。
- [ExpressionBindingNames.cs](../src/VrmToResonitePackage/Expressions/ExpressionBindingNames.cs)：読みやすいキーの禁止文字変換と衝突回避。
- [ExpressionModel.cs](../src/VrmToResonitePackage/Expressions/ExpressionModel.cs)：変換用の中間カーブと安定した出力 ID。

### 固定Slot参照の動的入力（Version 36）

実行対象のResonite DLLでの型名はDynamicVariableObjectInput<Slot>で、DynamicReferenceInput<Slot>という型は存在しない。
固定の参照先はDynamicReferenceVariable<Slot>へ移し、各ボードの入力はその変数名を読む。共有参照はExpressionSystem空間を使う。
Static.API・Static.Catalogは明示した変数を再利用する。それ以外はStatic.OutputsなどをExpressions/DV配下に生成する。Static.APIはExpressions/APIを指し、公開・内部の送信で共有する。Static.InternalとそのDVスロット・入力ノードは生成しない。Version 84でSource未接続の対応表検索へ変更し、参照元がなくなったGestureTable変数を削除した。
null定数は入力未接続で表現し、Static.Noneも参照入力ノードも生成しない。同じ非null参照先は1つのDVを共有し、参照名の禁止文字は置換して衝突時は連番を付ける。
祖先空間を直接読むDynamicVariable入力は維持し、使われなくなった仮の参照にDVは作らない。
保存・複製時は通常のSlot参照としてリマップされる。参照を編集すると、DynamicVariableの更新後に参照先の走査・書き込み・Impulse送信へ反映される。

Version 38の生成時点では、手入力モジュール配下を指す参照は各モジュール内からのみ使用している。
全消費先を確認して内部参照をモジュール空間へ移し、外部からも使われる参照がある場合は共有空間へ残す。
参照変数の命名・重複回避も空間ごとに行う。モジュールを削除すると、その内部参照変数も一緒に削除される。

### 書き込み先の空間解決（Version 39）

WriteDynamicValueVariable / WriteDynamicObjectVariableはTarget未接続ならノードのSlotから名前付き空間を解決する。
生成時に固定Targetとノードの同名祖先空間が同一の場合だけ接続を省く。導入当時はCoreの状態と各手のCandidate・Since・Stableが対象だった。Version 48では各手の状態DVを廃止し、StoreとElapsedTimeFloatを使う。
Playbackが走査中のOutput.Resultへ書き込む場合は別空間なので、取得したOutput SlotをTargetに接続する。
不要になった仮のSlot入力は参照変数生成前に除去するため、Static.Coreと各モジュールのStatic.Left/Rightは生成しない。

### null参照の入力未接続化（Version 40）

Static.Noneとそれを読むDynamicVariableObjectInput<Slot>は生成しない。
表情の選択解除、未装着時のState.CurrentExpressionの分岐、LifecycleからPlaybackへ送るnull引数は、該当するオブジェクト入力を未接続にして表現する。
State.CurrentExpressionなど、実行中に値が変わる参照変数は初期値がnullでも維持する。

## Version 41：状態名と固定参照、Internal の階層

状態変数の `Core.` 接頭辞を削除した。`LeftGesture`、`RightGesture`、
`AllowHandGestures.Left`・`Right`、`CurrentExpression` は ExpressionSystem 空間に直接登録する。
`CurrentExpression` と `GestureTable.LnRm` は References 接頭辞の対象外。
固定参照は `References.Receiver`、`References.Catalog`、`References.Outputs`、
`References.Internal.Lifecycle`、`References.Internal.Selection`、`References.Internal.Playback` などに統一する。
`Core/Logic` は `Internal` に置き換え、Lifecycle・Selection・Playback をその直下へ配置する。
変数の配置先は引き続き Expressions/DV。既存パッケージへの反映には再変換・再インポートが必要。

## Version 42：内部Impulseの送信先を共有

Lifecycle・Selection・Playbackへの固定参照はすべてDynamicImpulseTriggerのTargetHierarchy専用だったため、
`ExpressionSystem/References.Internal` の1変数に統合した。参照先は `Expressions/Internal`。
導入時はInitialize・Reset・Selection・Playbackのタグで子階層の受信処理を選んでいた。Version 74で未使用のInitializeを廃止した。
`References.Internal.Lifecycle`・`References.Internal.Selection`・`References.Internal.Playback` は生成しない。

## Version 43：公開API参照の名前変更

公開Impulseの送信先変数を `References.Receiver` から `References.API` に変更した。
参照先は引き続き Expressions/API/Receivers。外部から直接参照する場合は変数名を更新する。

## Version 44：ClipのEnabled・Sourceを削除

`ExpressionSystem.Catalog.Clip/Enabled` と `ExpressionSystem.Catalog.Clip/Source` は生成しない。
Enabledの読み取りとSlotのアクティブ状態による選択判定も削除した。
GestureTableから取得したSlotはそのままPlaybackへ送る。Select APIはnull（表情解除）または自身のCatalog直下のSlotを受理する。ContextMenuで選択済みの表情を再度押すと、ButtonDynamicImpulseTriggerWithReference<Slot>はnullを送る。選択・解除とも左右のジェスチャー許可をfalseにし、LeftGesture=0、RightGesture=0を書いてからPlaybackへ送る。途中でGestureTableは再評価しない。
表情SlotやCatalogが非アクティブでも選択・適用される。装着状態によるAPI受付制御は維持する。
Sourceは生成物の説明用メタデータで実行処理には使われていなかったため、Catalog・Templatesの両方から省く。

## Version 45：ClipメタデータとBinding階層の整理

Clip/Id・Clip/DisplayNameを削除し、メニュー生成時にContextMenuItemSource.Labelへ表情名を直接設定する。
ClipのDVにはOutput.Idと同名のfloat変数だけを並べる。Binding.接頭辞とDV/Bindingグループは生成しない。
例：ExpressionSystem.Catalog.Clip/Body.SmileをCatalog/<表情>/DV/Body.Smileへ置く。
Playbackと追跡Driveは同じ短い名前を読む。テンプレート複製後の表示名もLabelを直接編集する。
既存パッケージへの反映には再変換・再インポートが必要。外部処理で変数を直接読む場合も名前を更新する。

## Version 46：InputsのOnStartを削除

Keyboardの左右とHandGesturesの各機種・各手からOnStartを削除した。
キーボードは受付条件の変化、ハンドジェスチャーは手形・安定待ち・装着状態の変化で処理する。
LifecycleとPlaybackのOnStartは引き続き初期化・出力適用に使用する。

## Version 47：手形ごとに入力コードを整理

Touch・Index・Cosmosの複数コードを手形ごとのIndexOfFirstValueMatch<byte>へまとめ、
単一コードはValueEquals<byte>で比較する。結果をboolのIndexOfFirstValueMatchに1〜7の順で渡し、Index+1を選択番号にする。
未対応のCosmos/RockNRollはfalse行、どの手形にも一致しない場合はNeutral（0）。詳細は[コントローラー別ジェスチャー判定](controller-gestures.md)を参照。
