# コントローラー別ジェスチャー判定

標準ProtoFluxでTouch、Index、Vive、WindowsMR、Cosmosの入力を判定し、
左右の `ExpressionSystem/Method.TrySetGesture.Left`・`Right` へintを送る。
番号は0=Neutral、1=Fist、2=HandOpen、3=FingerPoint、4=Victory、5=RockNRoll、6=HandGun、7=ThumbsUp。
Neutralは有効な入力であり、機器の受付外を表す-1とは区別する。指ボーンの姿勢は駆動しない。

設定の型と配置は[変数リファレンス](expression-variables.md#ハンドジェスチャー設定)、
入力後の選択・保持は[表情システム](expression-system.md)を参照。

## Touch

親指接触はButtonYB_Touch、ButtonXA_Touch、JoystickTouch、ThumbRestTouchのいずれかがtrueの状態。
人差し指はTriggerTouchとTriggerClickで3状態に分ける。ClickはTouch=falseでも優先する。
GripはGripClickを使い、アナログ量の独自しきい値は追加しない。

| 親指 | GripClick | 人差し指を離す | 触れるだけ | トリガーを引く |
|---|---|---|---|---|
| 離す | false | HandOpen (2) | Neutral (0) | Neutral (0) |
| 触れる | false | Victory (4) | Neutral (0) | RockNRoll (5) |
| 離す | true | HandGun (6) | ThumbsUp (7) | ThumbsUp (7) |
| 触れる | true | FingerPoint (3) | Fist (1) | Fist (1) |

ComposeBits_byteはBit0=ButtonYB_Touch、1=ButtonXA_Touch、2=ThumbRestTouch、3=GripClick、
4=JoystickTouch、5=TriggerTouch、6=TriggerClick。Bit7は未接続。
連続するコードは共通のbyte→int変換とIsBetween_Int（両端を含む）で判定する。

| 手形 | 一致コード・範囲 |
|---|---|
| Fist | 41〜47、56〜63、73〜79、88〜95、105〜111、120〜127 |
| HandOpen | 0 |
| FingerPoint | 9〜15、24〜31 |
| Victory | 1〜7、16〜23 |
| RockNRoll | 65〜71、80〜87、97〜103、112〜119 |
| HandGun | 8 |
| ThumbsUp | 40、72、104 |
| Neutral | 上記以外（32〜39、48〜55、64、96） |

7手形の一致結果のNORをNeutralとして、8行のIndexOfFirstValueMatch<bool>から0〜7を直接出力する。
同じ手形のまま親指の接触先だけが変わっても再送しない。
これはResonite向けの対応表であり、VRChatクライアントの入力アルゴリズムとの同一性は保証しない。

## Index

装着者のUserFingerPoseSourceから各指の近位関節Rotationを読み、Euler角に変換する。
IndexController.IsActiveで機種を限定する。指ごとにOpen・Closedを別々に判定し、
どちらにも当てはまらない角度を中間域とする。

| 指 | Open（初期設定） | 中間 | Closed |
|---|---|---|---|
| 人差し指〜小指 | X < 20 | 20 <= X < 40 | X >= 40 |
| 左親指 | Y > 45 | 25 < Y <= 45 | Y <= 25 |
| 右親指 | Y > -5 | -25 < Y <= -5 | Y <= -25 |

4指のOpen境界は `FingerThreshold - max(0, FingerNeutralRange)`。
親指は左右の符号を適用したThumbThresholdに `max(0, ThumbNeutralRange)` を足す。
幅0では中間域がなくなる。初期20度はResoPonの調整値で、VRChatのしきい値の再現値ではない。

ComposeBits_ushortのBit0/1、2/3、4/5、6/7、8/9に、人差し指・中指・薬指・小指・親指の
Closed/Openをそれぞれ接続する。両方falseなら中間状態。残りのビットは未使用。

| 手形 | 一致コード |
|---|---|
| Fist | 341 |
| HandOpen | 682 |
| FingerPoint | 342 |
| Victory | 346 |
| RockNRoll | 150、406、662 |
| HandGun | 598 |
| ThumbsUp | 597 |

RockNRollだけは親指を問わず、中間・Closed・Openの3コードを認める。
7手形のどれにも一致しなければNeutral。Touchと同じ8行セレクターを使う。
Neutralは表情番号の判定であり、SDKのIdleアニメーションや指の見た目を再生する機能ではない。

## Cosmos

ComposeBits_byteのBit0=JoystickTouch、1=GripClick、2=TriggerTouch、3=TriggerClickを使う。

| 手形 | 一致コード |
|---|---|
| Fist | 12 |
| HandOpen | 0 |
| FingerPoint | 3 |
| Victory | 1 |
| RockNRoll | なし |
| HandGun | 2 |
| ThumbsUp | 4 |

7行セレクターのRockNRollをfalse固定とし、Index+1で番号を得る。未一致はNeutralになる。
未定義の組み合わせを別の手形へ丸めない。

## Vive・WindowsMR

Touchpadの方向を `LegacyRoundToInt(atan2(pad.x, pad.y) * RadToDeg / 45) + 4` で分類し、
8を0へ折り返す。接触していなければNeutral、接触中の(0,0)は上方向として扱う。

| 設定 | 方向 | 初期番号 |
|---|---|---|
| Direction.0 | 下 | 0 |
| Direction.1 | 左下 | 1 |
| Direction.2 | 左 | 2 |
| Direction.3 | 左上 | 3 |
| Direction.4 | 上 | 4 |
| Direction.5 | 右上 | 5 |
| Direction.6 | 右 | 6 |
| Direction.7 | 右下 | 7 |

番号への割り当てはResoPonの設定であり、引用元の手形対応ではない。
左右のパッドを独立して読み、WindowsMRの原版にあるPrimaryHandへの統合は行わない。

## 入力の受付と送信

各機種・各手に独立した次のグラフを生成する。

1. ローカル着用、Controller.IsActive、装着者のUserVR_Active、該当する手の許可設定をANDで判定する。
2. 受付中は手形番号、受付外は-1を現在値とし、FireOnLocalValueChange<int>で変化を検出する。
3. 受付中の変化はStartAsyncTask → LocalImpulseTimeoutSeconds → DelayWithValueSecondsFloat<int>へ進む。
   TimeoutとDelayは同じStabilitySeconds（初期0.05秒）を読む。
4. Timeoutは最初の入力を通し、期限内の入力を破棄する。待ち行列も期限延長もなく、Resetは未接続。
5. 通過した番号と待機時間をDelayが保持する。待機後に入力が受付中で、保持した番号と現在値が同じなら送る。
6. 受付外への変化はTimeoutとDelayを通らず、-1を即送る。Timeoutの状態はリセットしない。

Bを受け付けた直後にCへ変えると、Cは破棄され、Bも完了時の不一致で送られない。
そのままCを保持しても期限到来による自動再送はなく、期限後の次の変化が必要。
B→A→Bなら最初の待機が一致して送られることがある。短時間の停止・再開でも同じ扱いになる。
設定変更は新たに通過する入力へ適用し、既存の待機時間・遮断期限は変えない。
0秒でも非同期Delayと完了時の一致確認は行う。

切断・VR終了で送る-1は、許可されていればTrySetGestureからSetGestureへ渡る。
対応表に該当行がなければ選択はnullになるが、メニュー保持中は適用を拒否する。
許可をfalseにした際の-1はTrySetGestureが拒否する。未装着時も左右値の更新自体は可能だが、
UpdateSelectionにはローカル着用条件がある。入力モジュールには装着開始用のOnStartを置かない。
モジュールの削除を通知する仕組みはないため、残った状態はResetなどで解除する。

## 出典とライセンス

判定の基礎はAvatar Expression Editor v1.12.1内のハンドサインアドオン
（Touch V1.4.3、Index V1.2.2、Vive V1.3.1-plusEye、Win MR V1.0.0、Cosmos V1.0.0）。
Touchの接触状態表とIndexの中間域は上記のResoPon仕様を使う。
AnalogFist・Strength、ViveのOptional.Eye、原版の表情ウェイト合成は移植しない。

配布通知は[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。生成物では
`Inputs/HandGestures/THIRD-PARTY-NOTICES` のCommentに説明を置き、その子に引用元の名称、
License、Commentを保存する。メニューを生成しない場合も通知を含める。
名称・本文・改行・License値は[埋め込みリソース](../src/VrmToResonitePackage/Resources/AvatarExpressionEditorLicense.json)をそのまま使う。
RequireCredit=false、CanExport=false、著作権行を維持し、InspectorのGizmoLinkは含めない。

## 検証と実装

[ExpressionGestureInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionGestureInputSetup.cs)が判定表を、
[ExpressionInputSetup.cs](../src/VrmToResonitePackage/Expressions/ExpressionInputSetup.cs)が受付・送信を生成する。
ExpressionInputEventChecksはセンサー出力を置き換え、Touch全128入力、Indexの2値姿勢と5指3状態、
Cosmos全16コード、Vive・MRの8方向を左右で照合する。境界、設定変更、Timeout中の破棄、
VR終了、切断・再接続、許可、左右独立性も検証する。
ライセンスとグラフ構造は生成・複製・保存再読込で確認する。
実機での操作感とVRChatでの見た目との比較はヘッドレステストの対象外。
