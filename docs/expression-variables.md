# 表情システムの DynamicVariable・定数リファレンス

現行の生成実装（`ExpressionSystem/Version = 19`）に基づく。構成・操作方法は[表情システム](expression-system.md)を参照。

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
| GestureTable | `ExpressionSystem`（変数名 `GestureTable.Pair.*`） | 左右64通りの表情参照 |
| Catalog/各表情、API/Templates/各表情 | `ExpressionSystem.Catalog.Clip` | 表情の設定と固定値一覧への参照 |
| 各表情/Bindings/各項目 | `ExpressionSystem.Catalog.Clip.Binding` | 出力レコードへの参照と固定値 |
| Outputs/各項目 | `ExpressionSystem.Output` | BlendShape の基礎入力・混合・最終出力 |
| Inputs/Keyboard/Left・Right | `ExpressionSystem.Input.Keyboard` | 各手の共通設定と8キーの割当 |
| Inputs/HandGestures/Modules/各機種 | `ExpressionSystem.Input.HandGestures` | しきい値と安定待ち時間 |
| 各機種/Left、Right | `ExpressionSystem.Input.HandGestures.Hand` | 片手の入力判定状態 |
| Diagnostics/Import warning | `ExpressionSystem.Diagnostics.ImportWarning` | 変換時の警告文 |
| Diagnostics/Graph modules/各項目 | `ExpressionSystem.Diagnostics.GraphModule` | ボードのパスとノード数 |

`Record()` は空間名を必須引数で受け取り、`OnlyDirectBinding=true` の空間を作る。
例外は Expressions 自身（false）で、Core と GestureTable には空間を追加せず、Expressions/DV の `ExpressionSystem/Core.*` と `ExpressionSystem/GestureTable.Pair.N` を共有空間へ登録する。
すべての DynamicVariable（値・参照・DynamicField）は、所属する空間の Slot 直下の `DV` に、1変数1子 Slot で配置する。
子 Slot 名は `/` 以降の変数名。例：`Expressions/DV/Core.LeftGesture`、`Expressions/DV/GestureTable.Pair.0`、
`Catalog/各表情/DV/Id`、`Outputs/各項目/DV/Result`。
以下の配置先は論理的な所属を示し、変数の実体は各空間の `DV/変数名` に置く。
単なる整理用の Catalog・Outputs・Bindings・Diagnostics には空間を追加しない。
名前の定義は [ExpressionSpaces.cs](../src/VrmToResonitePackage/Expressions/ExpressionSpaces.cs) に集約する。
ProtoFlux の読み書きは対象の空間名と変数名を明示し、変数生成は配置先の空間名を使う。単一モジュールの接頭辞は Slot 表示名から推測せず、明示的に付ける。
固定の読み取り先がノード自身の祖先と同じ名前付き空間を指す場合は Dynamic Variable Input にする。
各手のキーボード設定・装着状態、コントローラーの状態と親機種の設定、Core 内の Selection／Playback の状態・表情参照、各 Output 内の入力値が対象。
実行時に対象が変わるレコードと Pair.N の可変名は ReadDynamicVariable を使う。Core と固定の表セルは、各モジュールから共通の ExpressionSystem 空間の Dynamic Variable Input で読める。

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
| Expressions | `Version` | int | 19 | 定義。生成システムのバージョン。実行時の分岐には使わない |
| Expressions | `Receiver` | Slot | API/Receivers | 定義。公開 Dynamic Impulse の送信先 |
| Expressions | `Catalog` | Slot | Catalog | 定義。表情一覧への参照 |
| Expressions/DV/GestureTable.Pair.N | `Pair.0`〜`Pair.63` | Slot | コンパイルした表情、または null | 設定。番号は `左 × 8 + 右`。子の並び順ではなく変数名で検索する |

## Catalog/各表情

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | 元・生成表情の ID | 定義。Select API の検索キー。直接選択メニューの送信値も追従する。Pair は ID ではなく Slot を参照 |
| `DisplayName` | string | 表情名 | 設定。直接選択メニューの表示名 |
| `Enabled` | bool | true | 設定。false は選択・メニュー利用対象外 |
| `Source` | string | 元データの説明、または空文字 | 定義。由来の記録。再生判定には使わない |
| `Bindings` | Slot | 直下の Bindings | 定義。固定ポーズの値一覧への参照。null・削除・無効化時は選択無効 |
| `MenuAvailable` | bool | false | 状態。メニュー生成時のみ作成。装着開始・対応表や有効状態などの変更時に再計算。Slot が有効・Enabled=true・対応表に参照ありなら true。ロード完了は判定しない |

