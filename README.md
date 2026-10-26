# wiifit-lab

Wii Fit バランスボードを PC につなぐ、自作アプリ群のソースだ。

| フォルダ | 内容 |
|---|---|
| `WiiFitLab/work/wii-fit-lab` | バランスボードの計測・診断・ミニゲームを備えた Windows アプリ(WPF)。4隅の荷重、重心、温度補正、CSV記録、技術モニター、疑似ペダル、ミニゲームを含む。v0.10.0 |
| `WiiFitVRBridge` | 歩行・スキー・スノーボードの入力を VRChat 公式の OSC に送るブリッジ。v0.1.0 |
| `WiiFitVRBridge/world/WiiFitMotionLab` | ブリッジの動作確認用の VRChat ワールド(Unity 2022.3.22f1 / VRChat Worlds SDK 3.10.4) |

C# / .NET 8 / WPF / Unity。

English: source of my Wii Balance Board apps for Windows: a measurement, diagnostics and minigame app, and a bridge that sends walking / ski / snowboard input to VRChat over OSC.

## 他人のコードについて(重要)

このリポジトリには、他人のコードと DLL を **含めていない**。次のものは別途用意する必要がある。

- [Balance Board Controller](https://github.com/Vabian124/balance-board-controller)(MIT): `BalanceBoard.Core` を使っている。`WiiFitLab/work/reference-balance-board-controller` に置く想定のパス参照になっている。
- WiimoteLib(Brian Peek 氏、MIT): バランスボードとの HID 通信に使っている。`WiiFitLab/work/wiimotelib-decompiled` を参照する設定になっている。
- VRChat Worlds SDK / UdonSharp: VRChat Creator Companion(VCC)で復元する。

`.csproj` の `ProjectReference` と `Copy` の参照先は上のパス構成のままなので、このリポジトリ単体ではビルドできない。参照先を自分の環境に合わせて直す必要がある。このリポジトリだけの状態でのビルドは、確認していない。

Wii、Wii Fit、Nintendo は任天堂の商標である。VRChat は VRChat Inc. の商標である。このプロジェクトは、どちらとも関係がなく、承認も受けていない。

## 構成

- Wii Fit Lab: 上の `BalanceBoard.Core` を参照して、計測、記録、ミニゲームを提供する。
- WiiFitVRBridge: ボードの重心から移動入力を作り、OSC で VRChat に送る。コアのテストは `dotnet test` で動く。
- ワールド: `Documentation/SETUP.md` の手順で、VCC からプロジェクトを開く。

```powershell
dotnet test .\WiiFitVRBridge\tests\WiiFitVRBridge.Tests\WiiFitVRBridge.Tests.csproj -c Release
```

## 安全

バランスボードに乗るときは、平らで滑らない床に置き、壁や手すりにつかまる。ジャンプや強く踏む動きは避ける。

## License

[MIT](LICENSE)。他人のコードと DLL には、それぞれの上流のライセンスが適用される。
