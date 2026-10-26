# wiifit-lab

Wii Fit バランスボードの中身を読んで、PC 向けの別のものに作り替えた記録だ。

Bluetooth HID の通信を直接読み、ボードが返す生の値を取り出して、計測ツール、ゲーム、ペダル、VRChat の入力装置にしている。

English: I opened up the Wii Balance Board at the protocol level (raw Bluetooth HID reports, factory calibration data, temperature correction) and rebuilt it as a measurement tool, a set of minigames, a pedal controller, and a VRChat input device.

## やったこと

- **生の通信を読む**: 4つの荷重センサーの 16-bit 生値を、通信レポート単位(実機で約100回/秒)で取り出している。
- **ボード内部の値を使う**: ボードの個体ごとに持つ 0 / 17 / 34 kg の工場校正点を読み、区間ごとの感度を計算して重量を出す。内部温度と工場基準温度から温度補正も掛け、補正前後の重量を並べて確認できる。
- **見える化する**: 技術モニターで、各センサーの生値、実測の受信周波数、重量・重心・温度の直近60秒の波形を表示する。診断画面と CSV 記録も付けた。
- **入力装置に作り替える**: 左足がブレーキ、右足がアクセルの疑似ペダル(キー入力と vJoy のアナログ出力)。重心移動で遊ぶブロック崩し、ホバーレース、3D バランステーブル、サイドスタンスのスノーボードなどのミニゲーム。
- **VRChat につなぐ**: 歩行・スキー・スノーボードの動きをボードの重心から作り、VRChat 公式の OSC で送るブリッジ。80ms フィルター、モードごとの校正、250ms 信号が途切れたら出力をゼロに戻す安全条件を入れた。確認用の VRChat ワールドも作った。

関連: Wii Fit を Windows の巨大なトラックパッドにする [wiifit-trackpad](https://github.com/bruiselea/wiifit-trackpad)。

## 中身

| フォルダ | 内容 |
|---|---|
| `WiiFitLab/work/wii-fit-lab` | 計測・診断・ミニゲーム・疑似ペダルを備えた Windows アプリ(WPF)。v0.10.0 |
| `WiiFitVRBridge` | 歩行・スキー・スノーボードの入力を VRChat の OSC に送るブリッジ。v0.1.0 |
| `WiiFitVRBridge/world/WiiFitMotionLab` | ブリッジの動作確認用の VRChat ワールド(Unity 2022.3.22f1 / VRChat Worlds SDK 3.10.4) |

C# / .NET 8 / WPF / Unity。

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
