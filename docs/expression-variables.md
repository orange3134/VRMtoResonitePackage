# 表情システムの DynamicVariable・定数リファレンス

現行の生成実装（`ExpressionSystem/Version = 34`）に基づく。構成・操作方法は[表情システム](expression-system.md)を参照。

## 名前・型・編集区分

変数名は `空間名/項目名`。以下の項目一覧では空間名とモジュール接頭辞を省略する。
例えば Core の `LeftGesture` は `ExpressionSystem/Core.LeftGesture`、Catalog の `Id` は `ExpressionSystem.Catalog.Clip/Id`。
システム内で単一のモジュールは ExpressionSystem 空間を共有し、変数名の接頭辞をドットで区切る。
複数インスタンスを持つレコードだけに別の空間を作り、空間名もドットで階層を表す。
左右の手や各表情は、それぞれ独立した DynamicVariableSpace を持つため、同じ名前でも値は別になる。

| 配置先（Expressions からの相対位置） | 空間名 | 定義の役割 |
|---|---|---|
| Expressions 自身 | `ExpressionSystem` | バージョンと入口への参照 |
| Core | `ExpressionSystem`（変数名 `Core.*`） | 入力・選択・再生状態 |
| DV/GestureTable | `ExpressionSystem`（変数名 `GestureTable.*`） | 左右64通りの表情参照 |
| Catalog/各表情、API/Templates/各表情 | `ExpressionSystem.Catalog.Clip` | 表情の設定とBinding.*の固定値 |
| Outputs/各項目 | `ExpressionSystem.Output` | BlendShape の基礎入力・混合・最終出力 |
| Inputs/Keyboard/Left・Right | `ExpressionSystem.Input.Keyboard` | 各手の共通設定と10キーの割当 |
| Inputs/HandGestures/Modules/各機種 | `ExpressionSystem.Input.HandGestures` | しきい値と安定待ち時間 |
| 各機種/Left、Right | `ExpressionSystem.Input.HandGestures.Hand` | 片手の入力判定状態 |
| Diagnostics/Import warning | `ExpressionSystem.Diagnostics.ImportWarning` | 変換時の警告文 |
| Diagnostics/Graph modules/各項目 | `ExpressionSystem.Diagnostics.GraphModule` | ボードのパスとノード数 |

`Record()` は空間名を必須引数で受け取り、`OnlyDirectBinding=true` の空間を作る。
例外は Expressions 自身（false）で、Core と GestureTable には空間を追加せず、Expressions/DV の `ExpressionSystem/Core.*` と `ExpressionSystem/GestureTable.LnRm` を共有空間へ登録する。
DynamicVariable（値・参照・DynamicField）は、所属する空間の Slot 直下の `DV` に、1変数1子 Slot で配置する。対応表は `DV/GestureTable`、Clip の `Binding.*` は `DV/Binding` の子へまとめる。
子 Slot 名は `/` 以降の変数名。対応表の子 Slot 名は `LnRm`。例：`Expressions/DV/Core.LeftGesture`、`Expressions/DV/GestureTable/L0R0`、
`Catalog/各表情/DV/Id`、`Outputs/各項目/DV/Result`。
以下の配置先は論理的な所属を示し、変数の実体は各空間の `DV/変数名`（対応表は `DV/GestureTable/LnRm`、Clip の固定値は `DV/Binding/Binding.<Output.Id>`）に置く。
単なる整理用の Catalog・Outputs・Diagnostics には空間を追加しない。表情値はClip空間のBinding.*変数として配置する。
名前の定義は [ExpressionSpaces.cs](../src/VrmToResonitePackage/Expressions/ExpressionSpaces.cs) に集約する。
ProtoFlux の読み書きは対象の空間名と変数名を明示し、変数生成は配置先の空間名を使う。単一モジュールの接頭辞は Slot 表示名から推測せず、明示的に付ける。
固定の読み取り先がノード自身の祖先と同じ名前付き空間を指す場合は Dynamic Variable Input にする。
各手のキーボード設定・装着状態、コントローラーの状態と親機種の設定、Core 内の Selection／Playback の状態・表情参照、各 Output 内の入力値が対象。
実行時に対象が変わるレコードと GestureTable.LnRm の可変名は ReadDynamicVariable を使う。Core と固定の表セルは、各モジュールから共通の ExpressionSystem 空間の Dynamic Variable Input で読める。

