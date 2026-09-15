# 表情システムの実装と編集方法

左右それぞれの現在のジェスチャーを 0〜7 の整数で保持し、
`LeftGesture * 8 + RightGesture` で64通りの対応表を引く。
対応表の参照先が変わったときだけアニメーションを切り替える。
VRChat の Animator 条件とレイヤーは変換時に評価し、アバター内には状態機械を生成しない。
標準コンポーネントと ProtoFlux で動作し、利用側に ResoPon の DLL は不要。

```mermaid
flowchart LR
    H[機種別ジェスチャー] --> E[DynamicImpulseReceiver]
    K[キーボード] --> E
    M[左右のコンテキストメニュー] --> E
    X[外部イベント] --> E
    E --> S[LeftGesture / RightGesture]
    S --> T[64通りの GestureTable]
    C[共有 Catalog] --> T
    T --> P[選択変更時に再生開始]
    O[直接選択の Override] --> P
    P --> R[出力と追跡入力の合成]
```

## 生成される構成

```text
Expressions/
  Catalog/                         表情ごとの定義と AnimX
  GestureTable/                    64個の Catalog 参照
  Core/                            左右の状態、現在の表情、再生開始時刻
    Logic/
      Lifecycle/                   初期化、所有者変更、更新順序
      Selection/                   対応表・直接指定の選択と切り替え
      Playback/                    再生時刻、フェード、出力の合成
  Outputs/                         BlendShape ごとのベース入力と最終出力
  Inputs/
    ContextMenu/
    Keyboard/Bindings/             左右8種ずつのショートカット
    HandGestures/Modules/
      Touch|Index|Vive|WindowsMR/   削除できる機種別入力
        Left/Logic/                左手の入力検出・安定化・通知
        Right/Logic/               右手の入力検出・安定化・通知
  API/Receivers/Logic/
    Gesture/                       左右の入力受付
    Select/                        Catalog の直接指定受付
    Automatic/                     直接指定の解除
  API/Examples/                    外部入力用の小さな Command
  API/Templates/                   Catalog に複製する表情テンプレート
  Diagnostics/                     自動設定できなかった理由
```

Core に入力元の一覧・優先順位・有効期限・汎用 Animator パラメーターは持たない。
各手の最後の入力 Command への参照は、削除・切断された入力を解放するためだけに使う。

Flux は1スロット1ノードで、各モジュール内を名前付きの節と8列のグリッドに配置する。
ProtoFlux Tool では調べたい `Selection`、`Playback` などのモジュールを個別に Unpack する。
モジュール間では ProtoFlux ノードを直接接続しない。所有者・時刻・定数の取得も各モジュール内で完結するため、
1つの入力や再生処理を開くだけで全機種・APIまでつながった巨大なグラフにはならない。

Core の更新は `Lifecycle` → `Selection` → `Playback` の順で行う。
呼び出しには対象モジュールだけを宛先とする同期 Dynamic Impulse を使い、
選択の更新が終わってから同じ更新内で再生・出力を計算する。
モジュール間の状態は Core の DynamicVariable で渡し、公開 API v2 のタグと既存変数名を維持する。
Dynamic Impulse は装着者のクライアントで実行され、所有者による処理制限も各入口で確認する。

## 不具合の調べ方

まず Core の変数を見て、入力・選択・再生のどこで期待とずれたかを分ける。
Inspector 上の実際の変数名には `Expr/` が付く。

| Core の変数 | 確認する内容 |
|---|---|
| `LeftGesture` / `RightGesture` | 各入力から届いた0〜7の状態 |
| `LeftInput` / `RightInput` | 最後にその手を設定した Command |
| `PairIndex` | 左×8＋右で求めた対応表の番号 |
| `MappedExpression` | その行に割り当てられた Catalog の表情 |
| `Override` / `CandidateExpression` | 直接指定と、検証前の選択候補 |
| `SelectionStatus` | 0=未割当、1=対応表、2=直接指定、3=無効またはアセット未ロード |
| `CurrentExpression` | 実際に再生している表情 |
| `PlaybackStart` / `PlaybackElapsed` | 再生開始時刻と経過秒数 |
| `FadeDuration` / `FadeWeight` | フェード秒数と現在の混合率（0〜1） |

