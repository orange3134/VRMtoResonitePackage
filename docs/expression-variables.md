# 表情システムの変数リファレンス

操作・API・ライフサイクルは[表情システム](expression-system.md)、
機種別の入力判定は[コントローラー入力](controller-gestures.md)を参照。
ここでは生成するDynamicVariableの名前、型、初期値、編集時の制約を定義する。

## 空間と配置

変数名は `空間名/項目名`。以下の表は項目名だけを示す。
値とstringは `DynamicValueVariable<T>`、参照は `DynamicReferenceVariable<T>`、
外部フィールドを公開するResultだけは `DynamicField<float>` を使う。

| 所属Slot（Expressionsからの相対位置） | 空間名 | 内容 |
|---|---|---|
| Expressions自身 | ExpressionSystem | Config.*、State.*、Reference.*、GestureTable.* |
| Clips/各表情 | ExpressionSystem.Clip | 出力キーごとのfloat値 |
| Outputs/各出力 | ExpressionSystem.Output | 基礎入力・合成設定・出力参照 |
| Inputs/Keyboard/Left・Right | ExpressionSystem.Input.Keyboard | 各手のキー設定 |
| Inputs/HandGestures/各機種 | ExpressionSystem.Input.HandGesture | 機種ごとのしきい値・待機時間 |

Expressions自身は `OnlyDirectBinding=false`、各レコード空間はtrue。
変数の実体は所属Slotの `DV/項目名` に1変数1Slotで置く。
対応表だけは `Expressions/DV/GestureTable/LnRm` に置き、変数名は `ExpressionSystem/GestureTable.LnRm`。
API、Clips、Outputs、GestureTableなどの整理用Slotには別空間を追加しない。

Expressions直下の順序はAPI、Clips、Outputs、DV、Inputs、Drivers（生成される場合）。
共通DVはConfig.*、State.*、Reference.*の順、分類内はOrdinal順、最後にGestureTableを置く。
固定参照は必要なものだけ生成し、各モジュールから祖先の名前付き空間を検索する。
可変Slotや可変の対応表名にはReadDynamicVariableを使う。

## 共通設定・状態

| 名前 | 型 | 生成時の値 | 用途 |
|---|---|---|---|
| Config.AllowHandGesture.Left / Right | bool | true | 各手の機種別入力とTrySetGestureの許可。SetGesture・キーボードには適用しない |
| Config.SmoothingSpeed | float | 10 | 瞬き以外のメッシュ出力のSmoothValue.Speed。秒数ではなく追従速度 |
| State.ContextMenuSelected | bool | false | メニュー選択の保持。ApplyExpressionで表情更新を拒否する条件 |
| State.Gesture.Left / Right | int | 0 | 最後に受理した左右入力。範囲制限なし |
| State.CurrentExpression | Slot | null | ApplyExpressionが受理した選択表情 |
| Reference.API | Slot | API | 公開・内部Impulseの共通送信先 |
| Reference.Clips | Slot | Clips | 表情一覧とメニュー入力の検証先 |
| Reference.* | Slot | 対応する内部Slot | Outputsなど、グラフが使う固定参照だけを生成 |
| GestureTable.L0R0〜L7R7 | Slot | コンパイルした表情またはnull | 左右入力に対応する表情。L0R0はnull |

設定は編集可能、State.*はロジックが更新する診断用の状態として扱う。操作には公開APIを使う。
Reset・着用開始は保持と左右値・選択をクリアし、許可設定を維持する。
許可の変更だけでは保持中の表情も左右値も変わらない。
SmoothingSpeedは各SmoothValueと同じSlotのDynamicValueVariableDriverから参照し、未解決時も10を使う。
複製・再読込後も各アバター内の変数へ結合する。

## 表情の固定値

`Clips/各表情/DV/<出力キー>` に `ExpressionSystem.Clip/<出力キー>` のfloatを置く。
値は顔カーブの最後のキー値。例は `Body.Smile`、`Body.Blink_L`。
Id、DisplayName、再生時刻、Duration、Versionなどのメタデータ変数は生成しない。
メニュー生成時の表示名は各Clipの `ContextMenuItemSource.Label` に保存し、
グループ内のボタンはこのLabelに追従する。Slot名の変更ではLabelは変わらない。

