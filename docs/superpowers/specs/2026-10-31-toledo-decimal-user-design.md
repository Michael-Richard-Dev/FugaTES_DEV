# Toledo: decimal comma and SAP weighing user

Direct, complete operator request: accept Guardian decimal comma and dot; outbound SAP decimals use dot. UsuarioPesagem keeps its first15 characters when longer. Preserve original JSON, names, identifiers, dates, E/S, ID10, partners, NF-e, durable audit and duplicate controls.

Approach: strict field-specific numeric parsing and outbound projection in the existing two components. Global JSON replacement would corrupt text/identifiers; editing original persisted data would break audit, so neither is used. Only weights, item percentage and measured quality values are decimal fields. No thousands separators or mixed separators; reject invalid/nonfinite/rounded/negative business measurements. Preserve precision and trailing zeros; do not change numeric JSON values into strings unnecessarily.

Current projection applies normalization. Exact-hash read-only reconstruction retains the previous projection for long historical usernames. No historical projection can be selected by a sending gateway. No new schema/provider/worker. No real POST, replay0020326, deployment, migration or GRANT in offline preparation. Keep canonical working changes and historical packages.
