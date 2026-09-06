# Decision Log

Use one entry per durable decision. Do not use this file as a chat transcript.

## 2026-09-06 — 4つのコード解析・ナレッジ管理ツールの導入

- Status: accepted
- Context:
  AI エージェント（Claude Code / Codex / Antigravity）がコード全体を毎回走査することによるトークン消費と推測の発生を防ぎ、正確なシンボル探索・意味検索・依存関係分析・影響範囲特定・レビューを行えるようにするため、Zenn記事「トークン2000分の1——オントロジー×ナレッジグラフでClaude Codeの推測を消す」および「プロジェクト横断コンテキスト運用手順」に基づきツールを導入した。
- Decision:
  以下の4ツールを採用し、役割を分担させた:
  1. `code-review-graph` (2.3.7): pre-commit フックによるレビューコンテキスト供給（トークン削減）
  2. `better-code-review-graph` (3.21.0): 意味検索、変更影響範囲（blast radius）分析、レビュー、セキュリティスキャン
  3. `serena-agent` (1.6.0): LSP（Language Server Protocol）経由での型・定義元・実装クラス・参照先一覧の行番号付き特定
  4. `graphifyy` (0.9.28): コード・ドキュメント横断の構造可視化（ナレッジグラフ生成）
  
  また、ツール間の重複を避けるため、ルーティングルールを `CLAUDE.md` および `AGENTS.md` に明記した。導出ファイルは `.gitignore` に登録して Git 管理対象外とした。
- Consequences:
  - ローカルにグラフDB（.code-review-graph, .graphify, .serena 等）が生成される（Gitにはコミットしない）。
  - 各種クエリがファイル全走査なしに高速かつ低トークンで実行可能となる。
  - 大規模なリファクタリング後はインデックス更新コマンドの再実行が必要。
- Removal:
  不要になった場合は `.mcp.json`, `CLAUDE.md`, `AGENTS.md` からエントリを削除し、`.code-review-graph`, `.graphify`, `graphify-out`, `.serena` ディレクトリおよび `.git/hooks/pre-commit` を削除する。
