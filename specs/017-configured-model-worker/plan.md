# Implementation plan · Configured model workers

Compose the existing Agent Framework worker with a text-only IChatClient
registered for a single deployment profile. Carry confirmed gateway failures
through the adapter's typed outcome contract. Keep broker authority, persistent
claims, and existing clients. Log a fixed warning for ambiguous model outcomes.

Use an opt-in model mode in the existing chiseled worker image. Ship
production templates and qualify the extracted archive on hosted runners using
the release fixture's HTTPS service and ephemeral credentials. Run ordinary
unit tests on both hosted platforms. Model configuration remains external.

## Constitution check

Use .NET 11 and shell with pinned public dependencies. Policy and service
identities stay intact. The wire adapter has a finite payload and does not
perform application-level replay, disclose upstream errors, or follow redirects.
Only fixture aliases and generated fixture credentials are permitted in public
checks. Review the staged diff and scan it before each commit.
