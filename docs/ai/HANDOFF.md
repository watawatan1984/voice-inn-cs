# Current Handoff

Updated: 2026-09-07 by Assistant

## Objective

voice-inn-cs リポジトリへの4つのナレッジ管理ツール導入 — **完了**。

## Completed and verified

- `.gitignore` への AI コード解析導出物の除外設定追加 → `git check-ignore` で全パス確認済み
- `.mcp.json` MCP 接続設定（code-review-graph, better-code-review-graph）
- `CLAUDE.md` ツール選択ルーティングテーブル・競合回避・フォールバック
- `AGENTS.md` プロジェクト AI 行動規約
- `docs/ai/PROJECT_CONTEXT.md` プロジェクト事実ファイル
- `docs/ai/DECISIONS.md` ツール採用意思決定ログ
- `code-review-graph` install + build → 547ノード / 2660エッジ, pre-commitフック設置済み
- `better-code-review-graph` doctor + graph build + graph embed → 543ノード / 948エッジ / 475埋め込みベクトル
- `Serena` project create --index → C# 61ファイルインデックス済み
- `Graphify` extract --code-only → 979ノード / 1918エッジ / 45コミュニティ, GRAPH_TREE.html生成済み
- `git status` クリーン（設定ファイルはコミット済み、導出物は .gitignore で管理外）

## Current state

- Branch: fix/paste-reliability
- Working tree: clean
- Environment: Windows, .NET 10, Python 3.13, uv

## Next actions

1. 通常の開発作業を再開する
2. 大きなリファクタリング後は各ツールのインデックス更新を実行する:
   - `code-review-graph build`
   - `better-code-review-graph graph build && better-code-review-graph graph embed`
   - `serena project index`
   - `graphify extract . --code-only`

## Risks, blockers, and decisions needed

- None known.

## Do not share

- Credentials, tokens, personal data, or `.env` content.