OutputのIdとClip変数のキーを一致させる。明示した0は有効な表情値であり、
変数が見つからない場合だけDefault（なければBase）へ戻す。Active状態では選択を制限しない。
通常出力の編集を反映するには、別表情またはnullへ切り替えてから選び直す。
同じSlotの再送やState.CurrentExpressionの直接編集だけでは通常出力は更新されない。
追跡出力は選択中の変数の変更・追加・削除を継続反映する。

### 出力キーの生成規則

キーは解決したメッシュ名とBlendShape名を `.` で結ぶ。数値bindingも実メッシュのShape名へ解決する。

- 名前の前後をTrimし、空名はMesh・Shapeにする。
- 半角スペース、`-`、`.`、`_`、日本語、英数字は保持する。
- それ以外のUnicode記号・句読点・空白、制御文字・書式文字・UTF-16サロゲートは `_` にする。
  例：Body/FaceとSmile(1)は `Body_Face.Smile_1_`。
- 元bindingのPath・ShapeをOrdinal順に並べ、置換後の衝突には `.2`、`.3` … を付ける。
  元から番号を含む名前とも衝突しないようにする。同名Rendererも別の出力として扱う。
- キーは生成時に固定する。後からOutput.Idを変更するなら各ClipのVariableNameも合わせる。
  同一空間に重複名を作らない。

## 出力

各Outputは `ExpressionSystem.Output` 空間を持つ。

| 名前 | 型 | 生成時の値 | 用途 |
|---|---|---|---|
| Id | string | 生成した出力キー | Clipのfloat変数を検索する名前 |
| Base | float | 両手Neutralの値、未解決なら元フィールド値 | 通常出力のデフォルト。既存ドライバーがある場合は追跡からの入力 |
| Default | float | 両手Neutralの値 | 追跡入力とNeutralが異なる場合だけ生成。追跡中のBaseを上書きせず解除ポーズを保持 |
| TrackingWeight | float | 0 | 表情からBaseへ寄せる割合。使用時に0〜1へ制限 |
| BlinkMode | int | 通常0、瞬き1または2 | 0=通常合成、1=max、2=min。ClosedState < OpenStateなら2 |
| Result | float（DynamicField） | 元フィールド値 | 実際の書き込み先フィールドへの参照 |
| OriginalDriver | ISyncRef | 元のActiveLink | 既存ドライバーがある場合だけ生成する参照 |

```text
fallback = Defaultがある ? Default : Base
sample  = 装着中かつ選択表情にId名のfloat変数がある ? 表情値 : fallback
desired = lerp(sample, Base, clamp01(TrackingWeight))
Result  = BlinkMode == 1 ? max(desired, Base)
        : BlinkMode == 2 ? min(desired, Base) : desired
```

通常出力はローカル装着者の選択変更時にだけ上式を評価し、Resultが変わる場合に書く。
未装着中は最後の通常出力を保持する。OriginalDriverが非nullの出力は通常Writeから除外する。
追跡出力は専用のTrackingボードが全クライアントで評価し、未装着中はfallbackとBaseを合成する。

メッシュ出力はRendererごとに `DynamicBlendShapeDriver` を作り、使用するシェイプだけを登録する。
同名Rendererはコンポーネントの同一性で区別する。登録時は各駆動参照を明示的にリンクする。
Renderer設定済みのDriverへエントリーを足すだけで名前の再解決が行われるとは扱わない。

```text
通常: ApplyExpressionのWrite → Result → SmoothValue.TargetValue
      → SmoothValue.Value → DynamicBlendShapeDriver → BlendShape
追跡: 既存ドライバー → Base → TrackingのDrive → Result → 同じ出力経路
瞬き: EyeLinearDriver → Base → Trackingのmax/min → Result
      → DynamicBlendShapeDriver → BlendShape（SmoothValueなし）
```