Version 17 で単一モジュールを統合・空間名を階層化し、Version 18 で DV 配下の1変数1スロット配置に統一した。旧形式の空間は新規生成しない。既存パッケージは再変換・再インポートで更新する。
DynamicVariable を直接読む外部処理は新しい名前へ変更する。公開 Dynamic Impulse の Tag・引数は Version 4 と同じ。

- **設定**：動作を調整する編集用の値。固定的に使われても、実装上は変更可能な変数。
- **定義**：生成時の識別子・参照・メタデータ。編集時は関連データとの整合が必要。
- **状態**：ロジックが更新する値。診断用に読み、操作には公開 API を使う。

値型と string は `DynamicValueVariable<T>`、Slot・User・フィールドなどへの参照は `DynamicReferenceVariable<T>`。
以下の型はその T を示す。初期値は生成直後の値で、実行開始・初期化後の値とは限らない。

## Expressions 直下と対応表

| 配置先 | 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|---|
| Expressions | `Version` | int | 31 | 定義。生成システムのバージョン。実行時の分岐には使わない |
| Expressions | `Receiver` | Slot | API/Receivers | 定義。公開 Dynamic Impulse の送信先 |
| Expressions | `Catalog` | Slot | Catalog | 定義。表情一覧への参照 |
| Expressions/DV/SmoothingSpeed | `SmoothingSpeed` | float | 10 | 設定。全Rendererの表情用SmoothValue.Speedをまとめて変更。変数名は `ExpressionSystem/SmoothingSpeed` |
| Expressions/DV/GestureTable/LnRm | `L0R0`〜`L7R7` | Slot | コンパイルした表情、または null | 設定。n は左、m は右の int 値（生成時は各0〜7）。`FormatString` の `ExpressionSystem/GestureTable.L{0}R{1}` に左・右の順で渡し、変数名を直接検索する |

Version 22で左右を明示する参照名に変更し、Version 28で `.Pair` を除いた `GestureTable.LnRm` に変更した。例：左1・右2は
`GestureTable.L1R2`。選択時に左×8＋右の番号へ変換せず、左右を文字列化して直接参照する。
Core の診断値も数値の `PairIndex` から文字列の `PairKey` へ変更した。
Version 23のSelect APIはCatalogからIDを検索してCurrentExpressionを直接設定する。左右値とPairKeyは維持する。
生成アバターは旧 `Pair.0`〜`Pair.63` を参照しない。旧パッケージの変更には再変換が必要。

Version 20から `SmoothingSpeed` を共有する。各SmoothValueと同じSlotの
`DynamicValueVariableDriver<float>` がこの変数を読み、Speedを駆動する。未解決時の既定値も10。
このDriverはDynamicVariableBaseを継承するが変数の定義ではなく参照側なので、DVではなく駆動対象と同じSlotに置く。
値は補間秒数ではなく追従速度で、小さいほどゆっくり、大きいほど速い。各Speedの直接編集ではなく、
共通変数のValueを編集する。通常の表情と追跡を合成した出力の両方へ適用される。
複製・保存再読込後もそれぞれのアバター内の変数を参照し、ほかのアバターの速度には影響しない。

## Catalog/各表情

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | 元・生成表情の ID | 定義。Select API の検索キー。直接選択メニューの送信値も追従する。Pair は ID ではなく Slot を参照 |
| `DisplayName` | string | 表情名 | 設定。直接選択メニューの表示名 |
| `Enabled` | bool | true | 設定。false は表情の選択対象外。メニュー項目の Enabled は変更しない |
| `Source` | string | 元データの説明、または空文字 | 定義。由来の記録。再生判定には使わない |

Version 21ではメニュー項目の Enabled を対応表の参照有無や表情の Active・Enabled から駆動しない。
`MenuAvailable`、`GestureTable/Logic`、`Inputs/ContextMenu/Logic`、内部 `MenuRefresh` を生成しない。
直接選択と Imported menu の項目には自動の選択可否制御を付けず、表の変更を監視してメニューを走査しない。
Select APIはGestureTableへの割り当てを条件にせず、Catalogの有効な表情を選択する。
無効な表情や存在しないIDでは選択状態を変更しない。左右値は直接選択では常に保持する。

各表情の `ExpressionSystem.Catalog.Clip` 空間に、`Binding.` 接頭辞の
`DynamicValueVariable<float>` を保存する。配置先は各表情の `DV/Binding/Binding.<Output.Id>`。
独立したBinding空間、Bindingsスロット・参照、旧Output参照・Valueレコードは生成しない。

