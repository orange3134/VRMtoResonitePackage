# 表情システムの DynamicVariable・定数リファレンス

現行の生成実装（`ExpressionSystem/Version = 5`）に基づく。構成・操作方法は[表情システム](expression-system.md)を参照。

## 名前・型・編集区分

変数名は `空間名/項目名`。以下の項目一覧では空間名を省略する。
例えば Core の `LeftGesture` は `ExpressionCore/LeftGesture`、Catalog の `Id` は `ExpressionClip/Id`。
変数定義が異なるレコードには別の空間名を付け、同じ定義のインスタンス間だけで名前を共用する。
左右の手や各表情は、それぞれ独立した DynamicVariableSpace を持つため、同じ名前でも値は別になる。

| 配置先（Expressions からの相対位置） | 空間名 | 定義の役割 |
|---|---|---|
| Expressions 自身 | `ExpressionSystem` | バージョンと入口への参照 |
| Core | `ExpressionCore` | 入力・選択・再生状態 |
| GestureTable | `ExpressionGestureTable` | 左右64通りの表情参照 |
| Catalog/各表情、API/Templates/各表情 | `ExpressionClip` | 表情の設定と AnimX 参照 |
| 各表情/Bindings/各項目 | `ExpressionBinding` | 出力レコードへの参照 |
| Outputs/各項目 | `ExpressionOutput` | BlendShape の基礎入力・混合・最終出力 |
| Inputs/Keyboard/Bindings/各項目 | `ExpressionKeyboardBinding` | キー割当と押下状態 |
| Inputs/HandGestures/Modules/各機種 | `ExpressionGestureSettings` | しきい値と安定待ち時間 |
| 各機種/Left、Right | `ExpressionGestureHand` | 片手の入力判定状態 |
| Diagnostics/Import warning | `ExpressionImportWarning` | 変換時の警告文 |
| Diagnostics/Graph modules/各項目 | `ExpressionGraphModule` | ボードのパスとノード数 |

`Record()` は空間名を必須引数で受け取り、`OnlyDirectBinding=true` の空間を作る。
例外は GestureTable（false）で、子 Slot の `ExpressionGestureTable/Pair.N` を表のスコープから読む。
単なる整理用の Catalog・Outputs・Bindings・Diagnostics には空間を追加しない。
名前の定義は [ExpressionSpaces.cs](../src/VrmToResonitePackage/Expressions/ExpressionSpaces.cs) に集約する。
ProtoFlux の読み書きは対象の空間名を明示し、変数生成は配置先の空間名を使う。
固定の読み取り先がノード自身の祖先と同じ名前付き空間を指す場合は Dynamic Variable Input にする。
各手の状態と親機種の設定、Core 内の Selection／Playback の状態・表情参照が対象。
実行時に対象が変わるレコード、Pair.N の可変名、祖先にない兄弟 Core への参照は ReadDynamicVariable を使う。

Version 4 以前の共通 `Expr` 空間は新規生成しない。既存パッケージは再変換・再インポートで更新する。
DynamicVariable を直接読む外部処理は新しい名前へ変更する。公開 Dynamic Impulse の Tag・引数は Version 4 と同じ。

- **設定**：動作を調整する編集用の値。固定的に使われても、実装上は変更可能な変数。
- **定義**：生成時の識別子・参照・メタデータ。編集時は関連データとの整合が必要。
- **状態**：ロジックが更新する値。診断用に読み、操作には公開 API を使う。

値型と string は `DynamicValueVariable<T>`、Slot・User・フィールドなどへの参照は `DynamicReferenceVariable<T>`。
以下の型はその T を示す。初期値は生成直後の値で、実行開始・初期化後の値とは限らない。

## Expressions 直下と対応表

| 配置先 | 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|---|
| Expressions | `Version` | int | 5 | 定義。生成システムのバージョン。実行時の分岐には使わない |
| Expressions | `Receiver` | Slot | API/Receivers | 定義。公開 Dynamic Impulse の送信先 |
| Expressions | `Catalog` | Slot | Catalog | 定義。表情一覧への参照 |
| GestureTable/各セル | `Pair.0`〜`Pair.63` | Slot | コンパイルした表情、または null | 設定。番号は `左 × 8 + 右`。子の並び順ではなく変数名で検索する |

