# Project Agent Guide

## Read first

Before proposing or changing code, read:

1. `docs/ai/PROJECT_CONTEXT.md`
2. `docs/ai/HANDOFF.md`
3. `docs/ai/DECISIONS.md` when the task affects an existing decision
4. `CLAUDE.md` for AI tool routing and query policies

## Working agreement

- Treat the files above as the project source of truth; do not invent missing facts.
- Keep changes scoped. Preserve unrelated working-tree changes.
- Never put credentials, personal data, or production secrets in repository files.
- Before reporting completion, run the relevant tests or state precisely why they could not run (`dotnet test`).
- After a meaningful decision, release, or handoff, update `docs/ai/HANDOFF.md` and, when durable, `docs/ai/DECISIONS.md` in the same commit/pull request.

## Code Intelligence & Tools

This project integrates 4 knowledge management tools:
- **better-code-review-graph**: Semantic code search, blast radius impact analysis, automated diff review.
- **Serena**: LSP-based exact symbol definitions, implementations, references, and symbol renaming.
- **code-review-graph**: Pre-commit hook provider for git commit context reduction.
- **Graphify**: Broad repository architecture and cross-document knowledge graph.

Refer to `CLAUDE.md` for specific tool routing rules and conflict resolution.
