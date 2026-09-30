# コントローラー別ジェスチャー判定

## 調査元と適用範囲

2026-09-27にResoLoopで接続したワールド内の **Avatar Expression Editor v1.12.1** を調査した。
`BlendShapeInspector/Style/Main/Content/Vert/Element/Hori/ButtonE/Button/Pref` 内の
ハンドサイン表情アドオン（Touch V1.4.3、Index V1.2.2、Vive V1.3.1-plusEye、
Win MR V1.0.0、Cosmos V1.0.0）の `LogiX` と `Handsign` を参照した。
入力を組み立てるFluxだけでなく、`ValueEqualityDriver<byte>` と `MultiBoolConditionDriver`
による一致判定まで確認している。ノードの型・ポートは実行中Resonite 2026.9.18.82の
Reflection／Flux-SDKメタデータで確認し、ビルド・実行テストでも実DLLに対して検証する。
セッションID・一時的なResoniteLink ID・調査JSONはコミットしない。
著作権表示とMITライセンスは [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) を参照。

ResoPonのVersion 19から、従来の共通Grip／Triggerしきい値・ボタン優先判定を廃止する。
標準ProtoFluxだけで機種別入力を判定し、既存の左右int APIと64組のGestureTableへ渡す。
Touch／Index／Cosmosは完全一致する手形がなければNeutral (0)、一致コード0はHandOpen (2)。
Cosmosの原版にRockNRollは存在しない。未定義の組み合わせを別の手形へ丸めない。

## Touch・Index・Cosmos（旧TouchはVersion 51まで）

`ComposeBits_byte` の下位から順に次の値を接続する。

| 機種 | Bit0 | Bit1 | Bit2 | Bit3 | Bit4 |
|---|---|---|---|---|---|
| Touch | ButtonYB_Touch | ButtonXA_Touch | GripClick | JoystickTouch | TriggerClick |
| Index | 人差し指 | 中指 | 薬指 | 小指 | 親指 |
| Cosmos | JoystickTouch | GripClick | TriggerTouch | TriggerClick | 未使用 |

TouchのA/B/X/Yは接触を使い、押下優先の上書きは行わない。GripClick／TriggerClickの閾値は
ResoniteのControllerノードに委ね、ResoPonで0.55/0.45の二重判定を加えない。

Indexは装着者の `UserFingerPoseSource` → `FingerPose` の各 `_Proximal` のRotationを
`EulerAngles_floatQ` で角度にする。人差し指〜小指は `X >= FingerThreshold`（既定40度）、
親指は左が `Y <= ThumbThreshold`（25度）、右が `Y <= -ThumbThreshold`（-25度）。
これは原版の接続方向をそのまま移したもので、親指だけ左右でしきい値の符号が変わる。
IndexControllerのIsActiveで入力機種を限定する。

| ジェスチャー | Touchの一致コード | Indexの一致コード | Cosmosの一致コード |
|---|---|---|---|
| Fist (1) | 28, 22, 21, 23 | 31 | 12 |
| HandOpen (2) | 0 | 0 | 0 |
| FingerPoint (3) | 5, 6, 12, 7 | 30 | 3 |
| Victory (4) | 2, 8, 1, 3 | 28 | 1 |
| RockNRoll (5) | 17, 18, 24 | 6, 22 | なし |
| HandGun (6) | 4 | 14 | 2 |
| ThumbsUp (7) | 20 | 15 | 4 |

原版の各条件は互いに排他的。Version 47では各手形ごとに一致判定をまとめる。
複数コードの手形は `ComposeBits_byte` を `IndexOfFirstValueMatch<byte>.Match` に接続し、
`Values` にその手形のコードだけを並べる。単一コードは `ValueEquals<byte>` で比較する。
各判定ノードのSlot名には `Fist (1)` などの手形名と番号を付ける。

