# Installation, options and search validation

This change targets repeated layout hashing during replacement reconciliation. The memo belongs to one `Reconcile` call and is keyed by the loaded `PakIndex` object. No file hashes, path checks, ownership checks, journal writes or recovery steps are skipped. No extracted-file reuse or parallel game-file hashing is introduced.

## Measurements

The generated representative fixture has 1,027 deployed files, 368 invalidated rows across two PAKs, and a 286,816-row base table. Its payload is about 64 MiB. These are warm runs on one Windows machine, alternating the former calculation and the memo, after one warm-up pair. Each operation has five measured pairs. Other checks ran on the same machine, so the ranges include that variability.

| Operation | Former median (range), ms | Memo median (range), ms |
|---|---:|---:|
| Install/update another mod | 9,523 (7,319–17,028) | 1,786 (1,692–3,439) |
| Disable | 9,435 (6,845–18,370) | 1,732 (1,666–3,486) |
| Enable | 8,422 (7,317–16,653) | 1,900 (1,663–3,504) |
| Change priority | 9,544 (7,324–16,480) | 1,972 (1,773–3,422) |

Layout calculations fall from 368 to 2 per operation. This measures operations with an existing deployed set; it is not a first-install, cold-cache or real-game timing. Large payloads, slower disks, extraction and fixers can dominate the complete install. The 10,270-file / 3,680-invalidation control uses a much smaller table and one pair for stress/parity, not a speed estimate. Small-fixture timings and phase counters are also emitted by the harness.

The small fixture's medians span 104–127 ms before and 108–126 ms with the memo; its ranges overlap, with no clear timing benefit. The large metadata control completed with 3,680 layout calls reduced to 2, but its single pair is insufficient to assess a speed change.

`InstallMetrics` is off by default. Set `SLOTSLOADER_PROFILE_FILE` to a writable TSV filename to collect aggregate calls, milliseconds and bytes on process exit. It records no mod names or file paths. Phases include package parsing, preparation, automatic review, game PAK loading, reconciliation, layout hashing, path verification, current-file SHA, execution, state commit, PAK stamps, patch gaps, history, manager records and Installed refresh. Nested timings overlap; do not sum them.

Run a build's `Slotsloader Manager.exe --install-speed-check <new-output-directory>` for generated small, representative and large controls. Add `large` to run only the large control. The comparison switch exists only inside the fixture harness. The output directory must not exist. PAK bytes, deployed hashes, ownership/priority/enabled state, same-size/same-time drift refusal and byte-exact uninstall restoration are checked in both paths.

## Option contract

The detector requires at least two top-level leaf components with one nonempty normalized `NameAsBundle`, no base/main component or dependency, and identical nonempty case-insensitive unions of native and eligible root-file destinations. It runs after existing name/option-order discovery, so it does not rename the mod or reorder the leaves. An internal synthetic parent makes the existing radio and backend choice rules apply in both install modes.

Each inferred group permits at most one member. It is optional when another valid component supplies files. An otherwise empty selection asks the user to choose a group; neither GUI defaults nor the queue invent a choice. CLI and Assistant option output include the group, parent IDs and empty defaults. Assistant rules distinguish optional from required groups. External conflicts remain visible.

Only persisted selections use normalization. For a currently recognized cohort, keep the last member in saved layer order and compare the complete destination-to-winning-entry map before and after. Missing IDs or a failed invariant refuse recomposition. This establishes equivalence against the current source, not historical installed bytes. Older records have no historical per-option manifests: a structurally different package that no longer forms a cohort follows ordinary composition/review rules, without guessing which parts were once alternatives. Partial overlays are deliberately not reclassified. Byte-copy recovery, Enable and Reapply remain source-independent.

The read-only detector audit covered 884 sources in two local mod libraries, excluding download folders: 26 matching sources/cohorts, zero read errors. Both spotlight adjusters matched; the stage-lighting control did not. Private paths and the detailed library inventory are excluded from this repository.

## Search and ordering

Installed PAK matching comes from recorded payload files, including external-manager and leftover-PAK rows. Library text can complement those facts, but a changed or missing source cannot alter the installed PAK fact. Library indexing stores an additive nullable payload field; old records retain known identity while the field is backfilled. A temporary failure leaves that field unknown and preserves identity. Matching and sorting perform no archive or filesystem reads per keystroke.

Portrait priority is a stable partition after the existing sort. It counts the complete distinct fighter set, including fighters outside the current filter. Pending, error and filename guesses do not receive single-character priority; pending display names do not demote known file identity. No portrait selection preserves the former order.

## Checks and limits

Affected suites cover generated archives, options/CLI/Assistant, queue, replacement lifecycle/recovery, source reload, presets, slots/variants, filters and hidden UI renders. Real-input filter and bundle checks read source archives and write only generated outputs. The Assistant pack builder now has a UTF-8 BOM so Windows PowerShell and PowerShell 7 generate the same embedded text.

This is source work for a draft PR: no version bump, deployment, release or in-game validation. Run the full `test.ps1 -RealArchives` before a later release package. Measurements do not establish a universal speedup or guarantee the absence of every regression.