Bindings の子には ExpressionSystem.Catalog.Clip.Binding 空間で次の2項目を保存する。

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Output` | Slot | 対応するOutputsレコード | 定義。値の適用先 |
| `Value` | float | コンパイル済みカーブの最後のキー値 | 設定。表情の固定値。元カーブや接線は保存しない |

選択時は全出力のHasPoseをfalseにしてから、有効なBindingsの子を列挙し、各OutputのPoseへValueを書き込んでHasPose=trueにする。
値や子レコードの編集後は表情を再選択する。Bindings参照自体の変更は自動反映する。

`API/Templates/Expression (copy into Catalog)` は最初の表情の複製で、Id を空文字、Enabled を false に変更する。
コピー後は一意の ID、Bindings、必要な定義を整え、有効にして Pair へ割り当てる。表にない ID は Select API で選べない。

## Core：入力・選択・再生

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `AllowExternalInput` | bool | true | **設定**。通常のジェスチャー・キーボード・外部左右 API の受付可否。メニュー操作で false、初期化で true。許可 API でも変更可能 |
| `LeftGesture` / `RightGesture` | int | 0 | API が受理した各手の 0〜7。メニューも同じ値を更新 |
| `PairIndex` | int | 0 | Selection が計算した LeftGesture × 8 + RightGesture（0〜63） |
| `CurrentExpression` | Slot | null | Slot 有効・Enabled=true・Bindings参照先が有効な候補。それ以外は null |

Core の DynamicVariable に保持する表情参照は CurrentExpression だけ。Selection は対応表から読み取った参照を検証し、
切替前後の比較後に CurrentExpression へ直接渡す。
MappedExpression／CandidateExpression の診断用 DynamicVariable は生成しない。
対応表の参照先の確認には PairIndex と GestureTable の行を使う。

AllowExternalInput 以外は状態として扱う。SelectionStatus は生成しない。入力モードは AllowExternalInput、再生対象は CurrentExpression で確認する。未割当と無効はいずれも CurrentExpression=null となる。
PlaybackStart・PlaybackElapsed・AnimationTime は生成しない。表情の時間再生は行わない。

Lifecycle は OnStart とローカル装着状態の変更時に動き、現在の装着者がローカルユーザーの場合だけ、初期化確認 → Selection とメニュー表示更新を実行する。
初期化済みかは保存されない `StoredValue<bool>` だけで管理し、PreviousOwner は保持しない。
公開 API も入力許可を判定する前に同じ初期化確認を呼ぶため、装着状態の変更イベントより早い左右入力も保持する。

初期化時は左右値・PairIndex を 0、AllowExternalInput を true、
CurrentExpression を null、全OutputsのHasPoseをfalseにし、通常出力のResultをBaseに書き戻す。追跡出力は専用DriveがBaseを反映する。
その後の Selection と Playback の同期Writeで現在の対応表に応じた状態になる。

装着が終了すると、初期化済みのクライアントだけが一度このクリア処理を実行し、フラグを false に戻す。
既に別のユーザーが装着している場合は共有値をクリアせず、ローカルフラグだけを戻す。
未装着で生成・複製したインスタンスや閲覧者のクライアントはクリアを書き込まない。
未装着中は Selection を実行しない。通常出力は初期化・権限取得時にホストがBaseをWriteし、追跡出力は専用DriveがBaseに追従する。
再装着・複製・読み込み後の最初の処理では再び初期化する。

## Outputs/各 BlendShape

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | binding キーの SHA-256 の先頭24桁（小文字16進数） | 定義。出力の安定した識別子。実行時はOutput参照を使う |
| `Path` | string | 元 binding のパス | 定義。出力の由来・識別情報 |
| `Shape` | string | BlendShape 名 | 定義。出力の由来・識別情報 |
| `Baseline` | float | initialWeight があればその値、なければ元フィールド値 | 定義。生成時の基準値の記録。Playback は読まない。編集しても生成済み Neutral の固定値 は変わらない |
| `Base` | float | 元フィールド値 | 状態／基礎入力。既存の瞬き・viseme ドライバーがあれば出力先をここへ移す。表情にトラックがない場合の値でもある |
| `Pose` | float | 0 | 状態。選択時に取得したトラック終端値。時間で変化しない |
| `HasPose` | bool | false | 状態。選択した表情に該当トラックがある場合はtrue。falseなら現在のBaseを使用 |
| `TrackingWeight` | float | 0 | 設定。表情値から Base へ寄せる割合。使用時に 0〜1 に制限。0=表情値、1=Base。自動更新処理はない |
| `BlinkMode` | int | 通常0、既存の OpenCloseTarget は1または2 | 設定。0=通常の混合、1=max(追跡混合値, Base)、2=min(追跡混合値, Base)。生成時の Eye.ClosedState が OpenState より小さい場合は2。それ以外の瞬きは1 |
| `Result` | float | 元フィールド値 | 状態。DynamicField<float> が DynamicBlendShapeDriver の該当 BlendShapes[].Value を参照する。通常出力は共有PlaybackがWriteし、既存の追跡がある出力だけTrackingのDriveが駆動する |
| `Target` | IField&lt;float&gt; | 元の BlendShape フィールド | 定義。出力先の記録。DynamicBlendShapeDriver の Renderer・シェイプ名は生成時に別途設定するため、この参照だけ変更しても送信先は変わらない |
| `OriginalDriver` | ISyncRef | 元のドライバー | 定義。既存 ActiveLink が ISyncRef の場合だけ作成。Base へ付け替えたドライバーの記録 |

Result 以外の数値レコードは DynamicValueVariable、Result だけは外部フィールドを参照する DynamicField。
DynamicVariable としてのパスと float 型は同じなので、Read Dynamic Variable で引き続き読み取れる。
メッシュ以外の単独 IField を使う内部テスト等では、そのフィールドをWriteまたは追跡用Driveの対象とし Result から参照する。

表情なし・該当トラックなしの場合は sample の代わりに Base を使う。
選択イベントで全出力のPose・HasPoseを同期更新し、通常出力にはResultをWriteする。
元から追跡ドライバーがある出力だけTrackingのValueFieldDriveで継続合成する。
出力ごとのFireOnLocalChange・通知イベント、LocalUpdate・アセット検索・サンプリング・切り替え補間は生成しない。
未装着時は Result=Base とする。装着中は以下の式で評価する。通常出力のWriteは値が異なる場合だけ行う。

```text
sample  = HasPose ? Pose : Base
desired = lerp(sample, Base, clamp01(TrackingWeight))
Result  = BlinkMode == 1 ? max(desired, Base) : BlinkMode == 2 ? min(desired, Base) : desired
通常出力: Playback → WriteDynamicValueVariable(Result) → DynamicBlendShapeDriver.BlendShapes[].Value → BlendShape
Result (DynamicField<float>) ──参照──> 同じ BlendShapes[].Value
```

Trackingボードのある出力で EyeLinearDriver の OpenCloseTarget を変更する場合は、該当 Output の Base の Value フィールドへ接続し、
BlinkMode を閉じる方向に合わせて1または2にする。TrackingWeight=0でも瞬きが合成される。
同じ BlendShape を DynamicBlendShapeDriver と EyeLinearDriver の両方から直接 Drive しない。
BlinkMode は生成時に閉じる方向を設定する。後から OpenState／ClosedState を反転した場合は BlinkMode も変更する。
表情は選択イベントで固定値を保持し、瞬きは追跡用Driveが合成し続ける。
通常出力のBase・TrackingWeight・BlinkMode・Bindings/Value編集は再選択で反映する。
追跡対象ではBase・TrackingWeight・BlinkModeの変更が自動反映される。
Trackingのない出力に追跡を後付けする場合、Baseに接続するだけでは足りず、追跡元を設定してシステムを再生成する。
OriginalDriverは生成時の経路の記録であり、編集して追跡の有効・無効を切り替える設定ではない。

## Inputs/Keyboard/Left・Right

各手の DynamicVariableSpace は `ExpressionSystem.Input.Keyboard`。
変数は `DV/Tag`、`DV/Shift`、`DV/Control`、`DV/Key.0`〜`DV/Key.7` の各 Slot に置く。

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Tag` | string | 左右の Gesture API Tag | 設定。イベントの送信先 Tag |
| `Key.0`〜`Key.7` | Renderite.Shared.Key | Keypad0〜Keypad7 | 設定。添字が送信する手の状態（0=Neutral、1=Fist、7=ThumbsUp）。None は未割当 |
| `Shift` | bool | true | 設定。その手の全キーに共通の Shift 押下状態の一致条件 |
| `Control` | bool | 左=false、右=true | 設定。その手の全キーに共通の Ctrl 押下状態の一致条件 |