| VariableName の例 | 型 | 値 |
|---|---|---|
| `ExpressionSystem.Catalog.Clip/Binding.Body.Smile` | float | 対応するカーブの最後のキー値 |
| `ExpressionSystem.Catalog.Clip/Binding.Body.Blink_L` | float | 対応するカーブの最後のキー値 |

`Outputs/各項目/DV/Id` はBinding.接頭辞を除いたキー（例：`Body.Smile`）。
PlaybackはOutputsを1回走査し、CurrentExpression自身をSourceとして
`ExpressionSystem.Catalog.Clip/Binding.` + Output.Id を読む。FoundValue=falseならBaseを使う。
値が0であることと、変数が存在しないことを区別する。表情値の子スロットを走査する処理はない。
通常出力の値・名前・子レコードの編集後は表情を再選択する。
追跡出力は同じ名前を継続的に読み、値・追加・削除・名前変更をDynamicVariable更新後に反映する。
CurrentExpressionの変更直後にResoPon/Expression/Internal/PlaybackのDynamicImpulseをPlaybackスロットへ送る。通常出力は参照の直接編集だけでは更新されない。Playbackの値選択ではClip・Binding・Outputスロットのアクティブ状態を判定しない。

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

`API/Templates/Expression (copy into Catalog)` は最初の表情の複製で、Id を空文字、Enabled を false に変更する。
コピー後は一意の ID、Binding.*の値、必要な定義を整え、有効にして Pair へ割り当てる。表にない ID は Select API で選べない。

## Core：入力・選択・再生

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `AllowHandGestures` | bool | true | **設定**。ハンドジェスチャーとGesture APIだけの受付可否。キーボード・メニューは常に受け付ける。メニュー操作で false、初期化で true。許可 API でも変更可能 |
| `LeftGesture` / `RightGesture` | int | 0 | API が受理した各手の int 値。範囲制限なし。メニューでは変更しない |
| `PairKey` | string | L0R0 | 最後の左右入力イベントでSelectionが組み立てたキー。直接選択中の表情を示すものではない |
| `CurrentExpression` | Slot | null | Slot 有効・Enabled=trueの候補。それ以外は null |

Core の DynamicVariable に保持する表情参照は CurrentExpression だけ。Selection は対応表から読み取った参照を検証し、
切替前後の比較後に CurrentExpression へ直接渡す。
MappedExpression／CandidateExpression の診断用 DynamicVariable は生成しない。
左右入力の参照先確認にはPairKeyとGestureTableの行を使う。直接選択の確認にはCurrentExpressionを使う。

AllowHandGestures 以外は状態として扱う。SelectionStatus は生成しない。入力モードは AllowHandGestures、再生対象は CurrentExpression で確認する。未割当と無効はいずれも CurrentExpression=null となる。
PlaybackStart・PlaybackElapsed・AnimationTime は生成しない。表情の時間再生は行わない。

Lifecycle は OnStart とローカル装着状態の変更時に動き、現在の装着者がローカルユーザーの場合だけ、初期化確認 → Selection を実行する。
初期化済みかは保存されない `StoredValue<bool>` だけで管理し、PreviousOwner は保持しない。
公開 API も入力許可を判定する前に同じ初期化確認を呼ぶため、装着状態の変更イベントより早い左右入力も保持する。

初期化時は左右値を 0、PairKey を L0R0、AllowHandGestures を true、
CurrentExpressionをnullにし、通常出力のResultをBaseに書き戻す。追跡出力は専用DriveがBaseを反映する。
その後の Selection と Playback の同期Writeで現在の対応表に応じた状態になる。

装着が終了すると、初期化済みのクライアントだけが一度このクリア処理を実行し、フラグを false に戻す。
既に別のユーザーが装着している場合は共有値をクリアせず、ローカルフラグだけを戻す。
未装着で生成・複製したインスタンスや閲覧者のクライアントはクリアを書き込まない。
未装着中は Selection を実行しない。通常出力は初期化・権限取得時にホストがBaseをWriteし、追跡出力は専用DriveがBaseに追従する。
再装着・複製・読み込み後の最初の処理では再び初期化する。

