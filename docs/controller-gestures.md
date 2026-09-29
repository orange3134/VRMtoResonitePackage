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

## Touch・Index・Cosmos

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
タッチ解除はNeutral。機器切断は最後に受理した値を保持する。

## 共通の実行制御と移植しない出力

各手の判定値は既存のStabilitySeconds（既定0.05秒）を満たしてから送信する。
装着者のローカル実行だけが左右状態を更新する。非装着・入力禁止・機器停止では送信しない。
Version 48ではCandidateと最後の送信値Stableを各手のStore<int>へ移し、状態DVと手ごとの変数空間を廃止する。
候補変更時にElapsedTimeFloatをResetし、経過時間がStabilitySeconds以上になれば確定する。Since変数は持たない。
Stableは重複送信の抑制に必要。一瞬別の候補を経由して元の手形に戻っても、手動で選んだ表情を上書きしない。
装着中の入力禁止・機器停止ではStoreを-1へ戻す。非装着では初期化フラグを解除し、次の装着時にStoreを初期化する。
入力再開時は現在の候補でタイマーをリセットして改めて確定する。0秒設定は即時送信する。
ElapsedTimeFloatはResonite 2026.9.18.82の実DLLでResetとSyncTime Proxyを確認済み。内部の開始時計は同期される。
原版にある近くのユーザーやホストへの代替は使わず、ResoPonの装着者に限定する。

今回の対象はジェスチャーによる離散的な表情選択。
原版のAnalogFist／Strength、ViveのOptional.Eye、表情ウェイトのSmoothLerpは、
既存のResoPonの最終ポーズ選択・SmoothValue・独立した瞬き出力へ直接移植しない。
Standard Controller V1.0も調査済みだが、Strength・Secondary・Grabの3つのウェイトを直接出力し、
0〜7の手形判定は持たないため、自動ジェスチャーモジュールとしては追加しない。
手形へ変換できる判定を持つ5機種を生成する。

## 検証

`ExpressionInputEventChecks` は生成された実Fluxのセンサー出力だけを置き換える。
左右ごとにTouchの全32コード、Indexの全32指姿勢、Cosmosの全16コード、
Vive／MRの全8方向を観測表と照合する（基本192ケース）。さらに角度しきい値の両側、
方向境界と下向きの継ぎ目、設定変更、入力禁止／再許可、切断／再接続、安定待ち、
手を止めた際の手動入力保持、反対の手へ干渉しないことを検証する。
Version 48では計測満了後の新候補、待機中の候補変更、短い候補の揺れからの復帰、再装着後の同じ手形の再送も検証する。
既存のExpressionSmokeでクローン・保存再読込・瞬き・口パクとの共存も検証する。
模擬センサーによる単一ユーザー検証であり、実機を装着した操作や複数ユーザーの確認は含まない。

2026-09-27の実アバター回帰ではMarycia 2Pを変換・inspectし、28 Catalog・132出力・64/64割り当てを確認した。
保存済みパッケージのメニューを通して全64組・14種類の表情と132出力の値を照合した。

## 手入力グラフを簡略化する際の同値条件

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
