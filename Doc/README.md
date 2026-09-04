# Voice In - ドキュメント一覧 (Documentation Index)

Voice In の設計、アーキテクチャ、要件、およびシーケンス仕様に関する技術文書群です。

---

## 📚 ドキュメント構成

| ドキュメント | 説明 | 主な内容 |
| :--- | :--- | :--- |
| **[要件定義書 (requirements.md)](requirements.md)** | システム全体の要件定義 | 開発背景、ペルソナ、機能要件一覧 (FR-001〜012)、非機能要件（性能・セキュリティ・信頼性） |
| **[アーキテクチャ設計書 (architecture.md)](architecture.md)** | システムの全体構造とレイヤー設計 | レイヤードアーキテクチャ図、スレッドモデル（Dispatcher/Worker/Hook）、外部API連携 |
| **[データフロー図 & 状態遷移図 (data-flow.md)](data-flow.md)** | データの流れと状態マシン | OverlayWindow の5つの状態遷移、Level 0 / 1 DFD、設定・履歴エンティティ定義 |
| **[シーケンス図集 (sequence.md)](sequence.md)** | 主要ユースケースの時系列処理フロー | 録音〜AI〜自動貼り付けの正常系、VAD無音スキップ、プロバイダ切替、設定永続化、エラー回復 |
| **[クラス構造設計書 (class-diagram.md)](class-diagram.md)** | オブジェクト指向設計とクラス仕様 | 全体クラス図 (Mermaid)、各クラスの責務、SOLID設計原則の適用解説 |

---

## 🛠️ 開発者向けクイックリンク
- [メイン README.md (プロジェクト概要・使い方)](../README.md)
- [単体テスト仕様 (tests/VoiceIn.Tests)](../tests/VoiceIn.Tests)
