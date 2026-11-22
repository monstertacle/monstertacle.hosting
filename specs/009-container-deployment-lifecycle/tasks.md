# Implementation Tasks

- [x] Render fixture configuration and reject exposure or storage drift before install.
- [x] Qualify initial install and edge certificate replacement on both engines.
- [x] Stop and recreate all services while retaining broker and worker state.
- [x] Archive stopped volumes, restore into fresh names and qualify the restored stack.
- [x] Document installation, safe cutover and rollback.
- [x] Exercise the lifecycle with a native rootless Podman run and use the existing Linux container jobs for hosted qualification.
