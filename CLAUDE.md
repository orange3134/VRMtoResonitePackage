# CLAUDE.md

このファイルは、このリポジトリでコーディングエージェント（Claude Code 等）が作業する際の
ガイドです。プロジェクト固有の前提・ハマりどころ・実機検証で確定した値をまとめています。
ユーザー向けの使い方は [README.md](README.md) を参照してください。

## プロジェクト概要

VRM アバター（および VRChat アバター入り `.unitypackage`）を Resonite の
`.resonitepackage` に変換する Windows 用ツール。**ヘッドレスで FrooxEngine を起動**し、
Resonite 本体と同じ `ModelImporter` でインポートしたうえで、VRM の正確なメタデータを使って
ゲーム内 AvatarCreator 相当のセットアップ（リグ・視点・リップシンク・揺れもの・マテリアル）を
自動で行う。

- 言語/ランタイム: C# / .NET 10（`net10.0-windows`, WPF GUI 付き, x64）
- 出力: 単一ファイル exe（`SelfContained=false`、Resonite DLL は非同梱）

## ビルドとリリース

```powershell
dotnet build src/VrmToResonitePackage -c Release
dotnet publish src/VrmToResonitePackage -c Release -o publish   # 配布用
```

- **Resonite DLL の参照解決**: csproj が `ResonitePath` プロパティ（既定は Steam の標準パス
  `C:\Program Files (x86)\Steam\steamapps\common\Resonite`）または環境変数 `RESONITE_PATH`
  から FrooxEngine 系 DLL を参照する。これらは**コンパイル時参照のみで再配布しない**
  （`<Private>false</Private>`）。別パスにインストールしている場合は
  `-p:ResonitePath="D:\Games\Resonite"`。
- **バージョン**: csproj の `<Version>` がビルド日時 `yyyy.MM.dd.HHmm` で自動生成される
  （`-p:Version=...` で上書き可）。`AppVersion.Display` が AssemblyInformationalVersion から
  取得し、変換ログ先頭・コンソールヘッダ・GUI タイトルに表示する。
  → **バグ報告のログからビルドを一意に特定**するための仕組みなので壊さないこと。
- **配布スクリプト**: `publish.ps1`（単一ファイル exe）。`release.ps1` は GitHub Release への
  アップロード（`gh` 認証済み前提、ローカル実行）。詳細は [RELEASE.md](RELEASE.md)。
  - **重要**: これらの `.ps1` は Windows PowerShell 5.1 で実行されるため **ASCII のみ**で書くこと。
    BOM なし UTF-8 の日本語コメントはパースエラーになる。

## 実行とテスト

- **テスト実行**: 環境変数 `RESOPON_NOPAUSE=1` を立てるとキー入力待ちをスキップする
  （CI/自動実行向け）。
- **検証（エンジン起動なし）**: `--inspect <package>` で生成済みパッケージの
  コンポーネント構造を表示（FrooxEngine を起動せずに確認できる）。`--inspect-verbose` で詳細。
- **テスト用モデル**: VRM 0.x / VRM 1.0 の**同一モデルのバージョン違いペア**を用意して
  両系統を確認するのが定石。`.vrm` と `test-input/` `test-output/` は `.gitignore` 済み
  （リポジトリにはコミットしない）。
- **検証用一時ファイル**: リポジトリ内に作るビルド出力・展開物・変換結果は `.tmp_verify/`
  配下に置く（`.gitignore` 済み）。`.tmp/` など別名の一時ディレクトリは作らない。
