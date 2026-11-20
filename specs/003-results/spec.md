# Feature 003: Embedded runtime and verified results

## User scenarios and acceptance

1. WorkerHost embeds public Multisession Common, SQLite and native lease
   packages directly. A registration supplies the runtime binding server-side.
   Two deterministic runtime registrations exercise the same public contract.
2. A result manifest includes schema version, session/job identity, terminal
   evidence and bounded artifact IDs, byte sizes and SHA-256 digests. Partial
   failures and uncertainties are visible.
3. Broker verifies supplied evidence against the authenticated assignment and
   recomputes artifact digests and manifest identity before storage. Readers
   need current result and child lane grants before aggregate and artifact reads.
4. An explicit acceptance for a verified digest is separate from retrieval.
   Revocation blocks identical retries and previously issued artifact IDs.
