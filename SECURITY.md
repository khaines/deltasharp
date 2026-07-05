# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Report privately using **[GitHub Private Vulnerability Reporting](https://github.com/khaines/deltasharp/security/advisories/new)** —
click **"Report a vulnerability"** on the repository's
[Security tab](https://github.com/khaines/deltasharp/security/policy). GitHub
opens a private advisory visible only to you and the maintainers, so exploit
details never touch a public channel. This is the preferred report path and works
without depending on any external email or DNS.

If you are unable to use GitHub Private Vulnerability Reporting, reach a maintainer
privately (see [GOVERNANCE.md](GOVERNANCE.md)) and ask them to open an advisory on
your behalf. **Do not** disclose the issue in a public issue, discussion, or pull
request.

Please include:

- a description of the issue and its impact;
- steps to reproduce or a proof of concept;
- affected versions/components, and any suggested mitigation.

## Our commitment

- We aim to **acknowledge** reports within **3 business days** and provide an
  initial assessment within **10 business days**.
- We practice **coordinated disclosure**: we will work with you on a fix and a
  public advisory, and credit you (with your consent) once a fix is available.
- Please give us reasonable time to remediate before any public disclosure.

## Supply-chain security

DeltaSharp's supply-chain controls — GitHub-native secret scanning with push protection,
dependency/SCA scanning and gating, SBOM generation, deterministic-build provenance, the
artifact-signing posture, and the branch-protection policy — are documented in
[docs/engineering/design/supply-chain-security.md](docs/engineering/design/supply-chain-security.md).
Secret scanning with push protection is enabled on the repository; if you believe a real
credential has been committed, report it through the private channel above so it can be rotated.

## Supported versions

DeltaSharp is pre-release. Until a 1.0 release, only the latest `main` and the most
recent tagged release receive security fixes. This table will be updated as
releases are published.

| Version | Supported |
|---|---|
| `main` (unreleased) | ✅ |
| latest pre-release | ✅ |
| older pre-releases | ❌ |