- **変換ログの場所**: **exe と同じフォルダの `Logs\`**（例:
  `src/VrmToResonitePackage/bin/Debug/Logs/convert_*.log`）。
  ※エンジンのデータ/キャッシュ（`Data\` `Cache\`）は `%LOCALAPPDATA%\ResoPon\`
  配下だが、**ログだけは exe 横**。README に `%LOCALAPPDATA%\...\Logs` と書いてある箇所は古い。

### 主な診断・オプションフラグ（`Program.cs`）

| フラグ | 用途 |
|---|---|
| `--inspect` / `--inspect-verbose` | エンジン起動なしでパッケージ構造を表示 |
| `--assimp-dump` | 各ブレンドシェイプの `movedVerts`（実際に動く頂点数）を出力 |
| `--vrchat-dump` | `.unitypackage` の解析結果をエンジン不要で確認 |
| `--avatar <name>` | VRChat パッケージ内の対象アバターを指定（完全一致優先→部分一致） |
| `--face-tracking` | VRM 表情のフェイストラッキング連動をオプトイン（既定は付与しない） |
| `--no-protection` | SimpleAvatarProtection を付けない（既定は付与） |
| `--view-forward` / `--view-up` / `--near-clip` | 視点オフセット・NearClip の上書き |
| `--import-timeout <sec>` | インポートのタイムアウト（既定 300） |
| `--keep-working-files` | 中間ファイルを残す |

## リポジトリ構成

主要なソースは `src/VrmToResonitePackage/`。

- `Program.cs` / `GuiApp.cs` — CLI / WPF GUI エントリポイント、引数パース
- `Converter.cs` — 変換の統括（VRM 経路 / VRChat 経路を拡張子で分岐）
- `AvatarSetup.cs` — ゲーム内 AvatarCreator 相当のセットアップ再現
- `SpringBoneSetup.cs` — VRM スプリングボーン → DynamicBoneChain 変換
- `MaterialTuner.cs` — MToon → XiexeToon マテリアル変換
- `PackageInspector.cs` — `--inspect` のコンポーネント構造表示
- `ResoniteLocator.cs` — Resonite インストールパスの自動検出
- `LocalDbMaintenance.cs` — 使い捨て Data ディレクトリと孤児掃除
- `AppVersion.cs` — ビルドバージョン表示
- `Vrm/` — `VrmParser.cs`（VRM0/1 両対応）, `VrmModel.cs`（中間モデル）, `GlbPreprocessor.cs`（互換ワークアラウンド群）
- `Unity/` — `UnityYaml.cs`, `UnityPackageExtractor.cs`, `UnityScene.cs`（`.unitypackage` 解析基盤）
- `Vrchat/` — VRChat アバター対応一式（パーサ・liltoon 変換・アダプタ）

## ヘッドレス FrooxEngine 起動の知見

`FrooxEngine.dll` を Resonite 外のアプリから `StandaloneFrooxEngineRunner` で動かすときの
確証済みの注意点（ドキュメントがなく症状から原因が見えにくいので明文化）。

- **CWD を Resonite フォルダにする（必須）**: `EngineInitializer` が
  `Directory.EnumerateFiles(".", "ProtoFluxBindings.dll")` でカレントを検索する。
  やらないと ProtoFlux 初期化で `Sequence contains no matching element`。
- **ネイティブ DLL**: `runtimes\win-x64\native` と `runtimes\win10-x64\native` に
  freetype6 等がある。`AssemblyLoadContext.Default.ResolvingUnmanagedDll` でフックして解決。
- **マネージド DLL**: `AppDomain.CurrentDomain.AssemblyResolve` で
  `Assembly.LoadFrom(resoniteDir + name.dll)`。
- **`Userspace.ExitWorld`/`RunAction` はヘッドレスでハングする**: `LeaveWorld` がフォーカス
  移譲先ワールドを無限待機する。`DoNotAutoLoadHome=true` だと親がいないため永久に終わらない。
  → ワールドは閉じずに 1 つを使い回し、終了は `runner.Shutdown()` にタイムアウトを付けて
  最後に `Environment.Exit`。
- **更新ループはフォアグラウンドスレッド**: Shutdown が完了しないとプロセスが終わらないので
  必ず `Environment.Exit`。
- **DataDirectory は専用パスに**: `Instance.lock` が Resonite 本体と競合しないよう
  `%LOCALAPPDATA%\<app>\Data` 等を使う。
- **LocalDB はシングルプロセス前提**: GUI 分離プロセスと CLI の並行実行で共有 Data ディレクトリが
  破損する（Invalid password）。→ **実行ごとに使い捨て `Data-xxxxxxxx` ディレクトリ**を使い、
  終了時削除＋次回起動時に `Instance.lock` で生存確認しつつ孤児掃除。`LocalKey.bin` だけは
  共有保管して MachineID を安定化させる。
- **パッケージ出力**: `Slot.SaveObject(DependencyHandling.CollectAssets)` →
  `RecordHelper.CreateForObject<Record>` → `PackageCreator.BuildPackage`
  （`PackageExportable.cs` が手本）。
- **正確な API が必要なとき**: インストール済み DLL を直接読むのが確実。
  `ilspycmd -t <型名> FrooxEngine.dll`。ローカルにデコンパイル済みソースを置いて grep するのも
  有効だが、デコンパイルは古い場合がある。

## ModelImporter の既知クラッシュと GLB 前処理ワークアラウンド

Resonite の `ModelImporter` 由来のクラッシュを、変換前に GLB を自前修正して回避している
（`Vrm/GlbPreprocessor.cs`）。

- **空名ブレンドシェイプ複数で例外**: 旧 UniVRM は targetNames が primitive extras 側にしかなく
  Assimp が読めず全シェイプ名が空になる。`HasBlendShape("")` が常に false で重複チェックを
  素通しし `MeshX.AddBlendShape` で「already exists」例外。→ mesh.extras.targetNames へ昇格・補完。
- **プリミティブ間のモーフ属性不一致**: 同一メッシュ内でモーフの NORMAL 有無が混在すると
  マージで `Normals[i]` 範囲外。→ 先頭プリミティブの HasNormals を基準に不一致ターゲットから
  NORMAL/TANGENT を除去。
- **インポートのハング**: インポートコルーチンが例外で死ぬと `ImportModelAsync` が永遠に
  完了しない。→ タイムアウト必須（`--import-timeout`、既定 300 秒）。

### JoinIdenticalVertices によるブレンドシェイプ破壊

`ModelImporter` は常に `PostProcessSteps.JoinIdenticalVertices` を適用する
（`ModelImportSettings` で無効化不可・ハードコード）。このステップは位置/法線/全 UV/全頂点カラー/
ボーンウェイトが一致する頂点をマージするが、**モーフターゲット（ブレンドシェイプ）のデルタは
比較対象に含めない**。VRM 顔メッシュは静止時に座標が一致する頂点（歯・舌が一点に潰れていて
シェイプで展開する等）を持つことが多く、これらがマージされて片方のデルタしか残らず
「シェイプキーを 0→1 にしても一部頂点が動かない」症状になる（UniVRM は独自インポータで Join
しないので出ない）。

- **回避策（`GlbPreprocessor.AddMorphVertexGuardChannel`）**: モーフ持ちメッシュの全 primitive に
  **頂点ごとに一意な値を持つ追加 TEXCOORD チャンネル**を付与する。UV が 1 つでも違う頂点は
  マージ候補にならないため別頂点が保持される。値＝頂点 index（整数間隔で join epsilon に非依存）、
  VEC2 float、POSITION accessor 単位で共有。トーンマテリアルは UV0 しか使わないので無害。
  データは BIN チャンクに追記し `buffer[0].byteLength` と BIN チャンク長を拡張する。
- **検証**: `--assimp-dump` が各ブレンドシェイプの `movedVerts` を出力する。生 glTF の非ゼロデルタ数
  （Python で target POSITION accessor を読む）と比較し、大幅に下回ればマージで潰れている。

## 座標系の取り扱い

実測＋デコンパイルで確定した座標系の規約。コライダーオフセット等の符号を間違えると左右/前後が
反転するため、ここが最重要。

- Resonite `ModelImporter.PreprocessScene` が Assimp シーン全体に **scale(-1,1,1) の X ミラー**
  ＋ FlipWindingOrder を適用する（RH→LH）。つまり**エンジンのスロット値＝glTF 値の X 反転**。
- glTF インポートは `MakeLeftHanded` なしで glTF 座標を数値そのまま使う。VRM0 の Unity 座標データ
  （スプリングボーン offset 等）は X 反転が必要、VRM1 はそのまま。
- **UniVRM0 は ReverseZ エクスポート**（モデルは -Z 向き、VRM0 拡張の spring オフセットは Unity 生値）、
  **UniVRM1 は ReverseX**（+Z 向き、オフセットは glTF ノードローカル）。
- -Z 向きのままだと VRIK `CreateCenteredRoot` が CenteredRoot に Y180 を入れるため、
  `GlbPreprocessor` で正規化済み VRM0 に **Y180 ベイク**（全 translation の X,Z 反転＋IBM に
  R=diag(-1,1,-1) を左乗算。回転ゼロ階層でのみ成立）。FlipX∘Y180∘ReverseZ＝恒等となり
  エンジン＝Unity 座標で回転 0 になる。
- **コライダーオフセット変換**: VRM1=`(-x,y,z)` / VRM0 ベイク済=`(x,y,z)` /
  VRM0 非ベイク=`(-x,y,-z)`。（旧実装の「VRM0 は X 反転のみ」は Z 符号が誤り。対称コライダーでは
  発覚しない。）

### Blender VRM アドオン製 VRM1 の前後反転

UniVRM/VRoid は VRM1 を **ReverseX（鏡映エンコード＝解剖学的左が +X）**で出すが、
**Blender VRM アドオンは「正配置」glTF（左が -X、Blender→glTF は反射なしの回転のみ）**を出す。
Resonite インポータは前者前提で FlipX するため、Blender 製は鏡映のまま残り、さらに VRIK
`CreateCenteredRoot` が足から前方向を測って Y180 を CenteredRoot に注入する。結果
Y180∘FlipX＝Z 鏡映で**体・頭が前後逆・腕は左右(X)保持**になる。

- **判定**: glTF で `rightUpperLeg.x > leftUpperLeg.x`（脚で判定。腕は稀に変な静止回転を持つ）。
- **修正（`GlbPreprocessor.MirrorXForProperHandedVrm1`）**: glTF 全体を X 反射 P=diag(-1,1,1) して
  UniVRM 規約に揃える。自前 X 鏡映∘インポータ FlipX＝恒等で忠実・正面向き・全 X そのまま。
- **反射は負スケールなしの共役で実装**: ノード local は translation.x 反転＋quat
  (x,y,z,w)→(x,-y,-z,w)・scale 不変、IBM は P·IBM·P（列優先 index {1,2,3,4,8,12} 反転）、
  POSITION/NORMAL の X 反転、TANGENT は x+w 反転、三角 winding 反転（2,3 番入替）。
  **これは反射(det -1)なので winding 反転が必須**（VRM0 の Y180 ベイクは proper rotation で
  winding 不変。混同しないこと）。
- **検証**: 出力パッケージの `CenteredRoot` 回転が (0,1,0,0)→恒等になれば OK。
  追跡は `VrmModel.OrientationMirroredX`。

## アバターセットアップ規約（実機確認で確定した値）

ヘッドレス変換では実機の見た目・操作感を確認できないため、**ユーザーの実機検証で確定した値が
唯一の正解**。VRIK の軸推測や Resonite の既定値は VRM アバターに合わないことが多い。

- **手のリファレンス向き**: **+Z=手首→中指方向、+Y=手の甲方向**（両手とも同じ規則）。
  VRIK の `WristToPalmAxis` 推測はモデルごとにバラつくので使わず、ボーン位置（中指近位・親指）から
  計算する。
- **BipedRig ボーン割り当て**: `Bones.Clear()` してから **VRM マップのみで構築**する。インポート時の
  名前ヒューリスティック（`BipedRig.ClassifyBiped`）は装飾ボーンを Chest/UpperChest/Neck/Head 等に
  誤分類し、実スケルトンが使わない BodyNode に入ったゴミが残ると VRIK の `AssignFrom` が
  `chest = UpperChest ?? Chest` で誤ったボーンを拾う。ログの `Duplicate Bone type` 警告は
  誤分類発生のサイン。
- `BipedRig`/`VRIK` はインポート済みモデル子スロットではなく avatar root に作る。子に置くと
  `VRIKAvatar.Setup` の `CenteredRoot` も `AvatarRoot/Model/CenteredRoot` になり、ルート階層が一段深くなる。
- **視点位置**: 目ボーン中点だと顔メッシュに埋まる。**眉間（顔表面）へオフセット**する
  （前方＝目間距離×0.7 をクランプ 3〜9cm、上＝×0.2 を 0.5〜2.5cm）。`--view-forward`/`--view-up` で
  上書き可。
- **AvatarRenderSettings**: ルート直下に作り **NearClip=0.075、FarClip=null**（前髪が一人称視点を
  遮る問題の回避）。`--near-clip` で変更可（0 で無効）。
- **EyeRotationDriver.MaxSwing=4**（既定 15 は VRM には大きすぎて目が突き抜ける）。
- **ビセーム/瞬き**: Resonite の名前推測（`EnsureVoiceOutput` 内の `TrySetupVisemes`）を
  **完全バイパス**し、VRM Expression のバインドのみで設定。`DirectVisemeDriver` は Aa/Ih/Ou/Ee/Oh の
  5 つだけ、`EyeLinearDriver` は Left/Right 2 要素に blinkLeft/blinkRight。音声出力部分
  （VolumeMeter/AudioOutput 等）は自前複製。
- **DirectVisemeDriver の誤登録是正**: Resonite の名前推測は VRoid の `Fcl_EYE_Joy_R` を RR に
  誤登録する。目・眉系シェイプキーの誤登録は解除する。
- **AvatarExpressionDriver（TrySetupFaceTracking）は既定で生成しない**。フェイストラッキング非対応
  アバターでは不要かつ `Fcl_EYE_Angry` 等が誤登録される。`--face-tracking` でオプトイン。
- **アンカー位置**: Tool=中指先端の 3cm 先（指先＝末節の子エンドボーン優先、無ければ前セグメント長
  ×0.8 で外挿）/ GrabArea=中指付け根から手のひら向き 1cm / Toolshelf=手首から手の甲向き 5cm。
- **SimpleAvatarProtection は既定で付与**（root＋各 SkinnedMeshRenderer スロット）。ヘッドレスでは
  所有者なしだが `ReassignUserOnPackageImport` 既定 true でインポートした人が所有者になる。
  `--no-protection` でオフ。

## マテリアル変換（MToon → XiexeToon）

- **公式 MToon10XiexeConverter 準拠**
  （[Resonite.UnitySDK](https://github.com/Yellow-Dog-Man/Resonite.UnitySDK) の
  MaterialConverters/Custom/MToon/）。Alpha＝ZWrite Off（transparentWithZWrite なら On）＋MToon 仕様の
  キュー（blend:3000+offset / zwrite 付き:2501+offset / mask:2450 / opaque:2000、VRM0 はファイル内
  renderQueue 値）。※RenderQueue=2000 固定は破綻するため廃止。
- **XiexeToon 化時**: `ShadowRamp.Target=null、ShadowSharpness=0` が MToon の見た目に近い。
- **ShadowRamp は除去ではなく shadeColor/shadingShift/shadingToony から 256x256 生成**
  （VRM0 値は UniVRM MToon10Migrator 式で 1.0 換算: rangeMin=shift, rangeMax=lerp(1,shift,toony),
  toony10=(2-(max-min))/2, shift10=-(max+min)/2）。ShadowRim=白。
- **リム変換**: 公式コンバーターの式（RimRange=1/power, RimThreshold=lift）は**シェーダー実装と不一致で
  巨大リム化する**ため独自導出を使う。MToon=`pow(1-N·V+lift, power)`、
  XSToon=`smoothstep(range±sharp, 1-N·V)`。range=0.5^(1/power)−lift、
  sharpness=0.75/(power×0.5^((power-1)/power))。RimThreshold（N·L 明所ゲート）は MToon に無い概念→0 固定。
- **アウトライン幅**: XiexeToon(XSToon2.0) は**オブジェクト空間で `_OutlineWidth*0.01` 押し出し**
  （[Resonite.UnityShaders](https://github.com/Yellow-Dog-Man/Resonite.UnityShaders) の XSGeom.cginc）、
  MToon World モードはメートル。→ **Xiexe 値＝幅(m)×100**（VRM0 の `_OutlineWidth` は cm なので 1:1、
  VRM1 の `outlineWidthFactor` は ×100）。ShadowRamp 生成時もアウトライン幅だけは公式の生値代入では
  なく**幅(m)×100**を維持する。
- **OutlineMask**: MToon の `_OutlineWidthTexture`/`outlineWidthMultiplyTexture` は XiexeToon の
  **OutlineMask** と同義（メイン UV・赤チャネル・幅乗算）。ただし標準 glTF スロット外なので Assimp は
  取り込まない→GLB バイナリから自前抽出して `LocalDB.ImportLocalAssetAsync` でインポート。

## VRChat `.unitypackage` 対応

VRM に加え、VRChat アバター入りの `.unitypackage` も変換できる（隠し機能扱い。UI 表記は VRM のみ）。

- **設計**: VRChat パーサが `VrmModel` を生成するアダプタ方式で、リグ/視点/ビセーム/瞬き/揺れものの
  下流処理（`AvatarSetup`/`SpringBoneSetup`）を共用する。ボーン/メッシュは
  **GameObject 名＝インポート後スロット名**、ブレンドシェイプは**名前(ビセーム)/index(瞬き)**で
  解決する。VRChat 固有の liltoon マテリアルのみ別経路。`VrmModel` に
  `ModelSource{Vrm0,Vrm1,VrchatFbx}` を追加し、`Converter` は拡張子で分岐
  （`ConvertVrchat` は GLB 前処理せず FBX を `.fbx` にコピーして `ModelImporter`）。
- **Unity YAML パーサ（`Unity/UnityYaml.cs`）の注意**: **ブロックシーケンスは親キーと同インデント**
  （`m_Component:` の次行 `- component:` が同列）。`ParseFlatDocument` は `.meta` 等のヘッダ無し単一
  doc 用。
- **アバター選定**: `VRCAvatarDescriptor` 付きルート prefab で GameObject 数最大を主要アバターとする
  （`--avatar` で上書き、完全一致優先→部分一致）。`.unity` シーンも走査し、`IsAvatarDescriptor` は
  script GUID 一致**または**フィールド署名（`ViewPosition`＋`baseAnimationLayers`/`VisemeBlendShapes`）で
  SDK 版差の GUID 違いに対応。
- **ビセームは全 15 対応**（VRChat の `VisemeBlendShapes` 順＝Resonite `FrooxEngine.Viseme` enum 順で
  完全一致。ただし sil の enum 名は `Silence`）。`BlendShapeWeights` は 0-1 スケール
  （prefab SMR の Unity 0-100 値を /100）。
- **瞬きは `eyelidsBlendshapes[0]`（=Blink）のみ**。`[1]`=LookingUp/`[2]`=LookingDown は無視。
- **PhysBoneCollider**: `insideBounds==1`（内側保持）と `shapeType==2`（plane）は Resonite 再現不可で
  スキップ。`rootTransform` 指定時はそのボーンへ付与、=0 時は自 GO の親ボーンへ局所変換を畳む。
  署名（node+offset+tail+radius）で重複排除し chain 間共有。
- **FBX モデルの Prefab Variant 対応**: 1 つの全部入り FBX から派生 prefab を作る構造（Kipfel 等）に
  対応。`VRCAvatarDescriptor` が FBX インスタンスへの追加コンポーネントで所有 GameObject が
  stripped（名前無し）の場合、descriptor 自身または所有 stripped GameObject の
  `m_PrefabInstance`→`PrefabInstance.m_SourcePrefab` からソース FBX guid を解決してインポートする。
  追加コンポーネント自身の `m_PrefabInstance` が 0 で所有 GameObject 側だけに参照がある場合もある。診断ログの
  `descriptor(guid=1, strippedOwner=1), variant=True, source=[X.fbx ...]` がこの構造のサイン。
  ファイル間の Unity オブジェクト参照は meta の GUID と local fileID の組で解決する。ローカルの
  stripped object は `m_CorrespondingSourceObject` を辿って元アセットの GUID/fileID に戻す。
  `ModelImporter.fileIdsGeneration: 2` の FBX オブジェクト fileID は
  `Type:<type>-><Unity import path>0` の xxHash64。パスは `//RootNode` から始まり、synthetic な
  `root` を含む場合があり、末尾に `SkinnedMeshRenderer` 等の component type が付く。
  `.unitypackage` 内の FBX 本体は拡張子なしの `asset` なので、Assimp で階層を読む際は一時的な
  `.fbx` パスへコピーする。
  Milltina v1.01 は Variant の初期 blendshape override の検証基準で、`--vrchat-dump` が初期値を
  8 renderer から取得し、blink index 15 on `Body`、目ボーン `LeftEye` / `RightEye` になればよい。
  Resonite `ModelImporter` は MeshX 保存前に `StripEmptyBlendshapes()` を無条件実行し、全 delta が
  0.001 未満の shape を削除する。Unity prefab の blendshape weight は index 指定なので、区切り用の
  微小/空 shape が消えると後続 index がずれる。FBX の renderer ごとの元順序を Assimp で記録し、
  import 後に欠落した shape を空 frame として元位置へ挿入してから prefab weight を適用する。ただし
  空 frame も全頂点分の delta 配列を確保するため、数値 index 参照（blink/prefab initial weight）がある
  renderer の、最大参照 index までだけ復元する。名前参照だけの renderer を全復元すると巨大モデルで極端に遅い。
  Eku PC v1.2.0 は検証基準で、`Body_base` は FBX 66 / import 62 から 4 shape を復元し、
  index 4 が `Foot_heels.Foot_heels`、index 7 が `Breast_inside.Breast_inside` になる。
  Fyuett 1.0 は性能基準で、`Body` に 1036 shape があるが数値参照はないため復元しない。
  `Body_01` は prefab weight の最大 index 99 までに必要な 1 shape だけを復元する。
  stripped renderer の通常の `m_Materials` は読めないが、FBX `.meta` の
  `ModelImporter.externalObjects` にある「FBX 内マテリアル名→外部 `.mat` GUID」を使い、Resonite が生成した
  `Material: <name>` placeholder を置換する。解決可能な prefab renderer override はその後に優先適用する。
