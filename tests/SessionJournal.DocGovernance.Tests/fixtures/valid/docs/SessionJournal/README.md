[tracked target](target.md)
[percent decoded target](space%20name.md)
[local anchor](#current-verified-claim-ledger)
[web](https://example.invalid/not-checked)

```markdown
[fenced fake](missing.md)
```

## Current verified claim ledger

| `claim_id` | 窄 claim / owner | role · lifecycle | `verified_against` | `read_when` |
|---|---|---|---|---|
| `valid.one` | one | `component-guide`, `canonical-contract` · `current` | `0123456789abcdef0123456789abcdef01234567` | one |
| `valid.two` | two | `component-guide`, `canonical-contract` · `current` | `89abcdef0123456789abcdef0123456789abcdef` | two |

## Normative、frozen 与 closed entries

| `claim_id` | role · lifecycle | 窄边界 | 入口 |
|---|---|---|---|
| `valid.closed` | `completion-record` · `closed` | closed | target |
