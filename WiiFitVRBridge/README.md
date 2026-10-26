# Wii Balance VR Bridge v0.1

Wii Balance BoardをPC版VRChatの公式OSC移動入力へ変換する、専用Windowsアプリと試験Worldです。既存の `E:\WiiFitLab` とは別プロジェクトで、vJoy・キー入力注入・SteamVR仮想トラッカーは使いません。

## 構成

- `src/WiiFitVRBridge.Core`: 80msフィルター、校正、歩行／スキー／スノボ変換、OSC送信
- `src/WiiFitVRBridge.App`: .NET 8 WPF Windows x64 UI、Bluetooth再接続、グローバルホットキー
- `tests/WiiFitVRBridge.Tests`: 変換とOSCループバックの自動試験
- `world/WiiFitMotionLab`: Unity 2022.3.22f1、VRChat Worlds SDK 3.10.4、UdonSharp試験World
- `data`: 接続情報・モード別校正値
- `outputs`: 実行可能な配布フォルダーとZIP
- `tools`: Eドライブ配置のVCC、Unity Hub、Unity 2022.3.22f1

ルートの `OPEN-BRIDGE.cmd`、`OPEN-VCC.cmd`、`OPEN-UNITY-HUB.cmd` から各ツールを起動できます。

## Bridgeの安全条件

OSC出力には、現在のモードでのニュートラル記録と明示的な `VRChat出力ON` の両方が必要です。F8/F9/F10でモードを変えた場合は自動で停止します。F12、切断、250msの信号停止、終了時には両軸がゼロになります。

## 開発・試験

ローカルの.NET 8 SDKを使う例:

```powershell
E:\WiiFitLab\work\.dotnet\dotnet.exe build E:\WiiFitVRBridge\src\WiiFitVRBridge.App\WiiFitVRBridge.App.csproj -c Release
E:\WiiFitLab\work\.dotnet\dotnet.exe test E:\WiiFitVRBridge\tests\WiiFitVRBridge.Tests\WiiFitVRBridge.Tests.csproj -c Release
```

WorldのセットアップとBuild & Test手順は `world/WiiFitMotionLab/Documentation/SETUP.md` を参照してください。
