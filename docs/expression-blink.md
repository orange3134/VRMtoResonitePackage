# 表情と EyeLinearDriver の瞬き合成

この資料は `ExpressionPlaybackSetup.cs` の `MixOutput` と、EyeLinearDriver の
CloseSubtractLimits を使う代替方式の設計判断に使う。現行実装の置き換えは行っていない。

## 現在の処理

EyeLinearDriver.Eyes[].OpenCloseTarget は出力の Base へ付け替え、
`Outputs/<出力>/Tracking` の ValueFieldDrive が表情と Base を継続合成する。
通常方向は `max(desired, Base)`、逆方向は `min(desired, Base)`。
desired は選択表情と Base を TrackingWeight で混合した値である。

ApplyExpression も同じ MixOutput を生成するが、OriginalDriver がある出力は
ForEach 内の Write 対象から除外する。実際の瞬きは Tracking 側で処理され、
瞬きごとに ApplyExpression の Impulse や ForEach が実行されるわけではない。
CloseSubtractLimits への移行で変わるのは主に継続評価と接続構成である。

## CloseSubtractLimits の意味

インストール済み FrooxEngine.dll の `FrooxEngine.CommonAvatar.EyeLinearDriver` の
`OnCommonUpdate`、`UpdateTarget` を確認する。古いデコンパイル済みソースだけで判断しない。

各 EyeCloseLimit は Source（IField<float>）と Multiplier（既定1）を持つ。
値は合計せず、0から最大値を取る。

```text
L = max(0, LeftCloseSubtractLimits の Source × Multiplier,
           CombinedCloseSubtractLimits の Source × Multiplier)
R = max(0, RightCloseSubtractLimits の Source × Multiplier,
           CombinedCloseSubtractLimits の Source × Multiplier)

leftInput     = clamp01(EyeManager.LeftEyeClose     - L)
rightInput    = clamp01(EyeManager.RightEyeClose    - R)
combinedInput = clamp01(EyeManager.CombinedEyeClose - max(L, R))

t      = clamp01(inverseLerp(MinInputCloseness, MaxInputCloseness, input))
output = lerp(OpenState, ClosedState, t)
```

Side に対応する input を各 Eye に渡す。制限は Eye エントリー単位ではなく
EyeLinearDriver の左右・共通単位であり、左だけの制限も Combined の出力に影響する。
CombinedEyeClose は EyeManager の値を使い、この段階で左右平均などを再計算しない。
負の Source × Multiplier で閉じ量を加算することはできない。

## 単独では現在の max/min と同じにならない

標準の入力範囲0〜1、OpenState=0、ClosedState=1で、表情の閉じ量を P、瞬きを B とすると、
Source=P の出力は `clamp01(B-P)` になる。P=0.6、B=0.8なら0.2で、
現在の `max(P,B)=0.8` とは異なる。瞬きが0なら出力も0で、表情の半目を保持しない。

この機能は、別の表情シェイプで既に閉じた分だけ、瞬きシェイプの変形を弱める用途に適する。
例えば笑顔で閉眼する表情を別シェイプに保持し、その閉じ量を Source として瞬きを抑える。
ただし、異なるシェイプの頂点変形が単純な閉じ量の加算に対応するとは限らない。
表情名やシェイプ名だけから閉じ量と左右の対応を自動推定できるものではない。

## 同じシェイプの合成を置き換える案

### 残りの瞬き量を加算する

P、B が0〜1なら `P + clamp01(B-P) = max(P,B)`。
表情Pと瞬きの残量を別フィールドへ出力し、最後に加算すれば通常方向の合成を再現できる。
ただし合成処理自体は残る。反転・非単位の出力範囲では正規化と出力範囲への変換が必要。

### Eye の出力範囲と入力範囲も変更する

TrackingWeight=0、元の入力範囲0〜1、既存の減算制限がなく、同じ側の出力が
共通の正規化閉じ量Pを使う場合は、EyeLinearDriver 内だけで同じ値を作れる。

```text
CloseSubtractLimits.Source = P
Multiplier                 = 1
MinInputCloseness           = 0
MaxInputCloseness           = P < 1 ? 1-P : 1
OpenState                  = lerp(元のOpenState, 元のClosedState, P)
ClosedState                = 元のClosedState
```

P<1では入力の残量 `clamp01(B-P)` を0〜1-Pから再正規化して表情の開閉範囲へ写すため、
通常方向はmax、逆方向はminになる。P=1ではOpenStateとClosedStateを同じ値にし、
入力範囲を0幅にしない。元の閉眼値が0.4などの非単位範囲にも同じ式を使える。

この案で代替する場合は次の設計が必要になる。

- Pには表情の独立した値を使う。EyeLinearDriver 自身の出力や合成後の Result を
  Source に戻すとフィードバックになる。
- 同じ側に複数のシェイプがあり、それぞれの表情の閉じ量が異なる場合は、
  ドライバーの分離や対象のグループ化が必要。現在のシェイプごとのmax/minとは粒度が違う。
- 非既定の MinInputCloseness/MaxInputCloseness、既存の減算制限、
  TrackingWeight が0以外の混合は、この簡易式だけで現行動作と等価にはできない。
- 未着用・表情解除・トラック欠落時にP=0と元の設定へ戻す。
  未着用中にApplyExpressionを走らせない現在の構成では、着用状態の扱いを別途用意する。
- OpenCloseTarget と DynamicBlendShapeDriver を同じフィールドへ重ねてDriveしない。
  表情値の書き込み先を設定用フィールドへ変更し、最終出力の駆動元を一本化する。
- 追跡出力の各クライアントでのローカル評価と、複製・保存再読込時の参照を維持する。

汎用の BlinkMode をそのまま削除する変更ではなく、対応条件を限定した生成経路として設計する。
表情と瞬きが別シェイプの場合は、現在のmax/minの代替よりも追加の瞬き抑制として検討する。
