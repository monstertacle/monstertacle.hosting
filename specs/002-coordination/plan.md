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

For acceptance 5, use the real loopback HTTPS broker, independent lane
certificates and two native runtime stores. Give each Agent Framework agent a
deterministic chat client with its own response gate. Observe both clients
inside execution, then complete one while the other is held. Read broker
results and runtime records before and after releasing the second client.
This exercises the production admission, authorization, dispatch and custody
paths with test identities and synthetic provider replies. Verify on the
host, in rootless Podman and in the standard CI matrix.