1. 左右の番号が違う場合は、`API/Receivers/Logic/Gesture` と該当入力の `Logic` を調べる。
2. 番号が正しく表情が違う場合は、`PairIndex` に対応する `GestureTable` の参照と `Override` を確認し、`Selection` を調べる。
3. `SelectionStatus=3` の場合は、候補の `Enabled` と `StaticAnimationProvider` のアセット読み込みを確認する。
4. 選択が正しく見た目が違う場合は、`Playback` と該当する `Outputs` レコードの `Base`、`TrackingWeight`、`Snapshot`、`Result`、`Target` を調べる。

これらの状態は読み取り用の診断情報として扱い、再生結果を変えたい場合は公開 API、対応表、Catalog を編集する。
変換時の警告は引き続き `Diagnostics` に残る。
`Diagnostics/Graph modules` の各レコードには `Expr/Path` と `Expr/NodeCount` があり、モジュールの場所と規模を確認できる。
`PlaybackElapsed` と `FadeWeight` はローカルに駆動する表示値で、診断のための毎フレームの同期書き込みを増やさない。
これらは時計から求める値であり、Playback の処理が実行されたことを示すカウンターではない。
反映が止まっている場合は、アバターの装着状態、該当モジュールの有効状態と `Outputs/Result`・`Target` を確認する。