一致結果を `IndexOfFirstValueMatch<bool>`（Match=true）の7行へジェスチャー1〜7の順に接続し、
Index+1を出力する。CosmosのRockNRoll（5）の行はfalse固定とし、HandGun（6）・ThumbsUp（7）の番号を維持する。
FoundMatch=falseならNeutral（0）。Vive／Windows MRの方向判定はこの構成の対象外。

2026-09-29にresoloopで現在のワールドの
`Kipfel/Expressions/Inputs/HandGestures/Modules/Touch/Left/Logic` を読み取り、上記の手形別グループ構成を参照した。
参考側ではRockNRollのFoundMatchがbool一覧に重複接続されていたため、生成側は各手形1行の7行に揃える。
Touchの複数コード群はFist=28/22/21/23、FingerPoint=5/6/12/7、Victory=2/8/1/3、RockNRoll=17/18/24。
Touchの64〜73は原版のデスクトップ入力符号であり、VRセンサー判定には含めない。
キーボードはResoPonの左右別入力を継続する。

## Touchの接触状態による判定（Version 52）

VRC SDKのIdle／Neutral調査を受け、TouchだけをResonite向けの接触判定へ変更する。
これはVRCクライアントの厳密な入力アルゴリズムの移植ではなく、下記の明示した対応表である。
実機でのVRC同値性・ポーズの見た目は未検証。指ボーンの駆動は追加しない。

親指接触はButtonYB_Touch、ButtonXA_Touch、JoystickTouch、ThumbRestTouchのOR。
複数箇所への同時接触も1つの親指状態として扱う。4入力は1つのOR_Multi_Boolへ接続する。
ExpressionFlux.Orは3入力以上をOR_Multi_Bool、2入力をOR_Boolとして生成する。
人差し指はTriggerTouch／TriggerClickから離す・触れるだけ・引くの3状態に分ける。
TriggerClick=trueならTriggerTouch=falseでも「引く」を優先する。
GripはGripClickを使い、今回アナログ量の独自しきい値は追加しない。

| 親指 | GripClick | 人差し指を離す | 触れるだけ | トリガーを引く |
|---|---|---|---|---|
| 離す | false | HandOpen (2) | Neutral (0) | Neutral (0) |
| 触れる | false | Victory (4) | Neutral (0) | RockNRoll (5) |
| 離す | true | HandGun (6) | ThumbsUp (7) | ThumbsUp (7) |
| 触れる | true | FingerPoint (3) | Fist (1) | Fist (1) |

FluxではComposeBits_byteのBit0=親指接触、Bit1=GripClick、Bit2=TriggerTouch、
Bit3=TriggerClickとする。1〜7の手形は従来と同じ7行のグループで判定する。
新しい正規化コードはFist=7/11/15、HandOpen=0、FingerPoint=3、Victory=1、
RockNRoll=9/13、HandGun=2、ThumbsUp=6/10/14。
NeutralはTriggerTouch AND !TriggerClick AND !GripClickを名前付きノードで明示判定して優先する。
その他未一致も0となる（正規化コード8/12の、Gripなし・親指を離してトリガーだけ引く状態）。

Neutralは有効な表情入力であり、GestureTableの0行を選ぶ。VR終了・切断の-1と区別する。
同じ手形のまま親指の接触先が変わっても再送しない。
遅延・Timeout・入力許可・VRモード判定はVersion 51の共通処理を使う。
そのためTimeout中に発生したNeutral等の変更も破棄され、自動的な末尾再送は行わない。

検証では7つのセンサー（4つの親指接触、GripClick、TriggerTouch、TriggerClick）の
全128通りを左右で実行する。加えてVictory→接触のみのNeutral→Victory、
全接触解除のHandOpen、Grip＋接触のFist／ThumbsUp、Neutral表情の対応表選択、
同じ手形内の接触先変更による再送抑止を確認する。

## Vive・Windows MR