- **複数 FBX を合成する prefab**: Siro のように素体 FBX と髪/アクセサリ prefab を同じ avatar prefab に
  ネストする構造では、参照される全 FBX を再帰収集する。Humanoid 定義を持つ FBX を主モデルにし、残りも
  追加インポートする。最初に見つかった FBX だけへ潰すと髪やアクセサリが欠落する。
  FBX `ModelImporter.externalObjects` の「埋め込み material 名→外部 `.mat` GUID」は FBX ごとに保持する。
  複数 FBX 間で同じ material 名が出るため、1 つの辞書へ合流すると Body など primary renderer が
  追加 FBX 側の material/texture を参照してしまう。renderer の所属 FBX subtree から対応 map を選ぶこと。
  Unity Material Variant は `m_Parent` だけに `_MainTex` などがあり、子 material の `m_SavedProperties` が
  stencil/render queue 差分だけのことがある。lilToon 変換では親 chain を解決して texture/color/floats を継承する。
- **アバター候補の範囲と遅延解決**: 自身に VRCAvatarDescriptor がある prefab、単一親から Descriptor を
  継承する variant、複数 prefab を合成する外側 composition を候補表示する。ただし一覧生成ではキャッシュ済み
  UnityScene の prefab 参照グラフと FBX GUID だけを調べ、配置・renderer override・model metadata は解決しない。
  composition の詳細候補化は GUI/`--avatar` で選択された1件だけに遅延する。Fyuett 1.0 は28候補を約3.85秒・
  peak約90MBで列挙できることが基準（全候補を詳細解析すると8〜15GBまで増える）。GUI の候補は
  `SourcePath` のフォルダ順に並べ、表示は prefab 名とパスだけにする。内部の Descriptor/Variant/Composition
  情報は診断用に保持するが選択画面には出さない。Eku_Another は単一親 variant。