resoloop を使う場合は、リポジトリ直下から次の読み取り専用スクリプトで Core の状態を一覧にできる。
`-CoreSlot` には対象の正確なパスまたは現在のスロット ID、
`-Url` には ResoniteLink に表示される現在のポートを指定する。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/inspect-expression.ps1 -CoreSlot "Root/Plum/Expressions/Core" -Url "ws://localhost:42038"
```

`-Json` を付けると機械可読の JSON を返す。
URL を省略した場合は resoloop の環境変数・プロジェクト設定を使う。
参照値は現在の ResoniteLink 接続での ID として表示するため、保存後の固定 ID として使わない。
旧パッケージも読み取れるが、追加した診断項目は再変換・再インポート後に表示される。


## 入力の動作

ジェスチャー番号は次の対応になる。
各メニュー項目の Command はその手とジェスチャー番号を固定値として持つ。
押すたびに Command を書き換えるのではなく、その値をイベントの引数として Core に渡す。
動作確認では `Core/LeftGesture`、`RightGesture`、`CurrentExpression` と対応表の参照先を見る。

| 番号 | 状態 |
|---|---|
| 0 | Neutral |
| 1 | Fist |
| 2 | HandOpen |
| 3 | FingerPoint |
| 4 | Victory |
| 5 | RockNRoll |
| 6 | HandGun |
| 7 | ThumbsUp |

同じ手には最後に届いたイベントを採用し、もう一方の手は変更しない。
キーボードとメニューの指定はラッチする。キーやボタンを離しても戻らず、Neutral を選ぶと0に戻る。
物理入力は安定したジェスチャーが変化したときと接続時にだけ送る。
したがって、手を動かさずにキーボードで設定した状態は維持され、次に物理ジェスチャーが変わると更新される。

- コンテキストメニュー: `Left hand` / `Right hand` に各8項目。
- キーボード: 左手は Ctrl+Alt+1〜8、右手は Ctrl+Alt+Shift+1〜8。数字1が Neutral、8が ThumbsUp。
  `Bindings` の `Key`、`Shift`、`Enabled` を編集できる。
- コントローラー: 不要な `Modules/Touch|Index|Vive|WindowsMR` を削除できる。
  そのモジュールが最後に設定した手だけを0へ戻す。後から別入力が設定した状態は保持する。
  Grip/Trigger の押し込み・解放しきい値と安定待ち時間を機種ごとに編集できる。
  指を個別に取得できない機種の Victory/Rock はボタン操作から判定する。

直接表情を選ぶ `Select expression` は、左右の状態を保持したまま Override する。
`Return to gestures` で現在の左右状態による選択へ戻る。
インポートした ExpressionMenu は、単独のパラメーターで表情を確定できる項目をこの直接選択へ変換する。
元の Button/Toggle の押下期間・パラメーター保存・トグル解除は再現せず、どちらも直接選択としてラッチする。

## 対応表と表情の編集

`GestureTable` の各スロットには `Expr/Pair.N` という DynamicReferenceVariable<Slot> がある。
N は左×8＋右。例えば左1・右2は `Pair.10`。
参照先を `Catalog` の表情スロットへ変更するだけで割り当てを編集できる。
左右の組み合わせごとにアニメーションを複製せず、同じ表情は同じ Catalog エントリーを参照する。

表は変数名で引くので、スロットの並び替え・表示名の変更・別の行の削除で対応がずれることはない。
変数名は固定し、同じ名前を重複させない。削除した行を戻す場合は同じ変数名で再作成する。
行が未設定、参照先が削除・無効化、またはアセットが未ロードなら Outputs のベース入力を使う。
表の参照を編集すると次の更新で反映される。異なる行でも同じ表情を指す場合は再生を継続する。

表情の追加は `API/Templates` または既存の Catalog エントリーを Catalog へ複製し、
`Enabled` を有効にして `Id`、`DisplayName`、`Clip`、`Duration`、`Loop` を設定する。
`FadeIn` / `FadeOut` は切り替え秒数。削除は表情スロットごと行える。
テンプレートから複製したメニューの表示名・有効状態・参照先は複製先に追従する。

AnimX のトラックは Node=`Expression`、Property=出力の `Id` を使う。
既存の出力を使う表情追加では Flux の配線は不要。
新しい BlendShape を操作する場合は Outputs の出力レコードとフィールド接続も必要になる。
`Bindings` は編集時の参照情報であり、AnimX のトラックを自動で書き換えるものではない。

## 外部イベント API v2

アバター装着者のクライアントで `API/Receivers` を対象階層にして発火する。
旧 v1 の SourceState / Sequence / Generation / lease プロトコルは廃止した。
Dynamic Impulse 自体はネットワーク RPC ではない。装着者が最終出力を計算して同期する構成で、
各クライアントが状態だけを受け取って独立再生する方式ではない。

| タグ | 引数 | 動作 |
|---|---|---|
| `ResoPon/Expression/v2/Gesture` | Slot | 指定した手の現在状態を変更 |
| `ResoPon/Expression/v2/Select` | Catalog の Slot | 直接表情を選択 |
| `ResoPon/Expression/v2/Automatic` | なし | Override を解除 |

Gesture の引数は `API/Examples` の Command を複製して使う。
Command は `Expr` の DynamicVariableSpace と次の3項目だけを持つ。

| 変数名 | 型 | 値 |
|---|---|---|
| `Expr/Hand` | int | 0=左、1=右 |
| `Expr/Gesture` | int | 0〜7 |
| `Expr/Available` | bool | 通常 true、切断通知は false |

`DynamicImpulseTriggerWithObject<Slot>` から Command を渡すと、
`DynamicImpulseReceiverWithObject<Slot>` が受け取る。
Command は入力が有効な間保持する。削除・無効化すると、それが最後に設定した手を0へ戻す。
異なる入力元には別の Command を使う。Available=false は同じ Command が最後に設定した手だけを解放する。
範囲外の手・ジェスチャーと装着者以外のクライアントからの実行は無視する。

## 変換時の判定と制限

`GesturePairCompiler` は左右64通りについて、すべての開始状態から遷移をたどる。
元の遷移順序を使い、行き着く状態が一意になる組み合わせだけを割り当てる。
履歴により結果が変わる、または循環する組み合わせは未設定として診断する。

元のレイヤー順と重み、Write Defaults による既定値を使い、選ばれたクリップを変換時に合成する。
同じ結果は再利用する。単一の動画、定数ポーズとの合成、時間・キー配置・補間が一致する動画の合成では
Hermite 曲線を保持する。固定の正の再生速度もキー時刻と接線へ反映する。
ベース値は変換時のアバターの値を用いる。
上位レイヤーの空 Motion や、その状態でアニメーションされない出力は下位レイヤーの値を引き継ぐ。
右手が Neutral のときに、左手の表情を既定値で上書きしない。

ジェスチャーレイヤーに併用されたメニューの int/bool Toggle パラメーターは、宣言された初期値へ固定して
対応表を生成する。例えば Plum の `FacialSet=0` は最初の表情セットになる。
使用した固定値は Diagnostics と変換ログへ記録し、実行時の表情セット切り替えは生成しない。
メニューにないパラメーターや連続値は推測で固定しない。
`GestureLeftWeight` / `GestureRightWeight` が Motion Time に使われる場合は、
8種類の離散入力向けに最大値1の位置を固定ポーズとして取り込む。握り込みの連続変化は再現しない。
元のアニメーションクリップは直接選択用として Catalog に残す。

Exit Time、再生オフセット、上記の固定化で扱えないパラメーター・GestureWeight 条件が必要なレイヤー、
履歴に依存する Write Defaults、解決できない出力は自動割り当ての対象外。
ループや長さが異なる動画の同時合成、合成できないキー配置も診断する。
対応する独立クリップは Catalog に残し、手動で割り当てられる。
パーサー側の制限も継続する。BlendTree、ネスト、AvatarMask、加算レイヤー、StateMachineBehaviour、
遷移中断、Puppet、Sub-Menu の開閉パラメーターは自動再現しない。
材質・物体・Transform のトラック、weighted tangent、イベントを含む Clip も対象外。

実行時は選ばれた表情を1つの時計で再生する。元 Animator の遷移時間、自己遷移による再開始、
レイヤーごとの独立した再生位相は再現しない。表情を切り替えると直前の出力からフェードする。
未ループのアニメーションは終端を保持する。

既存の瞬き・口パク等のドライバーは Outputs の `Base` に接続し直す。
表情にトラックがない出力は Base を使い、ある出力はアニメーション値を使う。
出力ごとの `TrackingWeight`（0〜1）で Base の混合率を調整できる。
複製・再ロード・装着者変更時には左右状態と直接選択を初期化する。
VRM の感情表情も同じ Catalog を使うが、VRChat の条件がないため対応表は未設定から始まる。

## 検証

`dotnet run --project tests/ExpressionSmoke -c Release` で、パーサー、64通りの合成、
履歴依存の除外、実 ProtoFlux の入力・再生・編集・モジュール削除・追跡との接続・
複製とパッケージ再読み込みを検証する。
既知の実アバターは `scripts/test-local-avatar.ps1` で変換と inspect を確認する。
物理コントローラー、デスクトップのキーフォーカス、メニュー操作、複数クライアントの動作確認は
Resonite クライアント上で別途必要。

2026-09-14 の検証では ExpressionSmoke と登録済み LilLeo の変換・inspect が成功した。
比較用の保存パッケージ（同じ3機種の入力を残したもの）の Flux は2,530から892ノードへ減少した。
LilLeo の元の左右レイヤーには従来から未対応の挙動・欠落 BlendShape があり、自動割り当てを省略する。
この実アバターテストは変換の回帰確認であり、その顔表情の完全な取り込みを確認したものではない。

2026-09-15: Plum v1.0.1 で左右の表情レイヤーが Motion Time と表情セット条件のために除外される問題を修正した。
保存パッケージを実エンジンへ読み込み、メニューの ButtonDynamicImpulseTrigger を Command 未変更のまま呼び出し、
左右64通り・102出力を検査して8種類の表情を確認した。
空／別プロパティだけを持つ上位レイヤーが下位の値を保持する挙動は、Unity 2022.3.22f1 の
SkinnedMeshRenderer と2レイヤーの Animator を使った再現でも確認した。

2026-09-15 のモジュール分割では、既存 Plum パッケージの1グループ・1,098ノードに対し、
再変換後は15グループ・合計1,174ノード、最大グループ106ノードとなった。
所有者や時刻の取得をモジュールごとに持たせるため総数は増えるが、API・Core・機種別左右の間で
FluxGroupを共有しない。Core は Lifecycle 59、Selection 101、Playback 60ノード。
この構造上の境界は、見出しや座標だけでなく実際の FluxGroup で検査している。

Plum の旧・新パッケージで29クリップの全 AnimX 内容、Catalog設定、出力binding・基準値・追跡混合率、
64通りの割り当てを比較し、一致を確認した。保存パッケージのメニューボタンを実際に発火する試験でも、
64通り・102出力・8種類の表情が成功した。
ExpressionSmoke はフェード、診断値のローカル駆動、入力拒否、所有者の離脱・再装着、
複製と保存再読み込みも検査する。PrefabInputSmoke、PrefabSceneSmoke、Plum の変換・inspect も成功した。

今回の変更は変換器の生成処理へ適用される。既存ワールド内の変換済みアバターへ反映するには、
新しい ResoPon で再変換したパッケージを再インポートする。