原版は手形を推測せず、Touchpadの方向を8出力へ分配している。
`round(atan2(pad.x, pad.y) * RadToDeg / 45) + 4` を使い、TouchpadTouchがfalseなら解除する。
丸めには原版と同じ `LegacyRoundToInt_Float` を使い、下方向の+180度側（8）を0へ折り返す。

原版の方向出力にVRChatジェスチャー番号はないため、ResoPonでは次の編集可能な対応表を置く。
これはResoPonでの割り当てであり、原版のジェスチャー対応表ではない。

| 設定 | 方向 | 初期ジェスチャー |
|---|---|---|
| Direction.0 | 下 | Neutral (0) |
| Direction.1 | 左下 | Fist (1) |
| Direction.2 | 左 | HandOpen (2) |
| Direction.3 | 左上 | FingerPoint (3) |
| Direction.4 | 上 | Victory (4) |
| Direction.5 | 右上 | RockNRoll (5) |
| Direction.6 | 右 | HandGun (6) |
| Direction.7 | 右下 | ThumbsUp (7) |

設定は各機種のDVにあるintで、0〜7を指定する。接触中の座標(0,0)は原版同様に上方向として扱う。
MR原版はPrimaryHand優先・反対の手へのフォールバックで1つの出力へ統合するが、
ResoPonは左右64組の表情選択を維持するため、各手のTouchpadを独立して読む。
タッチ解除はNeutral。Version 51では機器切断・VRモード終了時に-1を即送信する。

## 共通の実行制御と移植しない出力

Version 51は、2026-09-30にresoloopで読み取った、yoshi1123_が着用しているPC_Akaneの
`Expressions/Inputs/HandGestures/Modules/Touch/Left/` の最新グラフを共通の手入力生成へ反映する。
Version 50の遅延・Timeoutに加え、`UserVR_Active`と入力受付外の即時送信を左右・5機種へ適用する。
実ワールドは読み取りのみ。調査時点のTouchはLeft側に実装されており、生成側では右手にも同じ制御を作る。
C#の`ExpressionFlux.Node`からFrooxEngineの標準コンポーネントを生成・接続し、
ProtoGraphやFlux-SDKによるビルド・配布物は使わない。

- 入力受付はローカル着用・コントローラーIsActive・着用者のUserVR_Active・AllowHandGesturesのAND。
  UserVR_ActiveとControllerのUserは同じGetActiveUserSelfを参照する。
  実DLLのUserVR_ActiveはUser.VR_Activeを読み、Userがnullならfalseを返す。
- 現在値は受付中の手形番号、受付外は-1。`FireOnLocalValueChange<int>`で変化を検出する。
- 受付外へ変わった場合はIf.OnFalseから同じ送信ノードへ直接進み、現在値-1を即送信する。
  Timeout・Delayを通らず、それらの状態もリセットしない。機器切断・VRモード終了ならAPIが受理し、
  対応するGestureTable行がなければBaseへ戻る。0（Neutral）とは別の値。
  AllowHandGestures=falseや未着用時にはAPIが拒否するため、送信だけで禁止中の表情を変更しない。
  受付外のままなら現在値は-1で変わらず、毎フレームの送信や他の手・キーボードへの上書きは起こさない。
- 受付中の変化から`StartAsyncTask`→`LocalImpulseTimeoutSeconds.Trigger`→
  `DelayWithValueSecondsFloat<int>`へ進む。TimeoutとDurationは同じStabilitySeconds（既定0.05秒）を参照する。
- Timeoutは最初のインパルスを通し、その時点から指定秒数の入力を破棄する。
  破棄された入力は待ち行列に入らず、期限も延長しない。Resetは未接続で、入力停止・再開でも解除しない。
- Timeoutを通過したときだけDelayが番号と待機時間を取り込む。待機後、取り込んだ番号が
  現在値と一致したらその手のint APIへ送信する。生成側では完了時にも入力受付を明示確認する。
  左右・5機種はそれぞれ独立したTimeoutとDelayを持つ。