- `.unitypackage` の出力ファイル名は入力 package 名ではなく、選択された `avatar.PrefabPath` の
  prefab ファイル名にする（例: `Fyuett_ver1.0.unitypackage` の `Fyuett_All.prefab` 選択時は
  `Fyuett_All.resonitepackage`）。GUI子プロセスは `RESOPON_OUTPUT:` 行で実出力パスを親へ通知する。
- **合成 prefab のスロット階層**: 複数 FBX を同列インポートするだけでは prefab 構造にならない。
  `PrefabInstance.m_TransformParent` の stripped Transform を `m_CorrespondingSourceObject` で prefab/FBX
  まで再帰解決し、FBX `ModelImporter.internalIDToNameTable` から親ノード名を得る。各 FBX をインスタンス名の
  ラッパースロットへインポートし、親 FBX 内の対応ノードへ付け替えて prefab のローカル Transform を適用する。
  ヒューマノイド本体として選んだ primary FBX も例外にしない。Descriptor 付き prefab が root 階層を表し、
  本体 FBX がその子になる構造では primary の配置を無視すると本体側が root 扱いになる。
  古い Unity の FBX `.meta` は `internalIDToNameTable` ではなく `fileIDToRecycleName` を使うことがある。
  Siro_HairRibbon のように root Transform override が `//RootNode` を指す追加 FBX は、人工ラッパーに
  transform を残さず読み込まれた root node へ適用し、ラッパーを畳んで Unity の prefab root に近づける。
