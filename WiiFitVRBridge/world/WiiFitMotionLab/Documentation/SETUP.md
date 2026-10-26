# Wii Fit Motion Lab v0.1

PC版VRChatでWii Balance VR BridgeのOSC入力を検証するための軽量Worldです。Quest単体版は対象外です。

## 初回セットアップ

1. Unity HubでUnity IDへサインインし、Unity Personalライセンスを有効にする。
2. VCCでこのフォルダーをWorld Projectとして登録し、Worlds SDKとUdonSharpを追加する。
3. Unityでプロジェクトを開く。
4. メニュー `Wii Fit Motion Lab > Build v0.1 Test Scene` を実行する。
5. `Assets/WiiFitMotionLab/WiiFitMotionLab.unity` を開き、VRChat SDK Control Panelから `Build & Test` を実行する。

この環境ではVCC、Unity Hub、Unity `2022.3.22f1`、Worlds SDK `3.10.4`をすべて `E:\WiiFitVRBridge` 配下へ配置済みです。Unity Hubのサインインだけは本人操作が必要です。

シーン生成メニューは平坦な歩行レーン、スキー斜面、スノボ斜面、リセット台、落下復帰をまとめて作成します。再実行すると試験シーンを作り直します。

## 試験手順

1. VRChat Action Menuの `Options > OSC > Enabled` をONにする。
2. Bridgeでボードを接続し、乗った状態でニュートラル記録を行う。
3. `F8/F9/F10` でWorld内のコースと同じモードを選び、`VRChat出力ON` を押す。
4. 歩行は歩行レーン、スキー／スノボは対応する色の斜面へ入る。
5. コースを出たとき、歩行速度・走行速度・横移動・ジャンプ・重力が入場前の値へ復元されることを確認する。
6. `F12`、ボード電源断、Bridge終了の各操作で300ms以内に入力がゼロへ戻ることを確認する。

## 物理初期値

- スキー最高速度: 12 m/s
- スノボ最高速度: 10 m/s
- 最大旋回: 55度/秒
- ブレーキ: 6 m/s²
- 抗力: 0.35/秒

入力を横方向の速度へ直接変換せず、雪面に沿った現在の進行方向を徐々に回転させます。前後入力は推進とブレーキの意図として扱い、斜面重力・速度・エッジ荷重が滑走へ影響します。

## 安全上の注意

Balance Board上では実際にジャンプしないでください。F12はBridgeの緊急停止であり、World内ではコース退出または黄色いRESET台で標準移動へ戻せます。