- Candidate／StableのStore、初期化フラグ、ElapsedTimeFloatは不要。状態DVも追加しない。
  未一致のIndex=-1を+1すればNeutral=0なので、手形選択のFoundMatch分岐も省略する。

Version 49では全変更が独立した待機を開始したが、Version 50ではTimeoutを通過した変更だけが待機する。
たとえばBを受け付けて待機中にCへ変えるとCの入力は破棄され、Bの待機も現在値と不一致になり送信しない。
そのままCを保持しても期限到来による自動送信はなく、期限後の次の入力変更から再び受け付ける。
B→A→Bのように戻れば最初のBは一致して送信するが、破棄された後のBは追加送信を起こさない。
入力停止時は現在値=-1となり待機中の送信を拒否するが、完了前に同じ手形で入力を再開すれば
以前の待機が一致する場合がある。短時間で再開してもTimeoutはリセットされない。
StabilitySecondsの変更は次に受け付ける入力から適用され、既存の遮断期限・待機時間は変わらない。
0秒設定ではTimeoutを通過でき、Delayによる非同期実行と完了時の一致確認を行う。

実Resonite 2026.9.18.82のDLLでDelayの値保持・Durationの評価と、Timeoutのローカルな
遮断期限（WorldTime基準）、通過時のみの期限更新を確認した。参照グラフの手形判定は変更されていない。
Redprintの梱包・表示用コンポーネントは生成しない。
原版にある近くのユーザーやホストへの代替は使わず、ResoPonの装着者に限定する。

今回の対象はジェスチャーによる離散的な表情選択。
原版のAnalogFist／Strength、ViveのOptional.Eye、表情ウェイトのSmoothLerpは、
既存のResoPonの最終ポーズ選択・SmoothValue・独立した瞬き出力へ直接移植しない。
Standard Controller V1.0も調査済みだが、Strength・Secondary・Grabの3つのウェイトを直接出力し、
0〜7の手形判定は持たないため、自動ジェスチャーモジュールとしては追加しない。
手形へ変換できる判定を持つ5機種を生成する。

## 検証

`ExpressionInputEventChecks` は生成された実Fluxのセンサー出力だけを置き換える。
左右ごとにTouchの全128入力をVersion 52の接触状態表、Indexの全32指姿勢、Cosmosの全16コード、
Vive／MRの全8方向を従来の観測表と照合する（基本384ケース）。さらに角度しきい値の両側、
方向境界と下向きの継ぎ目、設定変更、入力禁止／再許可、切断／再接続、安定待ち、
手を止めた際の手動入力保持、反対の手へ干渉しないことを検証する。
Version 50では初回Neutral、設定変更時の既存期限の維持、待機中の候補破棄・入力禁止、期限後の再受付、短時間での再許可、再装着と0秒設定を検証する。
Version 51ではさらに、デスクトップ中のセンサー無視、VR終了・機器切断時の即時-1、
VR復帰時の遅延、待機中のVR終了、禁止中のAPI拒否、受付外での単発送信と反対の手の維持を検証する。
既存のExpressionSmokeでクローン・保存再読込・瞬き・口パクとの共存も検証する。
模擬センサーによる単一ユーザー検証であり、実機を装着した操作や複数ユーザーの確認は含まない。

2026-09-27の実アバター回帰ではMarycia 2Pを変換・inspectし、28 Catalog・132出力・64/64割り当てを確認した。
保存済みパッケージのメニューを通して全64組・14種類の表情と132出力の値を照合した。

## 過去の比較記録（Version 48）

以下は旧方式との同値性を検討した記録。Version 49は上記の遅延照合方式を採用し、重複抑止の動作は引き継がない。

