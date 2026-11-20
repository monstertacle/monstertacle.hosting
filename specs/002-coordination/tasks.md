# Tasks: Bounded worker coordination and cancellation

- [ ] T101 Implement bounded registration, admission and lane-bound worker HTTP.
- [ ] T102 Persist claim and dispatch evidence, recover each child independently
  and prevent duplicate effects after uncertain delivery.
- [ ] T103 Implement idempotent cancel and prevent further dispatch when it wins.
- [ ] T104 Verify restart with two children, third-lane denial, cancel races,
  unsupported stop, replay and lane revocation.
