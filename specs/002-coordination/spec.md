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