2026-09-30にresoloopでユーザー作成のTouch/Rightの簡略版を読み取り、Version 48と比較した。
取得した配線からTouchの全32コードと右手への送信値を照合し、判定部分の一致を確認した。
実Resonite 2026.9.18.82のDLLでも、IndexOfFirstValueMatchの未一致Indexが-1であること、
FireOnLocalValueChangeが前回評価値と異なる場合だけ発火し、初回値では発火しないことを確認した。
ワールド側の入力操作や変更は行っていない。以下は配線とノード仕様からの比較結果。

- IndexOfFirstValueMatch<bool>のIndex+1は、未一致でも0となる。7行が手形1〜7の順なら、FoundMatchによる0への分岐を省略できる。
- Candidateとの一致は「待機中に候補が変わっていない」ことしか表さず、「前回送信値と異なる」ことは表さない。
  最後の送信値Stableを削除すると、Aを送信した後に別の手形が安定時間未満だけ現れ、Aへ戻って安定した際にもAを再送する。
  間にキーボードで選んだ表情もこの再送で上書きされる。既存版はStable=Aとして再送を抑える。
- ElapsedTimeFloat >= StabilitySecondsのbool変更だけを送信契機にすると、StabilitySeconds=0ではReset前後ともtrueで発火しない。
  既存版の0秒即時送信と同じにするには、候補変更・入力再開の経路に0以下での送信を残す必要がある。
- Stableを持たない構成では、手形を止めたままStabilitySecondsを経過時間より大きくしてから再度満了させる場合も同じ手形を再送する。
  既存版は設定変更によるタイマー判定の再成立時にも最後の送信値を比較する。

通常の正の待ち時間で候補変更時にResetし、入力許可・ローカル着用・機器IsActiveで送信を制限する構成は維持できる。
上記の境界条件を維持するには、候補の記憶と最後の送信値の記憶を区別して扱う。

### Candidateの更新をタイマー満了で制限した場合

同日の再確認では、候補変更時のSequenceが「ElapsedTime >= StabilitySecondsならCandidateへ書き込み、その後は常にReset」という構成へ変更されていた。
全32コードの判定と送信接続は引き続き一致する。次の結果は取得した配線のオフライン時系列確認であり、実機入力の再現テストではない。

- AからBへ変更して安定させる場合はBを送信できる。
- Aから一瞬BになってAへ戻ると、CandidateがBのままなので送信を抑止できる。
- 一方、AからBへ変更した20ms後にCへ変え、そのまま50ms以上保持した場合も、CandidateがBのままになる。
  満了時の「現在の手形C == Candidate B」がfalseなので、Cを送信できない。満了時にCandidateを更新する経路もない。
- 計測開始後50ms未満で入力を停止・再開した場合は、停止時のCandidate=-1を上書きできず、再開後に同じ手形を保持しても送信できない。

Candidateは毎回の候補変更で更新し、最後の送信値Stableとは分ける必要がある。
重複抑止はCandidateの更新を止めるのではなく、確定後の送信直前にStableとの比較で行う。

### 書き込み成功後のResetとbool変更からの直接送信

同日の次の修正では、CandidateのValueWrite.OnWrittenからElapsedTimeFloat.Resetへ接続され、
送信側は「時間満了 AND 入力受付 AND 候補一致」のFireOnLocalValueChange<bool>からTriggerへ直接接続された。
手形判定の全32コード・ビット順・右手Tagを再照合した。以下は最新配線とオフライン時系列の確認結果。

- Candidateの書き込みを時間満了で制限するIfは残っている。AからBへ変えた20ms後にCへ変えると、
  CandidateはBのまま、タイマーはBへの変更時点から進む。50msを過ぎてもCと一致せず、Cを送信できない。
  ResetをOnWrittenへ移しても、待機中の候補を記憶しない問題は解消しない。
- FireOnLocalValueChange<bool>はfalseへの変化でも発火する。Triggerへの直接接続では、
  候補変更・タイマーReset・入力停止などによる条件不成立時にも、保持中のCandidateを送信する経路になる。
  送信前にIfで現在の送信条件がtrueかを確認する必要がある。送信時の値はイベント実行順にも依存する。

