# ADR 0001: Enforce safety in the orchestrator and tools, not in the prompt

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

A tool-calling agent turns model output into real actions: refunds, record changes, messages.
Models follow instructions probabilistically, and any text they read, including tool results,
web pages and customer notes, can carry instructions (indirect prompt injection). A system prompt
that says "never refund more than 100 USD" is a request, not a control.

## Options

| Option | Strengths | Weaknesses |
|---|---|---|
| A. Prompt rules only | Fast to write | Bypassable by injection or model error; not testable |
| B. Separate guard model reviewing each step | Catches semantic issues | Second probabilistic system, extra cost and latency |
| C. Deterministic controls in code around the model | Testable, auditable, cannot be talked past | Needs explicit tool metadata and policies |

## Decision

Option C, with B as an optional later layer. Every control that protects money, data or
availability is code the model cannot change:

- **Least privilege:** each agent gets an explicit tool allow-list; tools outside it are neither
  advertised nor executable.
- **Write actions need approval:** tools are classified `Read` or `Write`; writes pass an
  `IApprovalGate`, which denies by default.
- **Business rules live in tools:** limits such as the maximum automatic refund are enforced by
  the tool itself.
- **Arguments are validated** against the tool definition before anything runs.
- **Tool output is untrusted data:** it is wrapped in delimited `<tool_output untrusted="true">`
  blocks and screened for injection phrases, which are flagged in the trace.
- **Budgets:** step limit, token budget, wall-clock timeout and identical-call loop detection.
- **Output hygiene:** e-mail addresses and Luhn-valid card numbers are redacted from final answers.

## Consequences

- Behaviour is testable offline with a scripted model, so CI catches orchestration regressions.
- The injection screen is a heuristic and will miss novel phrasing; the real protection is that a
  model influenced by injected text still cannot run an unapproved write or exceed tool limits.
- Every new tool needs an explicit effect classification and parameter schema.

## Revisit when

- Agents need multi-party approvals or per-amount approval thresholds, or
- a guard model shows measurable benefit on a labelled set of attack transcripts.
