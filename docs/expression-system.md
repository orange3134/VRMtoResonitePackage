# 表情システムの実装と編集方法

VRChat の FX Controller から対応可能な BlendShape アニメーション、条件遷移、ExpressionMenu を取り込み、
標準コンポーネントと ProtoFlux で動作する表情システムを生成する。VRM の感情・カスタム表情も同じ経路を使う。
利用側に ResoPon の DLL は不要。元の設計は [expression-system-design.md](expression-system-design.md)。

## 生成される構成

```text
Expressions/
  Catalog/                         表情ごとの定義と AnimX
  Core/
    SourceState/                   入力元ごとの要求・優先度・有効期限
    ParameterState/                合成後の Animator パラメーター
    Logic/                         公開 API、条件評価、合成
  Outputs/                         BlendShape ごとの基準値・追跡入力・最終出力
  Rules/
    ImportedAnimator/              対応可能な元 Controller のレイヤーと状態
    GestureOverrides/Left|Right/   Resonite で追加するジェスチャー割り当て
  Inputs/
    ContextMenu/
    Keyboard/
    HandGestures/Modules/Touch|Index|Vive|WindowsMR/
    External/
  API/Receivers/
  API/Examples/
  API/Templates/
  Diagnostics/
```

各設定レコードは `DynamicVariableSpace` の `Expr` 空間を持つ。
例えば `Id` の実際の変数名は `Expr/Id`。
パラメーター名は UTF-8 の16進数に符号化して `Expr/Value.<hex>` などにする。
DynamicVariable の名前には `/` を複数含められないため、元のパラメーター名をそのまま階層化しない。
API のパラメーター項目では元の名前をそのまま指定する。

### Flux のデバッグ用配置

生成する Flux は1スロットに1ノードを置く。`Core/Logic` は共有入力、公開 API、メニュー操作、
左右の手動割り当て、パラメーターごとの評価、Animator レイヤーごとの遷移、出力合成、
ライフサイクルの名前付きグループに分ける。
ノードのスロット名には番号・ノード型・定数値または参照先を付ける。
定数の共用はグループ内に限定し、参照線が離れたグループへ集中することを避ける。

各グループは生成順に左から右へ8列のグリッドで配置し、ポート数に合わせて行間を確保する。
グループは上から下へ、Core・入力アダプター・受信口の各ロジック領域は横に並べる。
この位置情報はパッケージにも保存され、ProtoFlux のデバッグ表示時の配置に使える。
Resonite では ProtoFlux Tool で対象の `Logic` Slot を Unpack すると、この位置でノードを表示できる。
機種固有のロジックは各機種 Slot の子のままなので、機種 Slot ごとの削除も維持される。

## 表情を使う・編集する

コンテキストメニューの **Expressions → Select expression** で表情を固定する。
同じ項目を再度選択すると、そのメニューの要求を解除する。
**Return to automatic** は直接指定を解除し、現在のジェスチャー・元 Controller の状態へ戻る。
`Neutral (authored baseline)` は基準表情の固定であり、自動へ戻す操作とは異なる。
`--no-expression-menu` はこのメニューだけを生成しない。`--no-avatar` は表情システム自体を生成しない。

`Catalog` の各 Slot には次の設定がある。

| 設定 | 内容 |
| --- | --- |
| `Id` | 外部 API・キーボードなどから使う一意な識別子 |
| `DisplayName` | メニュー表示名。Slot 名から独立して変更可能 |
| `Enabled` | 選択・再生の有効状態 |
| `Clip` | `IAssetProvider<Animation>` への参照 |
| `Duration` / `Loop` | 再生時間・ループ |
| `FadeIn` / `FadeOut` | 直接指定を切り替えた際の秒数 |
| `FullFace` | true なら未指定の出力にも直接指定を適用し、その部分は追跡の基礎値に戻す |