候補変更時は毎回Candidateを更新してResetし、満了時には受付条件と最後の送信値との差を確認して送信する。
送信後だけStableを更新することで、短い揺れの再送抑制と新しい手形の確定を両立する。

## VRC SDKのIdle／NeutralとTouchでの再現案（2026-09-30調査）

この節はVersion 51時点の調査と設計案の記録。調査時には生成コード・ワールドの配線を変更していない。
その後、表情入力の提案を上記Version 52として実装した。見た目の指ポーズは提案のまま。
実行中のUnity 2022.3.22f1プロジェクトから、com.vrchat.avatars / com.vrchat.base 3.10.5を確認した。
以下のパスはcom.vrchat.avatarsの `Samples/AV3 Demo Assets/Animation/` からの相対パス。
環境固有のプロジェクトパスや抽出物はコミットしない。

### SDKで確認した定義と形

- `Controllers/vrc_AvatarV3HandsLayer.controller` の実際のLeft Hand／Right Handステートマシンを辿ると、
  GestureLeft／Right == 0はIdle、1はFist、2はOpenへ遷移する。
  Idleは `ProxyAnim/proxy_hands_idle.anim`、Openは `proxy_hands_open.anim` を参照する。
  同ファイルには古い未参照の状態も残るため、名前検索だけで全Idle状態を採用しない。
- `vrc_AvatarV3HandsLayer2.controller` の左右0はIdle2で、
  `proxy_hands_idle2.anim` を参照する。通常のIdleは静止ポーズ（StopTime=0）、
  Idle2は5.5秒のループで、指カーブに小さな変動がある。
