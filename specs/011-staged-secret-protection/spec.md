# Staged secret protection

## Scope

Apply the same contributor rule to the public Hosting, Communication,
Multisession and SharpQrs repositories.

## Requirements

- SP-01. Keep real API keys, tokens, passwords, credential-bearing connection
  strings, private keys, confidential data and session or authentication state
  out of sources, tests, docs, examples, Git history, logs, artifacts, caches
  and images. Use runtime secret management and ephemeral synthetic test keys.
  Report confirmed exposure privately to the authorized owner without values
  and promptly rotate affected credentials with authorization.
- SP-02. Before each commit, inspect the exact staged diff and run a staged-only
  Gitleaks scan. A detection, missing dependency or scan error blocks the commit.
  Findings must be redacted.
- SP-03. Enable GitHub Secret Scanning and Push Protection and verify both on
  each repository. If permitted, record only open alert counts.
- SP-04. Demonstrate clean staged acceptance, staged synthetic credential
  rejection with redaction, and acceptance when the same value is unstaged.
  Verify each repository's exact pull request head and existing checks.