追加は既存の表情を複製し、`Id`、`DisplayName`、`Clip`、`Duration` を変更する。
全表情を削除した後も `API/Templates` から `Catalog` へ複製できる。
テンプレートは `Enabled=false`、`Id` が空なので、設定後に有効化する。
並べ替え・表示名変更は参照を壊さない。重複 ID の新規選択は拒否する。
使用中の定義を無効化・削除した場合、その寄与を取り除く。

AnimX のトラック名は `Node=Expression`、`Property=Outputs/<対象>/Expr/Id の値`。
変換した Clip は Unity の BlendShape 値を100で割り、接線も同じ比率で変換する。
既存出力への表情追加は配線変更不要。新しい BlendShape 自体を対象に加える場合は、
`Outputs` に対応フィールドへの単一ドライバーと基礎入力を追加する必要がある。

## ジェスチャーとキーボード

不要なコントローラーは `Inputs/HandGestures/Modules` 内の機種 Slot ごと削除する。
機種固有の入力ノード・判定ロジックはその子に置いてあり、他機種には依存しない。
中央に残った要求レコードは `Owner` が失われるため失効する。
有効な入力は0.1秒ごとに更新し、0.5秒途絶すると失効する。左右は独立して扱う。
Grip / Trigger の押下閾値0.55、解除閾値0.45、姿勢の安定時間0.05秒は機種 Slot で編集可能。

指を個別に取得できない機種もあるため、標準設定は Grip・Trigger・親指タッチ・ボタンの組み合わせによる近似。
Touch / Index は第1ボタンで Victory、第2ボタンで RockNRoll、
Vive / WindowsMR はタッチパッド押下と Grip の組み合わせを使う。
実機種ごとの指姿勢の完全互換は保証しない。必要に応じて各機種の判定グラフを編集する。

元 Animator の自動変換が省略された場合や別の割り当てが必要な場合は、
`Rules/GestureOverrides/<Left または Right>/Mappings/<0～7>` の `Expression` を指定して `Enabled=true` にする。
値は VRChat の Neutral=0、Fist=1、Open=2、Point=3、Victory=4、RockNRoll=5、Gun=6、ThumbsUp=7。
これらの追加割り当ては元レイヤー合成後に適用する。競合時は右21・左20の優先度が既定値。
通常の直接指定の優先度は100で、同順位は選択イベントの順序で決める。
これらは Resonite で追加した操作の規則であり、元 VRChat の左右優先度とは別。

キーボードは最初の9表情に `Ctrl+Alt+1～9` を割り当て、それ以降は未割り当て。
`Inputs/Keyboard` の各項目で Key、Control、Alt、Mode を変更する。
Mode は `Toggle`、`Hold`、`OneShot`。標準の `KeyHeld` がテキスト入力・フォーカスによる入力抑制を担当する。
新しいショートカットは既存項目を複製し、対応する SourceState も複製して Command.SourceSlot を接続する。

## 外部イベント API

装着者のクライアント上で、次の Dynamic Impulse を送る。

- 宛先: 対象アバターの `Expressions/API/Receivers`
- Tag: `ResoPon/Expression/v1/Request`
- 引数型: `Slot`（`DynamicImpulseReceiverWithObject<Slot>`）
- 引数: `API/Examples` にある Command と同じ構成の Slot

`Core/SourceState/External` を入力元ごとに複製し、Command の `SourceSlot` に指定する。
Source は `Core/SourceState` の直下に置き、`Owner` に入力元の寿命を表す Slot を設定する。
`Channel` は Command と Source で一致させる。`Priority`、`Lease` は Source 側の設定。
Lease=0 は明示解除まで、正値は最後の要求からの秒数を表す。

