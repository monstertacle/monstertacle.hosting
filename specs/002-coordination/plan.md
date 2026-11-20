# Implementation plan: Bounded worker coordination and cancellation

Persist child assignments within the session admission transaction. Add
authenticated lane-bound claim, observe, acknowledge and cancellation polling
routes. Persist monotonic state and ownership/fencing evidence. Repeated
observations deduplicate and cannot replace incompatible terminal evidence.
Have the worker persist a stable dispatch record before invoking the local
runtime. Resume by reconciliation. Keep an ambiguous result quarantined until
the runtime admits a safe continuation. Use server-side plan and queue limits.

Constitution check: broker authority, versioned transport, durable evidence and
credential-free restart tests.
