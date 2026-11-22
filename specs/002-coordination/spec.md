# Feature 002: Bounded worker coordination and cancellation

## User scenarios and acceptance

1. A server-side plan admits a bounded set of child jobs with stable IDs.
   The caller chooses a registered project and task. Each lane requires a grant.
2. An authenticated worker claims its assigned lane only. Restart after a lost
   claim, start or completion response preserves job identity. An uncertain
   runtime start cannot authorize a second start.
3. Cancellation has a stable mutation UUID. Pending work cannot dispatch after
   cancellation wins. Active work exposes intent and observed outcome until
   confirmed stopped, terminal, unsupported or uncertain.
4. Revoking a lane before dispatch denies new effects. Revocation during work
   denies new caller reads and mutations. Existing work reports under its
   authenticated assignment.
5. Two owners admit distinct sessions with one job each through the HTTPS
   broker. Separate lane-bound workers authorize dispatch over mutual TLS
   and execute concurrently on independent runtime state roots. Each job
   produces one runtime request and its own output and artifact digest.
   Completing either job leaves the other session's live state intact.
   Each owner can read their own status, result and artifact, while the
   other owner is denied even with the corresponding project read grants.
