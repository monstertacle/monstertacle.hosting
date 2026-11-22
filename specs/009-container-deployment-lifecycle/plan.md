# Implementation Plan

## Design

Extend the existing deployment smoke workflow. Render private Compose and Quadlet fixtures before mutation. Build the chiseled images through the existing paths, record container and volume identities, and qualify initial install, edge certificate replacement, full recreation and cold restore. Use a separate .NET 11 SDK helper for transport and volume inspections.

## Constitution review

The three images remain .NET 11 on the existing public base images. The fixture uses temporary credentials and verifies caller and service authentication separately. The deployed application and protocol remain under their existing tests. The lifecycle scenarios are separately verifiable and run within the current Linux container jobs. A genuine rootless Podman run is tracked separately from hosted CI and public ingress acceptance.

## Deliverables

- Preflight and lifecycle checks using the normal Compose and Quadlet launch paths.
- A cold archive and restore of both role volumes with proof of retained data and a bounded restored-stack launch.
- An operator runbook and exact-revision qualification evidence.