- `FBX Import Alignment` は VRChat FBX の上方向補正にだけ使う一時スロット。補正後は直下モデルの
  GlobalPosition/GlobalRotation/GlobalScale を保持してアバタールート直下へ移し、Alignment スロットを削除する。
  primary FBX のインポート用ラッパーも最終階層に残さず、子を Alignment 直下へ畳んでから AvatarSetup へ渡す。
  これで単独インポートと同じ `AvatarRoot/CenteredRoot/Armature...` 形になる。
  stripped 参照のため PhysBone/削除メッシュ/瞬きは引き続き制限あり。
- **FBX の単位変換**: VRChat FBX を直接 Resonite `ModelImporter` へ渡すと、Unity の ModelImporter が
  `useFileUnits`/`useFileScale` で行う単位変換は再現されない。FBX `UnitScaleFactor` は cm 単位なので、
  `globalScale * UnitScaleFactor / 100` をインポート後の FBX root hierarchy の scale に掛ける。
  FrooxEngine の `ModelImportSettings.Scale` はメッシュを拡縮する一方、FBX node/skeleton translation は
  source unit のままなので、そこへ単位変換を設定するとメッシュとリグの縮尺が不一致になる。
  importer scale は 1 にし、root hierarchy を一様に拡縮すること。Rusk/Plum は
  `UnitScaleFactor=1` のため root scale 0.01、Kipfel は `100` のため 1。Plum の回帰基準は
  `Hips.localY=72.3342` と `RootNode.scale=0.01`（global 約 0.723 m）。
  Kipfel の model prefab root には Unity 生成の uniform 0.01 scale も保存されるが、Assimp と上記
  import scale の後にこれを再適用すると逆に100分の1になる。top-level wrapper・transform targetなし・
  `UnitScaleFactor=100`・uniform 0.01 の組み合わせだけは root scale を 1 に正規化する。
