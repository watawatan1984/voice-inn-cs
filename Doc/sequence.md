# Voice In - シーケンス図集

**文書バージョン**: 1.0.0  
**作成日**: 2026-09-04  
**ステータス**: 正式承認版  

---

## 1. 音声入力・文字起こし・自動貼り付け (正常系)

ユーザーが `Left Alt` を長押しして話し、離すと自動的にアクティブウィンドウへ貼り付けられるメインフロー。

```mermaid
sequenceDiagram
    autonumber
    actor User as ユーザー
    participant Hook as KeyboardHook
    participant App as App (Orchestrator)
    participant Overlay as OverlayWindow
    participant Audio as AudioRecorder
    participant Proc as AudioSampleProcessor
    participant WinDet as WindowDetector
    participant AI as IAiProvider (Gemini/Groq)
    participant Hist as HistoryManager
    participant Paste as TextPaster
    participant Target as 入力先アプリ (VSCode/Slack)

    Note over Target: ユーザーがエディタ等にカーソルを置いている
    User->>Hook: Alt_L 押下 (KeyDown)
    Hook->>App: KeyPressed イベント
    App->>WinDet: GetActiveWindow()
    WinDet-->>App: targetWindow (HWND, プロセス名, タイトル)
    App->>Overlay: SetState("recording")
    Overlay->>Overlay: 録音パルスアニメーション開始
    App->>Audio: Start(deviceIndex, maxSeconds)
    
    loop 録音中 (ユーザー発話)
        Audio->>Proc: ApplyGain(buffer, bytes, gain)
        Audio->>Proc: Aggregate(gainBuffer, bytes)
    end

    User->>Hook: Alt_L 離脱 (KeyUp)
    Hook->>App: KeyReleased イベント
    App->>Audio: Stop()
    Audio-->>App: wavFilePath
    App->>Audio: IsSilence(minDuration, rmsThreshold, peakThreshold)
    Audio-->>App: false (有効な音声と判定)

    App->>Overlay: SetState("processing")
    Overlay->>Overlay: 黄色グラデーション & ⏳ 表示

    rect rgb(240, 248, 255)
        Note over App, AI: バックグラウンド非同期タスク (Task.Run)
        App->>WinDet: DetectCategory(targetWindow)
        WinDet-->>App: "DEV" (VSCodeの場合)
        App->>AI: TranscribeAsync(wavFilePath, devPrompt)
        AI-->>App: rawText ("const userId = getUser()")
        App->>App: ユーザー辞書置換適用 (settings.Dictionary)
        App->>Hist: AppendItem(text, null, provider)
    end

    App->>Overlay: Dispatcher.Invoke: SetState("success")
    Overlay->>Overlay: 緑色グラデーション & ✅ 表示

    App->>Paste: PasteTextAsync(text, targetWindow.Hwnd, delayMs)
    Paste->>Paste: クリップボードへテキスト設定 (STAスレッド)
    Paste->>Target: SetForegroundWindow(targetWindow.Hwnd)
    Paste->>Target: keybd_event: VK_MENU (Alt解除)
    Paste->>Target: keybd_event: Ctrl + V 送出
    Target-->>User: テキストが入力される！

    App->>Audio: Cleanup() (一時WAV削除)
    Note over App, Overlay: 1000ms タイマー経過
    App->>Overlay: SetState("idle")
```

---

## 2. VAD による無音・誤タッチの自動キャンセル (VAD スキップ)

誤ってキーに触れた場合や、無音状態でキーを離した際に、不要な API リクエストを送信せず即座に復帰するフロー。

```mermaid
sequenceDiagram
    autonumber
    actor User as ユーザー
    participant Hook as KeyboardHook
    participant App as App (Orchestrator)
    participant Overlay as OverlayWindow
    participant Audio as AudioRecorder

    User->>Hook: Alt_L 誤押下 (KeyDown)
    Hook->>App: KeyPressed
    App->>Overlay: SetState("recording")
    App->>Audio: Start()

    User->>Hook: Alt_L 即座に離脱 (KeyUp: <0.2秒 または 無音)
    Hook->>App: KeyReleased
    App->>Audio: Stop()
    Audio-->>App: wavFilePath
    App->>Audio: IsSilence(minDuration=0.2s)
    Audio-->>App: true (無音または最小時間未満)

    Note over App: API呼び出しを即座にキャンセル
    App->>Audio: Cleanup() (一時WAV削除)
    App->>Overlay: SetState("idle")
    Note over Overlay: ユーザーへのストレスなく待機状態へ復帰
```

