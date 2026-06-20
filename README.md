# YMB Thatuation

Ferdium 代替の軽量マルチサービス・メッセンジャーランチャー。
C# + WPF + WebView2(Microsoft Edge WebView2 Runtime)製。
Chromium を同梱せず OS の WebView2 を共有するため、Electron系ツールより省メモリ。

## 技術構成

- C# / .NET 8 (`net8.0-windows`) + WPF
- WebView2 SDK: 1つの `CoreWebView2Environment` を共有し、サービス(インスタンス)ごとに
  `CoreWebView2ControllerOptions.ProfileName` を分けることで Cookie / LocalStorage / 拡張機能設定を分離。
- UI は `wwwroot/` 配下の静的HTML/CSS/JS(IPCは `window.chrome.webview.hostObjects.ymb` 経由)。

## 主な機能

- **マルチインスタンス**: 左サイドバーにサービスアイコンが並ぶ。同一サービスを
  複数アカウントで同時利用可能(プロファイル分離)。
- **対応レシピ**: Gmail / Google Calendar / Google Chat / Chatwork / Slack / Microsoft Teams /
  Messenger / Discord / Notion / YouTube / X (Twitter) / deck.blue / Misskey / WhatsApp /
  Telegram / LinkedIn / Instagram / Reddit / Trello / GitHub / Twitch / Zoom / Google Drive /
  Google Keep / Threads / Linear / その他(URL指定)。
  サービスごとにブラウザUA偽装の既定/有効/無効を切り替え可能。
  追加時、URL指定サービスはfaviconを自動取得してアイコンに使用。
- **未読バッジ + デスクトップ通知**: ページタイトルの「(N)」表記を検知して
  サイドバー・タスクバーにバッジ表示、通知音つきデスクトップ通知。
  サービス単位で通知ミュート可能。
- **自動スリープ**: 非アクティブが続いたサービスのWebViewを破棄してメモリ解放。
  サービスごとに「スリープさせない」指定が可能。音声/動画再生中のサービスは
  自動スリープの対象外。
- **メモリ使用量表示**: 各サービス・本体プロセス群の実メモリ使用量を設定画面に表示。
- **Chrome拡張機能**: 展開済み拡張の読み込み、Chromeウェブストアからの直接インストール、
  更新チェック、初期設定のリセット(1Password等)に対応。
- **トレイ常駐 / 自動起動 / 二重起動防止 / ウインドウ状態の保存復元**
  (マルチモニタ構成が変わっても画面外に出ないよう補正)。
- **多言語対応 (i18n)**: 日本語 / English。言語切替は即時反映。
- **設定のエクスポート/インポート**: サービス一覧・各種設定をJSONで移行可能。
- **本体の更新確認**: GitHub上の `version.json` と現在バージョンを比較し、
  起動時に自動チェック+設定画面から手動チェックも可能。
- **新規ウインドウ/OAuthポップアップ**: 既定の枠付きポップアップ(鍵マーク操作で
  ブラウザプロセスが落ちる既知の問題があった)は使わず、既定の外部ブラウザで開く。

## ビルド・実行

```
cd src-csharp/YmbThatuation
dotnet build
dotnet run
```

## 配布用ビルド(self-contained + インストーラー)

```
cd src-csharp/YmbThatuation
dotnet publish -c Release -r win-x64 --self-contained true
```

`installer/setup.iss` を Inno Setup でコンパイルすると `installer/Output/` に
`YmbThatuation-Setup-<version>.exe` が生成される(WebView2 Runtime未導入の場合は案内表示)。

> **注意**: このインストーラーはコード署名されていません。ダウンロード・実行時に
> Windows SmartScreenが「不明な発行元」として警告を表示する場合があります。
> 「詳細情報」→「実行」で続行できます。

## データ保存先

`%APPDATA%\jp.yumebi.thatuation-cs\`
(`config\config.json`、`webview2\`(プロファイル)、`extensions\`、`logs\crash.log` 等)

## 使い方

- 左サイドバー: クリックでサービス起動/切り替え。半透明=スリープ中、
  虹色枠=表示中、赤バッジ=未読数。
- 右クリックで個別メニュー(スリープ/再読込/編集)。
- `⚙`: 設定画面(サービス管理 / 拡張機能 / 一般設定)。

## 備考

`src-tauri/` (Tauri + Rust による試作版)は未完成のため本リポジトリには含めていない。
Tauri 2 + wry の `with_profile_name`(WebView2のProfileName指定、マルチインスタンスの
プロファイル分離に必須)が現状devブランチのみで未リリースのため開発を一時保留している。
これが正式リリースされ、マルチインスタンス関連機能を実装できるようになった時点で
開発を再開する予定。

## ライセンス

[MIT License](LICENSE) © 2026 ymb
