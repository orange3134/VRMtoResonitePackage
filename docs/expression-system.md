# 表情システム

VRM・VRChatの顔表情を固定ポーズとして取り込み、ハンドジェスチャー、キーボード、
Resoniteのコンテキストメニュー、外部Dynamic Impulseから選択する。
生成物は標準コンポーネントとProtoFluxで動作し、利用側にResoPonのDLLは不要。
既存パッケージを更新する場合は再変換・再インポートする。

設定の型と初期値は[変数リファレンス](expression-variables.md)、
取り込み条件は[VRChat表情の検出と割り当て](vrchat-expression-import.md)、
機種ごとの判定は[コントローラー入力](controller-gestures.md)を参照。

## 対応範囲

- VRChatはFaceEmo基準で検出した最初の表情セットから、左右各0〜7の64通りの対応表を作る。
  元Animator全体や汎用パラメーターの状態機械は生成しない。
- 各顔カーブの最後のキー値を保存する。遷移時間、ループ、途中の動き、再生位相は再現しない。
  最後に開始姿勢へ戻る素材は、その戻った値になる。AnimXや元のキー列は出力しない。
- VRMの感情表情も同じ仕組みで選択できる。左右各0〜7の64通りを生成し、
  標準プリセットを[VRMの標準割り当て](#vrmの標準割り当て)でハンドサインに対応させる。
- 表情一覧の `Clips` と、ハンドサインの選択先を決める `GestureTable` は独立している。
  自動割り当てがなくても、Clipsに残った表情はメニューやAPIで選べる。
- 材質・物体・Transform・イベントなどが混在するClipからは顔カーブだけを採用する。
  元のExpression Menu、動的な表情セット切り替え、連続的な握り込み、表情ごとの追跡制御は再現しない。

### VRMの標準割り当て

XWear Packager 0.3.4の標準FXテンプレートに合わせ、左右に同じ番号を使う。
ResoPonでは左右各0〜7の8種を使い、`L0R0`〜`L7R7` の全64セルを生成する。
VRChatの元Animatorから取り込む64セルは従来の割り当てを使う。

| 番号 | VRM 1.0 | VRM 0.x | 動作 |
|---|---|---|---|
| 0〜2 | 割当なし | 割当なし | Neutral / Fist / HandOpen。反対の手の表情を維持 |
| 3 | sad | sorrow | 悲しみ |
| 4 | happy | joy | 喜び |
| 5 | angry | angry | 怒り |
| 6 | surprised | カスタム表情 Surprised | 驚き |
| 7 | relaxed | fun | リラックス |

右手の番号に取り込める表情があれば右を優先し、なければ左の表情を使う。
例: `L4R0`・`L4R1`・`L0R4`・`L4R4` はhappy、`L4R5` はangry。
プリセットの欠落や解決できる顔シェイプがない場合も、その手は空として扱う。
番号は詰めず、両手とも空ならnullへ解除する。`L0R0` も初期状態ではnullになる。

判定には表示名でなくプリセット種別を使い、同名のカスタム表情は標準割り当てに使わない。
ただしVRM0のカスタム表情 `Surprised` は、大文字小文字を区別せず番号6に割り当てる。
カスタム表情はClipsとメニューに残す。neutralは選択用Clipにせず、解除には元のBaseを使う。
口パク・瞬き・視線プリセットは既存の追跡設定が扱い、感情表情と同じ顔シェイプを使う場合は
共通システムの出力合成を通す。保存後は `DV/GestureTable/LnRm` の参照を変更できる。

## 操作

### コンテキストメニュー

左右どちらの手からも共通の `Expressions` メニューを開く。
ルートにはneutral用、`Reset` にはリセット用のアイコンを設定する。
画像の定義とSprite生成は `Expressions/ExpressionMenuIcons.cs` に集約している。
VRMから取り込んだ標準の感情表情には、表情直接選択でプリセット別のアイコンを設定する。
表示名ではなく正規化した標準プリセットで判定し、カスタム表情やunitypackage由来の表情には自動で付けない。
例外として、VRM0のカスタム表情 `Surprised`（大文字小文字を区別しない）には驚きアイコンを付ける。
VRM0には標準のsurprisedがなく、`presetName: unknown` として保存されるための互換対応で、番号6への自動割り当てにも同じ判定を使う。
アイコンは `Clips` の各表情に保持し、直接表示・グループ表示の選択ボタンから同じSpriteを参照する。

GUIの「表情メニューを生成しない」、またはCLIの `--no-expression-menu` を指定すると、
`Expressions` メニュー全体（表情直接選択、ハンドジェスチャーの有効／無効切り替え、`Reset`）を生成しない。
この設定はVRM・unitypackage共通で、表情クリップ、64通りの対応表、ハンドサイン・キーボード・外部APIによる操作は生成する。
口パク・瞬きの生成設定にも影響しない。

| 項目 | 動作 |
|---|---|
| Select expression | 表情を選択して保持。同じ表情をもう一度押すと解除。9個以上はGroup 1, Group 2, …から選択 |
| Hand gestures: Both / LeftOnly / RightOnly / None | 左右のハンドジェスチャーの許可状態を表示・変更 |
| Reset | メニュー保持を解除し、左右ジェスチャーを0、選択表情をnullへ戻す。許可設定は維持 |

選択できる表情が8個以下なら、`Select expression` の直下に表情と `Back` を並べ、サブグループを作らない。
9個以上なら表情グループを各8個までと `Back` で構成する。生成時の対応表から、左右共通、左のみ、右のみ、
両手の組合せのみ、未割当の順に分類し、分類内はClips順で並べる。分類をまたいで詰め合わせない。
8個以下の場合も同じ分類順で並べる。同名でも別Slotならそれぞれ表示し、Clipsの各表情に到達できるボタンを1つずつ生成する。

VRではハンドジェスチャーの許可を4択で設定し、一致する選択肢を緑で示す。
デスクトップでは両手トグルになり、両手有効なら両手無効、それ以外なら両手有効に切り替える。
この操作は左右ジェスチャー値・表情・メニュー保持を変更しない。
`Select expression` の入口はメニュー保持中に緑、それ以外は白になる。

メニュー保持中も左右の入力値と対応表の評価は更新されるが、表情への適用は拒否される。
瞬き・口パクなどの追跡合成は継続する。解除時は左右値を0に戻し、次の入力要求から適用を再開する。
保持中に拒否した要求を後で再生することはない。

### キーボード

既定は `Shift + テンキー` が左手、`Ctrl + テンキー` が右手。
`Ctrl + Shift + テンキー` は両手を更新する。

| 数字 | 手形 |
|---|---|
| 0 | Neutral |
| 1 | Fist |
| 2 | HandOpen |
| 3 | FingerPoint |
| 4 | Victory |
| 5 | RockNRoll |
| 6 | HandGun |
| 7 | ThumbsUp |
| 8・9 | 拡張用。対応表は自動生成しない。必要な行を追加して使用 |

ローカル装着中に「修飾キーと対象キーを押している」条件がfalseからtrueになったときだけ送る。
条件が成立したまま別キーを追加・切り替えしても再送しない。同時押しは最小の番号を採用し、
キーを離してもNeutralは送らない。各手の `DV/Modifier` と `DV/Key.0`〜`Key.9` で編集できる。
キーボードはハンドジェスチャー許可に関係なく左右値を更新するが、メニュー保持中の表情は上書きしない。
テキスト入力中のフォーカス抑制は生成していないため、クライアント上でも確認する。

### ハンドジェスチャー

Touch、Index、Vive、WindowsMR、Cosmosを生成する。装着者がローカル・VR中・機器が有効・
該当する手が許可されている場合に入力する。表情の番号を選ぶ機能であり、指ボーンは駆動しない。
判定表と待機中の入力破棄、機器切断時の扱いは[コントローラー入力](controller-gestures.md)を参照。

## 生成される構成

```text
Expressions                         ExpressionSystem空間
├─ API
│  ├─ Method                       公開操作の受信ボード
│  ├─ Internal                     UpdateSelection / ApplyExpression / ResetState
│  └─ Event/OnAvatarWorn            着用開始の初期化
├─ Clips/各表情/DV                  シェイプごとの固定値
├─ Outputs/各出力
│  ├─ DV                           基礎値・混合設定・最終出力への参照
│  └─ Tracking                     既存の追跡ドライバーがある出力だけ
├─ DV
│  ├─ Config.* / State.* / Reference.*
│  └─ GestureTable/L0R0 … L7R7      表情Slotへの参照（VRM・VRChat共通）
├─ Inputs
│  ├─ ContextMenu                  メニュー生成時のみ
│  ├─ Keyboard/Left・Right
│  └─ HandGestures/各機種/Left・Right
└─ Drivers/各Renderer               メッシュ出力がある場合
```

APIとその子は整理用Slotで、別の変数空間を持たない。全Impulseの宛先は
`ExpressionSystem/Reference.API` が参照するAPI。ボード間はImpulseとDynamicVariableで接続し、
FluxGroupを分ける。入力ボードにOnStartは置かず、自動初期化はOnAvatarWornが担当する。
各ボードの親はローカル原点、ノードは左から右へ配置する。

コンテキストメニューの項目は各サブメニューSlotの直下に置き、`ItemsRoot` は自分自身、
`SearchWholeHierarchy=false` とする。`Back` だけは親メニューを参照する。

## 選択と出力

`SetGesture` が左右値を更新し、`UpdateSelection` がローカル装着中に
`GestureTable.L{左}R{右}` を読んで `ApplyExpression` へ表情Slotを送る。
存在しない行や未割当はnullになる。生成時の `L0R0` は解除（null）とし、そのClip IDを参照する
他のセルもnullにする。このClipとコンパイル用の基準ポーズはClips・メニューへ保存しない。
同じ表示名の別Clipまで削除することはない。解除は各OutputのBaseへ戻る経路を使う。

`ApplyExpression` はメニュー保持がなく、受信Slotが現在と異なる場合だけ処理する。
非nullはローカル装着者だけが受理する。未装着時はnullによる参照解除を受理するが、
通常出力への書き込みは行わない。他ユーザーが装着しているアバターへの適用も行わない。

通常出力は選択時にOutputsを1回走査し、値が異なる場合だけ書く。同じSlotの再送では走査しない。
既存の瞬き・口パク等がある出力は、ドライバーをBaseへ付け替え、専用Driveで表情と継続合成する。
通常のメッシュ出力はSmoothValueを通し、生成時に瞬き接続を検出した出力は補間を省く。
式と接続上の注意は[出力変数](expression-variables.md#出力)を参照。

## 着用・解除・保存

OnAvatarWornは起動時と着用状態の変更時に動く。ローカル着用を検出すると、保存されない
初期化フラグを立て、ResetStateを呼ぶ。再装着・複製・再読込後の着用も同じ経路を使う。
ResetStateはメニュー保持をfalse、左右値を0にし、ApplyExpressionへnullを送る。
ハンドジェスチャーの許可設定は維持する。

脱着時は初期化フラグだけを解除し、共有の左右値・選択・許可設定は保持する。
通常出力は最後の値を保持し、追跡出力は未装着中にBaseへ追従する。
公開Resetは未装着でも状態クリアを受け付けるが、通常出力のBase復帰にはローカル着用が必要。

## 表情と対応表の編集

- 割り当て変更は `DV/GestureTable/LnRm` のSlot参照を編集する。nが左、mが右。
  変更後に入力を送り直す。同じ表情Slotに解決される場合は出力を再計算しない。
- 表情値は `Clips/表情/DV/<出力キー>` のfloat。0の明示指定と変数の欠落は異なり、欠落だけがBaseになる。
- 通常出力の値・混合設定を編集したら、一度別表情またはnullへ切り替えて選び直す。
  追跡出力は選択中の名前付き変数を継続して読むため、値の変更を自動反映する。
- メニューありで生成した表情を追加するにはClips直下の既存表情を複製し、値と
  `ContextMenuItemSource.Label` を編集する。Slot名の変更ではラベルは変わらない。対応表への割り当ては任意。
- グループメニューは生成時に構築する。追加した表情のボタンは既存ボタンを複製して参照を設定するか、
  APIから選択する。Clipsの編集だけでグループ一覧が自動再構築されるわけではない。
- 選択中のClipを削除してもメニュー保持は残る。Resetまたは別のメニュー操作で解除する。
  ClipのActive状態は選択可否の条件ではない。

## 外部イベントAPI

宛先は対象アバターの `Expressions/API`（`Reference.API`）。タグと引数型を一致させる。
メニュー選択には `DynamicImpulseTriggerWithReference<Slot>`、左右入力にはintのValue版を使う。

| タグ（`ExpressionSystem/` 以下） | 引数 | 動作 |
|---|---|---|
| Method.TrySetGesture.Left / Right | int | 該当する手が許可されている場合だけSetGestureへ転送 |
| Method.SetGesture.Left / Right | int | 左右値を更新してUpdateSelectionを呼ぶ。intの範囲制限なし |
| Method.SelectMenuExpression | Slot | nullまたは自身のClips直下だけ受理。保持解除→左右0→適用→非nullなら保持 |
| Method.ToggleHandGestures | なし | 両手とも許可なら両手を停止、それ以外なら両手を許可 |
| Method.Reset | なし | ResetStateを呼ぶ |

公開操作は7受信口。左右値や許可の更新自体には着用条件を置かず、実際の表情適用で判定する。
無効なSlotをSelectMenuExpressionへ送っても保持・左右値は変わらない。
未装着で有効な非nullを送ると保持と左右値は更新するが、表情は適用されず、次の着用でリセットされる。

内部タグは `Internal.UpdateSelection`（引数なし）、`Internal.ApplyExpression`（Slot）、
`Internal.ResetState`（引数なし）。内部ApplyExpressionはClips所属を検証しないため、
外部の直接選択は公開SelectMenuExpressionを使う。State.CurrentExpressionの直接編集だけでは通常出力は更新されない。

## 診断と検証

表情が変わらない場合は、変換ログの候補除外理由、Clipsの有無、左右値、対応表、
`State.ContextMenuSelected`、`State.CurrentExpression`、OutputのIdとClip変数名を順に確認する。
診断メッセージとボードごとのノード数は変換ログへ出し、生成物にDiagnostics Slotは置かない。

```powershell
$env:RESOPON_NOPAUSE = '1'
dotnet run --project tests/ExpressionSmoke -c Release
```

テストの対象と保存パッケージの検証方法は[ExpressionSmoke](../tests/ExpressionSmoke/README.md)、
実アバターの変換・inspectは[ローカルアバター回帰テスト](local-avatar-tests.md)を参照。
リリース前は自動検証に加え、物理コントローラー、キーフォーカス、VR・デスクトップのメニュー、
複数クライアント、再装着時の挙動をResonite上で確認する。ヘッドレス検証だけで実機確認済みとは扱わない。
