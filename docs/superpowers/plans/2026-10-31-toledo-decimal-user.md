# Toledo decimal and user normalization implementation plan

Goal: accept comma decimals and send a SAP user of at most15 characters without modifying Guardian source JSON.
Architecture: existing callback numeric parser and existing SAP projection/readback. Tech: .NET10/C#/xUnit. Spec: docs/superpowers/specs/2026-10-31-toledo-decimal-user-design.md.

## Constraints and review focus
Canonical repository only; no commits or parallel publisher. No real calls. Keep dates/Brasilia, E/S, ID10, NF-e, delivery/contract partner proofs, prePOST hash, fencing, idempotency. Reject malformed separators and precision loss. Preserve unchanged dot inputs and historical audit hashes, including a username previously longer than15. Truncate only outbound UsuarioPesagem; preserve source and all other text. Preserve numeric JSON kinds and never change identifiers. No UI refactor.

## Steps
- [x] Add synthetic tests for full receipt/normalized SAP output, every numeric field, user length boundaries, invalid formats/precision, culture independence and exact historic readback. Run RED before implementation.
- [x] Modify ToledoPesagemCallbackDto.Weight to accept one comma or dot decimal and preserve exact decimal validation/source JSON.
- [x] Modify ToledoSapODataPayload current projection to normalize only known numeric fields and cap UsuarioPesagem. Preserve former current projection in ToledoSapPayloadReadback for exact-hash observation only.
- [x] Run focused regression, Release publish and exact newDLLs with frozen installed dependencies. Review delta and package hashes, manifest, promotion/rollback. Leave deployment pending nominal package authorization.