## Catalog/各表情

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | 元・生成表情の ID | 定義。Select API の検索キー。直接選択メニューの送信値も追従する。Pair は ID ではなく Slot を参照 |
| `DisplayName` | string | 表情名 | 設定。直接選択メニューの表示名 |
| `Enabled` | bool | true | 設定。false は選択・メニュー利用対象外 |
| `Loop` | bool | 元クリップの設定 | 設定。true なら経過秒を Duration で剰余演算してサンプリング |
| `Duration` | float | 元の長さを最低 0.001 秒に補正 | 設定。ループ周期（秒）。正値を維持する。非ループ時は経過秒をそのまま使う |
| `FadeIn` | float | 0.1 | 設定。その表情へ切り替える秒数。0 以下は即時切替 |
| `FadeOut` | float | 0.1 | 設定。その表情から選択なしへ戻る秒数。別の有効表情へ移る場合は移動先の FadeIn を使う |
| `Source` | string | 元データの説明、または空文字 | 定義。由来の記録。再生判定には使わない |
| `Clip` | IAssetProvider&lt;Animation&gt; | 同じ Slot の StaticAnimationProvider | 定義。AnimX 供給元。AssetLoader にも参照をコピーする。アセット未ロードなら選択無効 |
| `MenuAvailable` | bool | false | 状態。メニュー生成時のみ作成。毎更新、Slot が有効・Enabled=true・対応表に参照ありなら true。ロード完了は判定しない |

`Bindings/各項目` の `Output`（Slot）は対応する Outputs レコードへの定義参照。
再生ループは Bindings を走査せず、Outputs の Id で AnimX トラックを検索する。

`API/Templates/Expression (copy into Catalog)` は最初の表情の複製で、Id を空文字、Enabled を false に変更する。
コピー後は一意の ID、Clip、必要な定義を整え、有効にして Pair へ割り当てる。表にない ID は Select API で選べない。

## Core：入力・選択・再生

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `AllowExternalInput` | bool | true | **設定**。通常のジェスチャー・キーボード・外部左右 API の受付可否。メニュー操作で false、初期化で true。許可 API でも変更可能 |
| `LeftGesture` / `RightGesture` | int | 0 | API が受理した各手の 0〜7。メニューも同じ値を更新 |
| `PairIndex` | int | 0 | Selection が計算した LeftGesture × 8 + RightGesture（0〜63） |
| `CurrentExpression` | Slot | null | Slot 有効・Enabled=true・アセット取得済みの候補。それ以外は null |
| `PlaybackStart` | float | 0 | CurrentExpression の参照が変わった WorldTimeFloat（秒）。同じ表情の再指定では再生を始め直さない |
| `FadeDuration` | float | 0.1 | 切替時に採用した FadeIn または FadeOut。途中で Catalog を編集してもその切替の値は再取得しない |
| `PlaybackElapsed` | float | 0 | ローカル駆動の診断値。WorldTimeFloat − PlaybackStart |
| `FadeWeight` | float | 1 | ローカル駆動の診断値。FadeDuration が正なら clamp01(PlaybackElapsed / FadeDuration)、それ以外は 1 |

Core に保持する表情参照は CurrentExpression だけ。Selection は対応表から読み取った参照を検証し、
切替前後の比較・フェード設定後に CurrentExpression へ直接渡す。
MappedExpression／CandidateExpression の診断用 DynamicVariable は生成しない。
対応表の参照先の確認には PairIndex と GestureTable の行を使う。

AllowExternalInput 以外は状態として扱う。SelectionStatus は生成しない。入力モードは AllowExternalInput、再生対象は CurrentExpression で確認する。未割当と無効・未ロードはいずれも CurrentExpression=null となる。
PlaybackElapsed・FadeWeight は ValueFieldDrive で各クライアントが駆動し、毎フレームの同期書き込みを行わない。
再生処理の実行回数を示す値ではない。

Lifecycle は現在の装着者がローカルユーザーの場合だけ、初期化確認 → Selection → Playback を実行する。
初期化済みかは保存されない `StoredValue<bool>` だけで管理し、PreviousOwner は保持しない。
公開 API も入力許可を判定する前に同じ初期化確認を呼ぶため、最初の LocalUpdate より早い左右入力も保持する。

初期化時は左右値・PairIndex を 0、AllowExternalInput を true、
CurrentExpression を null、Outputs の Result・Snapshot を Base、
キーボード Held を false に戻す。その後の Selection → Playback で現在の対応表に応じた状態になる。
PlaybackStart・FadeDuration はここでは変更しないため、時計から算出する PlaybackElapsed・FadeWeight は初期化完了の指標ではない。

