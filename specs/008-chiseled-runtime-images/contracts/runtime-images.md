# Runtime image contract

- The broker (`control-plane`), worker (`worker-host`) and edge (`mcp-host`)
  inherit the .NET 11 ASP.NET Core `11.0-resolute-chiseled-extra` image from
  Microsoft Container Registry. Keep their direct `dotnet` entrypoints, app
  UID 1654, system CA roots, ICU and time-zone data. Final images have no
  shell or package manager.
- The image's `/var/lib/hosting` directory is owned by UID 1654 and GID 0
  with mode 0700. Empty named volumes initialize through the existing
  engine semantics. The broker and worker create their existing SQLite files
  with nonempty SQLite headers.
- Both smoke modes inspect the running services from the host. All roots and
  certificate mounts are read-only. The worker reaches the broker by its DNS
  name and earns a successful claim response using the mounted CA and client
  certificate. The loopback edge serves trusted HTTPS and returns HTTP 401
  plus the Bearer discovery challenge for anonymous MCP requests.
- Certificate issuance and renewal, public ingress and deployment policy
  remain external to the image.
