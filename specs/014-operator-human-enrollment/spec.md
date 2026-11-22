# Operator-managed human access enrollment

**Feature branch** `feature/hosting-enrollment`
**Created** 2026-11-22
**Status** Approved scope

## User stories

### US1 · Enroll the first and subsequent people

An operator with local access starts from a version 1 policy with configured
projects and lanes. The operator supplies a stable Hosting principal ID, the
trusted provider's exact HTTPS issuer and opaque subject, and one or more
explicit project, lane and action grants. The operator follows the same process
for the first person and every additional person.

**Acceptance.** A granted identity can perform precisely the requested
actions. A valid token from an unlisted person is denied. A wrong issuer or
audience is rejected during authentication. Neither a display name nor an
email address is used to infer a subject.

### US2 · Safely retry and revoke access

An exact retry is a no-op. Reusing a principal or identity with conflicting
details fails without changing policy. Local operators can remove the grants
for a verified principal. Requests using a previously granted token are
checked against current policy. Existing projects, grants, identities, and
workload authorization survive unrelated changes.

**Acceptance.** Malformed policies, invalid grants, conflicting identities and
interrupted writes preserve the last complete file. Concurrent supported edits
are serialized. Edge and broker each read the same policy directory.

### US3 · Enroll from a source-free installation

A Linux x86-64 operator uses the deployment release with Docker Compose or
rootless Podman Quadlet. An opt-in configuration mounts the same directory
read-only at edge and broker. Existing role-specific configurations continue
to work. Enrollment runs locally without network access or an SDK.

**Acceptance.** Run enrollment and revocation from a clean extracted release on
both engines. Existing fixture and CI workload flows still pass.
