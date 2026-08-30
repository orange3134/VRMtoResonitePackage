# ヘッドレス FrooxEngine 実装

この資料は `Converter.cs`、`Program.cs`、`ResoniteLocator.cs`、`LocalDbMaintenance.cs` を変更するときに読む。

## DLLの読み込み

ResoPonはResonite DLLを配布せず、ビルド時と実行時の両方でインストール済みResoniteを参照する。

- マネージドDLLは `AppDomain.CurrentDomain.AssemblyResolve` からResoniteディレクトリを探索する。
- ネイティブDLLは `runtimes/win-x64/native` と `runtimes/win10-x64/native` を含め、
  `AssemblyLoadContext.Default.ResolvingUnmanagedDll` で解決する。
- `EngineInitializer` はカレントディレクトリから `ProtoFluxBindings.dll` を探すため、エンジン起動前に
  CWDをResoniteディレクトリへ変更する。省略するとProtoFlux初期化で失敗する。

## Resonite APIのバイナリ互換性

FrooxEngineの公開プロパティや列挙型の戻り値型が変わると、ソース互換でも既存EXEは
`MissingMethodException` になる。Resonite更新後にこの症状が出た場合は、対象環境のDLLを指定して
ResoPonを再ビルドする。

配布版と報告環境の比較には、変換ログ先頭のResonite表示バージョンと `FrooxEngine.dll` の
SHA-256を使う。表示バージョンが同一でも、DLL差し替えによりAPI世代が異なる場合がある。

互換性を長く保つ必要がある箇所では、戻り値型が変わりやすいコレクションの直接列挙を避け、
安定したメソッド、件数、インデクサーなどを優先する。ただし実DLLでシグネチャを確認してから変更する。

## LocalDB

Resonite本体とResoPon、または複数のResoPonプロセスでDataディレクトリを共有してはいけない。
`Instance.lock` や暗号鍵が競合し、`Invalid password` やDB破損につながる。

- 実行ごとに `%LOCALAPPDATA%/ResoPon/Data-xxxxxxxx` を作る。
- 終了時に削除し、次回起動時も孤児ディレクトリを掃除する。
- 掃除時は `Instance.lock` で生存中プロセスを判定する。
- `LocalKey.bin` だけは共有し、MachineIDを安定させる。
- Cacheは `%LOCALAPPDATA%/ResoPon/Cache` を共有する。

## ワールドと終了処理

`DoNotAutoLoadHome=true` のヘッドレス環境では、`Userspace.ExitWorld` や `RunAction` がフォーカス移譲先を
待ち続けることがある。そのため変換用ワールドを1つ作って全入力で使い回し、個別には閉じない。

終了時は `runner.Shutdown()` にタイムアウトを設ける。FrooxEngineの更新ループはフォアグラウンド
スレッドなので、Shutdownが戻らない場合でも最後は `Environment.Exit` でプロセスを終了させる。

パッケージ出力後にバックグラウンドのGatherJobが残っていると、終了中に `ObjectDisposedException` が
記録されることがある。出力完了前の例外と混同せず、`RESOPON_OUTPUT`、完了行、生成物の再読込で成否を判断する。

## パッケージ出力

現在の出力経路は次の順序である。

1. `Slot.SaveObject(DependencyHandling.CollectAssets)`
2. `RecordHelper.CreateForObject<Record>`
3. `PackageCreator.BuildPackage`

生成後は `--inspect` でRecordPackageを再読込し、スロット、コンポーネント、アセットを確認できる。
