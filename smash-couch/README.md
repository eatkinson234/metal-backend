# Smash Couch native Metal snapshot

Source snapshot: October 9, 2026. This contains the current `.53` emulator changes, the capped `53.2.0` launcher candidate, and the light UI preview from the three Smash Couch chats. The existing project under `src/` remains intact; its Ryubing revision differs from the exact build used by this work.

| Component | Included | Verified scope | Remaining |
| --- | --- | --- | --- |
| Native emulator `.53` | [109-file overlay](emulator/overlay), [cumulative patch](emulator/native-metal53.patch), [full source identity](emulator/SOURCE-MANIFEST.json), native bridge source | Source identity; recorded build/signing; Run199 native replay, normal game clock and ordinary close | Normal launcher profile, broader rendering and physical-controller acceptance; installation |
| Capped launcher `53.2.0` | [Source, tests and build scripts](launcher) | 85 recorded CPU checks; signed local candidate | Full normal-profile replay and real join/launch/reconnect checks; installation |
| Light UI preview | [Swift source, tests, artwork and build scripts](ui-preview) | 169 recorded software checks and native preview inspection | Live adapter, real controllers, TV and installation |

See [current status](STATUS.md), [source/build instructions](BUILD.md), and [verification summary](evidence/VERIFICATION-SUMMARY.json). The snapshot does not contain a finished release. It includes no game files, firmware, keys, user saves, runtime caches, signed app bundles, or active launch approvals.

The emulator overlay is byte-exact against the original source identity. Launcher and UI source are copied without implementation changes. Launcher test fixtures replace the original home path with a sample path, documented in [fixture provenance](launcher/FIXTURE-PROVENANCE.json). Historical raw runtime evidence remains local; its identifying hashes are recorded in the verification summary. No new gameplay or GPU test was run to publish this snapshot.

The Ryubing-derived emulator and launcher code retain their included MIT notices. The UI artwork is an original generated asset; its prompt is included. See each component's license file.
