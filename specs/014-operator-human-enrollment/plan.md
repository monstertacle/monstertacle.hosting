# Implementation plan for operator-managed human access enrollment

## Design

- Use the existing version 1 parser and semantic validator in
  `Monstertacle.Hosting.Security` for runtime and offline changes.
- Invoke the offline command through the control-plane image. Stream policy
  and request through private files. Let the host launcher serialize writers
  and atomically replace the canonical file under the same directory.
- Ship an opt-in Compose override and Quadlet drop-ins with a shared directory
  mount. Keep the existing role-specific configuration as the default.
- Exercise policy failures and its authorization effects in tests. Exercise
  the offline command, mount behavior and revocation from the bundled fixture.

## Constitution check

Use C# and Bash on the existing .NET 11 and Linux release baseline. The broker
retains admission authority. Credentials and mutable policy files stay on the
operator's host. Test credentials are ephemeral. Scan every staged commit.