- [Animator Parameters](https://creators.vrchat.com/avatars/animator-parameters/)のNeutral=0、
  HandOpen=2とも一致する。「Idle」と「Neutral」はここでは同じ番号0の状態を指す。
  [デスクトップ操作](https://docs.vrchat.com/docs/keyboard-and-mouse)のShift+F1もIdleである。
- SDKクリップの指はHumanoid muscleのStretched／Spreadで定義されている。
  IdleはOpenより曲がり、Fistほど握り込んでいない脱力した形と読める。
  親指や各関節の値は均一ではなく、全指へ一律の曲げ率を設定する形ではない。
  以下は左人差し指の実値であり、角度や0〜1のGrip値ではない。

| 左人差し指のmuscle | Idle | Open | Fist |
|---|---:|---:|---:|
| 1 Stretched | 0.25111935 | 0.51009136 | -0.33480328 |
| 2 Stretched | 0.111410186 | 0.51150346 | -0.30036262 |
| 3 Stretched | 0.30399844 | 0.820959 | -0.13991955 |

これらはSDKに含まれる標準サンプルの形。実アバターのカスタムGesture、
トラッキング、クライアントの入力処理まで同じ姿勢になるという証拠ではない。
特にこのSDKのAnimatorとクリップからは、Touchの接触・押下をGesture番号へ変換する
クライアント側のしきい値・優先順位は確定できない。

### Version 51までのResoPonが区別できない入力

Version 51までのTouch判定は5ビットの完全一致であり、TriggerTouch・ThumbRestTouch・アナログ量を使わない。
Neutral=0は明示した脱力形ではなく、既知の7手形に一致しないコードの戻り値である。
Neutralになるコードは9,10,11,13,14,15,16,19,25,26,27,29,30,31。

たとえばJoystickTouchだけがtrueならコード8でVictoryとなる。
この状態で人差し指をトリガーへ触れさせても、引かなければ同じコード8なのでVictoryのまま。
「人差し指を離している」と「触れて休めている」を区別できない。
5ビットがすべてfalseなら、TriggerTouch／ThumbRestTouchがtrueでもコード0でHandOpenとなる。

[公式Touch操作表](https://docs.vrchat.com/docs/touch)では指をトリガーから離す条件と引く条件を区別しているが、
Neutralの厳密な判定表は載っていない。
また[GestureWeightの説明](https://creators.vrchat.com/avatars/animator-parameters/#footnotes)には
FistでGesture=1のままトリガー量が0になり得る例がある。
TriggerClickだけでVRCのGesture／GestureWeightを完全再現できるとは扱わない。

### 表情入力としての提案

Touchの入力を次の状態へ正規化してから手形を選ぶ。

- 親指：ButtonXA_Touch OR ButtonYB_Touch OR JoystickTouch OR ThumbRestTouch。
- 人差し指：離している／TriggerTouchだけ／トリガーを引いている、の3状態。
  Clickを使う初期案なら「離している」は!TriggerTouch AND !TriggerClick、
  「触れているだけ」はTriggerTouch AND !TriggerClickとする。
- 中指側：GripClick、または実機で較正するGripの押し込み量。
  これは薬指・小指まで含めた実指の計測ではなく、コントローラー入力からの推定。

Neutralの実装候補は「親指を置き、人差し指は触れるだけで、GripもTriggerも押し込まない」状態。
既存のVictory等へ進む前にこの条件を明示判定する。
HandOpenは指を離す条件を使い、Neutralと分ける。
これはResonite向けの仮説であり、VRCクライアントの厳密な条件として確定していない。
Gripを握ったままTriggerに触れる場合などはFist／Weightとの照合が必要であり、
TriggerTouch=trueの全状態をNeutralへまとめない。

外部APIの番号は0=Neutral、1〜7=既存手形、-1=VR終了等の入力無効を維持する。
0はGestureTableのL0Rn／LnR0を選ぶ有効な入力であり、表情解除やBaseと常に同義ではない。
-1は既定の0〜7表の範囲外であるため、対応行がなければBaseへ戻る。
[Gesture Toggle](https://docs.vrchat.com/docs/gesture-toggle)の無効化も別の操作で、
VRC AV3は最後のGesture値を保持する。これをNeutralへの変更と混同しない。

### 見た目の指ポーズとしての提案

Resonite 2026.9.18.82の実DLLで次を確認した。

- `FingerPosePreset.PresetPose = Idle` は `FingerPosePresets.Idle` を返す。
  Resonite標準の脱力ポーズを手早く表示する候補だが、VRC標準Idleとの同一性は未検証。
- `FingerPoseMultiplexer` はSources／IndexでIFingerPoseSourceComponentを選び、
  InterpolationSpeedで補間する。`HandPoser.PoseSource` が指ポーズを受け取る。
  左右独立に選べる構成とし、使用中の指トラッキングとの切替を設ける。
- VRCの形を厳密に再現するなら、対象Humanoid AvatarでSDKクリップを評価し、
  得られた指ボーン回転をResoniteの手首基準・指の基準軸へ変換する必要がある。
  Unity muscle値をEuler角としてコピーしたり、全アバター共通の固定角度へ直接変換しない。
  手の見た目を駆動する経路と、表情を選ぶ0〜7の経路を分ける。

このプロジェクトで先に行うべきなのは表情入力の接触判定の改善。
手の見た目まで変更する場合は、ポーズ出力の追加を別の検証項目にする。
実機確認では同じ指の置き方についてVRCのGestureLeft／Right・Weightと、
Resoniteの各Touch／Click／アナログ値を照合する。
離す→触れる→浅く引く→深く引く、親指をスティック／ボタン／レストへ置く、
左右、Gripの有無を含め、Neutral／Open／Victory／Fistの境界を確定してから採用する。

検証済み：SDKの到達可能な左右状態とGUID参照、Idle/Open/Fistのカーブ値、
Idle2のループ時間、Version 51のTouch全32コードの割り当て、上記Resonite実DLLの型・メンバー。
未検証：VRChatクライアントの実機Gesture値、Resonite実機センサー値、ポーズの見た目の一致。
