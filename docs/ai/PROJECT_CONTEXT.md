# Project Context

Last reviewed: 2026-09-06  
Owner: watawatan1984  
Status: active

## Purpose

Voice In は Windows 上のあらゆるデスクトップアプリケーション（VSCode、ターミナル、Slack、Word 等）に対して、ホットキー（デフォルト: `Left Alt`）の長押しで音声認識・AI文章整形（Gemini 2.5 Flash / Groq LLM）を行い、カーソル位置へ自動流し込み（AutoPaste）を行う C# / .NET 10 WPF 常駐型音声入力アシスタントです。

## Start here

- Runtime: .NET 10 SDK / Windows 10/11
- Install / Restore: `dotnet restore`
- Build: `dotnet build`
- Test: `dotnet test`
- Local run: `dotnet run --project VoiceIn.csproj`
- Installer build: `powershell -ExecutionPolicy Bypass -File installer/build.ps1`

## Architecture map

- `App.xaml / App.xaml.cs`: アプリケーションエントリポイント、DIコンテナ構成、トレイアイコン管理、ライフサイクル。
- `Core/`:
  - `Audio/`: NAudio によるマイク音声キャプチャ、リアルタイム VAD（無音検出・誤タッチ破棄）、PCM バッファリング。
  - `Ai/`: 音声認識（Whisper）および LLM 整形（Gemini 2.5 Flash / Groq）、プロンプトマネージャー（DEV/BIZ/DOC/STD）、ユーザー強制辞書。
  - `Win32/`: Win32 P/Invoke（グローバル低レベルキーボードフック、アクティブウィンドウ情報取得、仮想キーストローク送出・AutoPaste）。
  - `Config/`: 環境変数（`.env`）および設定ファイル管理（`settings.json`）。
- `Ui/`: WPF フローティングオーバーレイ（半透明・丸型・録音パルス）、設定画面、履歴マネージャー。
- `tests/`: xUnit による単体テスト群（AudioSampleProcessor, HotkeyValidator, PromptManager, TextDiffApplier 等）。
- `Doc/`: 要件定義 (`requirements.md`)、アーキテクチャ設計 (`architecture.md`)、シーケンス図 (`sequence.md`)、クラス図 (`class-diagram.md`)。

## Operational constraints

- Environments: Windows 10 (Build 19041+) / Windows 11 のデスクトップ環境限定。
- Secrets: `.env` ファイルに `GEMINI_API_KEY`, `GROQ_API_KEY` 等を設定。`.env` はコミット禁止。
- Source of truth: C# ソースコードおよび `Doc/` 配下の設計ドキュメント。

## Verification contract

- コード変更時は必ず `dotnet test` を実行し、全テストパスを確認する。
- UI / Win32 P/Invoke 関連の変更時はビルドが通り、警告・破損がないことを確認する。

## Links

- Design docs: `Doc/`
- Current handoff: `docs/ai/HANDOFF.md`
- Decision log: `docs/ai/DECISIONS.md`
