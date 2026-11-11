# ResoPon — Upload Your Avatar to Resonite in One Shot!

A tool that makes uploading avatars to Resonite super easy!

Supports VRChat avatar Unitypackages and Prefabs, as well as VRM files. You don't have to use VRoid!

![BoothImageResoPon.png](assets/hero.png)

## ✨️Setup

Install [Resonite](https://store.steampowered.com/app/2519830/Resonite/?l=japanese) and launch it at least once.

Please download ResoPon from [Booth](https://orange3134.booth.pm/items/8515694) or the [GitHub releases](https://github.com/orange3134/VRMtoResonitePackage/releases).

## 🔰Getting Started

Launch ResoPon.exe.

(On first launch, you may be asked to allow network access — this is because Resonite is running. It won't affect ResoPon either way, so allow it or not.)

![image.png](assets/start.png)

Drag the avatar file you want to upload onto the ResoPon window. Unitypackage, Prefab, and VRM files are supported.

Conversion starts. It takes anywhere from a few dozen seconds to about a minute.

![image.png](assets/converting.png)

Once conversion finishes, drag the icon on screen into Resonite.

![image.png](assets/import-to-resonite.png)

Your avatar shows up!

![image.png](assets/imported-avatar.png)

Click the avatar and choose "Equip Avatar."

![image.png](assets/equip-avatar.png)

It's on! Open your inventory and use "Save Current Avatar" to keep it.

![image.png](assets/equipped-avatar.png)

![image.png](assets/save-avatar.png)

Enjoy Resonite with your favorite avatar!

## ☺️Changing Expressions

Open the context menu.
(Quest: B/Y button　Index: B button　Desktop: mouse wheel button)

Press Expression.

![image.png](assets/expression-menu.png)

Tap the expression you want to switch to.

![image.png](assets/select-expression.png)

## ⚠Notes

Please carefully review the usage terms and license info of the VRM file you're using, along with Resonite's Terms of Use and EULA, and follow the rules.

Resonite policies
[https://resonite.com/policies/index.html](https://resonite.com/policies/index.html)

## ❓️Q&A

### Is the uploaded avatar private to me? (Can other people save it?)

By default it's set to be yours only — others can't save it.

You can also make it usable by anyone by choosing "Don't add avatar protection" from the gear icon in the app.

### I want to use the avatar at its original size (so it doesn't get huge when I reset scale after equipping)

Turn on "Keep avatar's original size" from the gear icon in the app, then convert.

### Parts of my avatar glow in dark areas

Semi-transparent materials (MToon's AlphaMode set to Transparent) get converted this way.

Because of how XiexeToon works, it's hard to have both semi-transparency and shadow reception at once, so semi-transparency is prioritized.

You can choose how this is handled during conversion by turning on the following setting from the gear icon in the app.

If you pick Cutout, it won't glow anymore. (In exchange, transparency becomes binary — fully transparent or fully opaque.)

![image.png](assets/transparency-setting-en.png)

![image.png](assets/transparency-mode-en.png)

### Conversion failed / it converted but doesn't work right in Resonite

Please post the following in the [ResoPon thread](https://discord.com/channels/1155744905604366489/1514226522813239346) on the [Resonite Japan Discord](https://discord.gg/pvjt2BDMRG).

- The avatar you tried to convert
- What isn't working
- The log file (in the Log folder inside the same folder as ResoPon)

### How do I upload a face-tracking-compatible avatar?

Turn on "Generate face-tracking driver" from the gear icon in the app.

The avatar needs shape keys that meet Resonite's face-tracking requirements.

### Do you support custom Expressions?

No. Only the 5 standard VRM expressions are supported. (VRM0.x has 4.)

### There's no Expression button in the context menu

If the VRM avatar has no expressions set (BlendShapeClip in VRM0.x), the button won't appear.

## License

MIT license

## Special Thanks

Contributors

yoshi1123_

Claude Fable 5

Claude Opus 4.8

ChatGPT GPT5.5

Logo

YOUR

Everyone who joined the testing

All the avatar creators