キーごとの Enabled・Gesture は持たない。Flux は各手の `Logic` にまとめる。
`KeyHeld(Key.Control)` と `KeyHeld(Key.Shift)` で左右どちらの修飾キーも扱い、Alt は判定しない。
8個の KeyHeld を `IndexOfFirstValueMatch<bool>` に渡し、最初の true の添字をペイロードにする。
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
| Import warning/各レコード | `Message` | string | 変換時の警告・自動設定できなかった理由 |
| Graph modules/各レコード | `Path` | string | Expressions からのボードの相対パス |
| Graph modules/各レコード | `NodeCount` | int | 生成時のボード内 ProtoFlux ノード数 |

いずれも定義・記録であり、実行時に更新するカウンターではない。

## DynamicVariable 以外の定数・一時値

ExpressionFlux の `Constant<T>()` は ValueInput、`Text()` は ValueObjectInput、`Ref<T>()` は RefObjectInput を生成する。
これらはグラフ内の固定入力で、DynamicVariable の変数一覧には現れない。
Dynamic Variable Input の名前や Receiver の Tag には GlobalValue<string> も使う。
同じセクション内の定数は共有するが、ロジックボードは独立している。

| 定数・値 | 役割 |
|---|---|
| `ExpressionSystem`、`ExpressionSystem.Catalog.Clip` など | 上記のレコード定義別の空間名。変数パスは空間名 + `/` + 項目名 |
| `ExpressionSystem/GestureTable.Pair.` | 番号を付けて対応表を検索するパスの接頭辞 |
| 0〜7 | 0=Neutral、1=Fist、2=HandOpen、3=FingerPoint、4=Victory、5=RockNRoll、6=HandGun、7=ThumbsUp |
| 8 / 64 | 片手の状態数／左右の組合せ数。Pair の計算・逆引き・API 範囲検査に使用 |
| -1 | 手の未確定、Select の一致する Pair が未発見 |
| 0 / 1（float） | 追跡混合率の端点 |
| null | 未選択 Slot、未記録 User など参照なし |