## Outputs/各 BlendShape

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | 禁止文字を変換したメッシュ名.BlendShape名。衝突時は末尾番号付き | 定義。選択表情のDynamicVariableを読むキー |
| `Base` | float | 元フィールド値 | 状態／基礎入力。既存の瞬き・viseme ドライバーがあれば出力先をここへ移す。表情にトラックがない場合の値でもある |
| `TrackingWeight` | float | 0 | 設定。表情値から Base へ寄せる割合。使用時に 0〜1 に制限。0=表情値、1=Base。自動更新処理はない |
| `BlinkMode` | int | 通常0、既存の OpenCloseTarget は1または2 | 設定。0=通常の混合、1=max(追跡混合値, Base)、2=min(追跡混合値, Base)。生成時の Eye.ClosedState が OpenState より小さい場合は2。それ以外の瞬きは1 |
| `Result` | float | 元フィールド値 | 状態。DynamicField<float> が SmoothValue<float>.TargetValue を参照する。通常出力は共有PlaybackがWriteし、既存の追跡がある出力だけTrackingのDriveが駆動する |
| `OriginalDriver` | ISyncRef | 元のドライバー | 定義。既存 ActiveLink が ISyncRef の場合だけ作成。Baseへ付け替えた駆動参照の記録。生成時は変数の存在でTrackingを作り、実行時は参照がnullでない出力をPlaybackのWrite対象から除外する |

Version 34では実行処理に使わないPath・Shape・Target・BaselineのDVを生成しない。
元の基準値はCatalogのresopon:neutral表情に保持し、実際の出力先はResultからSmoothValueとDynamicBlendShapeDriverの接続を辿って確認する。

Result 以外の数値レコードは DynamicValueVariable、Result だけは外部フィールドを参照する DynamicField。
DynamicVariable としてのパスと float 型は同じなので、Read Dynamic Variable で引き続き読み取れる。
メッシュ以外の単独 IField を使う内部テスト等では、そのフィールドをWriteまたは追跡用Driveの対象とし Result から参照する。

表情なし・該当トラックなしの場合は sample の代わりに Base を使う。
選択イベント内でBinding.と各出力のIdを連結した変数名を読み、通常出力のResultをWriteする。Binding参照・HasPose・Poseコピーは生成しない。
元から追跡ドライバーがある出力だけTrackingのValueFieldDriveで継続合成する。
出力ごとのFireOnLocalChange・通知イベント、LocalUpdate・アセット検索・サンプリング・切り替え補間は生成しない。
未装着時は Result=Base とする。装着中は以下の式で評価する。通常出力のWriteは値が異なる場合だけ行う。

```text
sample  = 選択表情にBinding.<Output.Id>名のfloat変数がある ? その値 : Base
desired = lerp(sample, Base, clamp01(TrackingWeight))
Result  = BlinkMode == 1 ? max(desired, Base) : BlinkMode == 2 ? min(desired, Base) : desired
通常出力: Playback → WriteDynamicValueVariable(Result) → SmoothValue.TargetValue → SmoothValue.Value → DynamicBlendShapeDriver.BlendShapes[].Value → BlendShape
Result (DynamicField<float>) ──参照──> SmoothValue.TargetValue
```

Trackingボードのある出力で EyeLinearDriver の OpenCloseTarget を変更する場合は、該当 Output の Base の Value フィールドへ接続し、
BlinkMode を閉じる方向に合わせて1または2にする。TrackingWeight=0でも瞬きが合成される。
同じ BlendShape を DynamicBlendShapeDriver と EyeLinearDriver の両方から直接 Drive しない。
BlinkMode は生成時に閉じる方向を設定する。後から OpenState／ClosedState を反転した場合は BlinkMode も変更する。
追跡用DriveはCurrentExpressionからBinding.<Output.Id>名の値を読み、最新のBaseと合成し続ける。
通常出力のBase・TrackingWeight・BlinkMode・Binding.*変数の編集は再選択で反映する。
追跡対象ではBase・TrackingWeight・BlinkModeと選択中の表情の名前付き変数の変更が自動反映される。
Trackingのない出力に追跡を後付けする場合、Baseに接続するだけでは足りず、追跡元を設定してシステムを再生成する。
OriginalDriverは元コンポーネント自体ではなく、元のActiveLink（出力先を保持するISyncRef）への参照。生成時にそのTargetをBase.Valueへ付け替える。実行時にも通常Writeを除外する判定で読むが、編集してもTrackingグラフの作成・削除や駆動先の再接続は行わないため、追跡の切り替え設定としては使わない。

## Inputs/Keyboard/Left・Right

Version 24ではTagの既定値を `ResoPon/Expression/Keyboard/Left`・`Right` とする。Gesture APIと違い、AllowHandGestures=falseでも受け付ける。

