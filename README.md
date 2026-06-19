# YMB Thatuation

Ferdium 代替の軽量マルチサービス・メッセンジャー。Tauri 2 (Rust + WebView2) 製。
Chromium を同梱せず OS の WebView2 を共有するため、シェル本体は約30MB・配布は数MBで動く。

## 実装済み機能(2026-06-13)

- **マルチWebView / 多重起動**: 1ウィンドウ左サイドバーにインスタンスが並ぶ。
  インスタンスごとに WebView2 の `data_directory` を分離するので、Gmail×2・Chatwork×2 等の
  同一サービス別アカウントを同時利用できる。
- **インスタンス管理**: `%APPDATA%\jp.yumebi.thatuation\config.json`。
  設定画面から追加 / 編集 / 削除 / 並び替え。
- **レシピ(プリセット)**: gmail / googlecalendar / googlechat / googledrive / googlekeep /
  chatwork / slack / teams / messenger / discord / notion / youtube / x / deckblue / misskey /
  whatsapp / telegram / linkedin / instagram / reddit / trello / github / twitch / zoom /
  threads / linear / generic(URL指定)。
  Google系・Meta系(Messenger/Instagram/Threads/WhatsApp)・Notion・LinkedIn は
  既定でChrome UA を偽装(WebView系UAだとログイン拒否されることがあるため)。
  各インスタンスの編集画面でUA偽装を個別に上書き可能(既定/する/しない)。
  Google Chatは `chat.google.com` を使用(`mail.google.com/chat/` は初回ナビゲーションで
  about:blankになる問題があったため変更)。
  各サービスは公式ロゴSVG(ui/icons/<recipe>.svg、旧Ferdiumレシピ由来)をサイドバーに表示。
  generic は頭文字にフォールバック。識別カラーは右下のドットで表示。
  設定画面からローカル画像を選んで各サービスのアイコンを上書き可能(data URLでconfig.jsonに保存)。
- **未読バッジ + デスクトップ通知**: ページタイトル内のどこにある「(N)」も拾う
  (例: Gmail「Inbox (3)」, Chatwork「(5) Chatwork」)。数字以外の括弧は誤検知しない。
  未読が増えたとき通知(設定でOFF可)。
  ⚠ タイトル監視はWebView生存中のみ。**スリープ中は未読更新されない**(最後の値で固定)。
  例外: Chatwork はトークン登録でスリープ中もAPIで未読取得。
- **トレイ常駐**: ×で閉じてもトレイに残る(設定でOFF可)。
- **自動スリープ**: 非表示がN分続いたWebViewを自動破棄してメモリ解放。
  サービスごとに「スリープさせない」指定が可能。
- **メモリ使用量表示**: 各インスタンスのWebView2プロセス群の実メモリ(MB)を
  設定画面の一覧とサイドバーのツールチップに表示(sysinfoで集計、10秒ごとに自動更新)。
  設定画面の「メモリ使用量を更新」ボタンで即時再集計も可能。
