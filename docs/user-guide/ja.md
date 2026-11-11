# ResoPon アバターを一発でResoniteにアップロード！

アバターを超簡単にResoniteにアップロードできるツールです。

VRChat向けアバターのUnitypackageとPrefab、またはVRMに対応！（VRoidじゃなくても使えます！）

[The English documentation is available here.](en.md)

![BoothImageResoPon.png](assets/hero.png)

## ✨️準備

[Resonite](https://store.steampowered.com/app/2519830/Resonite/?l=japanese)をインストールし、一度は起動しておいてください。

## 🔰使ってみる

ResoPon.exeを起動してください。

![image.png](assets/start.png)

（初回起動時にネットワークの許可を求められる場合があります。Resoniteを起動しているためです。許可してもしなくてもResoPonには影響ありません。）

ResoPonの画面にアップロードしたいアバターファイル（Unitypackage、Prefab、VRMに対応）をドラッグしてください。

変換が始まります。数十秒～1分程度かかります。

![image.png](assets/converting.png)

変換完了したら、画面上のアイコンをResoniteの画面にドラッグしてください。

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

コンテキストメニューを開いてください。
（Quest：B/Yボタン　Index：Bボタン　デスクトップ：マウスホイールボタン）

Expressionを押してください。

![image.png](assets/expression-menu.png)

変えたい表情を押してください。

![image.png](assets/select-expression.png)

## ⚠注意

ご使用になるVRMファイルの使用許諾・ライセンス情報とResoniteのTerms of Use・EULAをよくご確認の上、ルールを守ってご使用ください。

Resonite ポリシー
[https://resonite.com/policies/index.html](https://resonite.com/policies/index.html)

## ❓️Q&A

### アップロードしたアバターは自分専用になっていますか？（他人に保存されたりしませんか？）

初期設定で自分専用・他人に保存できない状態になっています。

アプリの歯車アイコンから「アバター保護を付けない」で誰でも利用できるように変更することも可能です。

### アバターを原寸サイズで使いたい（着た後スケールリセットしてもデカくならないようにしたい）

アプリの歯車アイコンから「アバターの原寸サイズを維持」をオンにして変換してください。

### アバターの一部が暗いところで光って見えます

半透明になっている（MToonのAlphaModeがTransparent）マテリアルがそのように変換されます。

XiexeToonの仕様上半透明の表示と影を受け取る処理を両立することが難しいため、半透明を優先しています。

アプリの歯車アイコンから、以下の設定をオンにすることで変換時に処理を選ぶことができます。

Cutoutを選べば、光らなくなります。（代わりに透明度は透明か不透明かの2値になります）

![image.png](assets/transparency-setting-ja.png)

![image.png](assets/transparency-mode-ja.png)

### 変換失敗しました/変換できたけどResonite上でうまく動きません

[Resonite Japan Discord](https://discord.gg/pvjt2BDMRG)の[ResoPonスレッド](https://discord.com/channels/1155744905604366489/1514226522813239346)に以下の内容をお知らせください。

- 変換しようとしたアバター
- どこがうまく動いていないか
- ログファイル（ResoPonと同じフォルダにあるLogフォルダ内にあります）

### フェイストラッキング対応アバターをアップロードするには？

アプリの歯車アイコンから「フェイストラッキング用ドライバーを生成」をオンにしてください。

アバターがResoniteのフェイストラッキング要件に合ったシェイプキーを持っている必要があります。

### カスタムのExpressionに対応していますか？

していません。VRM標準の5種類のみに対応しています。（VRM0.xは4種類）

### コンテキストメニューにExpressionボタンがありません

VRMアバターに表情のExpression（VRM0.xはBlendShapeClip）が設定されていない場合はボタンを出さないようになっています。

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
