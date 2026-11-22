# Release process

From v1.9.0 onward, keep local releases outside Git under
`C:\Programowanie\RELEASES\WurmMiningCounter\<version>`, where `<version>`
is the numeric version only (for example `1.9.0`). Never alter earlier version
directories or add release artifacts to Git.

Each version directory contains `Compressed/`, `Full/`, `Lite/`,
`ReleaseNotes.md`, `SHA256SUMS.txt`, and the three versioned ZIP archives:

- `WurmMiningCounter-v<version>-win-x64-compressed.zip`
- `WurmMiningCounter-v<version>-win-x64.zip`
- `WurmMiningCounter-v<version>-win-x64-lite.zip`

Each variant directory contains exactly its matching ZIP's unpacked contents:
`WurmMiningCounter.exe`, the unchanged project `LICENSE`, the release `README.md`,
and an empty `Sounds/` directory. ZIPs include an explicit empty `Sounds/` entry.
Every EXE must have the release version and be built from the release commit.
`SHA256SUMS.txt` contains the verified SHA-256 hashes of the three ZIPs, and
`ReleaseNotes.md` describes that release.

Set release metadata and update the changelog and relevant versioned documents;
include those changes and any release-standard documentation in the release
preparation commit. Build and test the release, create and push the release
commit and annotated version tag, and build all three variants from that commit.
Before publication, verify versions, archive contents, hashes, and byte-for-byte
agreement between each archive and its unpacked directory. Publish exactly the
three ZIPs and `SHA256SUMS.txt` from the version directory to GitHub Release.
Keep the complete local directory after publication.