| Command のフィールド | 用途 |
| --- | --- |
| `Version` | 1 |
| `Generation` | 現在の `Core/Generation` |
| `Sequence` | 入力元ごとに増加させる int |
| `Operation` | Select / Release / Renew / Gesture / SetParameters / Reset |
| `ExpressionId` | Select 対象の Catalog ID |
| `Weight` / `Mode` | Select の重み0～1、Hold / Toggle / OneShot |
| `ReleaseGeneration` / `ReleaseSequence` | Release / Renew 対象の世代と選択時 Sequence |
| `Parameters` | SetParameters の項目を子に持つ Slot。各項目は `Expr/Name` string と `Expr/Value` float |
| `ResetAfter` | SetParameters を0へ戻すまでの秒数。0で自動復帰なし、上限60秒 |
| `Hand` / `GestureId` / `GestureWeight` / `Available` | Gesture 用。Source.Kind=1 が左、2 が右 |

`Release` はその入力元の表情とパラメーター上書きを解除する。
古い選択を対象にした Release / Renew は新しい選択を変更しない。
`Renew` は選択順を変えずに寿命を更新する。
`SetParameters` は全項目を検証してから適用し、未知の名前・重複項目・型に合わない値を拒否する。
失敗理由は `Core/LastError` に入る。非装着者、別アバターの Source、古い世代・Sequence は受け付けない。
Dynamic Impulse 自体はネットワーク RPC ではない。

## 対応範囲と設計との差分

- 外部 `.anim` の BlendShape のみの Clip、非加算の定数レイヤー、平坦な state machine、
  int / bool / float の基本条件、Entry / Any State / 通常遷移、基本 exit time とフェードを変換する。
- 一様な Write Defaults、または WD=false で各 state の binding 集合が同じ場合を自動再現する。
  空 state は基準値へ戻す。元のレイヤー順・左右条件を保持する。
- Button / Toggle / パラメーターなし Sub-Menu を変換する。Button は1秒のパルスにする。
  実際に変換できたレイヤーで使われないメニュー項目は省略する。
- BlendTree・ネストした state machine・AvatarMask・加算・遷移中断・StateMachineBehaviour・
  履歴依存の WD・時間パラメーター・Puppet・Sub-Menu の開閉パラメーターは自動再現しない。
  独立して読める BlendShape Clip は直接選択用に残す。混在する材質・物体・Transform トラック、
  weighted tangent、イベント付き Clip、未解決出力を含む Clip は除外して診断する。
- 既存の瞬き・口パク・顔追跡のドライバーを `Outputs/Base` へ移し、出力を一つにする。
  直接指定の未指定トラックは追跡値を保つ。出力ごとの `TrackingWeight` は直接指定に追跡値を混ぜる割合。
  設計の細かな目・口・顔チャンネル別 TrackingPolicy は未実装。
- **現在は装着者が最終値を計算し、その値を同期する。** 設計の各クライアントでの時刻再生・
  状態だけの同期は未実装。多人数環境での帯域・遅延評価は別途必要。
- 複製・再読込では非永続の ProtoFlux StoredValue により一時要求を初期化する。
  Saved パラメーターの属性は保持するが、VRChat のユーザー別保存データは移行しない。

## 検証

`dotnet run --project tests/ExpressionSmoke -c Release` は実際のインストール済み Resonite DLL で
入力受信、左右条件、両手 AND、古い解除、削除、手動割り当て、既存ドライバー、複製、
AnimX 接線、パッケージ保存・再インポート・再生を検証する。
ヘッドレスの単独ワールドは通常のフレーム更新が省略されるため、このテストだけ
`World.ForceFullUpdateCycle=true` にする。生成するアバターにこの設定は含めない。
Animation の動的参照だけではアセットをロードしないため、Catalog には `AssetLoader<Animation>` も接続する。

既知のローカルケース Lilleo / yuzuki で変換と `--inspect` を実施した。
Lilleo では元の左右レイヤーの未対応挙動・欠落 BlendShape により自動変換を省略する。
物理コントローラー操作と複数クライアント間の表示確認はこのヘッドレス検証には含まれない。
入力ファイル・生成物・個人環境のパスはコミットしない。