- **FBX の軸変換**: Resonite の直接 FBX import は Unity ModelImporter の上方向変換を再現しない。
  FBX `UpAxis`/`UpAxisSign` は診断表示に使うが、それだけで事前回転しない。Resonite/Assimp 側の変換と
  二重適用すると NagiyaRuri が Y- 向きに反転する。import 後の実際の Hips→Head 方向だけを Y+ へ
  最小回転で整列する。
- **prefab 削除メッシュの除去（`VrchatSceneSetup.RemoveDeletedMeshes`）**: 全部入り FBX から
  メッシュ GameObject を削除して派生 prefab を作る構造では FBX インポートで削除済みメッシュも
  復活する。`avatar.PrefabGameObjectNames` を保持し、インポート直後（セットアップ前）に
  prefab に無い名前のレンダラースロット＋孤立 StaticMesh アセットを削除する。
- **GUI 落とし穴**: `InstallAssemblyResolver` は CLI 経路（`Program.Main`）でしか呼ばれず GUI プロセスには
  無い。複数アバターの列挙（`ListAvatars`）は `Elements.Core.UniLog` を参照するため、リゾルバ未導入だと
  JIT 例外→catch で握り潰し→空リスト→ダイアログが出ずバッチ直行になる。
  `MainWindow.EnsureAssemblyResolver()` で列挙前にリゾルバを導入すること。

### VRChat 経路の実機検証 TODO（未確認の係数・前提）

- 座標: コライダーオフセットは `ConvertVector(VrchatFbx)=(-x,y,z)`（VRM1/glTF と同じ X 反転と仮定）。
  CenteredRoot 回転は恒等になったが、揺れものコライダー位置は要実機確認。
