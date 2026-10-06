---
title: iGloo command contract V1
type: specification
area: platform
status: current
implementation: partial
tags:
  - igloo
  - topic/architecture
  - topic/safety
aliases: []
reviewed: 2026-10-06
---

# iGloo command contract V1

This is an open, versioned interoperability specification for local iGloo capabilities. It is not an industry standard. The words MUST, MUST NOT, SHOULD and MAY have the meanings in RFC 2119 and RFC 8174 when written in capitals. The CLI is the reference text frontend; the execution engine is the logical Core (Core, Preflight, Iso, Migration, UsbWriter, recovery and distro providers). The CLI MUST NOT implement a second installer. Clients MAY call the dispatcher in process. They MUST NOT parse human terminal output or bypass the engine for a capability they claim to implement.

## Status and scope

The current runtime implements `system.version`, `system.capabilities`, `system.capability.describe`, `system.inspect`, `distro.list`, `distro.inspect`, `distro.compatibility`, `migration.inspect`, `migration.plan`, `recovery.inspect`, `operation.status`, `migration.status` and `iso.acquire`. `migration.plan` is a PLAN; `iso.acquire` is a low-risk cache MUTATION; the others are QUERY capabilities. `migration.plan` is explicitly `AdvisoryOnly` and `executable: false`. `recovery.inspect` currently reports `ObservationUnavailable` and no exact snapshot; it does not claim recovery readiness. `migration.status` can read a future local migration journal entry but no public migration mutation creates one yet. `iso.acquire` invokes the existing cryptographic acquisition service and omits the private cache path. `Igloo.Core.Host` composes the local backend for CLI, Community and Fleet Agent; its read-only factory can serve a future One client. Community's private staging sequence still handles sensitive input and `MigrationManifest` locally, with no public command that accepts it. Fleet's network work types remain assessment and dry run only. No migration, installation, recovery mutation or remote destructive execution capability is registered. An unregistered capability MUST return `UnsupportedCapability`.

The broader target remains incomplete. Community now calls Host interfaces for its preflight, ISO, staging, installation-preparation, USB and Windows-maintenance paths; those private in-process interfaces preserve the existing services and UI behavior. They are not public command capabilities. Fleet Agent's assessment and dry run use Host read-only interfaces. A file operation journal records ISO acquisition, and advisory migration queries return a revision fingerprint. Neither is an exact physical target binding or a cross-reboot install implementation. No endpoint-destructive command may be registered until exact identity, approval, stale-state checks, recovery gates, replay protection and continuation are integrated and tested. `MigrationManifest` remains private local execution/handoff state, never a public command result or Fleet transport record.


## Request and result

JSON uses camel-case property names and string enum names. `protocolVersion` and `capabilityVersion` are integers; `requestId` and `correlationId` are nonempty UUIDs. `input` MUST be an object matching the capability schema. `dryRun` is rejected unless discovery says the capability supports it. An `operationId` identifies accepted long-running work where implemented; ISO acquisition now persists safe lifecycle metadata in a local file journal. `idempotencyKey` remains reserved. `observedStateFingerprint` identifies an observation revision for advisory migration results; it is not authorization or a durable target identity. Its presence cannot enable mutation. The current `iso.acquire` is resumable through the existing cache and serialized per distro with a cross-process lease.

The response repeats protocol, capability and correlation fields. `succeeded` is authoritative. On success, `data` is typed by `resultSchemaId`; on failure, `error.code` is stable and no uncontrolled exception text is emitted. `warnings` and `evidence` are arrays, even when empty. Public evidence MUST be allowlisted and MUST NOT contain credentials, private file paths or raw preflight finding messages. The current `system.inspect` returns typed machine facts and finding codes/severities, not raw device IDs or finding text.

The generic envelope schemas are [request.schema.json](request.schema.json) and [result.schema.json](result.schema.json). [capability.schema.json](capability.schema.json) defines discovery metadata. The capability-specific V1 schemas and samples are in [capabilities.schema.json](capabilities.schema.json). The C# records in `Igloo.Command.Contracts` are the executable serialization model. Schema files describe this implemented subset; adding a future capability requires its own reviewed input/result schema and conformance tests.

Example request:

```json
{"protocolVersion":1,"capabilityId":"distro.inspect","capabilityVersion":1,"requestId":"11111111-1111-1111-1111-111111111111","correlationId":"22222222-2222-2222-2222-222222222222","input":{"distroId":"debian"}}
```

Example result shape (the display name is provider catalogue data):

```json
{"protocolVersion":1,"capabilityId":"distro.inspect","capabilityVersion":1,"requestId":"11111111-1111-1111-1111-111111111111","correlationId":"22222222-2222-2222-2222-222222222222","succeeded":true,"data":{"id":"debian","displayName":"Debian","status":"available"},"warnings":[],"evidence":[]}
```