公開イベント Tag は変数名とは別の仕組みで、Receiver へ値を送る。

| C# 定数 | Tag | 引数・役割 |
|---|---|---|
| `LeftTag` / `RightTag` | `ResoPon/Expression/Gesture/Left` / `ResoPon/Expression/Gesture/Right` | int 0〜7。通常の左右入力。AllowExternalInput に従う |
| `MenuLeftTag` / `MenuRightTag` | `ResoPon/Expression/Menu/Left` / `ResoPon/Expression/Menu/Right` | int 0〜7。メニュー専用にして片手を変更 |
| `SelectTag` | `ResoPon/Expression/Menu/Select` | string。表にある有効な表情 ID を最小 Pair 番号へ逆引きし、両手を変更してメニュー専用にする |
| `InputEnabledTag` | `ResoPon/Expression/AllowExternalInput` | bool。通常入力の許可・停止。左右値は維持 |
| `InitializeTag` | `ResoPon/Expression/Internal/Initialize` | 引数なし。Lifecycle の初期化確認 |
| `SelectionTickTag` | `ResoPon/Expression/Internal/Selection` | 引数なし。選択更新を同期実行 |
| `PlaybackTickTag` | `ResoPon/Expression/Internal/Playback` | 引数なし。終端ポーズの取得・適用を同期実行 |
| `MenuRefreshTag` | `ResoPon/Expression/Internal/MenuRefresh` | 引数なし。メニュー表示可否を再計算。メニュー生成時のみ |

Internal の4つは公開操作用ではない。メニューボタンの送信値はコンポーネントの PressedData に保持され、DynamicVariable ではない。
選択中の一時候補、逆引き Pair 番号、メニュー表示判定は LocalValue / LocalObject、初期化済みフラグは StoredValue<bool> を使う。
これらも DynamicVariable の保存変数とは区別する。

現行版は `PreviousOwner`、`Override`、`LeftInput`、`RightInput` という項目を生成しない。
表情固定は AllowExternalInput=false と左右のジェスチャー値で表現する。

## 実装の参照先

- [ExpressionSystemSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionSystemSetup.cs)：Core、Catalog、Outputs、対応表、診断レコードの生成。
- [ExpressionFlux.cs](../src/VrmToResonitePackage/Expressions/ExpressionFlux.cs)：スコープ、変数・定数ノード、読み書き。
- [ExpressionApiSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionApiSetup.cs)：入力検証、左右値の更新、モード変更、ID の逆引き。
- [ExpressionLifecycleSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionLifecycleSetup.cs)：装着状態による初期化・終了処理。
- [ExpressionPlaybackSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionPlaybackSetup.cs)：選択検証、終端ポーズの取得・通常出力のWrite・追跡専用Drive。
- [ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)：メニュー、キー割当、機種別入力。
- [ExpressionModel.cs](../src/VrmToResonitePackage/Expressions/ExpressionModel.cs)：変換用の中間カーブと安定した出力 ID。
