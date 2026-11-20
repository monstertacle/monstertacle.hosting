# Implementation plan: Packaging and platform qualification

Use multi-stage .NET 11 images, non-root processes, persistent volumes and
externally provisioned identity and service credentials. Document TLS ingress,
private TLS, OAuth settings and durable state. Container execution qualifies
Linux. Run native Windows build and integration tests on a Windows runner.

Qualify the examples without a production endpoint. Document separately which
checks require live deployment inputs and capture eventual evidence by platform,
client and identity scenario. Use standard tools and unchanged application
contracts across clients and runtime bindings.

Constitution check: public builds, isolated credentials and split deterministic
and live qualification.