An advisory plan request and result shape:

```json
{"protocolVersion":1,"capabilityId":"migration.plan","capabilityVersion":1,"requestId":"11111111-1111-1111-1111-111111111111","correlationId":"22222222-2222-2222-2222-222222222222","input":{"distroId":"debian","requestedLinuxBytes":42949672960}}
```

```json
{"protocolVersion":1,"capabilityId":"migration.plan","capabilityVersion":1,"requestId":"11111111-1111-1111-1111-111111111111","correlationId":"22222222-2222-2222-2222-222222222222","succeeded":true,"data":{"distroId":"debian","requiredBytes":42949672960,"blockerCodes":[],"executable":false,"disposition":"AdvisoryOnly"},"warnings":[],"evidence":[],"observedStateFingerprint":"A_FULL_SHA256_HEX_DIGEST_FROM_THE_OBSERVATION"}
```

The placeholder fingerprint above is illustrative; real results contain 64 hexadecimal characters. `observedAvailableBytes` is absent when capacity could not be established. A plan with no reported blocker still cannot be executed. `migration.status` and `operation.status` take `{ "operationId": "<UUID>" }`; unknown IDs return `NotSupported`. Current ISO acquisition creates durable operation records. No public migration operation does.

## Discovery, versioning and errors

`igloo capabilities list --json` returns descriptors. `igloo capabilities describe iso.acquire --json` returns one descriptor. Each gives ID, capability version, input and result schema versions/IDs, query/plan/mutation kind, risk, dry-run support, authority requirement, observed-state requirement and applicable provider IDs. Empty provider IDs mean no provider constraint. Consumers MUST check discovery rather than assume every installation step exists.

The CLI supports `version`, `inspect`, `capabilities list|describe`, `distros list|inspect|compatibility`, `migration inspect|plan|status`, `recovery inspect`, `operations status`, `iso acquire`, and `invoke` for a complete JSON request. `migration plan <distro> [requested-bytes]` is advisory. `--json` yields one result object on stdout; long-running progress stays on stderr in human mode.

The command protocol, capability semantics, serialized input/result schemas and product/runtime assembly version are distinct. Protocol V1 rejects other protocol versions. An unsupported capability version returns `UnsupportedVersion`. Compatible additive fields may be introduced only when old clients can ignore them safely. A destructive capability's meaning MUST NOT broaden in a compatible revision; use a new capability version with explicit approval and state binding. Unknown required fields or changed mutation semantics require a new schema/capability version. Deprecation requires a documented replacement and an overlap period; no deprecation is scheduled for this initial subset.

Error codes are `InvalidRequest`, `UnsupportedCapability`, `UnsupportedVersion`, `PreconditionFailed`, `StateChanged`, `ApprovalRequired`, `PolicyDenied`, `ResourceBusy`, `NotSupported`, `VerificationFailed`, `RecoveryRequired`, `Cancelled` and `InternalFailure`. Only the codes exercised by registered capabilities are currently emitted. Future handlers MUST normalize expected failures to this taxonomy without exposing exception class names or uncontrolled messages. CLI exit codes are 0 success, 2 invalid/unsupported command or version, 3 known operational failure, 4 internal failure, 130 cancellation. Machine mode writes one result JSON object to stdout; progress and diagnostics use stderr or an in-process progress callback.

## Authority and operation lifecycle

Discovery declares risk and authority; this is metadata, not a grant. A future endpoint mutation MUST revalidate current target identity, state, approval and recovery readiness at commit time, acquire cross-process resource leases for disk/partition table, boot configuration, staging and recovery state, and retain operation identity/evidence across reboot. Query, plan and mutation are disjoint capability kinds. A plan MUST NOT authorize execution by itself. The local journal stores IDs, state, stage, timestamps and allowlisted evidence codes, not credentials, private paths or manifests. Host wraps current Community mutations in cross-process resource leases, but these private paths still rely on the existing service safety gates; the journal does not yet span Windows, installer and first boot. No durable installation or recovery claim is made.

## Consumers and providers

An independent distro maintainer implements `IDistroPlugin` below Core, supplies a reviewed `distro.json` and signed ISO metadata, installs/registers the provider, and can call `igloo capabilities list --json`, `igloo distros list --json` and the supported commands without Community. Their own frontend can use the JSON envelope or an in-process dispatcher binding. Provider code MUST depend toward Core abstractions and MUST NOT take over disk or boot mutation. A future iGloo One AI layer can inspect discovery and typed results through this same contract; it must not get arbitrary shell execution or direct privileged Core access. Fleet Server remains policy/orchestration only; Fleet Agent is the local endpoint boundary.
