# IndexのNeutral／Idle調査とProtoFlux案

2026-10-02に、起動中Unity 2022.3.22f1のプロジェクト内にある
`com.vrchat.avatars` / `com.vrchat.base` 3.10.5を読み取り調査した。
SDK、Unityプロジェクト、生成コード、実ワールドの変更は行っていない。
この資料はIndexモジュールの改修案であり、実装済みの対応表は
[コントローラー別ジェスチャー判定](controller-gestures.md)を参照。
環境固有のプロジェクトパス、抽出物、検証スクリプトはコミットしない。

## SDKのIdleとIndexの入力判定は別の定義

SDKの `Samples/AV3 Demo Assets/Animation/` を基準として次を確認した。

- `Controllers/vrc_AvatarV3HandsLayer.controller` の実際の左右Handレイヤーから
  子ステートとAny State遷移を辿ると、GestureLeft／Right == 0はIdleで、
  `ProxyAnim/proxy_hands_idle.anim` を参照する。2はOpenで `proxy_hands_open.anim` を参照する。
- `vrc_AvatarV3HandsLayer2.controller` の0はIdle2で `proxy_hands_idle2.anim` を参照する。
  IdleはStopTime=0の静止形、Idle2はStopTime=5.5秒のループである。
- [公式Animator Parameters](https://creators.vrchat.com/avatars/animator-parameters/)でも
  0はNeutral、2はHandOpen。ここでIdleとNeutralは同じ番号0を指す。
- [公式Index操作](https://docs.vrchat.com/docs/valve-index)は実指姿勢を標準7手形へ照合すると説明し、
  各指のup／down条件を掲載している。Neutral専用の姿勢・数値しきい値は掲載していない。
  SDKのC#／JSONからもIndexの認識しきい値やNeutralの判定アルゴリズムは確認できなかった。
  SDKクリップは「番号を受け取った後のポーズ」であり、逆方向の判定条件を定義しない。

通常のIdleクリップは、4指をOpenほど伸ばさずFistほど曲げない形と読める。
ただしmuscle値は角度でもセンサーのcurl値でもなく、左右差・関節差がある。
例えば第1関節のStretchedの実値は次の通り。

| muscle | Idle | Open | Fist |
|---|---:|---:|---:|
| 左人差し指 | 0.25111935 | 0.51009136 | -0.33480328 |
| 左中指 | 0.06274103 | 0.44501653 | -0.476557 |
| 左薬指 | -0.10594944 | 0.4276399 | -0.4980211 |
| 左小指 | -0.022694644 | 0.34084624 | -0.5663649 |
| 右人差し指 | 0.103608675 | 0.5132862 | -0.31847566 |
| 右中指 | -0.13893783 | 0.40836343 | -0.4999805 |
| 右薬指 | -0.20971514 | 0.38006347 | -0.5322404 |
| 右小指 | -0.34317607 | 0.30952653 | -0.584296 |
| 左親指 | -1.1633571 | -1.2562445 | -1.3902905 |
| 右親指 | -1.1403292 | -1.1757555 | -1.3002338 |

親指のこの値はIdleがOpenとFistの間にさえ入らない。
全指を一律の「半分曲げる」にしたり、muscle値をそのままEuler角へ変換したりしない。
全40カーブはStretched／Spreadであり、正確な見た目の再現には関節ごとの評価が必要。

現行VRCについては [SteamVR Input 2.0](https://docs.vrchat.com/docs/steamvr-input-20)も参照する。
Indexの指の見た目にはSkeletal Input、アバター別Legacy Fingers、全体の
Avatars Use Finger Tracking設定が関係する。指追跡を表示に適用しなくてもGestureの認識は動く。
したがってNeutral=0になっても、指追跡中の手がSDKの固定Idle形に置き換わるとは限らない。
[Gesture Toggle](https://docs.vrchat.com/docs/gesture-toggle)を無効にしたAV3では最後の番号を保持する。
これもNeutralへの遷移とは別である。

## Gesture Managerにはコントローラー認識処理があるか

同じUnityプロジェクトの `vrchat.blackstartx.gesture-manager` 3.9.9も確認した。
この版にはIndex等のコントローラーの接触・押下・指curlからGesture番号を認識する処理はない。
手形を選択してAnimatorへ番号を渡すエミュレーターである。
[公式README](https://github.com/BlackStartx/VRC-Gesture-Manager)も左右の手形をUIボタンで試す方法を説明している。

- `Scripts/Editor/GestureManagerEditor.cs` の `OnCheckBoxGuiHand` は1〜7のチェックを表示し、
  `module.OnNewHand(hand, isOn ? i : 0)` を呼ぶ。選択解除がNeutral=0であり、実指の脱力判定ではない。
- `Scripts/Runtime/Data/ModuleBase.cs` の `OnNewHand` が左右へ分岐し、
  `Scripts/Editor/Modules/Vrc3/ModuleVrc3.cs` の `OnNewLeft`／`OnNewRight` が
  GestureLeft／Rightパラメーターを直接設定する。リスト上のマウスドラッグも選択行を番号へ変換する。
- `Vrc3WeightSlider.UpdatePosition` はマウス位置を0〜1へClampし、GestureWeightを設定する。
  Gesture変更時にWeightを更新する補助処理もあるが、実トリガー量の測定・手形判定ではない。
- `OpenSoundControl/OscSettings.cs` はOSCの入力値をパラメーターへ渡す。
  外部から値を受け取る機能であり、コントローラーセンサーからの手形認識は実装していない。

パッケージ内の全C#をXR／SteamVR／OpenVR／OVRInput／指curl／Grip／GetAxis／GetButton等で検索し、
上記のUIからパラメーター設定までの経路を直接読んだ。Neutralの実機しきい値の根拠としては使えない。

## 現在のResoPonのIndex

`ExpressionGestureInputSetup.BuildControllerGesture` は装着者の
`UserFingerPoseSource` → 各指の `FingerPose.Rotation` → `EulerAngles_floatQ` を読む。
4指はProximalのX >= 40度、親指は左Y <= 25度／右Y <= -25度を既定の曲げ判定とする。
ビット順は人差し指、中指、薬指、小指、親指。

Fist=31、HandOpen=0、FingerPoint=30、Victory=28、RockNRoll=6/22、HandGun=14、ThumbsUp=15。
`IndexOfFirstValueMatch<bool>` の未一致Index=-1に1を足してNeutral=0にする。
全32コードのうちNeutralは次の24コード。

```text
1,2,3,4,5,7,8,9,10,11,12,13,16,17,18,19,20,21,23,24,25,26,27,29
```

このNeutralは特定の脱力ポーズを検出した値ではなく、7手形のどれも成立しない値。
全指が曲げしきい値未満ならHandOpenへ、全指が曲げ判定を満たせばFistへ進むため、
脱力していてもこの2つへ分類され得る。NeutralのNOR行だけを追加しても分類は変わらない。

公式IndexのRock N Rollは親指downだが、現在の6/22は親指を問わない。
VRCへ厳密に合わせる変更と、既存利用者の互換性を保つ変更を区別する。

## 提案：伸び・中間・曲げの3状態で表情を選ぶ

Resonite向けの設計案であり、VRCクライアントの認識アルゴリズムとして確定したものではない。
各指にOpen判定とClosed判定を別々に設け、その間は両方falseとする。
Closedの否定をOpenとして使うと、中間域がOpenへ混入するため、この案の目的を満たせない。

較正済みの曲げ量cなら、例えば `Open = c <= 0.25`、`Closed = c >= 0.75` とする。
これらは動作説明用の数値であり、VRCの値や実機検証済みの既定値ではない。
左右・指ごとの伸ばした値／曲げた値を測り、脱力時が中間域に収まるか確認して境界を選ぶ。
親指は他4指と測定軸・方向が異なるため独立して較正する。

角度を使う最小案は現在のFingerPose経路を維持し、各指へ2つの比較を置く。
伸ばした角度aOpen／曲げた角度aClosedから
`c = Clamp01((a - aOpen) / (aClosed - aOpen))` を作るなら、標準の
`ValueSub<float>`、`ValueDiv<float>`、`Clamp01_Float` を使える。
分母が0の較正は拒否する。角度の折り返しを跨ぐ区間は、較正前に連続な角度へ直す。
Proximalのみで区別できない場合はIntermediate／Distalも測り、
同じ座標基準の回転を単純に加算せず、関節間の相対回転を基に曲げ量を作る。

次の条件を各手形の `AND_Multi_Bool` へ接続する。O=Open、C=Closed、*=条件なし。

| 番号／手形 | 人差し指 | 中指 | 薬指 | 小指 | 親指 |
|---|---|---|---|---|---|
| 1 Fist | C | C | C | C | C |
| 2 HandOpen | O | O | O | O | O |
| 3 FingerPoint | O | C | C | C | C |
| 4 Victory | O | O | C | C | C |
| 5 RockNRoll | O | C | C | O | * |
| 6 HandGun | O | C | C | C | O |
| 7 ThumbsUp | C | C | C | C | O |

RockNRollの親指*は既存6/22の互換性を保つ案。公式の説明へ寄せる場合はCへ変更する。
*の案では親指が中間でもRockNRollが成立するが、それ以外の必要な指が中間なら手形は成立しない。

```mermaid
flowchart LR
    P[UserFingerPoseSource → FingerPose] --> A[指ごとの曲げ量・較正]
    A --> O[Open比較／Closed比較]
    O --> G[7手形のAND条件]
    G --> N[NOR → Neutral条件]
    G --> I[IndexOfFirstValueMatch bool]
    N --> I
    I --> E[既存の入力受付・安定待ち → Gesture API]
```

`NOR_Multi_Bool.Operands` に7条件を接続してNeutralを作る。
`IndexOfFirstValueMatch<bool>.Match=true`、`Values=[Neutral,Fist,HandOpen,FingerPoint,Victory,RockNRoll,HandGun,ThumbsUp]`
とし、Indexをそのまま0〜7として出力する。Touch Version 56と同じ並べ方で、+1は不要。
全指が中間の脱力形も、テンプレート外の形もNeutralになる。
Neutralは有効なGestureTableの0行であり、表情解除・Baseと同義ではない。
VR終了や機器切断の-1、入力許可、変更検出、安定待ち／Timeoutは既存の共通制御を使う。
既存のTimeout中の変化破棄も継承するため、静止したNeutralへ戻る時系列は実機確認が必要。

## 指の見た目を再現する場合

実行中Resoniteの実DLLをilspycmdで確認し、次の標準コンポーネント／ノードが存在することを確認した。

- `FingerPosePreset.PresetPose=Idle` はResoniteの `FingerPosePresets.Idle` を供給する。
  Resonite標準の脱力形を表示する最小案。VRC SDKのIdleとの同一性は確認していない。
- `FingerPoseMultiplexer` のSources／Indexで追跡ソースとIdleソースを選び、
  InterpolationSpeedで補間できる。各手の `HandPoser.PoseSource` へ渡す。
  左右を独立して切り替える場合は、各手のHandPoserに対応する選択経路を用意する。
- VRCの固定形が必要なら、対象Humanoid AvatarでSDKのIdleクリップを評価し、得た指ボーン回転を
  Resoniteの指座標系へ変換してポーズソースへ保存する。Idle2まで再現するなら時間カーブも必要。

Indexで実指追跡を維持する目的なら、通常は表示を追跡ソースのままにし、表情番号の判定だけを変更する。
固定Idleを表示する案では、判定は表示切替より上流の追跡ソースから読む。
表示したIdleを再び入力として判定するフィードバックを作らない。

実DLLの `FingerPose` はデータ取得失敗時にPosition=0、Rotation=Identityを返し、成功フラグを出力しない。
現在のしきい値へIdentityを渡すと左はコード16でNeutral、右はコード0でHandOpenになり得る。
ソースnullや追跡停止を脱力と同一視しない。3状態判定だけではこの問題は解決しないので、
入力受付時には上流ソースの有効性・追跡状態を別に確認する必要がある。

## 今回の検証と実装時に残る確認

- SDKの両Controllerで実際に参照される左右レイヤーの全32遷移を辿り、0／2のクリップを照合した。
  Idle／Idle2／Open／Fistの各40muscleカーブとStopTimeを抽出した。
- 既存Indexの32コードを列挙し、Neutralが24コードであることを確認した。
- 提案の5指3状態の全243通りで条件の排他性を検証した。
  親指*のRockNRollを採用すると非Neutralは9通り、Neutralは234通り。
  中間を含まない32通りは既存の番号と一致し、全指中間はNeutralとなる。
- 起動中Resoniteと同じインストールのDLLでFingerPose、UserFingerPoseSource、NOR、
  IndexOfFirstValueMatch、FingerPosePreset、FingerPoseMultiplexer、HandPoserの定義を確認した。

243通りの検証は離散条件のオフライン検証であり、生成Fluxの実行テストや実機の認識テストではない。
実装時は左右でOpen→脱力→Fist、Victory→脱力、親指の接触先変更、境界での揺れ、
指追跡喪失、VR終了／復帰、Timeout中からのNeutral復帰、GestureTableの0行の選択を確認する。
VRC同値性を求める場合は、VRCのGestureLeft／Right表示と同じ実手形・バインディングで照合する。
