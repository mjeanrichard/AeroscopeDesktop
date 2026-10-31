# Security

## Reporting a vulnerability

Please report security problems privately through [GitHub's vulnerability reporting](https://github.com/mjeanrichard/AeroscopeDesktop/security/advisories/new), not in a public issue. Include what you found, how to reproduce it, and which version you used. This is a hobby project, so expect an answer within a couple of weeks.

Only the latest release gets fixes.

## Verifying a download

Releases are built by the [Release workflow](.github/workflows/release.yml) and carry a signed build provenance attestation. To check that an exe was built by this repository's workflow, and from which commit, run:

```
gh attestation verify Aeroscope-x.y.z.exe -R mjeanrichard/AeroscopeDesktop
```