---

## 3. AI プロバイダの即時切り替えフロー

タスクトレイのメニューから Gemini と Groq をワンクリックで切り替えるフロー。

```mermaid
sequenceDiagram
    autonumber
    actor User as ユーザー
    participant Tray as NotifyIcon
    participant App as App
    participant SettingsMgr as SettingsManager
    participant Overlay as OverlayWindow

    User->>Tray: トレイアイコンを右クリック
    Tray-->>User: コンテキストメニュー表示
    User->>Tray: 「Groq に切替」をクリック
    Tray->>App: SwitchProvider("groq")
    App->>SettingsMgr: CurrentProvider = "groq"
    SettingsMgr->>SettingsMgr: Environment.SetEnvironmentVariable("AI_PROVIDER", "groq")
    App->>Tray: UpdateTrayMenu() (チェックマーク更新)
    App->>Overlay: SetState("idle")
    Overlay->>Overlay: 枠線色を Groq カラー (#F55036) に更新
    App->>Tray: ShowBalloonTip("AIプロバイダを groq に切り替えました")
```

---

## 4. 設定変更と永続化フロー

設定ダイアログでプロンプトや録音キー、辞書を変更し、適用するフロー。

```mermaid
sequenceDiagram
    autonumber
    actor User as ユーザー
    participant Overlay as OverlayWindow / Tray
    participant SettingsWin as SettingsWindow
    participant SettingsMgr as SettingsManager
    participant Env as EnvLoader / .env
    participant App as App

    User->>Overlay: 右クリックメニュー「設定...」選択
    Overlay->>App: OpenSettings()
    App->>SettingsWin: new SettingsWindow() -> Show()
    SettingsWin->>SettingsMgr: LoadSettings()
    SettingsWin-->>User: 現在の設定値を画面表示

    User->>SettingsWin: 辞書登録、マイク変更、Geminiモデル編集
    User->>SettingsWin: 「保存して適用」ボタン押下
    SettingsWin->>SettingsMgr: Settings プロパティ更新
    SettingsWin->>SettingsMgr: Save() -> settings.json 出力
    SettingsWin->>Env: GEMINI_MODEL 等を環境変数に反映
    SettingsWin->>SettingsWin: SettingsSaved イベント発火
    SettingsWin->>App: 設定保存コールバック
    App->>App: UpdateTrayMenu()
    App->>Overlay: SetState("idle") (スタイル即時再計算)
    SettingsWin-->>User: 完了ダイアログ表示 -> ウィンドウクローズ
```

---

## 5. API エラー・ネットワーク切断時の回復フロー

外部 API のレート制限（429）やネットワーク切断時、クラッシュを防止し安全に復帰するフロー。

```mermaid
sequenceDiagram
    autonumber
    participant App as App
    participant AI as IAiProvider
    participant Overlay as OverlayWindow
    participant Hist as HistoryManager
    participant Tray as NotifyIcon
    participant Audio as AudioRecorder

    App->>AI: TranscribeAsync(...)
    Note over AI: ネットワークエラーまたは API 例外発生
    AI-->>App: throw HttpRequestException("429 Too Many Requests")

    Note over App: catch (Exception ex)
    App->>Hist: AppendItem("", ex.Message, provider)
    App->>Overlay: Dispatcher.Invoke: SetState("error")
    Overlay->>Overlay: 赤色グラデーション & ❌ 表示
    App->>Tray: ShowBalloonTip("Voice In 変換エラー", ex.Message, Error)

    App->>Audio: Cleanup() (一時ファイル削除)
    Note over App, Overlay: 2000ms タイマー経過
    App->>Overlay: SetState("idle")
```
