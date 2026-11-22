# Configured model workers

The deployment archive contains the chiseled .NET 11 worker. Its optional
`model-gateway` mode runs text requests through the existing Agent Framework
registration and broker claim protocol. MCP users select their authorized
project. The broker assigns its configured lanes, and each worker selects its
profile from private startup configuration.

## Configure a lane

Copy the example fields into the lane's private worker environment file. Keep
the existing broker connection, certificate, and state configuration. Assign
each worker its own broker client certificate, trust scope and state volume.
Set the exact HTTPS chat-completions URL, model or deployment identifier,
instructions and output token limit. Use a separately scoped gateway credential
with provider-side rate and spend limits. Mount the bearer file read-only below
that worker's private configuration directory. Give the runtime user read access
through the mount while restricting the enclosing directory on the host. Keep
the file content, account identifiers, hostnames and addresses in private
configuration. Credential rotation requires a worker restart.

Apply `model-worker/compose.yaml` after the release bundle's `compose.yaml` to
opt both workers in. For Quadlet, install the same `50-model.conf` under both
`hosting-worker.container.d/` and `hosting-worker-secondary.container.d/` next
to the installed units. To enable a single lane, apply the setting and outbound
network only to its worker. The opt-in adds outbound connectivity but publishes
no worker port. Apply an egress policy for the configured gateway and use the
normal system certificate validation. Never allow a gateway to forward the
bearer credential to an untrusted backend. Disable retries after dispatch at
the gateway as well.

## Gateway protocol

The configured address ends in `/chat/completions`. The worker sends one
non-streaming JSON request with `model`, `messages`, `max_completion_tokens`,
`n: 1` and `stream: false`. Qualify the gateway's actual model routing, token
limit enforcement and status code contract before enabling it. A successful
reply is a single assistant text choice with `finish_reason: stop`. The contract
treats 401, 403 and 429 as confirmed failed gateway requests before inference. A gateway that
can emit those statuses after dispatch needs a different adapter or must remap
them to an ambiguous error. A response with `length` or `content_filter` is a
confirmed failed completion without a published partial result. Transport
failure, a redirect, an unsupported response or an unclassified status is
ambiguous. See the [chat-completions wire format](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create).

## Change profiles and handle uncertainty

The broker assigns work by lane. Keep the model, gateway routing, instructions
and profile revision identical across replicas in each lane. Before switching
models, drain queued and running jobs or admit new work through a new lane.
Keep the state volume and profile revision available for reconciliation. Never
recreate a worker identity or delete its state to clear a claim.

A warning containing the job ID and the broker's `unknown` execution certainty
indicate that the worker cannot confirm the remote outcome. It retains the
assignment, occupying one worker slot. Keep its state, investigate the outcome
with the gateway operator, and keep healthy lanes available. This adapter cannot
verify a remote cancel or clear an ambiguous job automatically. A routine
restart keeps the claim. Monitor for this warning and qualify the operational
recovery boundary before broad rollout.

The public release performs source-free synthetic HTTPS checks on hosted Compose
and Quadlet runners. For private live acceptance, provision two approved lane
profiles on hosted infrastructure and use short non-sensitive tasks. Check both
lane artifacts and the gateway's routing and usage records. Keep credentials,
site-specific settings and acceptance evidence private.