各手の DynamicVariableSpace は `ExpressionSystem.Input.Keyboard`。
変数は `DV/Tag`、`DV/Shift`、`DV/Control`、`DV/Key.0`〜`DV/Key.9` の各 Slot に置く。

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Tag` | string | 左右の Gesture API Tag | 設定。イベントの送信先 Tag |
| `Key.0`〜`Key.9` | Renderite.Shared.Key | Keypad0〜Keypad9 | 設定。添字が送信する手の状態（0=Neutral、1=Fist、7=ThumbsUp、8・9は拡張用）。None は未割当 |
| `Shift` | bool | true | 設定。その手の全キーに共通の Shift 押下状態の一致条件 |
| `Control` | bool | 左=false、右=true | 設定。その手の全キーに共通の Ctrl 押下状態の一致条件 |

キーごとの Enabled・Gesture は持たない。Flux は各手の `Logic` にまとめる。
`KeyHeld(Key.Control)` と `KeyHeld(Key.Shift)` で左右どちらの修飾キーも扱い、Alt は判定しない。
10個の KeyHeld を `IndexOfFirstValueMatch<bool>` に渡し、最初の true の添字をペイロードにする。
`modular_avatar/AvatarWornLocal`、修飾キーの一致、FoundMatch の AND を
`FireOnLocalValueChange<bool>` で監視し、true になった時だけ送信する。OnStart も同じ条件を使う。
着用判定は既存の Avatar Root Identification が提供し、FirstPerson 設定がなくても表情生成時に用意する。

条件が成立したまま別キーを追加・解放・切り替えしても再送しない。
同時押しは最小の添字を採用する。一度条件を false に戻すと、次の成立時に送信できる。
キーを離しても Neutral は送らない。入力禁止中の押下は API が拒否し、押したまま再許可しても再送しない。
未着用では送信せず、押したまま着用した場合は条件成立時に送信する。
Held や前回マスクなどの DynamicVariable は作らず、変更検出の状態はローカルに保持する。

## Inputs/HandGestures/Modules：機種別入力

Touch・Index・Vive・WindowsMR・Cosmos の各モジュールに使用する設定だけを置く。

| 名前 | 型 | 初期値 | 役割 |
|---|---|---|---|
| `StabilitySeconds` | float | 0.05 | 全機種。候補が変わらず続く必要時間（秒） |
| `FingerThreshold` | float | 40 | Indexの人差し指〜小指の近位関節X角度。以上なら曲げた指 |
| `ThumbThreshold` | float | 25 | Indexの親指Y角度。左はこの値以下、右は符号を反転した値以下 |
| `Direction.0`〜`Direction.7` | int | 各添字の0〜7 | Vive・WindowsMRの下・左下・左・左上・上・右上・右・右下への割り当て |

Version 19でGrip/Triggerの押下・解放しきい値とGripHeld/TriggerHeldを廃止した。
Touch／CosmosはControllerの接触とClick出力、Indexは指の姿勢、Vive／MRはパッド方向を使う。
詳細は[コントローラー別ジェスチャー判定](controller-gestures.md)を参照。
各モジュールの Left / Right は独立した ExpressionSystem.Input.HandGestures.Hand スコープを持つ。
機種別入力もローカルな `StoredValue<bool>` で初期化済みかを管理し、User 参照は保持しない。
ローカルユーザーが装着者でなくなるとフラグを false に戻し、次の装着時に候補・安定値を初期化する。

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `Candidate` | int | -1 | 最新の入力判定候補。変化すると Since を更新 |
| `Since` | float | 0 | Candidate が変わった WorldTimeFloat（秒）。安定待ちの起点 |
| `Stable` | int | -1 | 安定判定後に送信したジェスチャー。変化時だけ再送するための比較値 |

-1 は未確定・未送信で、公開左右 API の有効値ではない。
判定した手形・受付状態の変化時に処理する。
Since + StabilitySeconds に時刻が達して安定判定が変化したときも処理するため、指を止めたままでも確定入力を送れる。
切断・非アクティブ時や入力禁止中は Candidate・Stable を -1 に戻し、入力イベントは送らない。
Core は左右それぞれで最後に受理した値を保持し、更新番号は保持しない。
再接続・入力再許可後に安定した手形を検出すると、その手の値を新しい入力で更新する。
モジュール自体の削除・無効化でも手の状態は残る。明示的な Neutral（0）で解除できる。
アバターの装着解除・再装着・複製に伴う Core の初期化は、機器の切断とは別に行う。

## Diagnostics

| 配置先 | 名前 | 型 | 役割 |
|---|---|---|---|
| Import warning/各レコード | `Message` | string | 変換時の警告・自動設定できなかった理由・インポート結果やキーボード割当の案内 |
| Graph modules/各レコード | `Path` | string | Expressions からのボードの相対パス |
| Graph modules/各レコード | `NodeCount` | int | 生成時のボード内 ProtoFlux ノード数 |

いずれも生成時の記録で、表情選択・Playback・Trackingは読み取らない。値を編集しても動作は変わらず、実行時に更新するカウンターではない。
Import warningはメッセージごとに同名のレコードを作り、各DV/Messageに格納する。警告以外の案内もこの名前で格納する。
Graph modulesはボードごとにレコードを作り、DV/PathとDV/NodeCountに生成時点の情報を保存する。後からFluxを編集しても自動更新しない。

## DynamicVariable 以外の定数・一時値

ExpressionFlux の `Constant<T>()` は ValueInput、`Text()` は ValueObjectInput、`Ref<T>()` は RefObjectInput を生成する。
これらはグラフ内の固定入力で、DynamicVariable の変数一覧には現れない。
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
| `LeftTag` / `RightTag` | `ResoPon/Expression/Gesture/Left` / `ResoPon/Expression/Gesture/Right` | int（範囲制限なし）。ハンドジェスチャー入力。AllowHandGestures に従う |
| `KeyboardLeftTag` / `KeyboardRightTag` | `ResoPon/Expression/Keyboard/Left` / `ResoPon/Expression/Keyboard/Right` | int（範囲制限なし）。AllowHandGesturesに関係なく左右値を更新。フラグは維持 |
| `SelectTag` | `ResoPon/Expression/Menu/Select` | string。Catalogの有効な表情IDを検索し、CurrentExpressionを直接変更してメニュー専用にする |
| `HandGesturesEnabledTag` | `ResoPon/Expression/AllowHandGestures` | bool。ハンドジェスチャーだけの許可・停止。左右値と表情は維持 |
| `ToggleHandGesturesTag` | `ResoPon/Expression/ToggleHandGestures` | 引数なし。現在のハンドジェスチャー許可を反転。左右値と表情は維持 |
| `InitializeTag` | `ResoPon/Expression/Internal/Initialize` | 引数なし。Lifecycle の初期化確認 |
| `SelectionTickTag` | `ResoPon/Expression/Internal/Selection` | 引数なし。左右入力イベントから選択更新を同期実行 |
| `PlaybackTickTag` | `ResoPon/Expression/Internal/Playback` | 引数なし。終端ポーズの取得・適用を同期実行 |

Internal の3つは公開操作用ではない。メニューボタンの送信値はコンポーネントの PressedData に保持され、DynamicVariable ではない。
選択中の一時候補、Catalog検索で見つけた表情参照は LocalObject、初期化済みフラグは StoredValue<bool> を使う。
これらも DynamicVariable の保存変数とは区別する。

現行版は `PreviousOwner`、`Override`、`LeftInput`、`RightInput` という項目を生成しない。
メニュー選択はAllowHandGestures=falseとCurrentExpressionで保持する。キーボードは停止せず、次のショートカットで表情を変更できる。

## 実装の参照先

- [ExpressionSystemSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionSystemSetup.cs)：Core、Catalog、Outputs、対応表、診断レコードの生成。
- [ExpressionFlux.cs](../src/VrmToResonitePackage/Expressions/ExpressionFlux.cs)：スコープ、変数・定数ノード、読み書き。
- [ExpressionApiSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionApiSetup.cs)：入力検証、左右値の更新、モード変更、IDからCatalogを直接選択。
- [ExpressionLifecycleSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionLifecycleSetup.cs)：装着状態による初期化・終了処理。
- [ExpressionPlaybackSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionPlaybackSetup.cs)：選択検証、終端ポーズの取得・通常出力のWrite・追跡専用Drive。
- [ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)：メニュー、キー割当、機種別入力。
- [ExpressionBindingNames.cs](../src/VrmToResonitePackage/Expressions/ExpressionBindingNames.cs)：読みやすいキーの禁止文字変換と衝突回避。
- [ExpressionModel.cs](../src/VrmToResonitePackage/Expressions/ExpressionModel.cs)：変換用の中間カーブと安定した出力 ID。