- **Chrome拡張**: `extensions\` フォルダの展開済み拡張を全サービスに読み込む
  (WebView2 `AddBrowserExtension`、MV3対応)。
  Chromeウェブストアからの直接インストール(設定画面にURL/IDを入力 → CRXダウンロード→展開)に対応。
  コンテンツ系拡張(uBlock Origin / Dark Reader等)が対象。Dark Reader / 1Password / 自作テスト拡張で動作確認済み。
  拡張の名前・説明文が `__MSG_xxx__` のまま表示される問題を解消(`_locales/<default_locale>/messages.json` を解決)。
- **タスクバー未読バッジ**: 全インスタンスの未読数合計が1以上のとき、タスクバーアイコンに
  赤丸のオーバーレイバッジを表示(`Window::set_overlay_icon`、Windows専用)。
- **待機画面**: 全サービスがスリープ中/初期起動時はメイン表示エリアに黒背景の待機画面
  (`ui/welcome.html`)を表示する。アプリロゴ・名前・簡単な使い方を表示し、
  サービスを起動すると自動的に隠れる。ウインドウ全体の背景色も黒に設定。
- **拡張機能のCWS自動更新**: 設定画面の「一覧を再読込」をクリックすると、
  CWSからインストール済みの拡張(フォルダ名が32文字のCWS ID)について最新版を取得し、
  バージョンが上がっていれば自動的に差し替える。
- **本体メモリ使用量の表示**: 各サービスのメモリ使用量に加えて、サイドバー/設定/待機画面の
  WebView2基盤プロセス群(本体自身)のメモリ使用量を設定画面に表示する。
- **UA偽装の個別設定**: サービスごとに「ブラウザのUA偽装」を
  既定(レシピに従う)/ Chromeとして偽装する / 偽装しない、から選択できる。
- **汎用未読数スキャナ**: ページ内の `unread` 系クラス名/aria-labelを持つ要素から
  数字バッジを5秒おきに拾い、`document.title` の先頭に `(N)` を付与する
  (API・トークン不要。既存の未読バッジ検知ロジックがそのまま反応する)。
- **別ウインドウ遷移(`window.open`/`target=_blank`/OAuthポップアップ)**:
  既定の枠なしポップアップではなく、タイトルバー付きの通常ウインドウとして開く
  (Dark Readerの寄付ページ、1Passwordの導入ページ等で動作確認済み)。
- **ウインドウサイズ・位置の保存**: `tauri-plugin-window-state` を登録済み
  (現状は実機での永続化確認に至っておらず、要追加調査)。
- **インストーラ(NSIS)**: `tauri.conf.json` の `bundle.active` を有効化し、
  `cargo tauri build` でNSISインストーラを生成できる設定にした(ビルド自体は未実施)。

メモリ実測(WorkingSet合計): 全サービススリープ時 約360MB / 1サービス起動 +600MB前後。
(参考: 同一マシンの Ferdium は約3.7GB)

## 追加実装(2026-06-14)

- **スリープ解除UI**: サイドバーのスリープ中サービスをクリックすると、その場では起動せず
  選択状態にするだけで、右側のメイン画面に「サービス名のスリープを解除する」ボタンが
  表示される。クリックすると起動して表示に切り替わる。
- **アクティブアイコンのレインボー枠**: 表示中サービスのアイコン枠を虹色グラデーションで
  強調表示。枠の形(2px帯のマスク)に`conic-gradient`を直接適用した静止表示
  (アニメーションなし)。
- **スリープ中アイコンのグレースケール表示**: スリープ中のサービスアイコンに
  グレースケールフィルタを適用し、起動中サービスとの視認性を向上。
- **再ナビゲートキックの個別設定化**: YouTube Musicで発生していた「初回ナビゲーションが
  about:blankのまま固まる」問題への対処(自動再読み込み)を、各サービスの編集画面の
  「読み込み画面が真っ白なまま固まる場合に再読み込みを試行する」チェックボックスで
  サービス単位にON/OFFできるようにした(既定はOFF)。
- **拡張機能の再設定機能の汎用化**: 旧「1Password再設定」ボタンを「拡張再設定」に変更し、
  1Password以外の拡張機能(初期設定/オンボーディングが必要なもの)にも対応。
  対象サービスの拡張機能の保存状態のみをリセットし、サービス自体のログイン情報は保持する。
- **拡張機能の更新チェックボタンのリネーム**: 設定画面の「一覧を再読込」を「更新」に変更。
- **多言語対応 (i18n)**: 日本語(既定)/ English をUI全体で切り替え可能。
  - 翻訳辞書は `ui/lang/ja.json` / `ui/lang/en.json` にバンドル。
  - `<app_data_dir>/lang/<language>.json` を置くと、該当キーをバンドル分に上書きできる
    (外部からの翻訳追加・修正に対応)。
  - 設定画面の「言語」セレクタで切り替え、保存すると即時に全画面が再翻訳される。
- **設定のエクスポート/インポート**: 設定画面の「本体」セクションから、
  サービス一覧・各種設定をJSONファイルへ書き出し/読み込みできる
  (`tauri-plugin-dialog`によるネイティブファイル選択)。別PCへの設定移行向け。
  ログイン情報(WebView2プロファイル)は対象外で、インポート後の反映には本体の再起動が必要。
- **通知音のカスタマイズ**: デスクトップ通知(未読増加・拡張機能更新)時に再生する音を
  設定画面でON/OFFでき、Windows標準の通知音の代わりに任意のwav/mp3ファイルを指定できる
  (`PlaySoundW`による再生、テスト再生ボタンあり)。
- **設定画面のタブ分割**: 「サービス」「拡張機能」「一般」の3タブに分割し見やすくした。
- **多重起動防止**: `tauri-plugin-single-instance`により、2回目以降の起動では
  既存ウインドウを前面に表示して新規プロセスは終了する。
- **スリープ中サービスのスリープ解除**: サイドバーアイコンの右クリックメニューから
  「スリープ解除」を選択して直接起動できる(待機画面のボタン経由と同じ)。

## ハマりどころ(本実装でも必須の知見)

1. **WebViewを生成するコマンドは必ず `async fn` にする。**
   Tauriの同期コマンドはメインスレッドで実行され、`add_child` が内部のメインスレッド
   ディスパッチ待ちで**デッドロックする**(白画面・IPC死亡)。`run_on_main_thread` も同じ理由でNG。
2. **`withGlobalTauri` は `add_child` で追加した子WebViewに注入されない。**
   UIでは `window.__TAURI_INTERNALS__.invoke` へのフォールバックを使う。
3. **ワーカースレッド(asyncコマンド/監視スレッド)では `Window::webviews()` が空を返す。**
   生存中WebViewの列挙・取得は `Manager::webviews()` / `get_webview()`(アプリ全体)を使う。
4. **`sysinfo` でコマンドライン判定する場合、`refresh_processes_specifics` で
   `ProcessRefreshKind::nothing().with_cmd(Always).with_memory()` を明示する**。
   デフォルトの `refresh_processes` は cmd を取得しないため `cmd()` が空になる。
5. Chrome拡張は WebView2 環境で `browser_extensions_enabled(true)` が必須。
   生COM(`ICoreWebView2Profile7::AddBrowserExtension`)を `with_webview` 経由で呼ぶ。
   `webview2-com` は `wry` と同一バージョンに固定すること(現状 0.38.2)。
6. `time` クレート 0.3.48 は `cookie` 0.18.1 と衝突するため 0.3.47 に固定(Cargo.lock)。

## 実行

```
cd src-tauri
cargo run
```

## 使い方

- 左サイドバー: クリックでサービス起動/切り替え。半透明+グレースケール=スリープ中、
  レインボー枠=表示中、赤バッジ=未読数。スリープ中サービスをクリックすると、
  右側のメイン画面に表示される「スリープを解除する」ボタンから起動できる。
- `Zzz`: 表示中サービスをスリープ(WebView破棄)。`⚙`: 設定画面。
- データは `%APPDATA%\jp.yumebi.thatuation\`(profiles / extensions / config.json)。

## 今後の候補

- `cargo tauri build`(NSIS)の実機検証
- ウインドウサイズ・位置保存の永続化確認(`close_to_tray`の`prevent_close`が
  `tauri-plugin-window-state`の保存フックを妨げていないか要調査)
