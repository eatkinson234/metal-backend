# Native Metal 53 capped launcher source

Snapshot of the reviewed arm64 `53.2.0` candidate. The source, build scripts and tests are preserved. Synthetic configuration fixtures substitute a sample home path; [fixture provenance](FIXTURE-PROVENANCE.json) records the original and published hashes.

Child profile: API validation off; global GPU shader validation off; server GC on; eight heaps; managed limit `0xA0000000` (2.5 GiB). The existing 8 GiB sampled process guard and telemetry-freshness requirements remain. The managed limit does not cap native/GPU memory or total footprint.

85 recorded CPU checks passed. Actual `.53` serializer round trips preserved unrelated settings and complete profiles for one, four and eight slots. These are software/configuration checks, not physical-controller tests. The capped normal profile has not completed its full replay acceptance, and the candidate is not installed.

Run `./run-cpu-checks.sh` for the included CPU suite and compile-only check; `./build-launcher.sh` builds the separate signed launcher. See [build boundaries](../BUILD.md) before interpreting the results. The installer remains review-only here: signed payload, original release manifest and runtime-approved gate are absent.
