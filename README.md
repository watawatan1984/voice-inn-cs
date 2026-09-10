# Voice In (C# / .NET 10 / Windows WPF)

<div align="center">

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Language](https://img.shields.io/badge/Language-C%23%2013-239120?style=for-the-badge&logo=csharp&logoColor=white)
![Framework](https://img.shields.io/badge/UI-WPF%20%2B%20Win32-00599C?style=for-the-badge)
![Tests](https://img.shields.io/badge/Tests-125%20Passed-brightgreen?style=for-the-badge)
![License](https://img.shields.io/badge/License-MIT-green.svg?style=for-the-badge)

### **あなたの声を、あらゆる場所でスマートにテキスト化。**
**キーを長押しして話すだけ。最新のクラウド AI が思考を整え、アクティブなアプリへ直接入力する次世代音声入力アシスタント。**

[ドキュメント一覧 (Doc/)](Doc/README.md) | [要件定義書](Doc/requirements.md) | [アーキテクチャ設計](Doc/architecture.md) | [シーケンス図](Doc/sequence.md) | [クラス図](Doc/class-diagram.md)

</div>

---

## 📖 目次
1. [サービス概要](#-サービス概要)
2. [開発の背景と解決したい課題](#-開発の背景と解決したい課題)
3. [なぜ Python から C# にリプレイスしたのか？](#-なぜ-python-から-c-にリプレイスしたのか技術選定の核心)
4. [主要機能一覧](#-主要機能一覧)
5. [詳細な技術スタック & 選定理由](#-詳細な技術スタック--選定理由)
6. [システムアーキテクチャ](#-システムアーキテクチャ)
7. [設計・実装のこだわり (Engineering Highlights)](#-設計実装のこだわり-engineering-highlights)
8. [ディレクトリ構成](#-ディレクトリ構成)
9. [環境構築 & 使い方](#-環境構築--使い方)
10. [ドキュメント一覧 (Doc/)](#-ドキュメント一覧-doc)
11. [今後のロードマップ](#-今後のロードマップ)
12. [ライセンス & 著者](#-ライセンス--著者)

---

## 💡 サービス概要

**Voice In** は、Windows 上のあらゆるデスクトップアプリケーション（VSCode、ターミナル、Slack、Notion、ブラウザ、Word など）に対して、ホットキー（デフォルト: `Left Alt`）を押し続けて話すだけで、高精度な音声認識・AI文章整形を行い、カーソル位置へ自動的にテキストを流し込む（Auto Paste）常駐型音声入力ツールです。

単なる「音声の文字起こし」にとどまらず、**「思考の速度で入力し、プロフェッショナルな文章として出力する」** ことを目指して開発されました。

```
[話す: "えーっとパイソンでスクリプト書いてギットハブにプッシュしといて"]
                 ⬇️ (Gemini 2.5 Flash / Groq LLM による文脈解析)
[入力結果: "Pythonでスクリプトを作成し、GitHubへプッシュしてください。"]
```

---

## 🎯 開発の背景と解決したい課題

### 1. 開発の背景
エンジニアやビジネスパーソンは、日々のコーディングコメント、プルリクエストの説明文、Issueの起票、Slackでの連絡、ドキュメント執筆など、膨大なタイピング作業に追われています。
タイピング速度の限界や腱鞘炎・手首の疲労は、知的生産性の大きなボトルネックとなっていました。

### 2. 既存ツールの課題
- **OS標準の音声認識**: 専門用語や英語混じりの文章（「Python」「Git」「API」等）が不自然なカタカナや誤変換になる。
- **一般的な文字起こしツール**: 音声アプリ側の画面にテキストが出力されるため、「テキストをコピーして対象アプリへ戻って貼り付ける」という手間が発生し、作業の流れが中断される。
- **無意味な発話ノイズの混入**: 「えーっと」「あー」などの言い淀み（フィラー）がそのまま入力され、後から手動で消去する二度手間が発生する。

### 3. Voice In がもたらす価値
- **思考の速度で直接入力**: 入力したいウィンドウにカーソルを置いたまま、キーを話しながら押すだけで直接文字が入る。
- **プロのテクニカルライター品質**: AIが前後の文脈を把握し、カタカナIT用語を英語スペルへ自動変換、フィラーを完全除去し、整った「です・ます調」へ瞬時にリライト。
- **アプリごとの自動最適化**: ターミナルならコマンド・英語重視、Slackなら敬語重視と、アクティブウィンドウに応じて最適なプロンプトを自動選択。

---

## ⚡ なぜ Python から C# にリプレイスしたのか？（技術選定の核心）

本プロジェクトは、先行して開発されていた **Python (PyQt6 + Rust core) 実装** を破棄し、**.NET 10 / C#** でゼロから完全リプレイスされました。その理由は以下の定量的・定性的な技術的課題にあります。

| 評価項目 | 旧 Python 実装 | 新 C# (.NET 10) 実装 | 選定理由・成果 |
| :--- | :--- | :--- | :--- |
| **起動時間 (Cold Start)** | **約 2.5 秒〜 4.0 秒** (インタープリタ読込) | **約 0.4 秒** (.NET 10 最適化) | デスクトップ常駐アプリとしてストレスのない瞬時起動を実現。 |
| **アイドル時メモリ消費** | **220 MB 〜 280 MB** (Python+Qtオーバーヘッド) | **45 MB 〜 65 MB** | **メモリ消費量を約 75% 削減**。常駐させてもPCの作業領域を圧迫しない。 |
| **OS ネイティブ API 連携** | `ctypes` / `pynput` での不安定なフック | **Win32 P/Invoke による堅牢な制御** | グローバルホットキー、アクティブウィンドウ取得、キー送出の信頼性が大幅向上。 |
| **音声・バイナリ処理** | PythonGIL / Rust拡張 (maturinビルドが必要) | **C# 純粋関数 (AudioSampleProcessor)** | 外部ビルドチェーン不要。ゼロコピー＆高速な16bit PCM演算と100%単体テスト化。 |
| **配布とランタイム** | Python 環境構築や uv/venv の管理が煩雑 | **単一の実行ファイル (.exe) 配布が可能** | エンドユーザーが Python ランタイム不要でダウンロードして即起動可能。 |

---

## 🚀 主要機能一覧

```
+-----------------------------------------------------------------------------------+
|                                Voice In 統合機能群                                 |
+-----------------------------------------------------------------------------------+
|  [🎙️ ホットキー録音]       [🤖 ハイブリッドAI]       [⚡ 自動ペースト (AutoPaste)] |
|   Left Alt 長押しで録音     Gemini 2.5 Flash /        アクティブアプリへ直接送信    |
|   離すと自動推論開始        Groq (Whisper + LLM)     フォーカス保全 & 修飾キー解除 |
+-----------------------------------------------------------------------------------+
|  [🎯 コンテキスト認識]     [🛡️ リアルタイムVAD]     [📚 ユーザー強制辞書]         |
|   DEV / BIZ / DOC / STD     無音・誤タッチ自動破棄    専門用語・固有名詞を          |
|   用途に応じたプロンプト    API コスト & 時間を削減   確実に置換                    |
+-----------------------------------------------------------------------------------+
|  [💫 フローティングUI]      [⚙️ 充実の設定画面]       [📜 履歴マネージャー]         |
|   半透明・丸型・パルス      プロンプト・マイク・ゲイン 直近50件の履歴閲覧           |
|   ドラッグ移動・座標保存    ホットキーの自由な変更    ワンクリック再コピー          |
+-----------------------------------------------------------------------------------+
```

1. **グローバルホットキー録音 (Hold-to-Talk)**:
   - 画面がどのアプリにあっても、設定したキー（`Left Alt`、`Right Alt`、`Left Ctrl`、`Right Ctrl`）を押している間だけマイク録音。
2. **アクティブウィンドウへの自動ペースト (Auto Paste)**:
   - 録音開始時のアクティブウィンドウハンドル (HWND) を記憶。AI整形後、クリップボード格納を経て修飾キーを解除した上で `Ctrl+V` を自動送出。
3. **高精度 VAD (Voice Activity Detection)**:
   - 録音時間が0.2秒未満の場合や、音声の RMS / Peak が閾値以下の場合は無音と判定し、API呼び出しを行わずに処理をキャンセル。誤爆時の無駄なAPI課金をゼロにします。
4. **選べるデュアル AI プロバイダ**:
   - **Gemini**: 高精度かつ長文に強い最新モデル（デフォルト: `gemini-2.5-flash`）。WAVをBase64インライン送信。
   - **Groq**: 超高速 Whisper Large v3 による文字起こし専用（モデルは `.env` の `GROQ_WHISPER_MODEL` で変更可）。文章の整形は Groq では行わず、設定画面「整形」タブで選んだ **Gemini**（既定 `gemini-flash-lite-latest`）または **NVIDIA**（既定 `nvidia/nemotron-3.5-lightning-30b-a3b`）が担当します。整形に失敗しても、文字起こし結果はそのまま貼り付けられます。
   - 各モデル欄は、設定画面の「更新」ボタンで各社 API から取得した一覧から選べるほか、一覧に無いモデル名を直接入力することもできます。
5. **コンテキスト認識プロンプト最適化**:
   - `DEV`: VSCode, Cursor, ターミナル等を検知。IT用語の英語化・変数名スネークケース対応・簡潔な出力。
   - `BIZ`: Slack, Teams, メール等を検知。丁寧なビジネス敬語（です・ます調）と適切な改行。
   - `DOC`: Word, Notion, メモ帳等を検知。論理構成を重視した書き言葉変換。
   - `STD`: 一般アプリ用標準プロンプト。フィラー完全除去。
6. **リアルタイム入力ゲイン調整 & クリッピング保護**:
   - 音声入力レベル（dB）を設定可能。奇数バイトパディングや 16bit PCM の境界値クリッピング（`-32768`〜`32767`）を厳密に処理。
7. **ユーザー辞書強制置換**:
   - AIが間違えやすい社内用語や固有名詞を「From → To」の辞書として登録可能。

---

## 🛠️ 詳細な技術スタック & 選定理由

### 技術スタック一覧

| レイヤー | 採用技術 | バージョン | 選定理由 |
| :--- | :--- | :--- | :--- |
| **言語** | **C#** | 13.0 (.NET 10) | 型安全性、最新のパターンマッチング、非同期処理 (`async/await`) の高いパフォーマンス。 |
| **UI フレームワーク** | **WPF (Windows Presentation Foundation)** | .NET 10 | 丸型半透明・グラデーション・パルスアニメーションの描画に優れ、GPUアクセラレーションが効く。 |
| **常駐管理** | **Windows Forms (`NotifyIcon`)** | .NET 10 | OS標準のタスクトレイとの親和性が最も高く、軽量で安定したトレイアイコン管理が可能。 |
| **音声キャプチャ** | **NAudio** | 3.0.1 | Windows CoreAudio / MME / DirectSound を幅広くカバーするデファクトスタンダード。 |
| **低レイヤ制御** | **Win32 API (P/Invoke)** | Windows 10/11 | `SetWindowsHookEx` (キー監視), `keybd_event` (キーストローク), `GetForegroundWindow` (ウィンドウ検知)。 |
| **外部 AI** | **Google Gemini REST API** | v1beta | 音声データを直接インラインでマルチモーダル推論でき、超低レイテンシで高品質。 |
| **外部 AI** | **Groq Cloud API** | OpenAI 互換 | LPU による圧倒的な推論速度（Whisper + LLM）。 |
| **テスト** | **xUnit** | 2.9.3 | .NET の標準的テストフレームワーク。モック不要な純粋関数設計により 125 件の単体テストを瞬時実行。 |

---

## 🏗️ システムアーキテクチャ

Voice In は、各コンポーネントが疎結合に保たれたレイヤードアーキテクチャを採用しています。

```mermaid
flowchart TB
    subgraph UI ["プレゼンテーション層 (WPF & Forms)"]
        Overlay[OverlayWindow <br/> 半透明丸型フローティング]
        Settings[SettingsWindow <br/> タブ付き設定画面]
        History[HistoryWindow <br/> 履歴管理画面]
        Tray[NotifyIcon <br/> タスクトレイ常駐]
    end

    subgraph AppCore ["オーケストレーション層"]
        App[App.xaml.cs <br/> ライフサイクル & イベント連携]
    end

    subgraph Domain ["ドメイン & コア層"]
        Hook[KeyboardHook <br/> Win32 WH_KEYBOARD_LL]
        Recorder[AudioRecorder <br/> 録音ライフサイクル]
        Processor[AudioSampleProcessor <br/> 純粋関数: ゲイン・RMS/Peak]
        Detector[WindowDetector <br/> プロセス検知 & カテゴリ判定]
        Paster[TextPaster <br/> フォーカス復元 & Ctrl+V]
        Factory[AiProviderFactory <br/> プロバイダ生成]
        IAi[<<interface>> IAiProvider]
    end

    subgraph External ["インフラ & クラウド層"]
        Gemini[GeminiProvider]
        Groq[GroqProvider]
        Storage[(settings.json <br/> history.json <br/> .env)]
    end

    Tray --> App
    Overlay --> App
    Settings --> App
    History --> App

    App --> Hook
    App --> Recorder
    App --> Detector
    App --> Paster
    App --> Factory

    Recorder --> Processor
    Factory --> IAi
    IAi <|.. Gemini
    IAi <|.. Groq

    App --> Storage
```

---

## 💎 設計・実装のこだわり (Engineering Highlights)

### 1. 純粋関数へのロジック分離と単体テスト 125 件完備
NAudio のイベントハンドラに埋もれていたゲイン計算（dB→線形倍率変換）および RMS / Peak 集計処理を、副作用のない静的クラス `AudioSampleProcessor` へ完全に抽出。
ハードウェアやマイクデバイスに依存しないため、クリッピング挙動、ゼロ除算防止、奇数バイト長の入力ガードなど、**100 パターンの単体テスト** を xUnit で網羅し、品質を数学的に保証しています。

### 2. ゼロコピー & 高速 PCM 演算
ゲインが `0.0 dB`（倍率 1.0）の場合は、新規メモリ確保を行わず入力バッファのポインタをそのまま透過させる早期リターンを実装。無駄な GC 圧力を最小限に抑えています。

### 3. 安全な修飾キー解除と確実な自動貼り付け
Windows で `Left Alt` を押しながら録音し、離した直後に `Ctrl+V` を送出すると、OS レベルで Alt キーの解放イベントが間に合わず、メニューバーがアクティブになって貼り付けに失敗する現象が発生します。
Voice In では、`TextPaster` 内で明示的に `VK_MENU` の `KEYUP` イベントを送信し、ターゲットウィンドウの HWND が現在のアクティブウィンドウと一致するかを直前で再検証してからペーストを実行します。

### 4. アプリケーション二重起動防止 (System Mutex)
グローバルミューテックス `VoiceIn_Application_SingleInstance_Mutex` を使用し、多重起動によるホットキーのフック競合やマイクデバイスの奪い合いを確実に防止しています。

---

## 📂 ディレクトリ構成

```text
voice-inn-cs/
├── VoiceIn.csproj              # .NET 10 WPF プロジェクト定義
├── App.xaml / App.xaml.cs       # アプリケーション起動・常駐・オーケストレーター
├── AssemblyInfo.cs              # テストプロジェクトへの内部可視化定義
├── GlobalUsings.cs              # WPF と WinForms の型競合解消エイリアス
├── .env.example                 # 環境変数設定テンプレート
├── .gitignore                   # 機密情報・ビルド成果物の除外設定
├── README.md                    # 本ドキュメント
│
├── Ai/                          # AI プロバイダサブシステム
│   ├── IAiProvider.cs           # プロバイダ共通インターフェース
│   ├── AiProviderFactory.cs     # 動的プロバイダ生成ファクトリ
│   ├── GeminiProvider.cs        # Google Gemini API 実装
│   └── GroqProvider.cs          # Groq Whisper + LLM 実装
│
├── Audio/                       # 音声キャプチャサブシステム
│   └── AudioRecorder.cs         # NAudio 録音制御 & AudioSampleProcessor (純粋関数)
│
├── Core/                        # コアドメイン・ユーティリティ
│   ├── EnvLoader.cs             # .env ローダー
│   ├── Settings.cs              # 設定データモデル (POCO)
│   ├── SettingsManager.cs       # 設定永続化マネージャー
│   ├── HistoryManager.cs        # 履歴永続化マネージャー
│   ├── KeyboardHook.cs          # Win32 低レベルキーボードフック
│   ├── WindowDetector.cs        # アクティブウィンドウ & カテゴリ検出
│   ├── TextPaster.cs            # クリップボード & キーストローク送出
│   └── Logger.cs                # ログ基盤
│
├── Ui/                          # WPF プレゼンテーション層
│   ├── OverlayWindow.xaml/.cs   # 丸型フローティングオーバーレイ
│   ├── SettingsWindow.xaml/.cs  # 設定画面 (一般 / プロンプト / 辞書)
│   └── HistoryWindow.xaml/.cs   # 履歴閲覧 & コピー画面
│
├── Doc/                         # 詳細技術ドキュメント
│   ├── README.md                # ドキュメント目次
│   ├── requirements.md          # システム要件定義書
│   ├── architecture.md          # アーキテクチャ設計書
│   ├── data-flow.md             # データフロー図 & 状態遷移図
│   ├── sequence.md              # シーケンス図集
│   └── class-diagram.md         # クラス構造設計書
│
└── tests/                       # 単体テストプロジェクト
    └── VoiceIn.Tests/
        ├── VoiceIn.Tests.csproj
        └── Audio/
            └── AudioSampleProcessorTests.cs # 音声演算テスト (100 tests)
```

---

## 🚀 環境構築 & 使い方

### 1. 必要要件
- OS: **Windows 10 / 11** (64-bit)
- ランタイム: **.NET 10 SDK** または **.NET 10 Desktop Runtime**

### 2. インストール手順

```powershell
# リポジトリのクローン
git clone https://github.com/watawatan1984/voice-inn-cs.git
cd voice-inn-cs

# .env ファイルの作成
Copy-Item .env.example .env
```

### 3. 環境変数 (.env) の設定

`.env` には API キーを書き込むため、**どこに置くか（＝同じ PC の他のアカウントから読めてしまわないか）が重要**です。`EnvLoader` は起動時に次の順で `.env` を探し、最初に見つかったものを読み込みます（この探索順は互換性のため今後も変更しません）。

| 優先順位 | パス | 位置づけ | 安全性 |
| :--- | :--- | :--- | :--- |
| 1 | 実行ファイルと同じディレクトリ | 開発時のクイックスタート / ポータブルモード用のフォールバック | ⚠️ `Program Files` や `C:\` 直下など複数ユーザーが共有するインストール先に置いた場合、既定の NTFS 権限では**同じ PC の他のローカルアカウントからも読み取れます**。個人 PC の単一ユーザー利用以外では避けてください。 |
| 2 | カレントディレクトリ | ショートカットの「作業フォルダ」設定に依存する後方互換用 | 上記と同様の注意が必要です。 |
| 3 | `%AppData%\VoiceIn\.env` | **推奨（第一候補）** | ✅ Windows のユーザープロファイル配下のため、既定の権限では**同じ PC の他のアカウントから読み取れません**。共有 PC でも安全に使えます。 |

**推奨手順**: どの環境であっても、API キー漏えいのリスクを避けるため `%AppData%\VoiceIn\.env` への配置を推奨します。

```powershell
# %AppData%\VoiceIn フォルダを作成し、.env を配置
New-Item -ItemType Directory -Force "$env:AppData\VoiceIn" | Out-Null
Copy-Item .env.example "$env:AppData\VoiceIn\.env"
notepad "$env:AppData\VoiceIn\.env"
```

開発時にリポジトリ直下で素早く試したいだけの場合は、手順2で作成した実行ファイル隣接の `.env` をそのまま使っても動作します。ただし共有 PC・複数ユーザーが使うインストール先では、上表の理由により `%AppData%\VoiceIn\.env` へ移すことを強く推奨します。

テキストエディタで `.env` を開き、お持ちの API キーを設定します（どちらか一方のみでも動作します）。

```ini
# Google Gemini API キー (https://aistudio.google.com/ で無料取得可能)
GEMINI_API_KEY=AIzaSy...
GEMINI_MODEL=gemini-2.5-flash

# Groq API キー (https://console.groq.com/ で無料取得可能)
GROQ_API_KEY=gsk_...

# デフォルトで使用するプロバイダ (gemini または groq)
AI_PROVIDER=gemini
```

### 4. ポータブルモード (`VOICEIN_PORTABLE`)

USB メモリなどに入れて複数の PC に持ち運びたい場合、環境変数 `VOICEIN_PORTABLE` を `1` に設定すると、設定 (`settings.json`)・履歴 (`history.json`)・ログ (`app.log`)・`.env` の保存先がすべて `%AppData%\VoiceIn` の代わりに**実行ファイルと同じディレクトリ**になります（移植元 Python 版の `VOICEIN_PORTABLE=1`（`src/core/utils.py` の `get_config_dir` / `get_state_dir`）と同じ仕様です）。

```powershell
# 実行ファイルと同じフォルダにすべてのデータを保存して起動する例
$env:VOICEIN_PORTABLE = "1"
.\VoiceIn.exe
```

- 値がちょうど `1` の場合のみポータブルモードになります。未設定・`0`・その他の値では従来どおり `%AppData%\VoiceIn` を使用し、既存ユーザーの挙動・データには一切影響しません。
- ポータブルモードで実行ファイルの隣に置く `.env` にも、上表の「共有 PC 上で他アカウントから読める」リスクがそのまま当てはまります。持ち運ぶ USB メモリ自体の紛失・盗難にも注意してください。

### 5. ビルドとテスト

```powershell
# テストの実行 (125件のテストがパスすることを確認)
dotnet test

# アプリケーションのビルド & 実行
dotnet run
```

---

## 📚 ドキュメント一覧 (Doc/)

詳細なシステム設計については、以下のドキュメントをご参照ください。

- 📋 **[要件定義書 (Doc/requirements.md)](Doc/requirements.md)**: 業務背景、機能要件 (FR-001〜012)、非機能要件、セキュリティ仕様。
- 🏛️ **[アーキテクチャ設計書 (Doc/architecture.md)](Doc/architecture.md)**: レイヤード構成、非同期スレッドモデル、各コンポーネントの責務。
- 🔄 **[データフロー図 & 状態遷移図 (Doc/data-flow.md)](Doc/data-flow.md)**: OverlayWindow の5つの状態マシン、レベル0/1 DFD。
- ⏱️ **[シーケンス図集 (Doc/sequence.md)](Doc/sequence.md)**: 録音から自動貼り付け、VAD無音スキップ、エラー回復の時系列フロー。
- 🧩 **[クラス構造設計書 (Doc/class-diagram.md)](Doc/class-diagram.md)**: 全体クラス図 (Mermaid)、クラス間依存関係、SOLID原則の適用。

---

## 🔮 今後のロードマップ

- [ ] **ローカル Whisper エンジンの内蔵**: `Whisper.net` を統合し、完全オフライン・機密データ保護モードを提供。
- [ ] **ストリーミング認識 (リアルタイム入力)**: 発話中に逐次テキストを入力するストリーミングモードの追加。
- [ ] **シングルファイル Self-Contained 配布**: .NET ランタイム不要の単一 `.exe` リリースインストーラーの配布（GitHub Actions CI/CD）。
- [ ] **カスタムプロンプトテンプレート集の拡充**: 翻訳モード、要約モード、箇条書き変換モードのワンタッチ切替。

---

## 📄 ライセンス & 著者

- **ライセンス**: [MIT License](LICENSE)
- **開発者**: [watawatan1984](https://github.com/watawatan1984)
- **リポジトリ**: [https://github.com/watawatan1984/voice-inn-cs](https://github.com/watawatan1984/voice-inn-cs)