装着が終了すると、初期化済みのクライアントだけが一度このクリア処理を実行し、フラグを false に戻す。
既に別のユーザーが装着している場合は共有値をクリアせず、ローカルフラグだけを戻す。
未装着で生成・複製したインスタンスや閲覧者のクライアントはクリアを書き込まない。
未装着中は Selection・Playback を実行せず、出力は終了時に戻した値を保持する。
再装着・複製・読み込み後の最初の処理では再び初期化する。

## Outputs/各 BlendShape

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Id` | string | binding キーの SHA-256 の先頭24桁（小文字16進数） | 定義。AnimX の Node=Expression、Property=この ID のトラックを検索 |
| `Path` | string | 元 binding のパス | 定義。出力の由来・識別情報 |
| `Shape` | string | BlendShape 名 | 定義。出力の由来・識別情報 |
| `Baseline` | float | initialWeight があればその値、なければ元フィールド値 | 定義。生成時の基準値の記録。Playback は読まない。編集しても生成済み Neutral の AnimX は変わらない |
| `Base` | float | 元フィールド値 | 状態／基礎入力。既存の瞬き・viseme ドライバーがあれば出力先をここへ移す。表情にトラックがない場合の値でもある |
| `TrackingWeight` | float | 0 | 設定。表情値から Base へ寄せる割合。使用時に 0〜1 に制限。0=表情値、1=Base。自動更新処理はない |
| `Result` | float | 元フィールド値 | 状態。Playback の最終出力。ValueCopy が元フィールドへコピー |
| `Snapshot` | float | 元フィールド値 | 状態。表情が変わる直前の Result。フェードの始点 |
| `Target` | IField&lt;float&gt; | 元の BlendShape フィールド | 定義。出力先の記録。ValueCopy の送信先は生成時に別途設定するため、この参照だけ変更しても送信先は変わらない |
| `OriginalDriver` | ISyncRef | 元のドライバー | 定義。既存 ActiveLink が ISyncRef の場合だけ作成。Base へ付け替えたドライバーの記録 |

表情なし・該当トラックなしの場合は sample の代わりに Base を使う。

```text
desired = lerp(sample, Base, clamp01(TrackingWeight))
Result  = lerp(Snapshot, desired, FadeWeight)
Result → ValueCopy → 元の BlendShape フィールド
```

## Inputs/Keyboard/Bindings/各項目

| 名前 | 型 | 初期値 | 区分・役割 |
|---|---|---|---|
| `Tag` | string | 左右の Gesture API Tag | 設定。イベントの送信先 Tag |
| `Gesture` | int | 項目ごとの 0〜7 | 設定。送信する手の状態 |
| `Enabled` | bool | true | 設定。そのショートカットの有効・無効 |
| `Key` | Renderite.Shared.Key | Alpha1〜Alpha8 | 設定。数字キー。None は無効 |
| `Shift` | bool | 左=false、右=true | 設定。Shift 押下状態の一致条件。Ctrl・Alt はロジック側の固定必須条件 |
| `Held` | bool | false | 状態。前回のキー条件成立状態。false→true でだけ送信し、連続発火を防ぐ |

キーを離しても Neutral は送らない。入力禁止中も Held は更新するが、送られた通常入力は API が拒否する。

## Inputs/HandGestures/Modules：機種別入力

Touch・Index・Vive・WindowsMR の各モジュールに以下の設定を持つ。

| 名前 | 型 | 初期値 | 役割 |
|---|---|---|---|
| `GripThreshold` | float | 0.55 | Grip の押下判定しきい値。Touch・Index で使用 |
| `GripReleaseThreshold` | float | 0.45 | GripHeld=true のときのしきい値。押下と解放の境界をずらして揺れを防ぐ |
| `TriggerThreshold` | float | 0.55 | Trigger の押下判定しきい値 |
| `TriggerReleaseThreshold` | float | 0.45 | TriggerHeld=true のときのしきい値 |
| `StabilitySeconds` | float | 0.05 | 候補が変わらず続く必要時間（秒） |

比較は厳密な `入力値 > しきい値`。Vive・WindowsMR は Grip の bool 出力を直接使うため、Grip の2設定は判定に使わない。
各モジュールの Left / Right は独立した ExpressionGestureHand スコープを持つ。
機種別入力もローカルな `StoredValue<bool>` で初期化済みかを管理し、User 参照は保持しない。
ローカルユーザーが装着者でなくなるとフラグを false に戻し、次の装着時に候補・安定値・押下判定を初期化する。

| 名前 | 型 | 初期値 | 更新元・役割 |
|---|---|---|---|
| `Candidate` | int | -1 | 最新の入力判定候補。変化すると Since を更新 |
| `Since` | float | 0 | Candidate が変わった WorldTimeFloat（秒）。安定待ちの起点 |
| `Stable` | int | -1 | 安定判定後に送信したジェスチャー。変化時だけ再送するための比較値 |
| `GripHeld` | bool | false | 前回の Grip 判定。次回の押下／解放しきい値を選ぶ |
| `TriggerHeld` | bool | false | 前回の Trigger 判定。次回の押下／解放しきい値を選ぶ |

-1 は未確定・未送信で、公開左右 API の有効値ではない。
切断・非アクティブ時や入力禁止中は Candidate・Stable を -1、GripHeld・TriggerHeld を false に戻し、入力イベントは送らない。
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
| `ExpressionSystem`、`ExpressionCore` など | 上記のレコード定義別の空間名。変数パスは空間名 + `/` + 項目名 |
| `ExpressionGestureTable/Pair.` | 番号を付けて対応表を検索するパスの接頭辞 |
| 0〜7 | 0=Neutral、1=Fist、2=HandOpen、3=FingerPoint、4=Victory、5=RockNRoll、6=HandGun、7=ThumbsUp |
| 8 / 64 | 片手の状態数／左右の組合せ数。Pair の計算・逆引き・API 範囲検査に使用 |
| -1 | 手の未確定、Select の一致する Pair が未発見 |
| 0 / 1（float） | フェード・混合率の端点。フェード秒数が正でなければ混合率 1 |
| null | 未選択 Slot、未記録 User など参照なし |
| `Expression` | AnimX の固定トラック Node 名 |

公開イベント Tag は変数名とは別の仕組みで、Receiver へ値を送る。

| C# 定数 | Tag | 引数・役割 |
|---|---|---|
| `LeftTag` / `RightTag` | `ResoPon/Expression/Gesture/Left` / `ResoPon/Expression/Gesture/Right` | int 0〜7。通常の左右入力。AllowExternalInput に従う |
| `MenuLeftTag` / `MenuRightTag` | `ResoPon/Expression/Menu/Left` / `ResoPon/Expression/Menu/Right` | int 0〜7。メニュー専用にして片手を変更 |
| `SelectTag` | `ResoPon/Expression/Menu/Select` | string。表にある有効な表情 ID を最小 Pair 番号へ逆引きし、両手を変更してメニュー専用にする |
| `InputEnabledTag` | `ResoPon/Expression/AllowExternalInput` | bool。通常入力の許可・停止。左右値は維持 |
| `InitializeTag` | `ResoPon/Expression/Internal/Initialize` | 引数なし。Lifecycle の初期化確認 |
| `SelectionTickTag` | `ResoPon/Expression/Internal/Selection` | 引数なし。選択更新を同期実行 |
| `PlaybackTickTag` | `ResoPon/Expression/Internal/Playback` | 引数なし。再生・出力更新を同期実行 |

Internal の3つは公開操作用ではない。メニューボタンの送信値はコンポーネントの PressedData に保持され、DynamicVariable ではない。
選択中の一時候補、逆引き Pair 番号、メニュー表示判定は LocalValue / LocalObject、初期化済みフラグは StoredValue<bool> を使う。
これらも DynamicVariable の保存変数とは区別する。

現行版は `PreviousOwner`、`Override`、`LeftInput`、`RightInput` という項目を生成しない。
表情固定は AllowExternalInput=false と左右のジェスチャー値で表現する。

## 実装の参照先

- [ExpressionSystemSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionSystemSetup.cs)：Core、Catalog、Outputs、対応表、診断レコードの生成。
- [ExpressionFlux.cs](../src/VrmToResonitePackage/Expressions/ExpressionFlux.cs)：スコープ、変数・定数ノード、読み書き。
- [ExpressionApiSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionApiSetup.cs)：入力検証、左右値の更新、モード変更、ID の逆引き。
- [ExpressionLifecycleSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionLifecycleSetup.cs)：装着状態による初期化・終了処理。
- [ExpressionPlaybackSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionPlaybackSetup.cs)：選択検証、フェード、追跡入力との合成。
- [ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)：メニュー、キー割当、機種別入力。
- [ExpressionAnimationConverter.cs](../src/VrmToResonitePackage/Expressions/ExpressionAnimationConverter.cs)：出力 ID と AnimX トラック。
