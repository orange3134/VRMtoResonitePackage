# ResoPon アバターを一発でResoniteにアップロード！

アバターを超簡単にResoniteにアップロードできるツールです。

VRChat向けアバターのUnitypackageとPrefab、またはVRMに対応！（VRoidじゃなくても使えます！）

[The English documentation is available here.](en.md)

このガイドは現行の操作に合わせています。画像は初期版のため、現在の画面と項目名・配置が異なる場合があります。

![BoothImageResoPon.png](assets/hero.png)

## ✨️準備

Windows x64と **.NET 10 Desktop Runtime（x64）** が必要です。

[Resonite](https://store.steampowered.com/app/2519830/Resonite/?l=japanese)をインストールし、一度は起動しておいてください。

[Booth](https://orange3134.booth.pm/items/8515694)または[GitHub Releases](https://github.com/orange3134/VRMtoResonitePackage/releases)からResoPonをダウンロードしてください。

Resoniteが自動検出されない場合は、ResoPonの歯車アイコンから「Resoniteフォルダ」を指定してください。

## 🔰使ってみる

ResoPon.exeを起動してください。

![image.png](assets/start.png)

（初回起動時にネットワークの許可を求められる場合があります。変換中にResoniteのエンジンを起動するためです。）

ResoPonの画面にアップロードしたいアバターファイル（Unitypackage、Prefab、VRMに対応）をドラッグしてください。

- VRMは0.x / 1.0に対応しています。
- `.unitypackage` に複数のアバターがある場合は、変換する対象を選んでください。
- `.prefab` は元のUnityプロジェクト内にあるファイルを指定してください。Prefabだけを別の場所へコピーしたものは使えません。Unity Editorの起動は不要ですが、関連アセットと必要なパッケージがプロジェクト内で解決できる必要があります。Modular Avatar / NDMFのビルド処理は実行しません。

変換が始まります。所要時間はアバターの複雑さやPCによって異なります。
出力される `.resonitepackage` は、初期設定では入力ファイルと同じフォルダに保存されます。変更する場合は歯車アイコンから「出力先フォルダ」を指定してください。

![image.png](assets/converting.png)

変換完了したら、Resoniteを起動してワールドに入り、画面上のアイコンをResoniteの画面にドラッグしてください。

![image.png](assets/import-to-resonite.png)

アバターが出てきました！

![image.png](assets/imported-avatar.png)

アバターをクリックして「アバターを着る」

![image.png](assets/equip-avatar.png)

うまく着られました！インベントリを開いて「現在のアバターを保存」しておきましょう。

![image.png](assets/equipped-avatar.png)

![image.png](assets/save-avatar.png)

お気に入りのアバターでResoniteをお楽しみください！

## ☺️表情を変える

アバターを着た状態でコンテキストメニューを開いてください。
（Quest：B/Yボタン　Index：Bボタン　デスクトップ：マウスホイールボタン）

`Expressions` → `Select expression` を押してください。
以下の画像は初期版の `Expression` メニューです。現行版では名称と階層が変わっています。

![image.png](assets/expression-menu.png)

変えたい表情を押してください。表情が9個以上ある場合は `Group 1`、`Group 2` などから選びます。
選んだ表情は保持されます。同じ表情をもう一度押すか、`Expressions` の `Reset` で解除できます。
以下は初期版の表情一覧です。現行版では `neutral` を選ぶ代わりに表情の保持を解除します。

![image.png](assets/select-expression.png)

### ハンドサイン・キーボードでも操作する

`Expressions` の `Hand gestures` から、`Both hands enabled`（両手）、`Left hand only`（左手のみ）、`Right hand only`（右手のみ）、`Both hands disabled`（無効）を選べます。
選択中の状態は `Hand gestures: Both / LeftOnly / RightOnly / None` と表示されます。

キーボードでは `Shift + テンキー0～7` が左手、`Ctrl + テンキー0～7` が右手、`Ctrl + Shift + テンキー0～7` が両手の操作です。
VRMの標準割り当ては、3が悲しみ、4が喜び、5が怒り、6が驚き、7がリラックスです。モデルにない表情は割り当てられません。
VRChatアバターでは取り込めた元の割り当てを使います。

メニューで選んだ表情を保持している間はハンドサイン・キーボードで上書きしません。切り替わらない場合は、先に `Reset` で解除してください。

## ⚠注意

ご使用になるアバター・素材の使用許諾・ライセンス情報とResoniteのTerms of Use・EULAをよくご確認の上、ルールを守ってご使用ください。

Resonite ポリシー
[https://resonite.com/policies/index.html](https://resonite.com/policies/index.html)

## ❓️Q&A

### アップロードしたアバターは自分専用になっていますか？（他人に保存されたりしませんか？）

初期設定ではアバター保護が付き、Resoniteにパッケージをインポートしたユーザーに所有者が設定されます。
保護によって他のユーザーによる通常の保存・利用を制限します。

アプリの歯車アイコンから「アバター保護を付けない」で誰でも利用できるように変更することも可能です。

### アバターを原寸サイズで使いたい（着た後スケールリセットしてもデカくならないようにしたい）

アプリの歯車アイコンから「アバターの原寸サイズを維持」をオンにして変換してください。

### アバターの一部が暗いところで光って見えます

半透明になっている（MToonのAlphaModeがTransparent）マテリアルがそのように変換されます。

XiexeToonの仕様上半透明の表示と影を受け取る処理を両立することが難しいため、半透明を優先しています。

アプリの歯車アイコンから、以下の設定をオンにすることで変換時に処理を選ぶことができます。

設定名は「半透明マテリアルの変換先を手動で選ぶ」です。VRMの対象マテリアルがある場合、変換前に材質ごとに `Alpha` / `Cutout` を選べます。この選択画面はunitypackage / prefabには表示されません。

Cutoutを選ぶとライティングに馴染みやすくなります。（代わりに透明度は透明か不透明かの2値になります）

![image.png](assets/transparency-setting-ja.png)

![image.png](assets/transparency-mode-ja.png)

### 変換失敗しました/変換できたけどResonite上でうまく動きません

[Resonite Japan Discord](https://discord.gg/pvjt2BDMRG)の[ResoPonスレッド](https://discord.com/channels/1155744905604366489/1514226522813239346)に以下の内容をお知らせください。

- 変換しようとしたアバターの名前・形式と再現手順
- どこがうまく動いていないか
- ResoPonとResoniteのバージョン
- ログファイル（ResoPon.exeと同じフォルダにある `Logs/convert_*.log`）

設定画面の下部、または変換失敗画面の「ログフォルダを開く」からログを確認できます。
Resonite更新後に動かなくなった場合は、対応するResoPonの最新版を確認してください。

### フェイストラッキング対応アバターをアップロードするには？

アプリの歯車アイコンから「フェイストラッキング用ドライバーを生成」をオンにしてください。

アバターがResoniteのフェイストラッキング要件に合ったシェイプキーを持っている必要があります。

### カスタムのExpressionに対応していますか？

対応しています。VRMでは取り込める顔のブレンドシェイプを持つカスタム表情もメニューに残ります。ハンドサインへの標準割り当てがなくてもメニューから選べます。
VRM0.xのカスタム表情 `Surprised` は、驚きの標準割り当てにも使います。

VRChatでは検出できた最初の表情セットから顔の表情を取り込みます。元のExpression Menu全体や、衣装切り替えなどの任意のギミックを再現するものではありません。

### コンテキストメニューにExpressionsボタンがありません

歯車アイコンの「表情メニューを生成しない」と「アバターセットアップを行わない」がオフになっているか確認し、再変換・再インポートしてください。
取り込める顔の表情がない場合もメニューは生成されません。

「表情メニューを生成しない」はメニューだけを省略します。ハンドサイン・キーボードでの表情操作や口パク・瞬きは引き続き利用できます。

### 表示言語やアップデート確認を変更したい

歯車アイコンから「言語」と「起動時にアップデートを確認する」を変更できます。
更新が見つかると確認画面が表示され、同意するとダウンロード・実行ファイルの差し替え・再起動を行います。

## ライセンス

MIT license

## スペシャルサンクス

コントリビューター

yoshi1123_さん

claude Fable 5ちゃん

claude Opus4.8ちゃん

ChatGPT GPT5.5ちゃん

ロゴ作成

YOURさん

テストに参加いただいたみなさま

アバター作者のみなさま