- 瞬き: `eyelidsBlendshapes[0]` の index を Body レンダラの blendshape index 直で解決
  （Assimp が順序を保持する前提）。ズレたら名前解決へ。
- liltoon マテリアル: 影 border/blur・アウトライン幅・リムの数式は近似。実機で要調整。

## 外部参照リソース

- VRM 仕様: https://github.com/vrm-c/vrm-specification
- VRM の Unity 実装（UniVRM）: https://github.com/vrm-c/UniVRM
- Resonite シェーダー: https://github.com/Yellow-Dog-Man/Resonite.UnityShaders
- Resonite Unity SDK（マテリアルコンバーター）: https://github.com/Yellow-Dog-Man/Resonite.UnitySDK
- Resonite 本体の API 調査は、インストール済み DLL を `ilspycmd` で逆コンパイルするか、
  ローカルにデコンパイル済みソースを置いて grep する（デコンパイルは古い場合があるので最終確認は DLL で）。

## VRMFirstPerson notes

- VRM0 and VRM1 store mesh annotations differently. VRM0 uses `extensions.VRM.firstPerson.meshAnnotations[].mesh` and `firstPersonFlag`. VRM1 uses `extensions.VRMC_vrm.firstPerson.meshAnnotations[].node` and `type`; resolve the node to its glTF mesh index via `VrmModel.NodeMeshIndices`.
- Missing mesh annotations are not `Both`. UniVRM treats renderers without an explicit annotation as `Auto`, so the parser must add default `Auto` annotations for unannotated VRM0 meshes and unannotated VRM1 mesh nodes.
- `Both` intentionally does nothing.
- `ThirdPersonOnly` and `FirstPersonOnly` are implemented with `RenderMaterialOverride`, not by toggling renderer enabled state. Keep the ModularAvatar-compatible names and structure: `DynamicVariableSpace.SpaceName = "modular_avatar"`, `OnlyDirectBinding = true`, and `DynamicValueVariableDriver<bool>.VariableName = "modular_avatar/AvatarWornLocal"`.
- The wearer check is not constructed manually in code. Import the embedded `Resources/AvatarRootIdentification.resonitepackage` as a child of the avatar root. This package contains the same packed ProtoFlux structure ModularAvatar uses (`Check Avatar Worn`, `GetActiveUser`, `IsLocalUser`, `UserInput`, etc.). Do not replace it with a `LocalUpdate` value writer; undriven dynamic variables are global in Resonite and that changes behavior.
- The invisible material must stay compatible with ModularAvatar: create/reuse an `Assets/Invisible Material` slot with a `PBS_RimSpecular` material, transparent enabled, and zero alpha/color.
- `Auto` follows UniVRM headless mesh behavior. For skinned renderers, find the first-person/head bone, collect renderer bone indices whose slots are descendants of that bone, clone the `MeshX`, and remove any triangle where any vertex has a positive weight for one of those bones. If no erase bones are present, do nothing, matching `Both`.
- For `Auto` skinned renderers, the original renderer becomes third-person only. The generated `_headless_<renderer>` renderer uses the erased mesh and same bones, but deliberately strips blendshapes and does not copy `BlendShapeWeights`, matching UniVRM's `BoneMeshEraser` behavior. Material overrides make it visible only in `RenderingContext.UserView` while the avatar is worn locally. Its default materials should be invisible to avoid double rendering in other contexts.
- Run `ApplyFirstPersonAutoAsync` after `MaterialTuner.Apply`, so the headless renderer references the tuned final materials, not importer placeholders.

## VRChat FBX prefab notes