Resultは通常SmoothValue.TargetValueを、瞬きではDynamicBlendShapeDriverのValueを直接参照する。
SmoothValueはWriteBack=false。メッシュ以外のテスト用フィールドなどには直接書く。
瞬きの補間省略は生成時にOpenCloseTarget接続を検出した場合だけで、BlinkModeの編集では変わらない。

### 瞬き・追跡の接続上の注意

既存のActiveLinkがISyncRefなら、そのTargetをBase.Valueへ付け替える。
OriginalDriverは元コンポーネント自体ではなく、この出力先を保持する参照を記録する。
継承された駆動や対応できない駆動を含むClipは診断を出して除外する。

Trackingがある出力でEyeLinearDriver.OpenCloseTargetを変更するならBase.Valueへ接続し、
閉じるほど大きい場合はBlinkMode=1、小さい場合は2にする。TrackingWeight=0でも瞬きが合成される。
DynamicBlendShapeDriverとEyeLinearDriverを同じ最終ウェイトへ直接重ねてDriveしない。
Trackingがない出力へ追跡を後付けするには、追跡元を設定して表情システムを再生成する。
OriginalDriverの書き換えだけではTrackingの生成・削除・再接続は行われない。

EyeLinearDriverのCloseSubtractLimitsは現在の合成には使わない。
標準範囲で表情の閉じ量Pを減算すると `clamp01(B-P)` になり、現在の `max(P,B)` と異なる。
この機能だけに置き換えると瞬きが0のとき表情の閉眼も保持できない。
別シェイプ間の閉じ量を名前だけで推定して抑制する処理も生成しない。

## キーボード設定

左右それぞれ `ExpressionSystem.Input.Keyboard` 空間を持つ。

| 名前 | 型 | 初期値 |
|---|---|---|
| Tag | string | ExpressionSystem/Method.SetGesture.Left または Right |
| Modifier | Renderite.Shared.Key | 左Shift、右Control |
| Key.0〜Key.9 | Renderite.Shared.Key | Keypad0〜Keypad9 |

キーの添字が送信するint値。KeyのNoneは未割当。Modifierは各手共通の押下条件として使う。
左右の修飾キーは独立して編集でき、その他の修飾キーは排他条件にしない。
キーごとのEnabled・Gesture、押下状態の同期変数は生成しない。

## ハンドジェスチャー設定

各機種の `DV` は `ExpressionSystem.Input.HandGesture` 空間に属する。

| 名前 | 型 | 初期値 | 対象 |
|---|---|---|---|
| StabilitySeconds | float | 0.05 | 全機種。入力遮断時間と送信待機時間 |
| FingerThreshold | float | 40 | Indexの人差し指〜小指のClosed境界（X角度） |
| ThumbThreshold | float | 25 | Index親指のClosed境界（Y角度）。右は符号反転 |
| FingerNeutralRange | float | 20 | Indexの4指の中間域幅 |
| ThumbNeutralRange | float | 20 | Index親指の中間域幅 |
| Direction.0〜Direction.7 | int | 0〜7 | Vive・WindowsMRの下から時計回りの8方向への割り当て |

左右のLogicは独立したTimeoutとDelayを持つ。Candidate・Stable・ElapsedTimeFloatや各手の状態DVは作らない。
StabilitySecondsは「最後の変化から安定するまで待ち直す時間」ではなく、
通過した入力の待機と、その後の入力を破棄する時間である。詳しい送信条件は[コントローラー入力](controller-gestures.md#入力の受付と送信)を参照。

## 実装の参照先

- [ExpressionSpaces.cs](../src/VrmToResonitePackage/Expressions/ExpressionSpaces.cs)：空間名。
- [ExpressionSystemSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionSystemSetup.cs)：Clip・Output・対応表の生成。
- [ExpressionFlux.cs](../src/VrmToResonitePackage/Expressions/ExpressionFlux.cs)：変数配置と参照・書き込み。
- [ExpressionBindingNames.cs](../src/VrmToResonitePackage/Expressions/ExpressionBindingNames.cs)：出力キー生成。
- [ExpressionPlaybackSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionPlaybackSetup.cs)：選択と出力合成。
- [ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)：入力設定とメニュー。
