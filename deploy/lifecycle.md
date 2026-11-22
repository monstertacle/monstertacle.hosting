# Hosting deployment lifecycle

This runbook applies to the three .NET 11 Linux images built from a recursive
checkout. Keep the broker database and each worker ledger in separate persistent
volumes. Hosting owns their schema, worker identities, trust scopes and leases.
Keep the MCP edge stateless.

## Preflight and install

1. Select an exact application revision and record the image IDs or immutable
   digests for the broker, worker and edge. Check the host architecture against
   the images and qualify the revision on the target kernel, engine and storage
   driver. For rootless Quadlet, confirm the user manager, subordinate UID/GID
   mapping and SELinux labeling where applicable. Verify that application UID
   1654 can read its own configuration and write its own volume.
2. Provision the [configuration files](README.md#configuration). Verify the
   broker DNS name and CA, client certificate registrations, stable worker IDs
   and trust scopes, and matching issuer, audience and scope on broker and edge.
   Verify the chosen loopback port is free. Public ingress, certificate
   issuance and public issuer acceptance have their own prerequisites.
3. Run `bash deploy/smoke.sh --config-only` for Compose or
   `bash deploy/smoke.sh --quadlet --config-only` for Quadlet. These commands
   render generated fixtures. For your own deployment, review rendered
   configuration privately. Use `docker compose -p <project> -f deploy/compose.yaml
   config --quiet` to check interpolation. To inspect the full Compose model,
   keep the output private because it can contain resolved environment values.
   For Quadlet, use the rootless generator with `QUADLET_UNIT_DIRS` pointing at the
   prepared unit directory and `-user -dryrun` as supported by the installed
   version. Confirm that only the edge publishes a loopback port, that each
   `/run/hosting` bind is role-specific and read-only, and that only the broker
   and workers mount distinct state volumes. [Quadlet generator documentation](https://docs.podman.io/en/stable/markdown/podman-systemd.unit.5.html#debugging-a-limited-set-of-unit-files)
4. Use the [engine-specific installation instructions](README.md#docker-compose).
   Keep the same Compose project name or Quadlet volume names on later starts.
   Inspect every service's actual mounts, container ID and start time. Check
   the edge's HTTPS health and resource metadata using the expected CA, confirm
   anonymous MCP returns 401, and verify the worker registers and successfully
   claims over private broker mTLS. In an authorized environment, verify allowed
   callers can recover a known session and its results.

## Replace the edge serving certificate

Stage the replacement with the correct host names, chain, key and permissions.
Atomically replace the serving certificate in the mounted directory. Keep the
broker client identity and trust settings associated with the deployment. For
a PFX, replace the bundle as a single file. For PEM, stage a matched pair and
activate it with a coordinated configuration change before recreation.

- Compose from the selected project uses
  `docker compose -p <project> -f deploy/compose.yaml up -d --no-build --no-deps --force-recreate edge`.
- Quadlet uses `systemctl --user restart hosting-edge.service`. If you changed
  unit directives, validate them and reload the user manager first.

Confirm the edge container ID and served TLS identity changed. Compare broker
and worker container IDs, start times and mounted volumes with the previous
values. Repeat the health, worker mTLS, resource discovery and anonymous 401
checks. If the new serving certificate fails, reinstall the previous valid
certificate and repeat the edge-only recreation.

## Stop the stack and retain state

Quiesce admission at the approved ingress. Allow work to settle and record
outstanding jobs, worker leases and uncertain outcomes. Stop the edge, workers
and broker, as well as any helper that can write either volume. For Compose,
`docker compose -p <project> -f deploy/compose.yaml down` retains named
volumes. For Quadlet, use
`systemctl --user stop hosting-edge.service hosting-worker.service hosting-broker.service`
and include every additional worker unit. Confirm that the volumes still
exist and that no running container or host process has them mounted for
writing. Keep the deployment stopped for the duration of a coordinated backup.
[Compose down reference](https://docs.docker.com/reference/cli/docker/compose/down/)

Recreate with the same project or unit set and original named volumes. Check
for new container IDs, unchanged volume names, and expected persisted sessions
and worker observations. Re-run the transport and authentication checks from
installation.

## Cold archive and isolated restore

Use a private backup directory with restricted access. Record the archive set
as a single stopped-state capture with the image revision, engine and namespace
mapping, role-to-volume mapping, checksums and the configuration version needed
to recover it. Keep keys and configuration in their own approved custody.
Archive complete volumes, including any SQLite journal, WAL and shared-memory
sidecars that are present. A live copy of the SQLite main file can lose committed
transactions. [SQLite WAL documentation](https://sqlite.org/wal.html#the_wal_file)

With all writers stopped, select the engine and set `SOURCE_VOLUME` to the
inspected volume name and `ARCHIVE` to a new file in private storage. Repeat for
the broker and each worker. Run the helper in the same UID/GID mapping as
the workload storage. In the example the helper runs as namespace root.

```sh
ENGINE=podman                          # or docker, using the selected engine context
SDK=mcr.microsoft.com/dotnet/sdk:11.0
umask 077
"$ENGINE" volume inspect "$SOURCE_VOLUME" >/dev/null &&
  "$ENGINE" run --rm --network none --read-only --user 0:0 \
    -v "$SOURCE_VOLUME:/state:ro,nocopy" "$SDK" \
    tar --sort=name --numeric-owner --format=gnu -C /state -cf - . > "$ARCHIVE"
```

Treat a failed command as an incomplete archive. Record the SHA-256 of every
successful archive. Restore only verified archives from trusted custody.
Choose new, unique volume names on an isolated recovery host or engine context
and ensure they are absent before creating them. Repeat for each role, keeping
the original volumes available for rollback.

```sh
"$ENGINE" volume create "$RESTORE_VOLUME"
"$ENGINE" run --rm -i --network none --read-only --user 0:0 \
  -v "$RESTORE_VOLUME:/state:rw,nocopy" "$SDK" sh -ec '
    test -z "$(ls -A /state)"
    tar --numeric-owner --same-owner --same-permissions -C /state -xf -
  ' < "$ARCHIVE"
```

Before startup, re-archive each restored volume with the same options and
compare the SHA-256 against its source archive. Verify role ownership, expected
files and the image's schema compatibility. For Compose, use a separate
project and override the broker and worker volume entries with `external: true`
and their respective new names. For Quadlet, use separately named units and
set each `.volume` unit's `VolumeName=` to the corresponding restored volume.
Use isolated networks and a separate loopback port. Confirm rendered and
running mounts before permitting traffic.

Boot the restored stack with the application revision that wrote the archive.
Verify health, issuer discovery, anonymous denial and worker mTLS. In a
credentialed, isolated application check, look up known sessions and results,
check worker identity, and reconcile any outstanding leases or observations.
Keep restored workers away from live providers and shared queues during the
rehearsal. Qualify a different application revision against the restored copy
before using it for cutover. Rootless mapping and file metadata must be
validated on the destination. A successful same-engine rehearsal does not
establish cross-engine or cross-host portability.

## Cutover and rollback

Keep the previous image IDs, config and original stopped volume pair until
acceptance. Cut over only after isolated verification and only when there is
one active writer for each restored deployment role. Until the replacement
accepts writes or dispatches work, rollback can stop the replacement and
restart the old image and untouched volume pair. Once the replacement accepts
writes or creates external effects, reverting to the old snapshot can discard
new state or replay work. Quiesce both sides and reconcile admissions, results
and leases before a further cutover. Follow Hosting's schema compatibility
requirements before opening volumes written by another revision.