- For composed VRChat prefabs, an additional FBX whose prefab override targets Unity's `//RootNode` should apply the authored transform to the promoted root slot and reset a single direct payload child to identity. Siro_HairRibbon is the reference case: leaving the imported `HairRibbon` node's local transform intact double-applies the FBX root offset.
- Read `PrefabInstance.m_Modifications` transform values per target GUID/fileID, never by property path alone; bone overrides coexist with instance placement overrides. Imported models use synthetic `//RootNode` Transform fileID `-8679921383154817045` for placement. Marycia 2P is the regression case: an unrelated bone has `m_LocalScale.y = 1.3396468`, which must not become the RootNode scale.
- FBX prefab variants can assign every renderer material through `PrefabInstance.m_Modifications` entries such as `m_Materials.Array.data[0]`, while `ModelImporter.externalObjects` is empty or incomplete. Resolve each modification target's FBX renderer fileID with `UnityModelFileIdResolver` and collect the `objectReference.guid` by material slot index. MANUKA lilToon is the regression case: it has 23 prefab material assignments but only one FBX external material mapping.
- A selectable avatar variant may inherit its `VRCAvatarDescriptor` and use exactly the same FBX set as its parent. Do not require an outer prefab to add another FBX before listing it; merge renderer material/blendshape overrides from the prefab inheritance chain in base-to-derived order. `Eku_Another.prefab -> Eku.prefab -> Eku.fbx` is the regression case. Variants with `m_RemovedGameObjects` remain excluded until outer-variant object deletion is supported.
- Nested prefab property targets are not guaranteed to have serialized stripped documents. Unity derives omitted instance-object IDs as `(sourceFileID XOR prefabInstanceFileID) & long.MaxValue`; reverse this with both possible source sign bits, then recursively follow the `PrefabInstance.m_SourcePrefab` GUID until reaching a serialized prefab object or FBX stable fileID. Do not infer renderer assignments from material names.
- `ModelImporter.meshes.fileIdsGeneration=1` hashes newly generated model object IDs as `xxHash64("Type:<type>-><objectName><duplicateIndex>")`, without the hierarchy path used by generation 2. Keep both candidate forms in `UnityModelFileIdResolver`; Listy v5 `tights` is the regression case (`SkinnedMeshRenderer` source ID `-5758174076135708240`).
- An inherited-descriptor candidate stores the descriptor's parent scene, not the selected outer variant scene. When collecting the selected variant's own modifications, reload `Candidate.Source` and use its single `PrefabInstance`; otherwise Listy_Default's `Listy_tights` override is skipped even after the Generation 1 renderer ID resolves.
- When the selected avatar lives in a `.unity` scene as a prefab instance, collect renderer modifications from the descriptor's owning `PrefabInstance`: recurse through its `m_SourcePrefab` chain first, then apply the scene instance's own modifications. Passing the `.unity` asset GUID to prefab-only traversal drops every material assignment; Yuzuki v2.1.1 is the regression case.
- `ModelImporter.externalObjects` can be empty even though Unity resolves imported FBX materials through material search. Reproduce the deterministic mapping from FBX metadata: prefer an exact unique `.mat` filename matching the embedded material name, then a unique `.mat` matching its diffuse texture basename. Yuzuki's VRoid materials use names such as `N00...Body (Instance)` with `texture/tx_body.psd`, which resolves to `tx_body.mat`.
- Prefab material-reference regression cases: Eku_Another costume renderers, BAMESSA PC v1.32 `VRC_BAMESSA Variant_Black` hair slots, Ricorine v1.0.1 `Ricorine_03_Albiflora` Dress renderers, Lina v2.00 `AD01_Lina_tora` hair=`AD01_Lina_hair02`, and Fyuett v1.0 `Fyuett_All_Rubi` (35 assignments across 21 renderers, including `Plane.001: FY_Tulip_Rubi`).
- Unity can fold a sole real mesh node under Assimp's artificial FBX root into the synthetic stable-ID path `//RootNode/root`. Register that path for the real child node, not the artificial root; Fyuett's Tulip uses `//RootNode/root/MeshRenderer` and `//RootNode/root/SkinnedMeshRenderer`.
- Unity model stable fileID hashing uses the bare hierarchy path for `GameObject` (`Type:GameObject->//RootNode/root/Bra0`), unlike components which append `/Transform`, `/SkinnedMeshRenderer`, etc. Including `/GameObject` produces the wrong hash. Resolve inherited `m_IsActive` modifications with these GameObject IDs in base-to-derived order; Eku/Eku_Another should import `Bra` and `Underwear` with `Active=false`.
- Scope prefab `m_IsActive` overrides by resolved source FBX GUID as well as GameObject name. Composed FBXs can reuse names: Fyuett_All_Lala disables the base FBX `HairFront` but its additional `Hair_Front_Macaron_Lala` FBX also contains `HairFront`, which must remain active.
- Unity applies FBX `BlendShapeChannel.DeformPercent` as the model prefab's default blendshape weight, so it does not appear as a prefab override. Assimp/Resonite imports the shape geometry but not this default weight; read binary FBX channel defaults, normalize by the channel's final `FullWeights` value like Unity's renderer conversion, and apply them before export, while letting explicit prefab `m_BlendShapeWeights` (including zero) win. Pilica/Kumagaya `Body_A` has `all=100`, and `Face` has `Mouth_N=100`.
- Apply VRChat initial blendshape weights to each imported `SkinnedMeshRenderer` immediately after blendshape repair, before `AvatarSetup.Build`, then reapply them during final scene setup. Use `SkinnedMeshRenderer.SetBlendShapeWeight`; do not bake default shapes into mesh geometry.
- `--inspect-verbose` reports each saved `SkinnedMeshRenderer`'s `blendshapes=[...]`. Sync-list entries are field dictionaries in the package DataTree, so inspect them through the field's `Data` node rather than treating list children as direct float values.
- Unity YAML can wrap long plain scalar values such as `m_ShaderKeywords` onto a deeper-indented continuation line. Tokenization must join those lines; otherwise parsing stops before `m_SavedProperties`, making valid lilToon materials look empty. Nagma PhysBone's `accessory`, `hair`, `clothes`, and `body` materials are regression cases.

## lilToon conversion notes

- The VRChat path is headless and parses Unity material YAML, so it cannot run lilToon's editor baker shaders. Preserve directly representable properties and texture scale/offset; main-layer, alpha-mask, combined-normal, emission-mask, and channel-combine baking require separate image processing.
- Xiexe `ShadowRampMask` needs a white fallback when lilToon `_ShadowStrengthMask` is absent. Generated lilToon ramps must vary vertically from white to the horizontal ramp so the mask can select shadow strength.
- lilToon MatCap maps only when `_MatCapBlendMode == 1` (Add) and `_MatCapBlendMask` is absent. Scale MatCap RGB by `_MatCapBlend * _MatCapColor.a`.
- MatCap texture opacity must also be baked into RGB (`RGB *= A`) before assigning it to Xiexe. Use `Elements.Assets.TextureDecoder` and save the processed `Bitmap2D` through LocalDB so formats supported by Resonite, including non-WIC Unity textures, remain decodable.
- Unity YAML double-quoted scalars can encode non-ASCII GameObject names with `\uXXXX`. Decode YAML escapes before matching prefab names to imported FBX slots; otherwise `RemoveDeletedMeshes` can delete every non-ASCII renderer. Platinum v1.3 is the regression case: 13 renderers should remain, including `髪`, `シャツ`, `ブレザー`, and `素体`.
