# Tasks: Packaging and platform qualification

- [x] T301 Add images, Docker Compose and Podman Quadlet examples.
- [x] T302 Run clean public restores, Linux checks and native Windows CI.
- [x] T303 Validate container build, configuration and available local smoke checks.
- [x] T304 Document deployment inputs and real three-client acceptance, including
  response loss, revocation and verified cross-client artifact retrieval.

- [Build and test for 09547f0](https://github.com/monstertacle/monstertacle.hosting/actions/runs/44833101988) passed native Linux and Windows tests and the Docker Compose smoke.
- The local Podman generator produced all seven example units. A separate rootless
  Podman job exercises the units on a hosted runner.
- The deployment guide defines the prerequisites and cases for live acceptance.
