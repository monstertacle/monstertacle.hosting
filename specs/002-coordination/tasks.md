# Tasks: Bounded worker coordination and cancellation

- [x] T101 Implement bounded registration, admission and lane-bound worker HTTP.
- [x] T102 Persist claim and dispatch evidence, recover each child independently
  and prevent duplicate effects after uncertain delivery.
- [x] T103 Implement idempotent cancel and prevent further dispatch when it wins.
- [x] T104 Verify restart with two children, third-lane denial, cancel races,
  unsupported stop, replay and lane revocation.

The integration suite uses two pinned lanes, preserves cancellation and
execution certainty separately, and drops successful claim, dispatch and
completion responses around a native runtime restart. Live provider stop
qualification is tracked separately.
